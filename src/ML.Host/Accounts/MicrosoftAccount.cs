using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ML.Core.Launch;

namespace ML.Host.Accounts;

/// <summary>
/// 账号簿：正版账号（微软设备码登录）与离线账号放在一起，启动时按 id 取一个。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么在 Host 而不在 Core：</b>内核的判据是「从磁盘上已有的东西启动一个实例」，
/// 它必须零网络（<c>tests/ML.Core.Tests/ArchitectureTests.cs</c> 用程序集引用断言这一点）。
/// 认证不属于那个闭包——它产出的是一个 <see cref="LaunchAccount"/>，拿到之后启动流程完全一样。
/// 所以网络代码全部留在宿主层，内核只接收结果。
/// </para>
/// <para>
/// <b>令牌怎么存：</b>只存 refresh token 与 Minecraft access token（明文，落在数据目录的
/// <c>accounts.json</c>，和其它启动器一样）。不存微软的 access token——它一小时就过期，
/// 存了也没用。要更严的话该上 DPAPI，但那是另一个决定，没在这里偷偷做。
/// </para>
/// <para>
/// 离线账号没有令牌，只有名字与 uuid（<see cref="OfflineAccount"/> 的 MD5 规则），
/// 和正版一样是一个 <see cref="StoredAccount"/>，区别只在 <see cref="StoredAccount.Kind"/>。
/// 分成两种存储会让"启动时选一个账号"出现两条分支，那是没必要的。
/// </para>
/// </remarks>
internal sealed class MicrosoftAccountService
{
    private const string DeviceCodeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string Scope = "XboxLive.signin offline_access";

    private const string KindOffline = "offline";
    private const string KindMicrosoft = "msa";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _storePath;
    private readonly string _legacyPath;
    private readonly Func<string?> _clientId;
    private readonly object _gate = new();

    private AccountBook _book;

    public MicrosoftAccountService(string dataRoot, Func<string?> clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _storePath = Path.Combine(dataRoot, "accounts.json");
        _legacyPath = Path.Combine(dataRoot, "account.json");
        _clientId = clientId;
        _book = Load();
    }

    public string? ClientId
    {
        get
        {
            string? configured = _clientId();
            return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        }
    }

    /// <summary>上次为什么退回离线账号。给日志用，成功时是 null。</summary>
    public string? LastFallbackReason { get; private set; }

    // ── 账号簿 ────────────────────────────────────────────────────────

    public IReadOnlyList<AccountView> List()
    {
        lock (_gate)
        {
            return _book.Accounts
                .OrderByDescending(a => a.Kind == KindMicrosoft)
                .ThenBy(a => a.AddedAt)
                .Select(ToView)
                .ToArray();
        }
    }

    public string? LastUsedId
    {
        get
        {
            lock (_gate)
            {
                return _book.LastUsedId;
            }
        }
    }

    public AccountView? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            StoredAccount? entry = _book.Accounts.FirstOrDefault(a => a.Id == id);
            return entry is null ? null : ToView(entry);
        }
    }

    /// <summary>
    /// 加一个离线账号。同名已存在时直接返回那一条，不重复添加——
    /// 用户连点两下「添加」不该得到两个一样的名字。
    /// </summary>
    public AccountView AddOffline(string name)
    {
        // OfflineAccount.Create 会校验名字（长度、空白、控制字符），非法名字从这里抛出去。
        LaunchAccount created = OfflineAccount.Create(name);

        lock (_gate)
        {
            StoredAccount? existing = _book.Accounts.FirstOrDefault(
                a => a.Kind == KindOffline && string.Equals(a.Name, created.PlayerName, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                RememberLocked(existing.Id);
                SaveLocked();
                return ToView(existing);
            }

            var entry = new StoredAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = KindOffline,
                Name = created.PlayerName,
                Uuid = created.Uuid,
                AddedAt = DateTimeOffset.Now
            };

            _book.Accounts.Add(entry);
            RememberLocked(entry.Id);
            SaveLocked();
            return ToView(entry);
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            StoredAccount? entry = _book.Accounts.FirstOrDefault(a => a.Id == id);

            if (entry is null)
            {
                return false;
            }

            _book.Accounts.Remove(entry);

            if (_book.LastUsedId == id)
            {
                _book.LastUsedId = _book.Accounts.FirstOrDefault()?.Id;
            }

            SaveLocked();
            return true;
        }
    }

    public void Remember(string id)
    {
        lock (_gate)
        {
            if (_book.Accounts.Any(a => a.Id == id))
            {
                RememberLocked(id);
                SaveLocked();
            }
        }
    }

    // ── 正版登录 ──────────────────────────────────────────────────────

    /// <summary>第一步：拿设备码。返回给界面显示「去这个网址输这个码」。</summary>
    public async Task<DeviceCodeInfo> BeginLoginAsync(CancellationToken cancellationToken)
    {
        string clientId = ClientId ?? throw new InvalidOperationException(
            "还没配置微软应用 ID。到 portal.azure.com 注册一个公共客户端应用，把「应用程序(客户端) ID」填进设置。");

        using var response = await Http.PostAsync(
            DeviceCodeEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = Scope
            }),
            cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(DescribeOAuthFailure("申请设备码失败", body, (int)response.StatusCode));
        }

        using var doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        return new DeviceCodeInfo(
            DeviceCode: root.GetProperty("device_code").GetString()!,
            UserCode: root.GetProperty("user_code").GetString()!,
            VerificationUri: root.GetProperty("verification_uri").GetString()!,
            Message: root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "",
            Interval: TimeSpan.FromSeconds(root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5),
            ExpiresIn: TimeSpan.FromSeconds(root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900));
    }

    /// <summary>第二步：轮询直到用户授权、拒绝或超时。成功时写进账号簿。</summary>
    public async Task<AccountView> CompleteLoginAsync(DeviceCodeInfo info, CancellationToken cancellationToken)
    {
        string clientId = ClientId ?? throw new InvalidOperationException("微软应用 ID 不见了。");
        DateTimeOffset deadline = DateTimeOffset.UtcNow + info.ExpiresIn;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(info.Interval, cancellationToken).ConfigureAwait(false);

            using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = info.DeviceCode
            }), cancellationToken).ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string? error = TryReadString(body, "error");

                switch (error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        // 微软要求放慢：把间隔加上去再加 5 秒，别再撞它。
                        info = info with { Interval = info.Interval + TimeSpan.FromSeconds(5) };
                        continue;
                    case "expired_token":
                        throw new InvalidOperationException("设备码已经过期，重新点一次登录。");
                    case "authorization_declined":
                        throw new InvalidOperationException("你在浏览器里拒绝了授权。");
                    default:
                        throw new InvalidOperationException(DescribeOAuthFailure("登录失败", body, (int)response.StatusCode));
                }
            }

            StoredAccount signed = await FinishSignInAsync(ReadTokens(body), cancellationToken).ConfigureAwait(false);
            return ToView(signed);
        }

        throw new InvalidOperationException("等授权超时了，重新点一次登录。");
    }

    // ── 启动时取账号 ──────────────────────────────────────────────────

    /// <summary>
    /// 按 id 取启动账号；没给 id 就用「最近使用」；都没有就退回实例自己的玩家名。
    /// 正版令牌过期会先静默刷新；刷新失败（被撤销、改密码）退回离线，不挡住启动。
    /// </summary>
    public async Task<LaunchAccount> ResolveAsync(string? accountId, string fallbackName, CancellationToken cancellationToken)
    {
        StoredAccount? entry;

        lock (_gate)
        {
            entry = _book.Accounts.FirstOrDefault(a => a.Id == accountId)
                    ?? _book.Accounts.FirstOrDefault(a => a.Id == _book.LastUsedId);

            if (entry is not null && entry.Kind == KindOffline)
            {
                return OfflineAccount.Create(entry.Name);
            }
        }

        if (entry is null || ClientId is null)
        {
            return OfflineAccount.Create(fallbackName);
        }

        try
        {
            if (entry.AccessToken is null
                || entry.AccessTokenExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5))
            {
                MsaTokens refreshed = await RefreshAsync(entry.RefreshToken ?? "", cancellationToken).ConfigureAwait(false);
                StoredAccount upgraded = await FinishSignInAsync(refreshed, cancellationToken).ConfigureAwait(false);
                return ToLaunchAccount(upgraded);
            }

            LastFallbackReason = null;
            return ToLaunchAccount(entry);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException)
        {
            // 网络不通或 refresh token 失效：退回离线，让用户至少能进单机。
            // 这条不静默：调用方会拿到 IsOffline=true 的账号，日志里也有记录。
            LastFallbackReason = ex.Message;
            return OfflineAccount.Create(entry.Name);
        }
    }

    private LaunchAccount ToLaunchAccount(StoredAccount entry) => new()
    {
        PlayerName = entry.Name,
        Uuid = entry.Uuid ?? OfflineAccount.ComputeUuid(entry.Name),
        AccessToken = entry.AccessToken ?? "0",
        UserType = KindMicrosoft,
        Xuid = entry.Xuid,
        ClientId = ClientId
    };

    // ── 协议细节 ──────────────────────────────────────────────────────

    /// <summary>MSA 令牌到手之后：XBL → XSTS → Minecraft → 档案，一路换到底再落盘。</summary>
    private async Task<StoredAccount> FinishSignInAsync(MsaTokens tokens, CancellationToken cancellationToken)
    {
        (string xblToken, string uhs) = await XboxLiveAuthenticateAsync(tokens.AccessToken, cancellationToken).ConfigureAwait(false);
        (string xstsToken, string xuid) = await XstsAuthorizeAsync(xblToken, cancellationToken).ConfigureAwait(false);
        (string mcToken, TimeSpan mcLifetime) = await MinecraftLoginAsync(uhs, xstsToken, cancellationToken).ConfigureAwait(false);
        (string uuid, string name) = await MinecraftProfileAsync(mcToken, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            // 同一个微软账号重复登录时更新那一条，不要变成两张卡。
            StoredAccount? existing = _book.Accounts.FirstOrDefault(
                a => a.Kind == KindMicrosoft && string.Equals(a.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

            var entry = (existing ?? new StoredAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = KindMicrosoft,
                Uuid = uuid,
                Name = name,
                AddedAt = DateTimeOffset.Now
            }) with
            {
                Name = name,
                Uuid = uuid,
                RefreshToken = string.IsNullOrEmpty(tokens.RefreshToken) ? existing?.RefreshToken : tokens.RefreshToken,
                AccessToken = mcToken,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow + mcLifetime,
                Xuid = xuid
            };

            if (existing is null)
            {
                _book.Accounts.Add(entry);
            }
            else
            {
                int index = _book.Accounts.IndexOf(existing);
                _book.Accounts[index] = entry;
            }

            RememberLocked(entry.Id);
            SaveLocked();
            LastFallbackReason = null;
            return entry;
        }
    }

    private async Task<MsaTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        string clientId = ClientId ?? throw new InvalidOperationException("微软应用 ID 不见了。");

        using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope
        }), cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // invalid_grant 通常意味着 refresh token 被撤销（改密码、在微软账号里移除授权）。
            throw new InvalidOperationException(DescribeOAuthFailure("令牌刷新失败", body, (int)response.StatusCode));
        }

        return ReadTokens(body);
    }

    private static async Task<(string Token, string Uhs)> XboxLiveAuthenticateAsync(string msaAccessToken, CancellationToken cancellationToken)
    {
        // 必须用字典：System.Text.Json 的 web 默认会把属性名改成小驼峰，
        // 而 Xbox Live 只认 PascalCase（实测 camelCase 直接 400，连令牌都不看）。
        // 字典键不受 PropertyNamingPolicy 影响，能原样发出去。
        var payload = new Dictionary<string, object>
        {
            ["Properties"] = new Dictionary<string, object>
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = $"d={msaAccessToken}"
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT"
        };

        using var response = await Http.PostAsJsonAsync(
            "https://user.auth.xboxlive.com/user/authenticate", payload, cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Xbox Live 认证失败（HTTP {(int)response.StatusCode}）：{Snippet(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        string token = doc.RootElement.GetProperty("Token").GetString()!;
        string uhs = doc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
        return (token, uhs);
    }

    private static async Task<(string Token, string Xuid)> XstsAuthorizeAsync(
        string xblToken, CancellationToken cancellationToken)
    {
        // 同上：XSTS 也要求 PascalCase。
        var payload = new Dictionary<string, object>
        {
            ["Properties"] = new Dictionary<string, object>
            {
                ["SandboxId"] = "RETAIL",
                ["UserTokens"] = new[] { xblToken }
            },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT"
        };

        using var response = await Http.PostAsJsonAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize", payload, cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 401 带 XErr 时是「账号问题」而不是网络问题，值得单独翻译成人话。
            throw new InvalidOperationException(DescribeXstsFailure(body));
        }

        using var doc = JsonDocument.Parse(body);
        string token = doc.RootElement.GetProperty("Token").GetString()!;
        string xuid = doc.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0]
            .TryGetProperty("xid", out var xid) ? xid.GetString() ?? "" : "";
        return (token, xuid);
    }

    private static async Task<(string Token, TimeSpan Lifetime)> MinecraftLoginAsync(
        string uhs, string xstsToken, CancellationToken cancellationToken)
    {
        var payload = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };

        using var response = await Http.PostAsJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox", payload, cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                (int)response.StatusCode == 429
                    ? "Minecraft 服务限流了，等几分钟再试。"
                    : $"Minecraft 登录失败（HTTP {(int)response.StatusCode}）。这个账号可能没有购买 Java 版。");
        }

        using var doc = JsonDocument.Parse(body);
        string token = doc.RootElement.GetProperty("access_token").GetString()!;
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 86400;
        return (token, TimeSpan.FromSeconds(expiresIn));
    }

    private static async Task<(string Uuid, string Name)> MinecraftProfileAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                (int)response.StatusCode == 404
                    ? "这个微软账号没有 Java 版 Minecraft（没买，或还没建角色名）。"
                    : $"读取 Minecraft 档案失败（HTTP {(int)response.StatusCode}）。");
        }

        using var doc = JsonDocument.Parse(body);
        return (doc.RootElement.GetProperty("id").GetString()!, doc.RootElement.GetProperty("name").GetString()!);
    }

    // ── 落盘 ─────────────────────────────────────────────────────────

    private AccountBook Load()
    {
        if (File.Exists(_storePath))
        {
            try
            {
                // 一定要带上 JsonOptions：它开了大小写不敏感 + 小驼峰，
                // 少了它读小驼峰的 accounts.json 会静默得到空表（属性全不匹配、也不报错）。
                AccountBook? loaded = JsonSerializer.Deserialize<AccountBook>(File.ReadAllText(_storePath), JsonOptions);
                if (loaded is not null)
                {
                    return new AccountBook
                    {
                        Version = 1,
                        LastUsedId = loaded.LastUsedId,
                        Accounts = loaded.Accounts ?? []
                    };
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // 坏文件不静默删：改名保留，界面会显示空账号簿。
                TryMoveAside(_storePath);
            }
        }

        return ImportLegacy();
    }

    /// <summary>
    /// 旧版只有一个 <c>account.json</c>（单条正版账号）。第一次跑新版时把它搬进账号簿，
    /// 然后把旧文件改名留底——不做这一步的话，升级一次就等于静默退出登录。
    /// </summary>
    private AccountBook ImportLegacy()
    {
        var book = new AccountBook();

        if (!File.Exists(_legacyPath))
        {
            return book;
        }

        try
        {
            StoredAccount? legacy = JsonSerializer.Deserialize<StoredAccount>(File.ReadAllText(_legacyPath), JsonOptions);

            if (legacy is not null && !string.IsNullOrWhiteSpace(legacy.RefreshToken))
            {
                var entry = legacy with
                {
                    Id = string.IsNullOrWhiteSpace(legacy.Id) ? Guid.NewGuid().ToString("N") : legacy.Id,
                    Kind = KindMicrosoft,
                    AddedAt = legacy.AddedAt == default ? DateTimeOffset.Now : legacy.AddedAt
                };

                book.Accounts.Add(entry);
                book.LastUsedId = entry.Id;
                Save(book);
            }

            TryMoveAside(_legacyPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            TryMoveAside(_legacyPath);
        }

        return book;
    }

    private void TryMoveAside(string path)
    {
        try
        {
            File.Move(path, path + ".broken", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 挪不动就算了，不影响使用。
        }
    }

    private void SaveLocked() => Save(_book);

    private void Save(AccountBook book)
    {
        string temp = _storePath + ".tmp";

        // 原子写：先写临时文件再 move，断电也不会留下半个 JSON。
        File.WriteAllText(temp, JsonSerializer.Serialize(book, JsonOptions));
        File.Move(temp, _storePath, overwrite: true);
    }

    private void RememberLocked(string id) => _book = _book with { LastUsedId = id };

    private static AccountView ToView(StoredAccount entry) =>
        new(entry.Id, entry.Kind, entry.Name, entry.Uuid);

    // 和其余配置文件一致用小驼峰；WhenWritingDefault 让离线账号不用带
    // AccessToken / AccessTokenExpiresAt 这些它根本没有的字段。
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    private static MsaTokens ReadTokens(string body)
    {
        using var doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        string accessToken = root.GetProperty("access_token").GetString()!;
        string refreshToken = root.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString() ?? ""
            : "";
        int expiresIn = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;

        return new MsaTokens(accessToken, refreshToken, TimeSpan.FromSeconds(expiresIn));
    }

    private static string? TryReadString(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(name, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把服务端返回压成一行塞进错误消息。之前只报状态码，等于把唯一的线索丢了——
    /// "HTTP 400" 和 "HTTP 400：{...具体字段...}" 是两种可排查性。
    /// </summary>
    private static string Snippet(string body)
    {
        string flat = body.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 240 ? flat : flat[..240] + "…";
    }

    private static string DescribeOAuthFailure(string prefix, string body, int? statusCode = null)
    {
        string? error = TryReadString(body, "error");
        string? description = TryReadString(body, "error_description");

        if (error is null)
        {
            // 连错误码都没解析出来时，至少把 HTTP 状态带出去——
            // "400" 和 "申请设备码失败" 是两条信息，用户搜前者才有结果。
            return prefix + (statusCode is null ? "" : $"（HTTP {statusCode}）") + "：服务器没有返回可读原因。";
        }

        // AADSTS 是微软的错误码，用户搜这个码才有结果，所以原样带上；
        // 但后面的 Trace ID / Correlation ID 对用户没意义，切掉只留第一句。
        string reason = description?.Split('\n')[0].Trim() ?? "";
        int trace = reason.IndexOf("Trace ID", StringComparison.OrdinalIgnoreCase);
        if (trace > 0)
        {
            reason = reason[..trace].Trim();
        }

        return $"{prefix}：{error}" + (string.IsNullOrWhiteSpace(reason) ? "" : $"（{reason}）");
    }

    private static string DescribeXstsFailure(string body)
    {
        string? xerr = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("XErr", out var value))
            {
                xerr = value.ValueKind == JsonValueKind.Number ? value.GetInt64().ToString() : value.GetString();
            }
        }
        catch (JsonException)
        {
            // 交给下面的通用分支。
        }

        return xerr switch
        {
            "2148916233" => "这个微软账号还没有 Xbox 档案。先去 xbox.com 建一个（免费），再回来登录。",
            "2148916235" => "这个账号所在地区不支持 Xbox Live。",
            "2148916238" => "这是未成年账号，需要先加入家庭组。",
            null => $"Xbox 授权失败，可能是网络问题，稍后再试。{Snippet(body)}",
            _ => $"Xbox 授权失败（XErr {xerr}）：{Snippet(body)}"
        };
    }

    private sealed record MsaTokens(string AccessToken, string RefreshToken, TimeSpan ExpiresIn);
}

/// <summary>账号簿的落盘形状。</summary>
internal sealed record AccountBook
{
    public int Version { get; init; } = 1;

    // 这两个是可写的：删除账号、切换「最近使用」都会就地改，不需要整本重建。
    public string? LastUsedId { get; set; }
    public List<StoredAccount> Accounts { get; set; } = [];
}

internal sealed record StoredAccount
{
    public required string Id { get; init; }

    /// <summary><c>offline</c> 或 <c>msa</c>。</summary>
    public required string Kind { get; init; }

    public required string Name { get; init; }
    public string? Uuid { get; init; }
    public string? RefreshToken { get; init; }
    public string? AccessToken { get; init; }
    public DateTimeOffset AccessTokenExpiresAt { get; init; }
    public string? Xuid { get; init; }
    public DateTimeOffset AddedAt { get; init; }
}

internal sealed record DeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string Message,
    TimeSpan Interval,
    TimeSpan ExpiresIn);

/// <summary>给界面看的账号（不含令牌）。</summary>
internal sealed record AccountView(string Id, string Kind, string Name, string? Uuid);

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using ML.Host.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ML.Host.Diagnostics;
using ML.Host.Shell;
using Velopack;

namespace ML.Host;

/// <summary>
/// 外壳进程：Kestrel 服页面，WebView2 显示，中间的桥走 <c>chrome.webview.postMessage</c>。
/// </summary>
/// <remarks>
/// 两个模式：
/// ① 默认 —— 启动器界面（<c>index.html</c>）。
/// ② <c>ML_SKELETON_AUTOCLOSE=1</c> —— 自检页（<c>selfcheck.html</c>），跑完四个集成边界自动关窗，
///    退出码即结论。这条路径是 CI 用的，不要动。
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // 必须是本方法的第一句。Velopack 在「安装 / 卸载 / 更新」这几个时机会接管进程
        // （打快捷方式、写注册表、替换文件），做完直接退出，我们后面的代码根本不该跑。
        VelopackApp.Build().Run();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool skeletonMode = Environment.GetEnvironmentVariable("ML_SKELETON_AUTOCLOSE") == "1";

        // 自检页要跑仓库里的 node 插件与 artifacts 目录，所以只有这条路要求「在仓库里」。
        // 产品路径（RunShell）不依赖任何源码文件：页面已经编进程序集，双击 exe 就能跑。
        string? repoRoot = null;
        if (skeletonMode)
        {
            repoRoot = ResolveRepoRoot();
            if (repoRoot is null)
            {
                Console.Error.WriteLine("[FAIL] 找不到仓库根（未找到 ml.slnx）");
                return 2;
            }
        }

        if (!EnsureWebViewRuntime(skeletonMode))
        {
            return 6;
        }

        var (app, token, address) = StartServer(
            EmbeddedWebAssets.Load(),
            Environment.GetEnvironmentVariable("ML_WEB_DIR"),
            Path.Combine(ResolveDataRoot(), "plugins"));
        try
        {
            return skeletonMode
                ? RunSkeleton(app, token, address, repoRoot!)
                : RunShell(app, token, address);
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// WebView2 运行时是外部依赖（随机安装的 Edge 一起进来，但「精简系统」「企业镜像」上真会缺）。
    /// 缺了必须**说清缺什么**：WinExe 没有控制台，只往 stderr 写一句话，用户双击之后看到的是
    ///「窗口一闪」或「什么都没发生」，那种报告没法处理。
    /// </summary>
    private static bool EnsureWebViewRuntime(bool silent)
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Console.WriteLine($"[OK] WebView2 运行时 {version}");
            return true;
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException)
        {
            const string message =
                "找不到 WebView2 运行时，界面无法显示。\n\n"
                + "请安装 Microsoft Edge WebView2 Runtime（免费，Windows 10/11 通常已自带）：\n"
                + "https://developer.microsoft.com/microsoft-edge/webview2/\n\n"
                + "安装后重新打开本程序。";

            Console.Error.WriteLine("[FAIL] 缺少 WebView2 运行时");
            Console.Error.WriteLine(message);

            if (!silent)
            {
                MessageBox.Show(message, "Enderman Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return false;
        }
    }

    // ── 服务器 ──────────────────────────────────────────────────────

    private static (WebApplication App, string Token, string Address) StartServer(
        IReadOnlyDictionary<string, byte[]> assets,
        string? webDirectoryOverride,
        string pluginsRoot)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            // 首次导航带 ?t=<token>，换成 SameSite=Strict 的 Cookie；
            // 之后页面自己发起的子资源请求才带得上凭据（否则 app.js 会被 401 挡掉）。
            if (ctx.Request.Query["t"] == token)
            {
                ctx.Response.Cookies.Append("ml_sid", token, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Path = "/"
                });
                await next();
                return;
            }

            if (ctx.Request.Cookies["ml_sid"] == token)
            {
                await next();
                return;
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
        });

        // 插件自带的图片 / css：/plugin/<id>/<相对路径>。
        // 只认插件目录内的文件，路径穿越一律 404——这是唯一一处把磁盘目录暴露给页面的地方。
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/plugin", out PathString rest))
            {
                await next();
                return;
            }

            string relative = Uri.UnescapeDataString(rest.Value ?? "").TrimStart('/');
            string[] parts = relative.Split('/', 2);

            if (parts.Length < 2)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            string root = Path.GetFullPath(Path.Combine(pluginsRoot, parts[0]));
            string full = Path.GetFullPath(Path.Combine(root, parts[1]));

            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(full))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            ctx.Response.ContentType = ContentTypeFor(full);
            await ctx.Response.SendFileAsync(full);
        });

        // ML_WEB_DIR 是开发期逃生口：改 HTML/CSS 不想等一次发布时指到源码目录。
        if (!string.IsNullOrWhiteSpace(webDirectoryOverride) && Directory.Exists(webDirectoryOverride))
        {
            var provider = new PhysicalFileProvider(webDirectoryOverride);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            Console.WriteLine($"[OK] 页面来源：磁盘 {webDirectoryOverride}");
        }
        else
        {
            // 嵌入资源中间件是终结性的（匹配不到就 404，不会落到后面），所以用 Run 而不是 Use。
            app.Run(EmbeddedWebAssets.Middleware(assets));
            Console.WriteLine($"[OK] 页面来源：程序集内嵌 {assets.Count} 个文件");
        }

        app.MapGet("/__alive", () => Results.Text("ok"));

        app.StartAsync().GetAwaiter().GetResult();

        string address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        return (app, token, address);
    }

    /// <summary>
    /// 处理宿主发来的窗口动作（「启动器可见性」设置）。
    /// 只认四种：hide / show / minimize / close。
    /// </summary>
    /// <summary>从事件 JSON 里取 data.instance；没有就是 null（插件/宿主级日志）。</summary>
    private static string? ReadEventInstance(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty("data", out JsonElement data)
                   && data.ValueKind == JsonValueKind.Object
                   && data.TryGetProperty("instance", out JsonElement instance)
                   && instance.ValueKind == JsonValueKind.String
                ? instance.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void HandleWindowEvent(string json, Form form, IEnumerable<LogWindow> logWindows)
    {
        if (!json.Contains("\"window\"", StringComparison.Ordinal))
        {
            return;
        }

        try
        {

            using JsonDocument document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("event", out JsonElement name)
                || name.GetString() != "window"
                || !document.RootElement.TryGetProperty("data", out JsonElement data)
                || !data.TryGetProperty("action", out JsonElement actionElement))
            {
                return;
            }

            string? action = actionElement.GetString();

            form.BeginInvoke(() =>
            {

                switch (action)
                {
                    case "hide":
                        // 日志窗口是独立窗体：只藏主窗会留下孤零零的日志窗
                        foreach (LogWindow window in logWindows)
                        {
                            window.Hide();
                        }

                        form.Hide();
                        break;

                    case "show":
                        form.Show();
                        form.WindowState = FormWindowState.Normal;
                        form.Activate();

                        foreach (LogWindow window in logWindows)
                        {
                            if (window.IsOpen)
                            {
                                window.Show();
                            }
                        }

                        break;

                    case "minimize":
                        form.WindowState = FormWindowState.Minimized;
                        break;

                    // 无边框没有系统按钮，标题栏那三个由前端发上来
                    case "maximize":
                        form.WindowState = FormWindowState.Maximized;
                        break;

                    case "restore":
                        form.WindowState = FormWindowState.Normal;
                        break;

                    case "toggle-maximize":
                        if (form is ChromeForm chrome)
                        {
                            chrome.ToggleMaximize();
                        }
                        else
                        {
                            form.WindowState = form.WindowState == FormWindowState.Maximized
                                ? FormWindowState.Normal
                                : FormWindowState.Maximized;
                        }

                        break;

                    // 拖动交给系统：贴边分屏、双击最大化都由系统原生处理
                    case "drag":
                        (form as ChromeForm)?.BeginSystemDrag();
                        break;

                    case "close":
                        form.Close();
                        break;
                }


            });
        }
        catch (Exception ex) when (ex is JsonException or ObjectDisposedException or InvalidOperationException)
        {
            // 窗口动作失败不该影响启动：游戏已经在跑了。
            Debug.WriteLine($"[window] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };

    // ── 界面模式 ────────────────────────────────────────────────────

    private static int RunShell(WebApplication app, string token, string address)
    {
        _ = app;

        string dataRoot = ResolveDataRoot();
        int exitCode = 0;

        // 崩溃守护：插件和宿主同进程，拦不住它把进程带崩——
        // 那就保证崩了之后留得下现场、并且能一键恢复。
        var crashGuard = new CrashGuard(dataRoot, ShellHost.LauncherVersion);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // 进程马上要死，这里只做一件事：把现场写盘。
            crashGuard.Record(e.ExceptionObject as Exception, "AppDomain");
        };

        Application.ThreadException += (_, e) =>
        {
            crashGuard.Record(e.Exception, "UI");
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // 后台任务里的异常默认没人管：记下来，然后标记已观察，别让它在 GC 时炸。
            crashGuard.Record(e.Exception, "Task");
            e.SetObserved();
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // 调试用：ML_TRAY_PREVIEW=<目录> 时只把托盘菜单渲染成 PNG 就退出，
        // 菜单是独立窗口，CDP 截不到，只能这样看外观。
        string? preview = Environment.GetEnvironmentVariable("ML_TRAY_PREVIEW");

        if (!string.IsNullOrWhiteSpace(preview))
        {
            Directory.CreateDirectory(preview);

            foreach (bool hover in new[] { false, true })
            {
                using var menu = new TrayMenu("退出");
                menu.SavePreview(Path.Combine(preview, hover ? "tray-menu-hover.png" : "tray-menu.png"), hover);
            }

            Console.WriteLine($"[OK] 托盘菜单预览已写出：{preview}");
            return 0;
        }

        using var form = CreateForm("Enderman Launcher");
        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);

        ShellHost? shell = null;
        // 每个实例一个日志窗口：多开时两边的输出不能混在一起。
        // 键是实例名；实例为 null 的日志（插件、宿主）发给所有打开的窗口。
        var logWindows = new Dictionary<string, LogWindow>(StringComparer.OrdinalIgnoreCase);
        var smokeEvents = new SmokeEventSink();

        form.FormClosed += (_, _) =>
        {
            // 走到这里说明是正常退出：把"我在运行"的标记删掉，下次启动就不会误报崩溃。
            crashGuard.MarkCleanExit();

            foreach (LogWindow window in logWindows.Values)
            {
                window.Close();
            }
        };

        // 托盘常驻：启动就有，点 × 只是藏窗口，从这里能叫回来。
        // 退出菜单要把 CloseForReal 置上，否则 OnFormClosing 又会把它拦成"隐藏"。
        using var tray = new TrayIcon(form, () =>
        {
            if (form is ChromeForm chrome)
            {
                chrome.CloseForReal = true;
            }

            form.Close();
            Application.Exit();
        });

        form.Shown += async (_, _) =>
        {
            try
            {
                var core = await InitializeWebViewAsync(webView);

                // emit 可能来自任意线程（装机在 Task.Run 里），而 WebView2 只认 UI 线程。
                // 不切线程会抛 InvalidOperationException，而且是从后台任务里抛，
                // 表现为「装机跑完了但界面一直停在准备中」。
                shell = new ShellHost(dataRoot, json =>
                {
                    smokeEvents.Observe(json);

                    // 窗口动作是宿主发的事件：窗口属于这一层，宿主不该直接碰 WinForms。
                    HandleWindowEvent(json, form, logWindows.Values);

                    if (form.IsHandleCreated && form.InvokeRequired)
                    {
                        form.BeginInvoke(() => Post(core, json));
                    }
                    else
                    {
                        Post(core, json);
                    }

                    // 日志按实例分发：没有实例的事件（插件、窗口动作）广播给所有开着的窗口。
                    string? eventInstance = ReadEventInstance(json);

                    if (eventInstance is null)
                    {
                        foreach (LogWindow window in logWindows.Values)
                        {
                            window.Post(json);
                        }
                    }
                    else if (logWindows.TryGetValue(eventInstance, out LogWindow? target))
                    {
                        target.Post(json);
                    }
                }, crashGuard, RestartSelf, () => PickFolder(form));

                // 窗口尺寸只有这一层知道：交给宿主，供「窗口大小」设置使用。
                shell.LauncherWindowSize = (form.ClientSize.Width, form.ClientSize.Height);
                Rectangle work = Screen.FromControl(form).WorkingArea;
                shell.ScreenWorkArea = (work.Width, work.Height);

                core.WebMessageReceived += async (_, e) =>
                {
                    string request = e.WebMessageAsJson;
                    string reply;

                    try
                    {
                        // 自绘标题栏发来的窗口动作（最小化/最大化/拖动）不走 shell 命令通道：
                        // 它没有 seq，也不需要回包，直接作用在窗体上就完事。
                        if (request.Contains("\"event\":\"window\"", StringComparison.Ordinal))
                        {
                            HandleWindowEvent(request, form, logWindows.Values);
                            return;
                        }

                        (string? command, string? instanceName) = ReadRequest(request);

                        // 启动游戏时开日志窗口：日志只在「真的在跑游戏」时有意义。
                        if (command == "launch.start" && instanceName is not null)
                        {
                            if (!logWindows.TryGetValue(instanceName, out LogWindow? window) || !window.IsOpen)
                            {
                                logWindows[instanceName] = new LogWindow(
                                    $"游戏日志 · {instanceName}",
                                    $"{address}/log.html?t={token}",
                                    InitializeWebViewAsync);
                            }
                        }

                        reply = await shell.HandleAsync(request).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        reply = JsonSerializer.Serialize(new
                        {
                            seq = 0,
                            payload = new { ok = false, error = $"{ex.GetType().Name}: {ex.Message}" }
                        });
                    }

                    core.PostWebMessageAsJson(reply);
                };

                core.NavigationCompleted += async (_, e) =>
                {
                    if (!e.IsSuccess)
                    {
                        Console.Error.WriteLine($"[FAIL] 页面加载失败 {e.WebErrorStatus}");
                        exitCode = 3;
                        form.Close();
                        return;
                    }

                    Console.WriteLine($"[OK] 界面已加载  root={dataRoot}");

                    // 无人值守三种：首屏冒烟 / 走完一次安装 / 走完一次启动。
                    string? smoke = Environment.GetEnvironmentVariable("ML_SHELL_SMOKE");

                    if (smoke == "install")
                    {
                        exitCode = await SmokePluginRefusalAsync(shell, form);
                    }
                    else if (smoke == "update")
                    {
                        exitCode = await SmokeUpdateAsync(shell, form);
                    }
                    else if (smoke == "apply")
                    {
                        exitCode = await SmokeApplyUpdateAsync(shell, form);
                    }
                    else if (smoke == "launch")
                    {
                        exitCode = await SmokeLaunchAsync(shell, smokeEvents, form);
                    }
                    else if (smoke == "launch-ui")
                    {
                        // 走界面这条路：点按钮 → 过桥 → 宿主开日志窗口。
                        // 直接调 shell 的 launch 模式测不到日志窗口，因为那是界面路径才会发生的事。
                        exitCode = await SmokeLaunchUiAsync(shell, core, smokeEvents, form, () => logWindows.Values.Any(w => w.IsOpen));
                    }
                    else if (smoke == "1")
                    {
                        await RunSmokeAsync(core, form);
                    }
                };

                // 外链一律交给系统浏览器，不在应用内开新窗口。
                core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    OpenExternal(e.Uri);
                };

                core.Navigate($"{address}/index.html?t={token}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] WebView2 初始化 {ex.GetType().Name}: {ex.Message}");
                exitCode = 4;
                form.Close();
            }
        };

        form.FormClosed += (_, _) => { };
        Application.Run(form);


        return exitCode;
    }

    /// <summary>
    /// 无人值守冒烟：等清单渲染进 DOM，读结论。
    /// 只验证「宿主起来 + 页面能拿到数据」，不点安装（那要下 600 MB）。
    /// </summary>
    private static async Task RunSmokeAsync(CoreWebView2 core, Form form)
    {
        string? verdict = null;

        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500);

            string raw = await core.ExecuteScriptAsync(
                // 版本页已删：改看「新建实例」的版本下拉框有没有被填上。
                // 判据还是同一个——宿主起来了，而且页面真的拿到了 version.list 的数据。
                "JSON.stringify({t:document.title, n:document.querySelectorAll('#new-version option').length,"
                + " f:document.getElementById('first-run')?.hidden === false, err:window.__mlError||null})");

            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw) ?? "{}");
            var root = doc.RootElement;

            if (root.TryGetProperty("err", out var err) && err.ValueKind == JsonValueKind.String)
            {
                verdict = $"FAIL:{err.GetString()}";
                break;
            }

            if (root.TryGetProperty("n", out var n) && n.GetInt32() > 0)
            {
                verdict = $"OK:{n.GetInt32()}";
                break;
            }

            // 没指定游戏目录时首屏的正确表现是**弹出首次启动面板**，而不是「0 个版本」。
            // 这是一种合法终态，所以算通过——但写成 OK:first-run，读日志的人能一眼看出
            // 这次跑的是哪条路径。
            if (root.TryGetProperty("f", out var firstRun) && firstRun.ValueKind == JsonValueKind.True)
            {
                verdict = "OK:first-run";
                break;
            }
        }

        Console.WriteLine($"[{(verdict?.StartsWith("OK", StringComparison.Ordinal) == true ? "OK" : "FAIL")}]  冒烟  {verdict ?? "超时：没等到版本下拉框"}");

        // 临时诊断：ML_UI_PROBE=1 时打印布局尺寸，查「莫名出现滚动条」这类只看截图猜不出的问题。
        if (Environment.GetEnvironmentVariable("ML_UI_PROBE") == "1")
        {
            string probe = await core.ExecuteScriptAsync(
                "JSON.stringify((() => { const de=document.documentElement, b=document.body;"
                + " const els=[...document.querySelectorAll('*')].map(e=>({t:e.tagName+'.'+(e.className||''),s:e.scrollWidth,c:e.clientWidth,sh:e.scrollHeight,ch:e.clientHeight}))"
                + " .filter(x=>x.s>x.c+1||x.sh>x.ch+1).slice(0,10);"
                + " return {win:[innerWidth,innerHeight], de:[de.scrollWidth,de.clientWidth,de.scrollHeight,de.clientHeight],"
                + " body:[b.scrollWidth,b.clientWidth,b.scrollHeight,b.clientHeight], over:els}; })())");
            Console.WriteLine("[probe] " + probe);
        }

        form.Close();
    }

    /// <summary>
    /// 无人值守验证更新检查的接线：调 <c>update.check</c>，只要回一条结构化结论就算通过。
    /// 没配 <c>ML_UPDATE_URL</c> 时正确行为是说「未配置更新源」，而不是抛异常、也不是假装检查过。
    /// 这条用例的价值在于把「更新检查在没服务器的情况下也不能崩」钉死。
    /// </summary>
    private static async Task<int> SmokeUpdateAsync(ShellHost shell, Form form)
    {
        string raw = await shell.HandleAsync("""{"seq":1,"cmd":"update.check"}""");
        Console.WriteLine($"[smoke] update.check → {raw}");

        bool ok = raw.Contains("\"ok\":true", StringComparison.Ordinal)
                  && raw.Contains("\"currentVersion\"", StringComparison.Ordinal);

        Console.WriteLine(ok ? "[OK] 更新检查已接线。" : "[FAIL] 更新检查没回结构化结论。");
        form.Close();
        return ok ? 0 : 8;
    }
    /// <summary>
    /// 无人值守验证「内核诚实拒绝」：调 <c>install.start</c>，它应当回一条结构化拒绝
    /// （<c>ok:false</c> + <c>pluginRequired:"installer"</c>），而不是假装开始下载、也不是抛异常。
    ///
    /// <para>
    /// 这条用例存在的意义：安装能力已经移出内核（见 <c>src/ML.Features</c>），内核唯一的正确行为
    /// 就是**说清自己不做**。若哪天有人为了让界面「有反应」在核心里补一个假实现，界面上会出现
    /// 一条永远停在 0% 的进度条，而这条冒烟会立刻变红。
    /// </para>
    /// </summary>
    /// <summary>
    /// 无人值守验证更新真的能装上：调 <c>update.apply</c>，Velopack 会另起更新器、等本进程退出后
    /// 替换 <c>current\</c> 并重启。重启后的新进程会再跑一遍这条冒烟，那时已是最新版本，直接通过。
    /// 所以这条例子的正确终态是「跑两次、版本变了、第二次说已是最新」。
    /// </summary>
    private static async Task<int> SmokeApplyUpdateAsync(ShellHost shell, Form form)
    {
        string raw = await shell.HandleAsync("""{"seq":1,"cmd":"update.apply"}""");
        Console.WriteLine($"[smoke] update.apply → {raw}");

        // 只要回执 ok:true 就算接线对了。真正的「装上了」由外部比对 sq.version 判断：
        // 本进程随后会被更新器换掉，在这里断言文件状态是自欺欺人。
        bool ok = raw.Contains("\"ok\":true", StringComparison.Ordinal);

        Console.WriteLine(ok ? "[OK] 更新流程已接线（本进程随后由更新器接管）。" : "[FAIL] 更新流程没回结构化结论。");
        await Task.Delay(500);
        form.Close();
        return ok ? 0 : 9;
    }
    private static async Task<int> SmokePluginRefusalAsync(ShellHost shell, Form form)
    {
        string versionId = Environment.GetEnvironmentVariable("ML_SHELL_SMOKE_VERSION") ?? "1.20.1";

        var raw = await shell.HandleAsync(JsonSerializer.Serialize(new
        {
            seq = 1,
            cmd = "install.start",
            data = new { versionId }
        }));

        Console.WriteLine($"[smoke] install.start → {raw}");

        bool refused = raw.Contains("\"ok\":false", StringComparison.Ordinal)
                       && raw.Contains("\"pluginRequired\":\"installer\"", StringComparison.Ordinal);

        Console.WriteLine(refused
            ? "[OK] 内核如实拒绝：安装属于插件。"
            : "[FAIL] 内核没有如实拒绝安装请求。");

        form.Close();
        return refused ? 0 : 6;
    }

    /// <summary>无人值守走完一次启动：建实例 → 启动 → 等到进程结束或超时归因。</summary>
    /// <summary>
    /// 走界面启动一次：建实例 → 切到实例页 → 点「启动」→ 等日志窗口开起来 → 等 launch.done。
    /// </summary>
    /// <remarks>
    /// 存在的理由：日志窗口只在界面路径上创建（宿主收到 <c>launch.start</c> 请求时才开），
    /// 而 <c>SmokeLaunchAsync</c> 直接调 <c>ShellHost</c>，绕过桥，测不到这条链路。
    /// 这也是唯一能自动验证「日志窗口会不会开、开了有没有内容」的办法。
    /// </remarks>
    private static async Task<int> SmokeLaunchUiAsync(
        ShellHost shell,
        CoreWebView2 core,
        SmokeEventSink events,
        Form form,
        Func<bool> logWindowOpen)
    {
        string versionId = Environment.GetEnvironmentVariable("ML_SHELL_SMOKE_VERSION") ?? "1.20.1";
        string name = $"smoke-{DateTime.Now:HHmmss}";

        string created = await shell.HandleAsync(JsonSerializer.Serialize(new { seq = 2, cmd = "instance.create", data = new { name, versionId } }));
        Console.WriteLine($"[smoke] instance.create → {created}");

        // 切页 + 点按钮都通过页面自己做，宿主不替它构造 DOM。
        await core.ExecuteScriptAsync("document.querySelector('[data-nav=\"instances\"]').click()");
        await Task.Delay(800);

        Console.WriteLine($"[smoke] 实例行数 → {await core.ExecuteScriptAsync("document.querySelectorAll('#instances .row').length")}");

        string clicked = await core.ExecuteScriptAsync($$"""
            (() => {
              const rows = [...document.querySelectorAll('#instances .row')];
              const row = rows.find(r => r.querySelector('.id')?.textContent === '{{name}}');
              if (!row) { return 'notfound'; }
              const button = row.querySelector('button.act.primary');
              if (!button) { return 'nobutton'; }
              button.click();
              return 'clicked';
            })()
            """);

        Console.WriteLine($"[smoke] 点击启动 → {clicked}");

        bool opened = false;
        for (int i = 0; i < 50 && !opened; i++)
        {
            opened = logWindowOpen();
            if (!opened)
            {
                await Task.Delay(200);
            }
        }

        Console.WriteLine($"[smoke] 日志窗口 {(opened ? "已打开" : "没有打开")}");

        var done = await events.WaitAsync("launch.done", TimeSpan.FromSeconds(90));
        Console.WriteLine($"[smoke] launch.done → {(done is null ? "超时" : done)}");

        bool ok = opened
            && clicked.Contains("clicked", StringComparison.Ordinal)
            && done is not null
            && (done.Contains("\"diagnosis\":\"TimedOut\"", StringComparison.Ordinal)
                || done.Contains("\"sawStartup\":true", StringComparison.Ordinal)
                || done.Contains("\"diagnosis\":\"CleanExit\"", StringComparison.Ordinal));

        form.Close();
        return ok ? 0 : 10;
    }

    private static async Task<int> SmokeLaunchAsync(ShellHost shell, SmokeEventSink events, Form form)
    {
        string versionId = Environment.GetEnvironmentVariable("ML_SHELL_SMOKE_VERSION") ?? "1.20.1";
        string name = $"smoke-{DateTime.Now:HHmmss}";

        Console.WriteLine($"[smoke] instance.create → {await shell.HandleAsync(JsonSerializer.Serialize(new { seq = 2, cmd = "instance.create", data = new { name, versionId } }))}");

        var launched = await shell.HandleAsync(JsonSerializer.Serialize(new
        {
            seq = 3,
            cmd = "launch.start",
            data = new { name }
        }));

        Console.WriteLine($"[smoke] launch.start → {launched}");

        var done = await events.WaitAsync("launch.done", TimeSpan.FromMinutes(5));
        Console.WriteLine($"[smoke] launch.done → {(done is null ? "超时" : done)}");

        bool ok = done is not null
            && (done.Contains("\"diagnosis\":\"TimedOut\"", StringComparison.Ordinal)
                || done.Contains("\"sawStartup\":true", StringComparison.Ordinal)
                || done.Contains("\"diagnosis\":\"CleanExit\"", StringComparison.Ordinal));

        form.Close();
        return ok ? 0 : 7;
    }

    /// <summary>
    /// 桥事件的旁听者。界面的消息照常发，这里额外截一份给无人值守模式等信号。
    /// </summary>
    private sealed class SmokeEventSink
    {
        private readonly List<(string Name, string Json)> _seen = [];
        private readonly object _gate = new();

        public void Observe(string json)
        {
            string? name = null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("event", out var e))
                {
                    name = e.GetString();
                }
            }
            catch (JsonException)
            {
                return;
            }

            if (name is null)
            {
                return;
            }

            lock (_gate)
            {
                _seen.Add((name, json));
            }
        }

        /// <summary>等某个事件出现；已经出现过就立刻返回。</summary>
        public async Task<string?> WaitAsync(string name, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    for (int i = 0; i < _seen.Count; i++)
                    {
                        if (_seen[i].Name == name)
                        {
                            return _seen[i].Json;
                        }
                    }
                }

                await Task.Delay(200).ConfigureAwait(true);
            }

            return null;
        }
    }

    // ── 自检模式（既有路径，勿动） ───────────────────────────────────

    private static int RunSkeleton(WebApplication app, string token, string address, string repoRoot)
    {
        _ = app;

        var plugin = NodePluginHost.TryStart(Path.Combine(repoRoot, "build", "skeleton", "node-plugin.mjs"));

        Console.WriteLine(plugin.Started
            ? $"[OK]  Node 子进程插件握手  pid={plugin.Pid} node={plugin.NodeVersion}"
            : $"[FAIL]  Node 子进程插件握手  {plugin.Error}");

        int exitCode = 99;
        using var form = CreateForm("ML Walking Skeleton", 960, 660);
        var webView = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(webView);

        form.Shown += async (_, _) =>
        {
            try
            {
                var core = await InitializeWebViewAsync(webView);

                core.WebMessageReceived += (_, e) =>
                {
                    var reply = HandleSkeletonMessage(e.WebMessageAsJson, plugin, repoRoot);
                    core.PostWebMessageAsJson(reply);
                };

                core.NavigationCompleted += async (_, e) =>
                {
                    if (!e.IsSuccess)
                    {
                        Console.Error.WriteLine($"[FAIL] 页面加载 {e.WebErrorStatus}");
                        exitCode = 3;
                        form.Close();
                        return;
                    }

                    Console.WriteLine("[OK]  页面已加载");

                    await Task.Delay(2500);
                    string title = await core.ExecuteScriptAsync("document.title");
                    title = title?.Trim('"') ?? "";
                    exitCode = title == "SKELETON_PASS" ? 0 : 5;
                    Console.WriteLine($"[{(exitCode == 0 ? "OK" : "FAIL")}]  页面结论  {title}");
                    form.Close();
                };

                core.Navigate($"{address}/selfcheck.html?t={token}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] WebView2 初始化 {ex.GetType().Name}: {ex.Message}");
                exitCode = 4;
                form.Close();
            }
        };

        Application.Run(form);

        plugin.Dispose();
        return exitCode;
    }

    private static void Post(CoreWebView2 core, string json)
    {
        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            // 窗口正在关的时候投递会失败，属正常收尾，不影响结论。
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// 从请求 JSON 里读命令名和实例名。解析失败一律返回 null 而不是抛：
    /// 这条路上出错的后果只是「日志窗口没开」，不该让整个命令跟着失败。
    /// </summary>
    private static (string? Command, string? InstanceName) ReadRequest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? command = root.TryGetProperty("cmd", out JsonElement cmd) ? cmd.GetString() : null;

            string? name = null;
            if (root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("name", out JsonElement nameElement))
            {
                name = nameElement.GetString();
            }

            return (command, name);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string HandleSkeletonMessage(string json, NodePluginHost plugin, string repoRoot)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
        long seq = root.TryGetProperty("seq", out var s) ? s.GetInt64() : 0;

        if (cmd == "metrics")
        {
            string dir = Path.Combine(repoRoot, "artifacts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "skeleton-metrics.json"),
                root.GetProperty("data").GetRawText(),
                Encoding.UTF8);
            return JsonSerializer.Serialize(new { seq, payload = new { ok = true } });
        }

        object payload = cmd switch
        {
            "ping" => new { ok = true, pong = seq, hostPid = Environment.ProcessId },
            // 匿名类型成员写成 `plugin.Pid` 会按原生大小写生成 `Pid`，JS 侧读 `p.pid` 得到 undefined。
            "plugin" => plugin.Started
                ? (object)new
                {
                    ok = true,
                    pid = plugin.Pid,
                    node = plugin.NodeVersion,
                    handshakeMicros = plugin.RoundtripMicros
                }
                : new { ok = false, error = plugin.Error },
            _ => new { ok = false, error = $"unknown cmd '{cmd}'" }
        };

        return JsonSerializer.Serialize(new { seq, payload });
    }

    // ── 共用 ────────────────────────────────────────────────────────

    /// <summary>启动器窗口的默认大小。1280×800 是「正常应用窗口」的尺寸，4K 屏上也不会显得铺满。</summary>
    private const int DefaultWindowWidth = 1280;

    /// <summary>启动器窗口的默认高度。</summary>
    private const int DefaultWindowHeight = 800;

    /// <summary>
    /// 弹系统的文件夹选择框。WebView2 那边点「添加目录」时调到这里。
    /// </summary>
    /// <remarks>
    /// 必须回到 UI 线程：这个调用是从网页消息里进来的，可能在别的线程上。
    /// 取消时返回 null，由调用方翻译成 canceled，不算错误。
    /// </remarks>
    /// <summary>重启自己：起一个新进程，然后退出当前进程。崩溃恢复里的"重启"走这里。</summary>
    private static void RestartSelf()
    {
        string? exe = Environment.ProcessPath;

        if (exe is not null)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
        }

        Application.Exit();
    }

    private static string? PickFolder(Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated)
        {
            return null;
        }

        if (form.InvokeRequired)
        {
            return (string?)form.Invoke(() => PickFolder(form));
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 Minecraft 目录（一般是 .minecraft）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        return dialog.ShowDialog(form) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    private static Form CreateForm(string title, int width = DefaultWindowWidth, int height = DefaultWindowHeight)
    {
        // 不按屏幕比例缩放：按比例算在高分屏上会得到一个铺满整屏的窗口，看着像全屏程序。
        // 固定一个正常尺寸，只在屏幕确实装不下时才缩。
        Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, width, height);

        return new ChromeForm
        {
            Text = title,
            Icon = AppIcon.Value,
            Width = Math.Min(width, work.Width),
            Height = Math.Min(height, work.Height),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(880, 560)
        };
    }

    private static async Task<CoreWebView2> InitializeWebViewAsync(WebView2 webView)
    {
        // 正常情况下用 %LOCALAPPDATA%\ML\WebView2。ML_WEBVIEW_DIR 是调试/自动化用的逃生口：
        // 用户自己开着一个启动器时，第二个实例会共用同一个 WebView2 浏览器进程，
        // 调试端口挂不上；指到别的目录才是独立进程。
        string? overridden = Environment.GetEnvironmentVariable("ML_WEBVIEW_DIR");

        string userData = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ML",
                "WebView2")
            : Path.GetFullPath(overridden);

        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await webView.EnsureCoreWebView2Async(env);

        var core = webView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        return core;
    }

    private static void OpenExternal(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    /// <summary>
    /// 启动器数据目录。优先级：
    /// ① <c>ML_DATA_DIR</c> 环境变量（调试/测试时指到临时目录）；
    /// ② **Velopack 安装形态** → <c>%LOCALAPPDATA%\ML\data</c>；
    /// ③ exe 同目录可写 → <c>&lt;exe 目录&gt;\data</c>（<b>便携版</b>，拷贝走整个文件夹就带着配置）；
    /// ④ 否则 <c>%LOCALAPPDATA%\ML\data</c>（装在 Program Files 这类不可写位置时的兜底）。
    /// </summary>
    /// <remarks>
    /// ② 这条是必须的，不是保守：Velopack 的安装布局是 <c>&lt;root&gt;\current\ML.exe</c>，
    /// 每次更新会把 <c>current</c> 整个换掉。数据目录若跟着 exe 走，用户升级一次就丢光设置与实例。
    /// 判据用布局自身（父目录有 <c>Update.exe</c>），不去读 Velopack 内部状态：
    /// 这个程序在没有更新源时也能运行，不该因为拿不到 locator 就换错目录。
    /// </remarks>
    /// <remarks>
    /// 默认便携而不是默认 LocalAppData，是因为分发形态里第一个就是「单 exe 便携版」：
    /// 用户把启动器和 <c>data\</c> 一起放进 U 盘或游戏目录，插件、配置、实例都跟着走。
    /// 写在 C 盘会让「换台机器继续玩」这件事直接失败。
    /// <para>
    /// 判定「可写」用的是真去创建目录并落一个探针文件，而不是查权限位：
    /// 权限位在受控文件夹访问（Windows Defender 的 Controlled folder access）下会骗人。
    /// </para>
    /// </remarks>
    private static string ResolveDataRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable("ML_DATA_DIR");

        if (!string.IsNullOrWhiteSpace(overridden))
        {
            Directory.CreateDirectory(overridden);
            return Path.GetFullPath(overridden);
        }

        if (LooksLikeInstalledLayout())
        {
            string installed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ML",
                "data");

            Directory.CreateDirectory(installed);
            return Path.GetFullPath(installed);
        }

        string portable = Path.Combine(AppContext.BaseDirectory, "data");

        if (TryPrepareDirectory(portable))
        {
            return Path.GetFullPath(portable);
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ML",
            "data");

        Directory.CreateDirectory(fallback);
        return Path.GetFullPath(fallback);
    }

    /// <summary>
    /// 是否是 Velopack 装出来的布局：exe 在 <c>&lt;root&gt;\current\</c>，同级父目录有 <c>Update.exe</c>。
    /// </summary>
    private static bool LooksLikeInstalledLayout()
    {
        DirectoryInfo? current = Directory.GetParent(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return current is not null && File.Exists(Path.Combine(current.FullName, "Update.exe"));
    }
    /// <summary>能建目录、能落探针文件，才算这个目录可用。</summary>
    private static bool TryPrepareDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            string probe = Path.Combine(directory, ".ml-writable");
            File.WriteAllText(probe, "1");
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ml.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>
/// 最小进程外插件宿主：JSON-RPC over stdio 一行一帧。
/// 只做握手 + 一次调用的往返计时，用来证明协议与延迟基线。
/// </summary>
internal sealed class NodePluginHost : IDisposable
{
    private readonly Process? _process;
    private readonly StreamWriter? _stdin;
    private readonly StreamReader? _stdout;

    private NodePluginHost(Process process, StreamWriter stdin, StreamReader stdout)
    {
        _process = process;
        _stdin = stdin;
        _stdout = stdout;
    }

    public bool Started { get; private init; }

    public int Pid { get; private init; }

    public string NodeVersion { get; private init; } = "";

    public long RoundtripMicros { get; private init; }

    public string? Error { get; private init; }

    public static NodePluginHost TryStart(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("node")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add(script);

            var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start 返回 null");

            var stdin = p.StandardInput;
            var stdout = p.StandardOutput;
            stdin.AutoFlush = true;

            var sw = Stopwatch.StartNew();
            stdin.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"handshake","params":{"hostPid":0}}""");
            string? line = stdout.ReadLine();
            sw.Stop();

            if (string.IsNullOrWhiteSpace(line))
            {
                p.Kill(entireProcessTree: true);
                return new NodePluginHost(p, stdin, stdout) { Started = false, Error = "插件没有返回握手响应" };
            }

            using var doc = JsonDocument.Parse(line);
            var result = doc.RootElement.GetProperty("result");

            return new NodePluginHost(p, stdin, stdout)
            {
                Started = result.TryGetProperty("alive", out var a) && a.GetBoolean(),
                Pid = result.GetProperty("pid").GetInt32(),
                NodeVersion = result.GetProperty("node").GetString() ?? "",
                RoundtripMicros = sw.Elapsed.Microseconds,
                Error = null
            };
        }
        catch (Exception ex)
        {
            return new NodePluginHost(null!, null!, null!)
            {
                Started = false,
                Error = $"{ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _stdin?.Close();
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }

            _process?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ML.Plugin.Runtime;

public sealed record InstallResult(bool Ok, string? PluginId, string? Message)
{
    public static InstallResult Success(string id) => new(true, id, null);

    public static InstallResult Fail(string message) => new(false, null, message);
}

/// <summary>
/// 把插件装进 <c>plugins/&lt;id&gt;/</c>：本地目录直接复制，GitHub 仓库下载 zip 再解压。
/// </summary>
/// <remarks>
/// <para>安装只做三件事：找到 manifest、校验它、复制到目标目录。加载是 <see cref="PluginManager"/> 的事。</para>
/// <para>
/// 目标目录已存在时**不覆盖**。覆盖安装等于替用户删东西，而他可能已经改过里面的配置；
/// 想换版本应该先停用再删，这个决定交给用户。
/// </para>
/// </remarks>
public static class PluginInstaller
{
    private static readonly HttpClient Http = CreateClient();

    public static InstallResult InstallFromLocalPath(string sourcePath, string pluginsRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return InstallResult.Fail("没有给路径。");
        }

        string full = Path.GetFullPath(sourcePath.Trim().Trim('"'));

        if (File.Exists(full) && full.EndsWith(".mlp", StringComparison.OrdinalIgnoreCase))
        {
            return InstallFromZip(full, pluginsRoot);
        }

        if (!Directory.Exists(full))
        {
            return InstallResult.Fail($"目录不存在：{full}");
        }

        // 允许指到"包了一层"的目录：有些插件的发布包是 Release/插件名/ 这种结构
        string? pluginRoot = FindPluginRoot(full);
        if (pluginRoot is null)
        {
            return InstallResult.Fail("这个目录里没有 manifest.json（也不在其直接子目录里）。");
        }

        return CopyInto(pluginRoot, pluginsRoot);
    }

    public static InstallResult InstallFromZip(string zipPath, string pluginsRoot)
    {
        string temp = Path.Combine(Path.GetTempPath(), "ml-plugin-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(temp);
            ZipFile.ExtractToDirectory(zipPath, temp, overwriteFiles: true);

            string? pluginRoot = FindPluginRoot(temp);
            if (pluginRoot is null)
            {
                return InstallResult.Fail("压缩包里没有 manifest.json。");
            }

            return CopyInto(pluginRoot, pluginsRoot);
        }
        catch (InvalidDataException)
        {
            return InstallResult.Fail("这不是一个有效的 zip 压缩包。");
        }
        catch (IOException ex)
        {
            return InstallResult.Fail($"解压失败：{ex.Message}");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// 从 GitHub 仓库装。走 <c>api.github.com</c>：它比 web 域名稳，而且能顺带问到默认分支。
    /// </summary>
    public static async Task<InstallResult> InstallFromGitHubAsync(
        string repositoryUrl,
        string pluginsRoot,
        CancellationToken cancellationToken)
    {
        if (!TryParseGitHubUrl(repositoryUrl, out string owner, out string repository, out string? branch))
        {
            return InstallResult.Fail("看不懂这个 GitHub 地址，形如 https://github.com/owner/repo");
        }

        string temp = Path.Combine(Path.GetTempPath(), "ml-plugin-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(temp);

            if (branch is null)
            {
                branch = await GetDefaultBranchAsync(owner, repository, cancellationToken).ConfigureAwait(false);
                if (branch is null)
                {
                    return InstallResult.Fail($"找不到仓库 {owner}/{repository}（私有仓库或者名字写错了）。");
                }
            }

            string zipPath = Path.Combine(temp, "plugin.zip");

            using (HttpResponseMessage response = await Http
                       .GetAsync($"https://api.github.com/repos/{owner}/{repository}/zipball/{Uri.EscapeDataString(branch)}",
                           HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return InstallResult.Fail(
                        $"下载失败（HTTP {(int)response.StatusCode}）。私有仓库、分支不存在、或者被网络拦住了。");
                }

                await using Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream local = File.Create(zipPath);
                await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
            }

            string extract = Path.Combine(temp, "x");
            Directory.CreateDirectory(extract);
            ZipFile.ExtractToDirectory(zipPath, extract);

            string? pluginRoot = FindPluginRoot(extract);
            if (pluginRoot is null)
            {
                return InstallResult.Fail("仓库里没有 manifest.json（可以在子目录里，但不能没有）。");
            }

            return CopyInto(pluginRoot, pluginsRoot);
        }
        catch (HttpRequestException ex)
        {
            return InstallResult.Fail($"连不上 GitHub：{ex.Message}");
        }
        catch (TaskCanceledException)
        {
            return InstallResult.Fail("下载超时。网络不稳时可以先浏览器下载 zip，再用「本地目录」装。");
        }
        catch (InvalidDataException)
        {
            return InstallResult.Fail("下载回来的不是有效的 zip。");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static InstallResult CopyInto(string pluginRoot, string pluginsRoot)
    {
        string manifestPath = Path.Combine(pluginRoot, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            return InstallResult.Fail("找不到 manifest.json。");
        }

        ManifestParseResult parsed;
        try
        {
            parsed = PluginManifestParser.Parse(File.ReadAllText(manifestPath), Path.GetFileName(pluginRoot));
        }
        catch (IOException ex)
        {
            return InstallResult.Fail($"读不了清单：{ex.Message}");
        }

        if (parsed.Manifest is null)
        {
            return InstallResult.Fail("清单有问题：" + string.Join("；", parsed.Errors));
        }

        string id = parsed.Manifest.Id;
        string target = Path.Combine(pluginsRoot, id);

        if (Directory.Exists(target))
        {
            return InstallResult.Fail($"plugins/{id} 已经存在。想换版本请先停用删除，再装。");
        }

        Directory.CreateDirectory(pluginsRoot);
        CopyDirectory(pluginRoot, target);

        return InstallResult.Success(id);
    }

    /// <summary>在目录（及一层子目录）里找 manifest.json，返回它所在的目录。</summary>
    private static string? FindPluginRoot(string directory)
    {
        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            return directory;
        }

        // 压缩包通常会多包一层 <repo>-<sha>/ 或 <插件名>/
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name is "__MACOSX" || name.StartsWith('.') || name is "obj" or "bin")
            {
                continue;
            }

            if (File.Exists(Path.Combine(child, "manifest.json")))
            {
                return child;
            }
        }

        return null;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (string directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: false);
        }
    }

    private static async Task<string?> GetDefaultBranchAsync(string owner, string repository, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http
                .GetAsync($"https://api.github.com/repos/{owner}/{repository}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return document.RootElement.TryGetProperty("default_branch", out JsonElement branch)
                   && branch.ValueKind == JsonValueKind.String
                ? branch.GetString()
                : "main";
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal static bool TryParseGitHubUrl(string url, out string owner, out string repository, out string? branch)
    {
        owner = string.Empty;
        repository = string.Empty;
        branch = null;

        string text = url.Trim();

        if (text.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://github.com/" + text["git@github.com:".Length..];
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        owner = parts[0];
        repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4]
            : parts[1];

        // 支持 https://github.com/o/r/tree/分支
        if (parts.Length >= 4 && parts[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
        {
            branch = Uri.UnescapeDataString(parts[3]);
        }

        return owner.Length > 0 && repository.Length > 0;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ML-Launcher", "0.1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响安装结果。
        }
    }
}

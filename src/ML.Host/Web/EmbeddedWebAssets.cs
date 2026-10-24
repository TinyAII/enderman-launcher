using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace ML.Host.Web;

/// <summary>
/// 页面文件（<c>src/ML.Web</c>）以嵌入资源的形式编进宿主程序集。
/// </summary>
/// <remarks>
/// 早先是 <c>PhysicalFileProvider</c> 直接从源码目录服文件的——只能在仓库里跑，
/// 打包成单个 exe 之后那个目录不存在，双击就是白屏。
/// 现在默认从程序集里读，另外留一个 <c>ML_WEB_DIR</c> 覆盖点：
/// 改 HTML/CSS 不想每次等一次发布时，指到源码目录即可，重启生效。
/// </remarks>
internal static class EmbeddedWebAssets
{
    private const string ResourcePrefix = "web/";

    /// <summary>资源名（相对 <c>src/ML.Web</c>）→ 内容。</summary>
    public static IReadOnlyDictionary<string, byte[]> Load()
    {
        Assembly assembly = typeof(EmbeddedWebAssets).Assembly;
        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            // MSBuild 的 RecursiveDir 在 Windows 上给的是反斜杠，这里统一成 URL 形态。
            string relative = name[ResourcePrefix.Length..].Replace('\\', '/');
            assets[relative] = buffer.ToArray();
        }

        return assets;
    }

    /// <summary>把请求路径映射到嵌入资源；<c>/</c> 落到 <c>index.html</c>。</summary>
    public static RequestDelegate Middleware(IReadOnlyDictionary<string, byte[]> assets) => async context =>
    {
        string path = context.Request.Path.Value ?? "/";
        if (path.Length <= 1)
        {
            path = "/index.html";
        }

        string key = path.TrimStart('/');
        if (!assets.TryGetValue(key, out byte[]? content))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = ContentTypeFor(key);
        // 本地回环、每次启动都可能是新版本，缓存没有收益只有「改了没生效」的困惑。
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.Body.WriteAsync(content, context.RequestAborted);
    };

    public static string ContentTypeFor(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return "application/octet-stream";
        }

        return path[(dot + 1)..].ToLowerInvariant() switch
        {
            "html" => "text/html; charset=utf-8",
            "css" => "text/css; charset=utf-8",
            "js" or "mjs" => "text/javascript; charset=utf-8",
            "json" or "webmanifest" => "application/json; charset=utf-8",
            "svg" => "image/svg+xml",
            "png" => "image/png",
            "ico" => "image/x-icon",
            "woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }
}

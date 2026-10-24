
namespace ML.Plugin.Runtime;

/// <summary>
/// 插件审计日志：谁、什么时候、想干什么、准没准。
/// </summary>
/// <remarks>
/// 写成一行一条的纯文本，不引日志库：用户想看懂的时候能直接 <c>type</c> 出来，
/// 出问题也能直接贴给作者。<b>写失败绝不能影响启动</b>，所以每次写入都吞 IO 异常。
/// </remarks>
public sealed class PluginAuditLog
{
    // 不带 BOM：日志要能用 type / grep 直接看，前面多三个字节很碍事。
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _path;
    private readonly object _gate = new();

    public PluginAuditLog(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public void Write(string pluginId, string action, string result, string? detail = null)
    {
        if (_path is null)
        {
            return;
        }

        string line = string.Join('\t',
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            pluginId,
            action,
            result,
            detail ?? string.Empty);

        lock (_gate)
        {
            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(_path, line + Environment.NewLine, Utf8NoBom);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 审计日志写不进去不是启动失败的理由。
            }
        }
    }
}

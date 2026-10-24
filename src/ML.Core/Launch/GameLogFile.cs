using System.Text;

namespace ML.Core.Launch;

/// <summary>
/// 一次游戏启动对应的日志文件。游戏输出本来只在界面上滚，关掉窗口就没了；
/// 起不来的时候（Java 版本不对、库缺失）MC 自己的 latest.log 一个字都不会写，
/// 那时候只有启动器手里的 stderr 有答案。
/// </summary>
/// <remarks>
/// <para>
/// 文件句柄一直开到进程退出：这样「清理日志」天然删不掉正在写的那个文件（Windows 会拒绝），
/// 不用另做标记位——但要靠 <see cref="IsActive"/> 主动跳过，别指望异常。
/// </para>
/// <para>
/// 写入不做异步队列：调用方已经在读输出流的回调里，那本身就在后台线程上；
/// 再加一层队列只会让"崩了以后最后几行丢了"的概率变大。
/// </para>
/// </remarks>
public sealed class GameLogFile : IDisposable
{
    /// <summary>Windows 文件名里不允许的字符。</summary>
    private static readonly char[] Invalid = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly object _gate = new();
    private StreamWriter? _writer;

    private GameLogFile(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    /// <summary>日志文件绝对路径。</summary>
    public string Path { get; }

    /// <summary>是否还在写（进程没退）。清理时用它决定跳不跳过。</summary>
    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _writer is not null;
            }
        }
    }

    /// <summary>
    /// 在 <paramref name="logsRoot"/> 下开一个 <c>实例名-yyyyMMdd-HHmmss.log</c>。
    /// </summary>
    public static GameLogFile Create(string logsRoot, string instanceName, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        Directory.CreateDirectory(logsRoot);

        string stem = Sanitize(instanceName);
        string stamp = startedAt.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string path = System.IO.Path.Combine(logsRoot, $"{stem}-{stamp}.log");

        // 同一秒内重复启动同一个实例：加序号，别覆盖上一个
        for (int index = 2; File.Exists(path) && index < 100; index++)
        {
            path = System.IO.Path.Combine(logsRoot, $"{stem}-{stamp}-{index}.log");
        }

        var writer = new StreamWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            Utf8WithBom,
            bufferSize: 16 * 1024)
        {
            AutoFlush = true
        };

        return new GameLogFile(path, writer);
    }

    /// <summary>写一行普通输出。<paramref name="source"/> 标记来源（out / err / launcher）。</summary>
    public void Write(string source, string text)
    {
        string stamp = DateTimeOffset.Now.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

        WriteRaw($"[{stamp}] [{source}] {text}");
    }

    /// <summary>写一段原样内容（诊断头信息用，多行）。</summary>
    public void WriteBlock(string text)
    {
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            WriteRaw(line);
        }
    }

    private void WriteRaw(string line)
    {
        lock (_gate)
        {
            _writer?.WriteLine(line);
        }
    }

    /// <summary>实例名里可能有 <c>:</c> 之类，直接拿来当文件名在 Windows 上会失败。</summary>
    public static string Sanitize(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return "instance";
        }

        var builder = new StringBuilder(trimmed.Length);

        foreach (char c in trimmed)
        {
            builder.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        string result = builder.ToString().Trim(' ', '.');

        if (result.Length == 0)
        {
            return "instance";
        }

        return result.Length > 40 ? result[..40] : result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
    }
}

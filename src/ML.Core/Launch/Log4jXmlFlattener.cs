using System.Text;
using System.Text.RegularExpressions;

namespace ML.Core.Launch;

/// <summary>
/// 把 Minecraft 控制台里的 log4j **XML** 日志压成人看的一行，例如
/// <c>[Render thread/INFO] Created: 256x256x4 minecraft:textures/atlas/chest.png-atlas</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要这个：官方 <c>logging.client.file</c> 指定的 log4j2 配置里，**控制台**用的是 XML
/// 布局，**文件**用（<c>logs/latest.log</c>）用的才是人读的文本布局。所以游戏窗口打出来的标准输出
/// 天生是 XML，直接塞进界面日志页就是一片 <c>&lt;log4j:Event&gt;</c>。真机抓到的样子：
/// </para>
/// <code>
///   &lt;log4j:Event logger="fuu" timestamp="1792559843620" level="INFO" thread="Render thread"&gt;
///     &lt;log4j:Message&gt;&lt;![CDATA[Created: 256x256x4 minecraft:textures/atlas/chest.png-atlas]]&gt;&lt;/log4j:Message&gt;
///   &lt;/log4j:Event&gt;
/// </code>
/// <para>
/// 一个事件横跨多行、且可能被 stdio 缓冲切成任意片段，所以这里按事件缓冲，
/// 见到 <c>&lt;/log4j:Event&gt;</c> 才吐一行。不是 XML 的输入原样透传——这样自定义了文本布局
/// 的版本（以及 <c>--version</c> 之类的普通输出）不需要另写一条分支。
/// </para>
/// <para>
/// 刻意不引 XML 解析器：stdout 是在流式到达、事件随时可能被腰斩，走 <see cref="System.Xml"/>
/// 只会拿到"文档不完整"的异常，还得自己拼完整再解析一遍。这里要的信息只有三个字段。
/// </para>
/// </remarks>
public sealed partial class Log4jXmlFlattener
{
    private const int MaxBufferedLines = 256;

    private readonly List<string> _buffer = [];

    /// <summary>喂进一行原始输出，返回这一行之后能确定下来的可读行（可能为空、可能多行）。</summary>
    public IReadOnlyList<string> Append(string? rawLine)
    {
        if (rawLine is null)
        {
            return [];
        }

        string line = rawLine.TrimEnd('\r');

        if (_buffer.Count == 0 && !line.Contains("<log4j:Event", StringComparison.Ordinal))
        {
            // 普通输出：直接透传。纯空行丢掉——日志页里除了滚屏没有别的作用。
            return string.IsNullOrWhiteSpace(line) ? [] : [line];
        }

        _buffer.Add(line);

        if (line.Contains("</log4j:Event>", StringComparison.Ordinal))
        {
            string block = string.Join('\n', _buffer);
            _buffer.Clear();
            return [Flatten(block)];
        }

        if (_buffer.Count >= MaxBufferedLines)
        {
            // 只开了头却永远不闭合：宁可把原文吐出去，也不能把日志吞了。
            string block = string.Join('\n', _buffer);
            _buffer.Clear();
            return [block];
        }

        return [];
    }

    /// <summary>进程结束时把残留缓冲吐干净——被腰斩的最后一个事件也要让人看见。</summary>
    public IReadOnlyList<string> Flush()
    {
        if (_buffer.Count == 0)
        {
            return [];
        }

        string block = string.Join('\n', _buffer);
        _buffer.Clear();
        return [Flatten(block)];
    }

    private static string Flatten(string block)
    {
        string? level = Group(LevelPattern(), block);
        string? thread = Group(ThreadPattern(), block);
        string? message = Group(CdataMessagePattern(), block) ?? Group(PlainMessagePattern(), block);

        if (message is null)
        {
            // 结构不认识（比如 <log4j:Throwable> 里塞了堆栈而没有 Message）就原样返回，别自作聪明。
            return block;
        }

        string prefix = (thread, level) switch
        {
            (not null, not null) => $"[{thread}/{level}] ",
            (null, not null) => $"[{level}] ",
            (not null, null) => $"[{thread}] ",
            _ => string.Empty
        };

        var text = new StringBuilder(prefix.Length + message.Length);
        text.Append(prefix).Append(message);

        string? throwable = Group(ThrowablePattern(), block);

        if (throwable is not null)
        {
            text.Append('\n').Append(throwable.TrimEnd('\n'));
        }

        return text.ToString();
    }

    private static string? Group(Regex pattern, string block)
    {
        Match match = pattern.Match(block);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // 这些只用于人工排查的输出，不值得为每个字段写状态机；正则跑在每事件一行的量级上。
    [GeneratedRegex("level=\"([^\"]*)\"")]
    private static partial Regex LevelPattern();

    [GeneratedRegex("thread=\"([^\"]*)\"")]
    private static partial Regex ThreadPattern();

    [GeneratedRegex(@"<log4j:Message>\s*<!\[CDATA\[(.*?)\]\]>\s*</log4j:Message>", RegexOptions.Singleline)]
    private static partial Regex CdataMessagePattern();

    [GeneratedRegex(@"<log4j:Message>\s*(.*?)\s*</log4j:Message>", RegexOptions.Singleline)]
    private static partial Regex PlainMessagePattern();

    [GeneratedRegex(@"<log4j:Throwable>\s*<!\[CDATA\[(.*?)\]\]>\s*</log4j:Throwable>", RegexOptions.Singleline)]
    private static partial Regex ThrowablePattern();
}

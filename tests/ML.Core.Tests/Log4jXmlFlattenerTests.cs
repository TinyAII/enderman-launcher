using ML.Core.Launch;

namespace ML.Core.Tests;

public class Log4jXmlFlattenerTests
{
    /// <summary>
    /// 真机抓到的四行（1.20.1 渲染图集时 Console appender 的 XML 布局，含前导空格）。
    /// 这几行不能凭想象改写——布局细节全在这个样本里。
    /// </summary>
    private static readonly string[] RealEvent =
    [
        "  <log4j:Event logger=\"fuu\" timestamp=\"1792559843620\" level=\"INFO\" thread=\"Render thread\">",
        "    <log4j:Message><![CDATA[Created: 256x256x4 minecraft:textures/atlas/chest.png-atlas]]></log4j:Message>",
        "  </log4j:Event>",
    ];

    private static List<string> Feed(params string[] lines)
    {
        var flattener = new Log4jXmlFlattener();
        var output = new List<string>();

        foreach (string line in lines)
        {
            output.AddRange(flattener.Append(line));
        }

        output.AddRange(flattener.Flush());
        return output;
    }

    [Fact]
    public void 真实事件被压成一行()
    {
        var output = Feed(RealEvent);

        Assert.Equal(["[Render thread/INFO] Created: 256x256x4 minecraft:textures/atlas/chest.png-atlas"], output);
    }

    [Fact]
    public void 非_XML_输出原样透传()
    {
        var output = Feed("OpenAL initialized on device OpenAL Soft", "Exception in thread \"main\" java.lang.Error");

        Assert.Equal(["OpenAL initialized on device OpenAL Soft", "Exception in thread \"main\" java.lang.Error"], output);
    }

    [Fact]
    public void 事件未闭合前不吐半根()
    {
        var flattener = new Log4jXmlFlattener();

        Assert.Empty(flattener.Append(RealEvent[0]));
        Assert.Empty(flattener.Append(RealEvent[1]));

        // 关闭标签到了才吐，且只吐一行。
        Assert.Single(flattener.Append(RealEvent[2]));
    }

    [Fact]
    public void 多个事件按到达顺序各吐一行()
    {
        var output = Feed([.. RealEvent, .. RealEvent]);

        Assert.Equal(2, output.Count);
        Assert.All(output, line => Assert.StartsWith("[Render thread/INFO] Created: 256x256x4", line, StringComparison.Ordinal));
    }

    [Fact]
    public void 缺少_thread_属性时只带级别()
    {
        var output = Feed(
            "<log4j:Event logger=\"x\" timestamp=\"1\" level=\"ERROR\">",
            "<log4j:Message><![CDATA[boom]]></log4j:Message>",
            "</log4j:Event>");

        Assert.Equal(["[ERROR] boom"], output);
    }

    [Fact]
    public void 没有_CDATA_的消息同样能取出来()
    {
        var output = Feed(
            "<log4j:Event logger=\"x\" timestamp=\"1\" level=\"WARN\" thread=\"main\">",
            "<log4j:Message>Shader sampler missing</log4j:Message>",
            "</log4j:Event>");

        Assert.Equal(["[main/WARN] Shader sampler missing"], output);
    }

    [Fact]
    public void 堆栈跟着主体一起出来()
    {
        var output = Feed(
            "<log4j:Event logger=\"x\" timestamp=\"1\" level=\"ERROR\" thread=\"Render thread\">",
            "<log4j:Message><![CDATA[Crash]]></log4j:Message>",
            "<log4j:Throwable><![CDATA[java.lang.NullPointerException: null",
            "    at net.minecraft.Foo.bar(Foo.java:1)]]></log4j:Throwable>",
            "</log4j:Event>");

        string line = Assert.Single(output);
        Assert.StartsWith("[Render thread/ERROR] Crash", line, StringComparison.Ordinal);
        Assert.Contains("NullPointerException", line, StringComparison.Ordinal);
        Assert.Contains("Foo.java:1", line, StringComparison.Ordinal);
    }

    [Fact]
    public void 事件被腰斩时_Flush_把原文吐出来()
    {
        var flattener = new Log4jXmlFlattener();

        flattener.Append(RealEvent[0]);
        flattener.Append(RealEvent[1]);

        // 进程被杀，永远等不到 </log4j:Event>。
        string line = Assert.Single(flattener.Flush());
        Assert.Contains("minecraft:textures/atlas/chest.png-atlas", line, StringComparison.Ordinal);

        // 吐完缓冲就空了，不会重复。
        Assert.Empty(flattener.Flush());
    }

    [Fact]
    public void 空输入不产生任何行()
    {
        Assert.Empty(Feed(string.Empty, string.Empty));
    }
}

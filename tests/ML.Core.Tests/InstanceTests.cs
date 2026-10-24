using ML.Core.Instances;

namespace ML.Core.Tests;

public sealed class InstanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ml-instance-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论。
        }
    }

    private InstanceStore Store => new(_root);

    [Fact]
    public void 新建实例会建目录与游戏目录并落盘()
    {
        var instance = Store.Create("生存", "1.20.1");

        Assert.Equal("生存", instance.Metadata.Name);
        Assert.Equal("1.20.1", instance.Metadata.VersionId);
        Assert.True(Directory.Exists(instance.Directory));
        Assert.True(Directory.Exists(instance.GameDirectory));
        Assert.True(File.Exists(instance.MetadataPath));
        Assert.Equal("2048", instance.Metadata.MaxMemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 落盘的字段名是小驼峰且能读回来()
    {
        Store.Create("dev", "1.20.1");

        string json = File.ReadAllText(Path.Combine(_root, "instances", "dev", "instance.json"));

        Assert.Contains("\"versionId\"", json, StringComparison.Ordinal);
        Assert.Contains("\"playerName\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"VersionId\"", json, StringComparison.Ordinal);

        var reopened = Store.Find("dev");

        Assert.NotNull(reopened);
        Assert.Equal("1.20.1", reopened.Metadata.VersionId);
    }

    [Fact]
    public void 扫描能列出多个实例并按最近游玩排序()
    {
        var older = Store.Create("旧的", "1.20.1");
        var newer = Store.Create("新的", "1.20.1");

        older.MarkPlayed(DateTimeOffset.Now.AddDays(-3));
        older.Save();
        newer.MarkPlayed(DateTimeOffset.Now);
        newer.Save();

        var names = Store.List().Select(i => i.Metadata.Name).ToArray();

        Assert.Equal(["新的", "旧的"], names);
    }

    [Fact]
    public void 没玩过的实例按创建时间参与同一套排序()
    {
        var played = Store.Create("玩过", "1.20.1");
        played.MarkPlayed(DateTimeOffset.Now.AddDays(-30));
        played.Save();

        Store.Create("刚建的", "1.20.1");

        // 排序键是「最近一次启动，没启动过就用创建时间」——刚建的实例排最前，
        // 因为它对用户来说是最新动作。一个刚建好却排在 30 天前玩过的实例后面，才是错的。
        Assert.Equal("刚建的", Store.List()[0].Metadata.Name);
    }

    [Fact]
    public void 找不到的实例返回_null()
    {
        Assert.Null(Store.Find("不存在"));
    }

    [Fact]
    public void 重名创建被拒绝()
    {
        Store.Create("生存", "1.20.1");

        var ex = Assert.Throws<ArgumentException>(() => Store.Create("生存", "1.18.2"));

        Assert.Contains("已存在", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 重名判断不区分大小写()
    {
        Store.Create("Survival", "1.20.1");

        // Windows 文件系统不区分大小写，两个只差大小写的目录其实是同一个。
        Assert.Throws<ArgumentException>(() => Store.Create("survival", "1.20.1"));
    }

    [Fact]
    public void 坏掉的实例进警告而不是被当成不存在()
    {
        Store.Create("好的", "1.20.1");

        string brokenDirectory = Path.Combine(_root, "instances", "坏的");
        Directory.CreateDirectory(brokenDirectory);
        File.WriteAllText(Path.Combine(brokenDirectory, "instance.json"), "{ 这不是 json");

        var scan = Store.Scan();

        Assert.Single(scan.Instances);
        Assert.Single(scan.Warnings);
        Assert.Contains("坏的", scan.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void 没有_instance_json_的目录被忽略且不报警()
    {
        Directory.CreateDirectory(Path.Combine(_root, "instances", "随手放的"));

        var scan = Store.Scan();

        Assert.Empty(scan.Instances);
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public void 删除实例是挪进回收目录而不是真删()
    {
        var instance = Store.Create("要删的", "1.20.1");
        File.WriteAllText(Path.Combine(instance.GameDirectory, "level.dat"), "存档");

        string? trash = Store.Delete("要删的");

        Assert.NotNull(trash);
        Assert.False(Directory.Exists(instance.Directory));
        Assert.True(Directory.Exists(trash));
        Assert.True(File.Exists(Path.Combine(trash, "game", "level.dat")));
        Assert.Empty(Store.List());
    }

    [Fact]
    public void 回收目录不会被当成实例列出来()
    {
        Store.Create("要删的", "1.20.1");
        Store.Delete("要删的");

        Assert.Empty(Store.List());
    }

    [Fact]
    public void 删除不存在的实例返回_null()
    {
        Assert.Null(Store.Delete("根本没有"));
    }

    [Fact]
    public void 保存后设置能读回()
    {
        var instance = Store.Create("配置", "1.20.1");

        instance.Metadata.MaxMemoryMb = 8192;
        instance.Metadata.WindowWidth = 1280;
        instance.Metadata.WindowHeight = 720;
        instance.Metadata.PlayerName = "Steve";
        instance.Metadata.JavaHome = @"C:\Java\jdk-21";
        instance.Metadata.ExtraJvmArguments.Add("-XX:+UseG1GC");
        instance.Save();

        var reopened = Instance.Open(instance.Directory);

        Assert.Equal(8192, reopened.Metadata.MaxMemoryMb);
        Assert.Equal(1280, reopened.Metadata.WindowWidth);
        Assert.Equal(720, reopened.Metadata.WindowHeight);
        Assert.Equal("Steve", reopened.Metadata.PlayerName);
        Assert.Equal(@"C:\Java\jdk-21", reopened.Metadata.JavaHome);
        Assert.Equal(["-XX:+UseG1GC"], reopened.Metadata.ExtraJvmArguments);
    }

    [Fact]
    public void 保存不会留下_tmp_文件()
    {
        var instance = Store.Create("原子", "1.20.1");
        instance.Save();

        Assert.False(File.Exists(instance.MetadataPath + ".tmp"));
    }

    [Fact]
    public void 克隆是深拷贝()
    {
        var instance = Store.Create("拷贝", "1.20.1");
        instance.Metadata.ExtraGameArguments.Add("--demo");

        var clone = instance.Metadata.Clone();
        clone.ExtraGameArguments.Add("--width");

        Assert.Single(instance.Metadata.ExtraGameArguments);
        Assert.Equal(2, clone.ExtraGameArguments.Count);
    }

    [Fact]
    public void 打开非实例目录会报错()
    {
        Directory.CreateDirectory(_root);

        var ex = Assert.Throws<InvalidDataException>(() => Instance.Open(_root));

        Assert.Contains("instance.json", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("生存")]
    [InlineData("Survival 1.20")]
    [InlineData("a")]
    [InlineData("整合包-v2")]
    public void 合法实例名被接受(string name)
    {
        Assert.Null(InstanceStore.ValidateName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" 前后有空格 ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("有/斜杠")]
    [InlineData("有\\反斜杠")]
    [InlineData("有:冒号")]
    [InlineData("有*星号")]
    [InlineData("结尾有点.")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    public void 非法实例名被拒绝并给出原因(string? name)
    {
        string? reason = InstanceStore.ValidateName(name);

        Assert.NotNull(reason);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void 超长实例名被拒绝()
    {
        Assert.NotNull(InstanceStore.ValidateName(new string('a', 49)));
        Assert.Null(InstanceStore.ValidateName(new string('a', 48)));
    }

    [Fact]
    public void 实例名带扩展名但主体是保留名也被拒绝()
    {
        Assert.NotNull(InstanceStore.ValidateName("CON.txt"));
    }

    [Fact]
    public void 残缺的instance_json_字段落回默认值而不是零()
    {
        // game/ 里有存档，所以「能读但字段残缺」的 instance.json 必须尽量救回来。
        // 若按 0 直接往下走，窗口会变成 320×320（或更糟：--width 0 让游戏直接崩）。
        var instance = Store.Create("残缺", "1.20.1");
        File.WriteAllText(instance.MetadataPath, """
            { "name": "残缺", "versionId": "1.20.1" }
            """);

        var reloaded = Instance.Open(instance.Directory);

        Assert.Equal(2048, reloaded.Metadata.MaxMemoryMb);
        Assert.Equal(854, reloaded.Metadata.WindowWidth);
        Assert.Equal(480, reloaded.Metadata.WindowHeight);
        Assert.Equal("Player", reloaded.Metadata.PlayerName);
    }
}

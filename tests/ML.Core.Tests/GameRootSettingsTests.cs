using ML.Core.Configuration;

namespace ML.Core.Tests;

/// <summary>
/// 目录列表与老配置迁移。这块全是纯逻辑，能直接测，不用起界面。
/// </summary>
public sealed class GameRootSettingsTests
{
    [Fact]
    public void 老配置的单个目录会迁成列表第一条()
    {
        var settings = new LauncherSettings { GameRoot = @"C:\Users\me\AppData\Roaming\.minecraft" }.Normalize();

        Assert.Single(settings.GameRoots);
        Assert.Equal(@".minecraft", settings.GameRoots[0].Note);
        Assert.Equal(settings.GameRoots[0].Id, settings.ActiveGameRootId);
        Assert.True(settings.HasGameRoot);
        Assert.Equal(settings.GameRoots[0].Path, settings.ActiveGameRoot?.Path);
    }

    [Fact]
    public void 已经有列表时不再迁移老字段()
    {
        var roots = new[] { new GameRootEntry { Path = @"D:\mc1", Note = "一号", Id = "r-aaaa" } };
        var settings = new LauncherSettings
        {
            GameRoot = @"C:\old\.minecraft",
            GameRoots = roots,
            ActiveGameRootId = "r-aaaa"
        }.Normalize();

        Assert.Single(settings.GameRoots);
        Assert.Equal("r-aaaa", settings.GameRoots[0].Id);
    }

    [Fact]
    public void 选中的目录被删掉时回落到第一条()
    {
        var settings = new LauncherSettings
        {
            GameRoots =
            [
                new GameRootEntry { Path = @"D:\mc1", Id = "r-aaaa" },
                new GameRootEntry { Path = @"D:\mc2", Id = "r-bbbb" }
            ],
            ActiveGameRootId = "r-不存在的id"
        }.Normalize();

        Assert.Equal("r-aaaa", settings.GameRoots[0].Id);
        Assert.Equal("r-aaaa", settings.ActiveGameRoot?.Id);
    }

    [Fact]
    public void 同一个路径重复登记只留一条()
    {
        var settings = new LauncherSettings
        {
            GameRoots =
            [
                new GameRootEntry { Path = @"D:\mc", Id = "" },
                new GameRootEntry { Path = @"d:\MC\", Id = "" }
            ]
        }.Normalize();

        // 大小写和结尾分隔符不同，但 Windows 上是同一个目录
        Assert.Single(settings.GameRoots);
    }

    [Fact]
    public void 备注为空时回落成文件夹名()
    {
        var entry = new GameRootEntry { Path = @"E:\浏览器\.minecraft", Note = "   " }.Normalize();

        Assert.Equal(".minecraft", entry.Note);
    }

    [Fact]
    public void 备注只改名字不改路径()
    {
        var entry = new GameRootEntry { Path = @"E:\浏览器\.minecraft", Note = "官方启动器目录" }.Normalize();

        Assert.Equal("官方启动器目录", entry.Note);
        Assert.Equal(@"E:\浏览器\.minecraft", entry.Path);
    }
}

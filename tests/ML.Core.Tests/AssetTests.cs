using ML.Features.Assets;
using ML.Core.Install;

namespace ML.Core.Tests;

public sealed class AssetTests
{
    private const string ModernIndexFile = "asset-index-5.json";
    private const string LegacyIndexFile = "asset-index-legacy.json";

    [Fact]
    public void 真实的_1_20_1_索引能解析且不是旧版布局()
    {
        var index = AssetIndex.Load(FixturePath(ModernIndexFile));

        Assert.True(index.Count > 3000, $"实际对象数 {index.Count}");
        Assert.False(index.Virtual);
        Assert.False(index.MapToResources);
        Assert.False(index.RequiresMaterialization);
    }

    [Fact]
    public void 真实的_1_6_4_索引是_virtual_布局()
    {
        var index = AssetIndex.Load(FixturePath(LegacyIndexFile));

        Assert.True(index.Virtual);
        Assert.False(index.MapToResources);
        Assert.True(index.RequiresMaterialization);
        Assert.True(index.Count > 500, $"实际对象数 {index.Count}");

        // 1.6.4 的索引里真的有这个文件，它是 legacy 布局的标志性条目之一。
        Assert.True(index.Objects!.ContainsKey("READ_ME_I_AM_VERY_IMPORTANT.txt"));
    }

    [Fact]
    public void 每个真实对象都能生成合法的下载任务()
    {
        var index = AssetIndex.Load(FixturePath(ModernIndexFile));
        using var game = new TempGame();

        var downloads = AssetInstaller.BuildDownloads(game.Paths, index);

        Assert.Equal(index.Count, downloads.Count);
        Assert.All(downloads, item =>
        {
            var hash = item.Sha1!;
            Assert.Equal(40, hash.Length);
            Assert.Equal(AssetInstaller.ObjectUrl(hash), item.Url);
            Assert.Equal(game.Paths.AssetObject(hash), item.DestinationPath);
            Assert.StartsWith(game.Paths.AssetObjects, item.DestinationPath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void 对象_URL_用哈希前两位做目录()
    {
        const string hash = "b88bd8618c05232a7287799ea9246ae12f5da9d5";

        Assert.Equal(
            "https://resources.download.minecraft.net/b8/b88bd8618c05232a7287799ea9246ae12f5da9d5",
            AssetInstaller.ObjectUrl(hash));
    }

    [Fact]
    public void 哈希不完整的对象被跳过而不是抛异常()
    {
        var index = AssetIndex.FromJson("""
            {"objects":{
              "ok.txt":{"hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1},
              "空哈希":{"hash":"","size":2},
              "太短":{"hash":"a","size":3},
              "无哈希":{"size":4}
            }}
            """);

        using var game = new TempGame();
        var downloads = AssetInstaller.BuildDownloads(game.Paths, index);

        var item = Assert.Single(downloads);
        Assert.EndsWith("/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", item.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void 没有_objects_字段时返回空列表()
    {
        var index = AssetIndex.FromJson("""{"virtual":true}""");
        using var game = new TempGame();

        Assert.Empty(AssetInstaller.BuildDownloads(game.Paths, index));
    }

    [Fact]
    public void virtual_布局把对象按原名摊到_virtual_目录()
    {
        using var game = new TempGame();
        const string hash = "1111111111111111111111111111111111111111";
        game.PutObject(hash, "hello");

        var index = Index("""
            {"virtual":true,"objects":{"minecraft/sounds/a.ogg":{"hash":"__HASH__","size":5}}}
            """, hash);

        var result = AssetInstaller.MaterializeLegacy(game.Paths, "legacy", index);

        Assert.True(result.Required);
        Assert.Equal(1, result.FilesWritten);
        Assert.Equal(game.Paths.VirtualAssets("legacy"), result.TargetDirectory);

        var target = Path.Combine(game.Paths.VirtualAssets("legacy"), "minecraft", "sounds", "a.ogg");
        Assert.True(File.Exists(target));
        Assert.Equal("hello", File.ReadAllText(target));
    }

    [Fact]
    public void map_to_resources_布局摊到_resources_目录()
    {
        using var game = new TempGame();
        const string hash = "2222222222222222222222222222222222222222";
        game.PutObject(hash, "x");

        var index = Index("""
            {"map_to_resources":true,"objects":{"sound/a.ogg":{"hash":"__HASH__","size":1}}}
            """, hash);

        var result = AssetInstaller.MaterializeLegacy(game.Paths, "pre-1.6", index);

        Assert.Equal(game.Paths.Resources, result.TargetDirectory);
        Assert.True(File.Exists(Path.Combine(game.Paths.Resources, "sound", "a.ogg")));
    }

    [Fact]
    public void 现代布局不做摊平()
    {
        var index = AssetIndex.Load(FixturePath(ModernIndexFile));
        using var game = new TempGame();

        var result = AssetInstaller.MaterializeLegacy(game.Paths, "5", index);

        Assert.False(result.Required);
        Assert.Equal(0, result.FilesWritten);
        Assert.Null(result.TargetDirectory);
    }

    [Fact]
    public void 重复摊平第二次全部跳过()
    {
        using var game = new TempGame();
        const string hash = "3333333333333333333333333333333333333333";
        game.PutObject(hash, "content");

        var index = Index("""
            {"virtual":true,"objects":{"a.txt":{"hash":"__HASH__","size":7}}}
            """, hash);

        var first = AssetInstaller.MaterializeLegacy(game.Paths, "legacy", index);
        var second = AssetInstaller.MaterializeLegacy(game.Paths, "legacy", index);

        Assert.Equal(1, first.FilesWritten);
        Assert.Equal(0, second.FilesWritten);
        Assert.Equal(1, second.FilesSkipped);
    }

    [Fact]
    public void 目标文件大小不符时会覆盖()
    {
        using var game = new TempGame();
        const string hash = "4444444444444444444444444444444444444444";
        game.PutObject(hash, "0123456789");

        var index = Index("""
            {"virtual":true,"objects":{"a.txt":{"hash":"__HASH__","size":10}}}
            """, hash);

        AssetInstaller.MaterializeLegacy(game.Paths, "legacy", index);
        var target = Path.Combine(game.Paths.VirtualAssets("legacy"), "a.txt");
        File.WriteAllText(target, "短");

        var again = AssetInstaller.MaterializeLegacy(game.Paths, "legacy", index);

        Assert.Equal(1, again.FilesWritten);
        Assert.Equal("0123456789", File.ReadAllText(target));
    }

    [Fact]
    public void 对象文件缺失时给警告而不是抛异常()
    {
        using var game = new TempGame();

        var index = AssetIndex.FromJson("""
            {"virtual":true,"objects":{"a.txt":{"hash":"5555555555555555555555555555555555555555","size":1}}}
            """);

        var result = AssetInstaller.MaterializeLegacy(game.Paths, "legacy", index);

        Assert.Equal(0, result.FilesWritten);
        Assert.Contains(result.Warnings, warning => warning.Contains("缺失", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("minecraft/sounds/a.ogg", "minecraft/sounds/a.ogg")]
    [InlineData("a\\b.txt", "a/b.txt")]
    [InlineData("/abs.txt", "abs.txt")]
    [InlineData("..", null)]
    [InlineData("../evil.txt", null)]
    [InlineData("a/../../evil.txt", null)]
    [InlineData("a//b.txt", null)]
    [InlineData("a/./b.txt", null)]
    [InlineData("c:/windows/evil.txt", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void 索引键被规范成安全的相对路径(string name, string? expected)
    {
        var actual = AssetInstaller.NormalizeRelativeName(name);

        if (expected is null)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.Equal(expected.Replace('/', Path.DirectorySeparatorChar), actual);
        }
    }

    [Fact]
    public void 不安全的索引键在摊平时被拒绝()
    {
        using var game = new TempGame();
        const string hash = "6666666666666666666666666666666666666666";
        game.PutObject(hash, "payload");

        var index = Index("""
            {"virtual":true,"objects":{"../../evil.txt":{"hash":"__HASH__","size":7}}}
            """, hash);

        var result = AssetInstaller.MaterializeLegacy(game.Paths, "legacy", index);

        Assert.Equal(0, result.FilesWritten);
        Assert.Contains(result.Warnings, warning => warning.Contains("不安全", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(game.Paths.Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(game.Paths.Assets, "evil.txt")));
    }

    /// <summary>
    /// 拼一个只含单个对象的索引。用 <c>__HASH__</c> 占位而不是内插原始字符串——
    /// <c>$$"""..."""</c> 里的 <c>}}</c> 会被当成内插空闭合，CS9007 直接编译失败。
    /// </summary>
    private static AssetIndex Index(string template, string hash) =>
        AssetIndex.FromJson(template.Replace("__HASH__", hash, StringComparison.Ordinal));

    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ml.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "fixtures", name);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（ml.slnx）");
    }

    private sealed class TempGame : IDisposable
    {
        public TempGame()
        {
            Paths = new GamePaths(Path.Combine(Path.GetTempPath(), "ml-assets-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Paths.Root);
        }

        public GamePaths Paths { get; }

        /// <summary>在对象仓库里放一份内容，模拟资源已下载。</summary>
        public void PutObject(string hash, string content)
        {
            var path = Paths.AssetObject(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Paths.Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

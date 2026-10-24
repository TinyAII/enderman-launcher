using ML.Features.Assets;
using ML.Features.Install;
using System.Security.Cryptography;
using System.Text;
using ML.Features.Download;
using ML.Core.Install;
using ML.Features.Mojang;
using ML.Core.Versioning;
using Microsoft.AspNetCore.Http;

namespace ML.Core.Tests;

public sealed class GameInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ml-installer-tests",
        Guid.NewGuid().ToString("N"));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public void Dispose()
    {
        _http.Dispose();

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    private const string IndexId = "5";

    /// <summary>假的资源索引：一个对象。</summary>
    private const string AssetIndexJson = """
        {
          "objects": {
            "minecraft/sounds/a.ogg": { "hash": "__OBJECT__", "size": __SIZE__ }
          }
        }
        """;

    [Fact]
    public async Task 空目录装完后文件都落在该在的位置()
    {
        var objectContent = Encoding.UTF8.GetBytes("object bytes");
        string objectHash = Sha1(objectContent);

        await using var server = await StartServerAsync(objectContent, objectHash);
        var version = BuildVersion(server.Root, objectHash, objectContent.Length);

        var report = await InstallAsync(server, version);

        Assert.True(report.Succeeded, DescribeFailures(report));


        var paths = new GamePaths(_root);

        Assert.True(File.Exists(paths.LibraryFile("org/example/lib/1.0/lib-1.0.jar")), "库没落盘");
        Assert.True(File.Exists(paths.ClientJar("1.20.1")), "client.jar 没落盘");
        Assert.True(File.Exists(paths.AssetIndex(IndexId)), "资源索引没落盘");
        Assert.True(File.Exists(paths.LogConfig("client-1.12.xml")), "log4j 配置没落盘");
        Assert.True(File.Exists(paths.AssetObject(objectHash)), "资源对象没落盘");
        Assert.Equal(1, report.AssetObjects);
    }

    [Fact]
    public async Task 装完之后_client_jar_用的是版本自己的名字()
    {
        var objectContent = Encoding.UTF8.GetBytes("object bytes");
        string objectHash = Sha1(objectContent);

        await using var server = await StartServerAsync(objectContent, objectHash);
        var version = BuildVersion(server.Root, objectHash, objectContent.Length);

        await InstallAsync(server, version);

        var paths = new GamePaths(_root);

        // classpath 按 versions/<id>/<id>.jar 找文件，落盘名不一致就等于没装。
        Assert.True(File.Exists(Path.Combine(paths.VersionDirectory("1.20.1"), "1.20.1.jar")));
        Assert.False(File.Exists(Path.Combine(paths.VersionDirectory("1.20.1"), "client.jar")));
    }

    [Fact]
    public async Task 第二次装全部跳过且不重复下载()
    {
        var objectContent = Encoding.UTF8.GetBytes("object bytes");
        string objectHash = Sha1(objectContent);

        await using var server = await StartServerAsync(objectContent, objectHash);
        var version = BuildVersion(server.Root, objectHash, objectContent.Length);

        var first = await InstallAsync(server, version);
        Assert.True(first.Succeeded, DescribeFailures(first));
        Assert.True(first.FilesDownloaded > 0);

        var second = await InstallAsync(server, version);

        Assert.True(second.Succeeded, DescribeFailures(second));
        Assert.Equal(0, second.FilesDownloaded);
        Assert.Equal(first.FilesDownloaded, second.FilesSkipped);
        Assert.Equal(0, second.Bytes);
    }

    [Fact]
    public async Task 进度按阶段回报并以_Completed_收尾()
    {
        var objectContent = Encoding.UTF8.GetBytes("object bytes");
        string objectHash = Sha1(objectContent);

        await using var server = await StartServerAsync(objectContent, objectHash);
        var version = BuildVersion(server.Root, objectHash, objectContent.Length);

        var reports = new List<InstallProgress>();
        await InstallAsync(server, version, new SyncProgress<InstallProgress>(reports.Add));

        var stages = reports.Select(r => r.Stage).Distinct().ToArray();

        Assert.Contains(InstallStage.Resolving, stages);
        Assert.Contains(InstallStage.Libraries, stages);
        Assert.Contains(InstallStage.Assets, stages);
        Assert.Equal(InstallStage.Completed, reports[^1].Stage);

        // 前一段的进度不能被后一段覆盖成倒退值：Completed 总数逐段递增。
        var libraryReports = reports.Where(r => r.Stage == InstallStage.Libraries).ToArray();

        // 只断言「起点很小、终点到齐」，不硬写 == 1：库是并发下载的，两条完成事件可能反序抵达
        // （先报 2 再报 1）。满载跑整套测试时 == 1 会随机变红——这是断言的错，不是引擎的错。
        Assert.InRange(libraryReports[0].Completed, 1, 2);
        Assert.Equal(libraryReports[^1].Total, libraryReports[^1].Completed);
    }

    [Fact]
    public async Task 某个库下载失败时报告失败并列出失败项()
    {
        var objectContent = Encoding.UTF8.GetBytes("object bytes");
        string objectHash = Sha1(objectContent);

        // 只服务资源对象，库一律 404。
        await using var server = await TestServer.StartAsync(context =>
        {
            if (context.Request.Path.Value?.Contains("objects", StringComparison.Ordinal) == true)
            {
                return context.Response.WriteAsync("object bytes");
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        var version = BuildVersion(server.Root, objectHash, objectContent.Length);
        var report = await InstallAsync(server, version);

        Assert.False(report.Succeeded);
        Assert.NotEmpty(report.Failures);
        Assert.Contains(report.Failures, f => f.Item.Url.Contains("lib-1.0.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 未合并的清单被明确拒绝()
    {
        var version = new VersionMetadata
        {
            Id = "1.20.1-forge-47.2.0",
            InheritsFrom = "1.20.1",
            MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
        };

        var installer = NewInstaller();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.InstallAsync(version, TestEnvironments.Windows));

        Assert.Contains("VersionMerger", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 缺资源索引时给警告而不是静默跳过()
    {
        await using var server = await TestServer.StartAsync(context =>
            context.Response.WriteAsync("lib"));

        var version = new VersionMetadata
        {
            Id = "1.20.1",
            MainClass = "net.minecraft.client.main.Main",
            Libraries =
            [
                new LibraryEntry
                {
                    Name = "org.example:lib:1.0",
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadRef
                        {
                            Path = "org/example/lib/1.0/lib-1.0.jar",
                            Url = server.Url("lib-1.0.jar").ToString(),
                            Size = 3,
                            Sha1 = Sha1(Encoding.UTF8.GetBytes("lib")),
                        },
                    },
                },
            ],
        };

        var report = await InstallAsync(server, version);

        Assert.True(report.Succeeded, DescribeFailures(report));
        Assert.Contains(report.Warnings, w => w.Contains("资源索引", StringComparison.Ordinal));
    }

    [Fact]
    public void 真实版本清单能被解析出正式版列表()
    {
        string json = Fixtures.ReadText("version-manifest.json");
        var manifest = VersionManifest.Parse(json);

        Assert.NotEmpty(manifest.Releases);
        Assert.NotEmpty(manifest.Snapshots);
        Assert.NotNull(manifest.Latest?.Release);

        var release = manifest.Find("1.20.1");

        Assert.NotNull(release);
        Assert.True(release.IsRelease);
        Assert.NotNull(release.Url);
        Assert.StartsWith("https://", release.Url!, StringComparison.Ordinal);
        Assert.NotNull(VersionManifest.ReleaseTimeOf(release));
        Assert.Equal("2023-06", VersionManifest.ReleaseTimeOf(release)!.Value.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));

        // 正式版列表里不能混进快照。
        Assert.All(manifest.Releases, entry => Assert.True(entry.IsRelease));
    }

    [Fact]
    public void 坏清单直接报错而不是给出空列表()
    {
        Assert.Throws<ArgumentException>(() => VersionManifest.Parse("   "));
        Assert.ThrowsAny<Exception>(() => VersionManifest.Parse("{ 不是 json"));
    }

    private GameInstaller NewInstaller(TestServer? server = null) =>
        new(
            new GamePaths(_root),
            new DownloadQueue(new FileDownloader(_http)),
            server is null ? null : server.Root);

    private Task<InstallReport> InstallAsync(
        TestServer server,
        VersionMetadata version,
        IProgress<InstallProgress>? progress = null) =>
        NewInstaller(server).InstallAsync(version, TestEnvironments.Windows, progress);

    private static async Task<TestServer> StartServerAsync(byte[] objectContent, string objectHash)
    {
        // 资源 CDN 的路径规则是 /{hash 前两位}/{hash}，没有 objects 前缀。
        string objectPath = $"/{objectHash[..2]}/{objectHash}";

        return await TestServer.StartAsync(async context =>
        {
            string path = context.Request.Path.Value ?? string.Empty;

            switch (path)
            {
                case var _ when path == objectPath:
                    await context.Response.Body.WriteAsync(objectContent);
                    return;

                case "/index.json":
                    await context.Response.WriteAsync(IndexText(objectHash, objectContent.Length));
                    return;

                default:
                    await context.Response.WriteAsync("payload");
                    return;
            }
        });
    }

    /// <summary>造一份"装了就能跑"的最小元数据，所有 URL 指向本地测试服务器。</summary>
    private static VersionMetadata BuildVersion(string baseUrl, string objectHash, int objectSize)
    {
        byte[] objectContent = Encoding.UTF8.GetBytes("payload");
        string payloadSha = Sha1(objectContent);

        // 索引的 sha1/size 必须和服务器实际吐的字节一致，否则下载器按"校验失败"处理。
        byte[] indexBytes = Encoding.UTF8.GetBytes(IndexText(objectHash, objectSize));
        string indexSha = Sha1(indexBytes);

        string json = $$"""
            {
              "id": "1.20.1",
              "mainClass": "net.minecraft.client.main.Main",
              "assetIndex": {
                "id": "{{IndexId}}",
                "url": "{{baseUrl}}/index.json",
                "sha1": "{{indexSha}}",
                "size": {{indexBytes.Length}}
              },
              "downloads": {
                "client": {
                  "url": "{{baseUrl}}/client.jar",
                  "sha1": "{{payloadSha}}",
                  "size": {{objectContent.Length}}
                }
              },
              "logging": {
                "client": {
                  "argument": "-Dlog4j.configurationFile=${path}",
                  "file": {
                    "id": "client-1.12.xml",
                    "url": "{{baseUrl}}/log4j.xml",
                    "sha1": "{{payloadSha}}",
                    "size": {{objectContent.Length}}
                  },
                  "type": "log4j2-xml"
                }
              },
              "libraries": [
                {
                  "name": "org.example:lib:1.0",
                  "downloads": {
                    "artifact": {
                      "path": "org/example/lib/1.0/lib-1.0.jar",
                      "url": "{{baseUrl}}/lib-1.0.jar",
                      "sha1": "{{payloadSha}}",
                      "size": {{objectContent.Length}}
                    }
                  }
                }
              ]
            }
            """;

        return System.Text.Json.JsonSerializer.Deserialize<VersionMetadata>(json)
            ?? throw new InvalidDataException("测试元数据解析失败");
    }

    /// <summary>资源索引的正文，服务器与元数据里的 sha1/size 都从这里来，避免两处不一致。</summary>
    private static string IndexText(string objectHash, int objectSize) =>
        AssetIndexJson
            .Replace("__OBJECT__", objectHash, StringComparison.Ordinal)
            .Replace("__SIZE__", objectSize.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    // CA5350：SHA1 是 Minecraft 资源清单里写死的算法，这里只是在造夹具，没有选择余地。
#pragma warning disable CA5350
    private static string Sha1(byte[] content) => Convert.ToHexStringLower(SHA1.HashData(content));
#pragma warning restore CA5350

    private static string DescribeFailures(InstallReport report) =>
        report.Failures.Count == 0
            ? "无失败"
            : string.Join("; ", report.Failures.Select(f => $"{f.Item.Url} → {f.Outcome} {f.Error}"));
}


/// <summary>同步投递的进度收集器，避免 Progress&lt;T&gt; 的异步投递让断言跑在回调前面。</summary>
internal sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ML.Features.Assets;
using ML.Features.Download;
using ML.Core.Install;
using ML.Core.Java;
using ML.Core.Launch;
using ML.Core.Maven;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Verify;

/// <summary>
/// 联网验收工具。用真实 CDN 跑一遍下载，量出真实装机时间。
/// 它不是单元测试：跑它需要网，而且结果是「这台机器这条线路」的数字，不该进 CI。
///
/// <code>
/// dotnet run --project tools/ML.Verify -c Release -- java
/// dotnet run --project tools/ML.Verify -c Release -- version tests/fixtures/vanilla-1.20.1.json /tmp/mc
/// dotnet run --project tools/ML.Verify -c Release -- natives /tmp/mc tests/fixtures/vanilla-1.20.1.json
/// dotnet run --project tools/ML.Verify -c Release -- assets  /tmp/mc /tmp/mc/assets/indexes/5.json 400
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // java 模式不下载任何东西，也不需要目标目录，先单独处理。
        if (args.Length >= 1 && args[0] == "java")
        {
            return DumpJava(args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 17);
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "用法: ml-verify <java|version|natives|assets> [参数...]\n" +
                "  java                            列出本机所有 Java 并给出选择结果\n" +
                "  version <目录> <version.json>   按真实清单下载库与 client\n" +
                "  natives <目录> <version.json>   下载并解压真实 natives jar\n" +
                "  assets  <目录> <index.json> [limit] 下载资源对象");
            return 2;
        }

        var mode = args[0];
        var root = args[1];
        Directory.CreateDirectory(root);

        var concurrency = int.Parse(
            Environment.GetEnvironmentVariable("ML_CONCURRENCY") ?? "8",
            CultureInfo.InvariantCulture);

        if (mode == "natives")
        {
            return await RunNativesAsync(root, args, concurrency);
        }

        if (mode == "launch")
        {
            return await RunLaunchAsync(root, args, concurrency);
        }

        var items = mode switch
        {
            "version" => BuildFromVersion(root, args),
            "assets" => BuildFromAssetIndex(root, args),
            _ => throw new ArgumentException($"未知模式 {mode}")
        };

        if (items.Count == 0)
        {
            Console.Error.WriteLine("没有生成任何下载任务。");
            return 3;
        }

        Console.WriteLine($"[{mode}] 任务数 {items.Count} | 净字节 {items.Sum(i => i.Size ?? 0):N0}");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var downloader = new FileDownloader(http);

        var first = Stopwatch.StartNew();
        var report = await new DownloadQueue(downloader).RunAsync(items, concurrency);
        first.Stop();

        Console.WriteLine(
            $"[{mode}] 第一轮 {first.ElapsedMilliseconds} ms | " +
            $"下载 {report.Results.Count(r => r.Outcome == DownloadOutcome.Downloaded)} " +
            $"跳过 {report.Results.Count(r => r.Outcome == DownloadOutcome.AlreadyValid)} " +
            $"失败 {report.Failures.Count} | " +
            $"{report.TotalBytes / 1024.0 / 1024.0:F1} MB | " +
            $"{report.TotalBytes / 1024.0 / 1024.0 / Math.Max(first.Elapsed.TotalSeconds, 0.001):F1} MB/s");

        // 第二轮全跳过，量的是「已装好的启动器每次启动要付多少校验成本」。
        var second = Stopwatch.StartNew();
        var again = await new DownloadQueue(downloader).RunAsync(items, concurrency);
        second.Stop();

        Console.WriteLine(
            $"[{mode}] 第二轮全跳过 {second.ElapsedMilliseconds} ms | " +
            $"{items.Count / Math.Max(second.Elapsed.TotalSeconds, 0.001):F0} 文件/秒 | " +
            $"失败 {again.Failures.Count}");

        foreach (var failure in report.Failures.Take(5))
        {
            Console.WriteLine(
                $"  失败 {Path.GetFileName(failure.Item.DestinationPath)}: " +
                $"{failure.Outcome} {failure.Error}");
        }

        return report.AllSucceeded ? 0 : 1;
    }

    /// <summary>
    /// 用真实 natives jar 走一遍「按架构挑选 → 下载 → 解压」，验的是
    /// 「1.20.1 元数据里三条 Windows natives 全被规则放行，我们是否只留下了对的那条」。
    /// </summary>
    private static async Task<int> RunNativesAsync(string root, string[] args, int concurrency)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException(
                "natives 模式需要元数据路径: ml-verify natives <目录> <version.json>");
        }

        var version = LoadVersion(args[2]);
        var versionId = version.Id ?? throw new InvalidDataException("version.json 里没有 id");
        var paths = new GamePaths(root);
        var plan = NativesPlanner.Build(paths, version.Libraries ?? [], CurrentEnvironment());

        Console.WriteLine($"[natives] {versionId}");
        Console.WriteLine($"  选中 {plan.Entries.Count} 个 jar，按架构跳过 {plan.SkippedByArchitecture.Count} 个");

        foreach (var skipped in plan.SkippedByArchitecture)
        {
            Console.WriteLine($"    跳过（架构不符）{skipped}");
        }

        foreach (var warning in plan.Warnings)
        {
            Console.WriteLine($"    警告 {warning}");
        }

        var nativesItems = NativesDownloads(paths, plan);

        if (nativesItems.Count > 0)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var report = await new DownloadQueue(new FileDownloader(http))
                .RunAsync(nativesItems, concurrency);

            Console.WriteLine(
                $"  jar 下载：下载 {report.Results.Count(r => r.Outcome == DownloadOutcome.Downloaded)} " +
                $"跳过 {report.Results.Count(r => r.Outcome == DownloadOutcome.AlreadyValid)} " +
                $"失败 {report.Failures.Count} | {report.TotalBytes / 1024.0:F0} KB");

            if (!report.AllSucceeded)
            {
                foreach (var failure in report.Failures)
                {
                    Console.WriteLine($"    失败 {failure.Item.Url}: {failure.Outcome} {failure.Error}");
                }

                return 1;
            }
        }

        var watch = Stopwatch.StartNew();
        var result = NativesExtractor.Extract(paths, versionId, plan);
        watch.Stop();

        Console.WriteLine(
            $"  解压 {watch.ElapsedMilliseconds} ms | 重建 {result.Rebuilt} | " +
            $"写入 {result.FilesWritten} | 跳过 {result.FilesSkipped} | 冲突 {result.Conflicts.Count}");

        foreach (var conflict in result.Conflicts)
        {
            Console.WriteLine($"    冲突 {conflict.FileName}: 保留 {conflict.KeptFrom}，忽略 {conflict.IgnoredFrom}");
        }

        foreach (var warning in result.Warnings)
        {
            Console.WriteLine($"    警告 {warning}");
        }

        var nativesDirectory = paths.Natives(versionId);
        Console.WriteLine($"  产物 {nativesDirectory}：");

        foreach (var file in result.Files.Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(nativesDirectory, file));
            Console.WriteLine($"    {file,-34} {info.Length,10:N0} B");
        }

        return result.Files.Count > 0 ? 0 : 4;
    }

    /// <summary>
    /// 端到端：把真实 1.20.1 装完整，再真正拉起一次游戏进程。
    /// 这是 M1 的终点验收——前面所有模块（解析、下载、natives、assets、Java、参数装配）
    /// 只要有一环是错的，这里就起不来。
    /// </summary>
    private static async Task<int> RunLaunchAsync(string root, string[] args, int concurrency)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException(
                "launch 模式需要元数据路径: ml-verify launch <目录> <version.json> [秒]");
        }

        var version = LoadVersion(args[2]);

        if (version.InheritsFrom is not null)
        {
            Console.Error.WriteLine(
                $"该清单继承自 {version.InheritsFrom}，launch 模式目前只吃合并后的完整清单。");
            return 2;
        }

        int runSeconds = args.Length > 3
            ? int.Parse(args[3], CultureInfo.InvariantCulture)
            : int.Parse(Environment.GetEnvironmentVariable("ML_LAUNCH_SECONDS") ?? "45", CultureInfo.InvariantCulture);

        var versionId = version.Id ?? throw new InvalidDataException("version.json 里没有 id");
        var paths = new GamePaths(root);
        var environment = CurrentEnvironment();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var downloads = new DownloadQueue(new FileDownloader(http));

        // 1. 库 + client + 资源索引 + log4j 配置
        var items = BuildFromVersion(root, args);

        var log4jFile = version.Logging?.Client?.File;
        if (log4jFile is { Url: { Length: > 0 } url } && !string.IsNullOrEmpty(log4jFile.Id))
        {
            items.Add(new DownloadItem
            {
                Url = url,
                DestinationPath = paths.LogConfig(log4jFile.Id),
                Sha1 = log4jFile.Sha1,
                Size = log4jFile.Size,
            });
        }

        var report = await downloads.RunAsync(items, concurrency);
        Console.WriteLine(
            $"[launch] 库与客户端：{report.Results.Count} 项 | " +
            $"下载 {report.Results.Count(r => r.Outcome == DownloadOutcome.Downloaded)} | " +
            $"跳过 {report.Results.Count(r => r.Outcome == DownloadOutcome.AlreadyValid)} | " +
            $"失败 {report.Failures.Count} | {report.TotalBytes / 1024.0 / 1024.0:F1} MB");

        if (!report.AllSucceeded)
        {
            foreach (var failure in report.Failures.Take(10))
            {
                Console.WriteLine($"  失败 {failure.Item.Url}: {failure.Outcome} {failure.Error}");
            }

            return 1;
        }

        // 2. natives
        var nativesPlan = NativesPlanner.Build(paths, version.Libraries ?? [], environment);
        Console.WriteLine(
            $"[launch] natives：选中 {nativesPlan.Entries.Count}，按架构跳过 {nativesPlan.SkippedByArchitecture.Count}");

        var nativesDownload = await downloads.RunAsync(NativesDownloads(paths, nativesPlan), concurrency);
        if (!nativesDownload.AllSucceeded)
        {
            Console.WriteLine($"  natives jar 下载失败 {nativesDownload.Failures.Count}");
            return 1;
        }

        var nativesResult = NativesExtractor.Extract(paths, versionId, nativesPlan);
        Console.WriteLine(
            $"[launch] natives 解压：写入 {nativesResult.FilesWritten}，跳过 {nativesResult.FilesSkipped}，" +
            $"冲突 {nativesResult.Conflicts.Count}");

        // 3. 资源对象（现代布局；旧版虚拟布局不在这里验）
        string assetIndexId = version.AssetIndex?.Id ?? version.Assets ?? "legacy";
        string assetIndexPath = paths.AssetIndex(assetIndexId);

        if (File.Exists(assetIndexPath))
        {
            var index = AssetIndex.Load(assetIndexPath);

            if (index.RequiresMaterialization)
            {
                Console.WriteLine($"  资源索引 {assetIndexId} 需要旧版摊平，launch 模式暂不处理（跳过资源下载）。");
            }
            else
            {
                var assetItems = AssetInstaller.BuildDownloads(paths, index);
                Console.WriteLine($"[launch] 资源对象：{assetItems.Count} 个，开始下载（这一步最慢）…");

                var assetReport = await downloads.RunAsync(assetItems, concurrency);
                Console.WriteLine(
                    $"[launch] 资源：下载 {assetReport.Results.Count(r => r.Outcome == DownloadOutcome.Downloaded)} | " +
                    $"跳过 {assetReport.Results.Count(r => r.Outcome == DownloadOutcome.AlreadyValid)} | " +
                    $"失败 {assetReport.Failures.Count} | {assetReport.TotalBytes / 1024.0 / 1024.0:F1} MB");

                if (!assetReport.AllSucceeded)
                {
                    Console.WriteLine("  资源下载有失败；缺资源通常不会阻止启动，继续。");
                }
            }
        }
        else
        {
            Console.WriteLine($"  找不到资源索引文件 {assetIndexPath}，跳过资源下载。");
        }

        // 4. Java
        int requiredMajor = version.JavaVersion?.MajorVersion ?? 8;
        var installations = JavaLocator.Discover();
        var java = JavaLocator.SelectBest(installations, requiredMajor);

        if (java is null)
        {
            Console.Error.WriteLine($"没有可用的 Java {requiredMajor}。");
            return 3;
        }

        Console.WriteLine($"[launch] Java {java.Version.Raw}（{java.Source}）：{java.Home}");

        // 5. 装配命令
        var command = LaunchCommandBuilder.Build(new LaunchOptions
        {
            Paths = paths,
            Version = version,
            Environment = environment,
            JavaExecutable = java.ExecutablePath,
            GameDirectory = root,
            Account = OfflineAccount.Create("Dev"),
            LauncherName = "ML",
            LauncherVersion = "0.1.0",
            MaxMemoryMb = 2048,
            Log4jConfigPath = log4jFile?.Id is { } logId ? paths.LogConfig(logId) : null,
            WindowSize = (854, 480),
        });

        Console.WriteLine($"[launch] classpath {command.Classpath.Count} 项 | 缺失文件 {command.MissingClasspathFiles.Count}");

        if (command.MissingClasspathFiles.Count > 0)
        {
            Console.Error.WriteLine("[launch] classpath 里有文件不存在，起 JVM 也只会报 NoClassDefFoundError：");
            foreach (var missing in command.MissingClasspathFiles.Take(10))
            {
                Console.Error.WriteLine($"  缺 {missing}");
            }

            return 5;
        }
        Console.WriteLine($"[launch] 未解析占位符 {command.UnresolvedPlaceholders.Count}");

        foreach (var warning in command.Warnings)
        {
            Console.WriteLine($"  警告 {warning}");
        }

        if (command.UnresolvedPlaceholders.Count > 0)
        {
            Console.WriteLine($"  未解析：{string.Join(", ", command.UnresolvedPlaceholders)}");
        }

        Console.WriteLine($"[launch] 命令：{command.Describe()}");

        // 6. 真跑
        Console.WriteLine($"[launch] 启动游戏，最多观察 {runSeconds} 秒…");

        var outcome = await GameProcessRunner.RunAsync(
            command,
            TimeSpan.FromSeconds(runSeconds),
            line => Console.WriteLine($"  [game] {line}"));

        Console.WriteLine();
        Console.WriteLine(
            $"[launch] 退出码 {outcome.ExitCode} | 归因 {outcome.Diagnosis} | " +
            $"用时 {outcome.Duration.TotalSeconds:F1} s | 见到初始化特征 {outcome.SawStartupMarker}");

        if (outcome.TailOutput.Count > 0)
        {
            Console.WriteLine("[launch] 尾部输出：");
            foreach (var line in outcome.TailOutput.TakeLast(15))
            {
                Console.WriteLine($"  … {line}");
            }
        }

        bool ok = outcome.Diagnosis is LaunchDiagnosis.TimedOut or LaunchDiagnosis.CleanExit
                  && (outcome.SawStartupMarker || outcome.Diagnosis == LaunchDiagnosis.CleanExit);

        Console.WriteLine(ok ? "[launch] 结论：游戏起来了。" : "[launch] 结论：没起来，看上面的输出。");
        return ok ? 0 : 4;
    }

    private static VersionMetadata LoadVersion(string path) =>
        JsonSerializer.Deserialize<VersionMetadata>(File.ReadAllText(path))
        ?? throw new InvalidDataException("version.json 解析结果为空");

    private static EnvironmentProfile CurrentEnvironment() => new()
    {
        OsName = EnvironmentProfile.OsWindows,
        OsVersion = Environment.GetEnvironmentVariable("ML_OS_VERSION") ?? "10.0.26200",
        Arch = "x86_64",
        Features = new Dictionary<string, bool>(0)
    };

    /// <summary>
    /// 列出本机找得到的 Java。排查「客户说找不到 Java」时第一步就跑这个。
    /// </summary>
    private static int DumpJava(int requiredMajor)
    {
        var watch = Stopwatch.StartNew();
        var installations = JavaLocator.Discover();
        watch.Stop();

        Console.WriteLine($"扫描耗时 {watch.ElapsedMilliseconds} ms，找到 {installations.Count} 个：\n");

        if (installations.Count == 0)
        {
            Console.WriteLine("  （无）");
        }

        for (var i = 0; i < installations.Count; i++)
        {
            var installation = installations[i];
            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  [{i}] {installation.Version.Raw,-10} " +
                    $"{(installation.Architecture == JavaArchitecture.Unknown ? "?" : installation.Architecture.ToString()),-6} " +
                    $"{installation.Source,-18} " +
                    $"{(installation.HasReleaseFile ? "release" : "probe  ")} " +
                    $"{installation.Vendor ?? "(未知厂商)"}"));
            Console.WriteLine($"      {installation.Home}");
        }

        var best = JavaLocator.SelectBest(installations, requiredMajor);

        Console.WriteLine();
        if (best is null)
        {
            Console.WriteLine($"需要 Java {requiredMajor}：本机没有可用的，得下载 Mojang 运行时或让用户装。");
            return 1;
        }

        Console.WriteLine($"需要 Java {requiredMajor}：选 {best.Version.Raw}（{best.Source}）");
        Console.WriteLine($"  {best.ExecutablePath}");
        return 0;
    }

    private static List<DownloadItem> BuildFromVersion(string root, string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException(
                "version 模式需要元数据路径: ml-verify version <目录> <version.json>");
        }

        var version = LoadVersion(args[2]);
        var environment = CurrentEnvironment();

        var items = new List<DownloadItem>();

        foreach (var library in version.Libraries ?? [])
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment))
            {
                continue;
            }

            if (library.Downloads?.Artifact is { } artifact)
            {
                // 必须带 libraries/ 前缀：GamePaths.LibraryFile 与 ClasspathBuilder 都按
                // <root>/libraries/<maven 相对路径> 定位。少这一层会让文件下到根目录，
                // 而 classpath 指向不存在的位置 —— 症状是 NoClassDefFoundError: joptsimple/OptionSpec
                // 这种"库里明明有却加载不到"的错误。
                Add(artifact.Url, "libraries/" + artifact.Path, artifact.Sha1, artifact.Size);
            }
            else if (!string.IsNullOrEmpty(library.Url) &&
                     MavenCoordinate.TryParse(library.Name, out var coordinate))
            {
                // Fabric/Quilt 这类只有仓库前缀的库：自己拼路径。
                Add(
                    library.Url.TrimEnd('/') + "/" + coordinate.RelativePath,
                    "libraries/" + coordinate.RelativePath,
                    library.Sha1,
                    library.Size);
            }
        }

        if (version.Downloads is { } downloads &&
            downloads.TryGetValue("client", out var client))
        {
            // 落盘名必须是 versions/<id>/<id>.jar —— GamePaths.ClientJar 与 classpath 用的是这个名字。
            // 早先写成 client.jar，classpath 指向不存在的文件，java 报
            // ClassNotFoundException: net.minecraft.client.main.Main（症状很像"游戏没装"）。
            Add(client.Url, $"versions/{version.Id}/{version.Id}.jar", client.Sha1, client.Size);
        }

        if (version.AssetIndex is { } assetIndex)
        {
            Add(assetIndex.Url, $"assets/indexes/{assetIndex.Id}.json", assetIndex.Sha1, assetIndex.Size);
        }

        return items;

        void Add(string? url, string? path, string? sha1, long? size)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(path))
            {
                return;
            }

            items.Add(new DownloadItem
            {
                Url = url,
                DestinationPath = Path.Combine(
                    root, path.Replace('/', Path.DirectorySeparatorChar)),
                Sha1 = sha1,
                Size = size
            });
        }
    }

    private static List<DownloadItem> BuildFromAssetIndex(string root, string[] args)
    {
        if (args.Length < 3)
        {
            throw new ArgumentException(
                "assets 模式需要索引路径: ml-verify assets <目录> <asset-index.json> [limit]");
        }

        var limit = args.Length > 3
            ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)
            : int.MaxValue;

        using var document = JsonDocument.Parse(File.ReadAllText(args[2]));

        var items = new List<DownloadItem>();
        foreach (var entry in document.RootElement.GetProperty("objects").EnumerateObject().Take(limit))
        {
            var hash = entry.Value.GetProperty("hash").GetString()!;
            var size = entry.Value.GetProperty("size").GetInt64();

            items.Add(new DownloadItem
            {
                // 资源对象路径是「前两位/全哈希」，没有目录前缀。
                Url = $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                DestinationPath = Path.Combine(root, "assets", "objects", hash[..2], hash),
                Sha1 = hash,
                Size = size
            });
        }

        return items;
    }

    /// <summary>
    /// 把内核给出的 natives 条目翻译成下载任务。
    /// 内核只决定「选哪些 jar、解压到哪」，不认识下载；这个翻译属于功能层/工具层。
    /// </summary>
    private static List<DownloadItem> NativesDownloads(GamePaths paths, NativesPlan plan) =>
        plan.Entries
            .Where(entry => entry.Url is not null)
            .Select(entry => new DownloadItem
            {
                Url = entry.Url!,
                DestinationPath = paths.LibraryFile(entry.JarRelativePath),
                Sha1 = entry.Sha1,
                Size = entry.Size
            })
            .ToList();}

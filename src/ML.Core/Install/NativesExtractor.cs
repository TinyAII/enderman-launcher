using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ML.Core.Maven;
using ML.Core.Rules;
using ML.Core.Versioning;

namespace ML.Core.Install;

/// <summary>一个待解压的 natives jar。</summary>
public sealed record NativesEntry
{
    /// <summary>原始 Maven 坐标，出问题时能直接定位到是哪一行元数据。</summary>
    public required string LibraryName { get; init; }

    /// <summary>生效的分类器，如 <c>natives-windows</c>。</summary>
    public required string Classifier { get; init; }

    /// <summary>jar 在 <c>libraries/</c> 内的相对路径。</summary>
    public required string JarRelativePath { get; init; }

    public string? Url { get; init; }

    public string? Sha1 { get; init; }

    public long? Size { get; init; }

    /// <summary>解压时要跳过的前缀，如 <c>META-INF/</c>。</summary>
    public IReadOnlyList<string> Excludes { get; init; } = [];
}

/// <summary>natives 的完整计划：解压哪些 jar、为什么跳过了什么。不产生下载任务——下载不属于内核。</summary>
public sealed record NativesPlan
{
    public required IReadOnlyList<NativesEntry> Entries { get; init; }

    /// <summary>规则放行但架构不符、被主动排除的分类器。1.19+ 在 64 位 Windows 上必然非空。</summary>
    public required IReadOnlyList<string> SkippedByArchitecture { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>两个 jar 想解压同名文件。先到先得，后面的记录在这里。</summary>
public sealed record NativesConflict(string FileName, string KeptFrom, string IgnoredFrom);

public sealed record NativesExtractResult
{
    /// <summary>false 表示清单命中，整轮解压被跳过。</summary>
    public required bool Rebuilt { get; init; }

    public required int FilesWritten { get; init; }

    public required int FilesSkipped { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public required IReadOnlyList<NativesConflict> Conflicts { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}

public static class NativesPlanner
{
    /// <summary>
    /// 从库列表里挑出当前机器该解压的 natives。
    /// 两种格式都支持：1.19+ 的「库名自带 classifier」和 1.18- 的 <c>natives</c> 映射。
    /// </summary>
    public static NativesPlan Build(
        GamePaths paths,
        IEnumerable<LibraryEntry> libraries,
        EnvironmentProfile env)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(env);

        var entries = new List<NativesEntry>();
        var skipped = new List<string>();
        var warnings = new List<string>();

        foreach (var library in libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, env))
            {
                continue;
            }

            if (NativeClassifier.IsModernNatives(library.Name))
            {
                AddModern(library);
            }
            else if (library.Natives is not null)
            {
                AddLegacy(library);
            }
        }

        return new NativesPlan
        {
            Entries = entries,
            SkippedByArchitecture = skipped,
            Warnings = warnings
        };

        void AddModern(LibraryEntry library)
        {
            if (!MavenCoordinate.TryParse(library.Name, out var coordinate)
                || coordinate.Classifier is not { } classifier)
            {
                return;
            }

            if (!NativeClassifier.MatchesArchitecture(classifier, env))
            {
                // 关键：这里必须拦。元数据的 rules 不判架构，三条 Windows natives 全部放行。
                skipped.Add(library.Name ?? classifier);
                return;
            }

            var artifact = library.Downloads?.Artifact;
            Add(
                library,
                classifier,
                artifact?.Path ?? coordinate.RelativePath,
                artifact?.Url ?? CombineRepository(library.Url, artifact?.Path ?? coordinate.RelativePath),
                artifact?.Sha1,
                artifact?.Size);
        }

        void AddLegacy(LibraryEntry library)
        {
            var key = NativeClassifier.ResolveClassifierKey(library, env);
            if (key is null)
            {
                return;
            }

            if (!NativeClassifier.MatchesArchitecture(key, env))
            {
                skipped.Add($"{library.Name} -> {key}");
                return;
            }

            if (library.Downloads?.Classifiers is null
                || !library.Downloads.Classifiers.TryGetValue(key, out var reference))
            {
                warnings.Add($"{library.Name} 的 natives 映射指向 {key}，但 downloads.classifiers 里没有这一项");
                return;
            }

            var relativePath = reference.Path
                               ?? (MavenCoordinate.TryParse(library.Name, out var coordinate)
                                   ? coordinate.RelativePath
                                   : null);

            if (relativePath is null)
            {
                warnings.Add($"{library.Name} 无法确定 natives jar 路径");
                return;
            }

            Add(
                library,
                key,
                relativePath,
                reference.Url ?? CombineRepository(library.Url, relativePath),
                reference.Sha1,
                reference.Size);
        }

        void Add(
            LibraryEntry library,
            string classifier,
            string relativePath,
            string? url,
            string? sha1,
            long? size)
        {
            entries.Add(new NativesEntry
            {
                LibraryName = library.Name ?? relativePath,
                Classifier = classifier,
                JarRelativePath = relativePath,
                Url = url,
                Sha1 = sha1,
                Size = size,
                Excludes = library.Extract?.Exclude ?? []
            });

            if (string.IsNullOrEmpty(url))
            {
                // 没有 URL 说明这份元数据里没写下载地址，只能用本地已有的 jar。
                warnings.Add($"{library.Name} 没有下载地址，只能假设 jar 已存在");
                return;
            }

        }
    }

    private static string? CombineRepository(string? repository, string relativePath)
    {
        return string.IsNullOrEmpty(repository)
            ? null
            : repository.TrimEnd('/') + "/" + relativePath;
    }
}

public static class NativesExtractor
{
    private static readonly string[] DefaultExcludes = ["META-INF/"];

    /// <summary>
    /// 把 natives jar 解压到 <c>versions/{id}/natives</c>。
    ///
    /// <para>解压目录是**扁平**的（不带子目录）：游戏靠 <c>-Djava.library.path</c>
    /// 从单一目录找 dll，嵌套目录里的文件它看不见。扁平化同时也顺手挡掉了
    /// zip 里的 <c>../</c> 路径穿越——<see cref="Path.GetFileName(string)"/> 只留文件名。</para>
    ///
    /// <para>有清单文件做幂等：jar 集合（路径 + SHA1）没变且文件都在，整轮跳过。
    /// jar 集合一变就把目录清空重解，避免旧版本残留的 dll 混进来。</para>
    /// </summary>
    public static NativesExtractResult Extract(GamePaths paths, string versionId, NativesPlan plan)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentNullException.ThrowIfNull(plan);

        var nativesDirectory = paths.Natives(versionId);
        var manifestPath = paths.NativesManifest(versionId);
        var warnings = new List<string>();

        // 用循环而不是 ToDictionary：整合包里偶尔有两条元数据指向同一个 jar，
        // ToDictionary 会直接抛重复键，而这种情况应该静默合并。
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Entries)
        {
            expected[entry.JarRelativePath] = entry.Sha1 ?? string.Empty;
        }

        var existing = NativesManifest.TryLoad(manifestPath);

        if (existing is not null
            && SameJarSet(existing.Jars, expected)
            && existing.Files.All(file => File.Exists(Path.Combine(nativesDirectory, file))))
        {
            return new NativesExtractResult
            {
                Rebuilt = false,
                FilesWritten = 0,
                FilesSkipped = existing.Files.Count,
                Files = existing.Files,
                Conflicts = [],
                Warnings = warnings
            };
        }

        if (Directory.Exists(nativesDirectory))
        {
            try
            {
                Directory.Delete(nativesDirectory, recursive: true);
            }
            catch (IOException ex)
            {
                // 目录被占用（典型是游戏还开着）时不要硬来，报出来让上层提示用户。
                warnings.Add($"无法清空 natives 目录：{ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                warnings.Add($"无法清空 natives 目录：{ex.Message}");
            }
        }

        Directory.CreateDirectory(nativesDirectory);

        var producedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<NativesConflict>();
        var files = new List<string>();
        var written = 0;

        foreach (var entry in plan.Entries)
        {
            var jarPath = paths.LibraryFile(entry.JarRelativePath);
            if (!File.Exists(jarPath))
            {
                warnings.Add($"natives jar 缺失：{entry.JarRelativePath}");
                continue;
            }

            try
            {
                ExtractOne(entry, jarPath, nativesDirectory, producedBy, conflicts, files, ref written, warnings);
            }
            catch (InvalidDataException ex)
            {
                warnings.Add($"{entry.JarRelativePath} 不是合法的 zip：{ex.Message}");
            }
            catch (IOException ex)
            {
                warnings.Add($"{entry.JarRelativePath} 读取失败：{ex.Message}");
            }
        }

        try
        {
            NativesManifest.Save(manifestPath, expected, files);
        }
        catch (IOException ex)
        {
            warnings.Add($"写 natives 清单失败：{ex.Message}");
        }

        return new NativesExtractResult
        {
            Rebuilt = true,
            FilesWritten = written,
            FilesSkipped = 0,
            Files = files,
            Conflicts = conflicts,
            Warnings = warnings
        };
    }

    private static void ExtractOne(
        NativesEntry entry,
        string jarPath,
        string nativesDirectory,
        Dictionary<string, string> producedBy,
        List<NativesConflict> conflicts,
        List<string> files,
        ref int written,
        List<string> warnings)
    {
        using var archive = ZipFile.OpenRead(jarPath);

        foreach (var zipEntry in archive.Entries)
        {
            if (zipEntry.FullName.EndsWith('/') || zipEntry.Name.Length == 0)
            {
                continue;
            }

            if (IsExcluded(zipEntry.FullName, entry.Excludes))
            {
                continue;
            }

            var fileName = Path.GetFileName(zipEntry.FullName);
            if (fileName.Length == 0)
            {
                continue;
            }

            if (producedBy.TryGetValue(fileName, out var owner))
            {
                if (!string.Equals(owner, entry.JarRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add(new NativesConflict(fileName, owner, entry.JarRelativePath));
                }

                continue;
            }

            var destination = Path.Combine(nativesDirectory, fileName);
            zipEntry.ExtractToFile(destination, overwrite: true);

            producedBy[fileName] = entry.JarRelativePath;
            files.Add(fileName);
            written++;
        }

        if (entry.Excludes.Count > 0)
        {
            warnings.Add($"{entry.LibraryName} 声明了解压排除项：{string.Join(", ", entry.Excludes)}");
        }
    }

    private static bool IsExcluded(string entryName, IReadOnlyList<string> excludes)
    {
        foreach (var prefix in DefaultExcludes)
        {
            if (entryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var prefix in excludes)
        {
            if (prefix.Length > 0 &&
                entryName.StartsWith(prefix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameJarSet(
        Dictionary<string, string> left,
        Dictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other)
                || !string.Equals(value, other, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>解压清单，只用来判断「要不要重解」，不参与游戏逻辑。</summary>
    private sealed class NativesManifest
    {
        [JsonPropertyName("jars")]
        public Dictionary<string, string> Jars { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("files")]
        public List<string> Files { get; set; } = [];

        public static NativesManifest? TryLoad(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var manifest = JsonSerializer.Deserialize<NativesManifest>(File.ReadAllText(path));
                return manifest is null || manifest.Files is null ? null : manifest;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        public static void Save(string path, Dictionary<string, string> jars, List<string> files)
        {
            var manifest = new NativesManifest { Jars = jars, Files = files };
            File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        }
    }
}

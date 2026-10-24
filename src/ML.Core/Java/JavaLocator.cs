using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ML.Core.Java;

/// <summary>发现开关。默认全开；测试和「只用用户指定的路径」场景可以关掉。</summary>
public sealed class JavaDiscoveryOptions
{
    public bool IncludeJavaHome { get; init; } = true;

    public bool IncludePath { get; init; } = true;

    public bool IncludeRegistry { get; init; } = true;

    public bool IncludeWellKnownDirectories { get; init; } = true;

    public bool IncludeMojangRuntimes { get; init; } = true;

    /// <summary>IDE（IntelliJ 的 .jdks）和其它启动器（HMCL 等）自带的 Java。</summary>
    public bool IncludeOtherLaunchers { get; init; } = true;

    /// <summary>额外的候选根目录，排在最前面（最高优先级）。</summary>
    public IReadOnlyList<string> AdditionalHomes { get; init; } = [];
}

/// <summary>
/// 找 Java。不启动 JVM 就能判定的绝不启动——先读 <c>release</c> 文件，
/// 只有文件缺失时才退化成跑一次 <c>java -version</c>。
/// </summary>
public static class JavaLocator
{
    private const string ExecutableName = "java.exe";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>扫描所有来源，按优先级去重，按版本从高到低返回。</summary>
    public static IReadOnlyList<JavaInstallation> Discover(JavaDiscoveryOptions? options = null)
    {
        options ??= new JavaDiscoveryOptions();

        var found = new List<JavaInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? home, JavaSource source)
        {
            if (string.IsNullOrWhiteSpace(home))
            {
                return;
            }

            var installation = Inspect(home, source);
            if (installation is null)
            {
                return;
            }

            if (seen.Add(CanonicalKey(installation.Home)))
            {
                found.Add(installation);
            }
        }

        foreach (var home in options.AdditionalHomes)
        {
            Consider(home, JavaSource.User);
        }

        if (options.IncludeJavaHome)
        {
            Consider(Environment.GetEnvironmentVariable("JAVA_HOME"), JavaSource.JavaHome);
        }

        if (options.IncludePath)
        {
            foreach (var home in HomesFromPath())
            {
                Consider(home, JavaSource.Path);
            }
        }

        if (options.IncludeRegistry && OperatingSystem.IsWindows())
        {
            foreach (var home in HomesFromRegistry())
            {
                Consider(home, JavaSource.Registry);
            }
        }

        if (options.IncludeWellKnownDirectories)
        {
            foreach (var home in HomesFromWellKnownDirectories())
            {
                Consider(home, JavaSource.WellKnownDirectory);
            }
        }

        if (options.IncludeMojangRuntimes)
        {
            foreach (var home in HomesFromMojangRuntimes())
            {
                Consider(home, JavaSource.MojangRuntime);
            }
        }

        if (options.IncludeOtherLaunchers)
        {
            foreach (var home in HomesFromOtherTools())
            {
                Consider(home, JavaSource.WellKnownDirectory);
            }
        }

        found.Sort((left, right) => right.Version.CompareTo(left.Version));
        return found;
    }

    /// <summary>
    /// 检查单个目录是不是合法的 Java 根目录。不合法返回 <see langword="null"/>。
    /// </summary>
    public static JavaInstallation? Inspect(string home, JavaSource source = JavaSource.User)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var executable = Path.Combine(normalized, "bin", ExecutableName);
        if (!File.Exists(executable))
        {
            return null;
        }

        // PATH 里的 java.exe 常常是符号链接（Oracle 的 javapath），顺着链接找回真正的根目录。
        var resolvedHome = ResolveSymlinkedHome(executable) ?? normalized;
        if (!string.Equals(resolvedHome, normalized, StringComparison.OrdinalIgnoreCase))
        {
            executable = Path.Combine(resolvedHome, "bin", ExecutableName);
            if (!File.Exists(executable))
            {
                resolvedHome = normalized;
                executable = Path.Combine(normalized, "bin", ExecutableName);
            }
        }

        var release = JavaRelease.TryLoad(resolvedHome);
        var architecture = ParseArchitecture(release?.OsArch)
                           ?? NonUnknown(InferArchitectureFromPe(executable))
                           ?? InferArchitectureFromPath(resolvedHome);

        if (release is not null && JavaVersion.TryParse(release.VersionString, out var fromRelease))
        {
            return new JavaInstallation
            {
                Home = resolvedHome,
                ExecutablePath = executable,
                Version = fromRelease,
                Architecture = architecture,
                Vendor = release.Implementor,
                Source = source,
                HasReleaseFile = true
            };
        }

        // 老 JRE（Java 8 的 JRE 版）没有 release 文件，只能问它本人。
        return ProbeVersion(executable, out var probed)
            ? new JavaInstallation
            {
                Home = resolvedHome,
                ExecutablePath = executable,
                Version = probed,
                Architecture = architecture,
                Source = source,
                HasReleaseFile = false
            }
            : null;
    }

    /// <summary>
    /// 给某个版本需求挑一个 Java。
    /// <para>优先主版本精确匹配；没有再挑「最小的、比它高的」版本——
    /// 21 跑 17 的实例通常没问题，17 跑 21 的实例直接起不来。</para>
    /// </summary>
    public static JavaInstallation? SelectBest(
        IEnumerable<JavaInstallation> installations,
        int requiredMajor,
        JavaArchitecture? requiredArchitecture = null,
        IReadOnlyList<string>? preferredOrder = null)
    {
        ArgumentNullException.ThrowIfNull(installations);

        var candidates = installations
            .Where(installation => installation.Version.IsKnown)
            .Where(installation => requiredArchitecture is null
                                   || installation.Architecture == requiredArchitecture
                                   || installation.Architecture == JavaArchitecture.Unknown)
            .ToArray();

        // 用户在列表里排过优先级：先按那个顺序取第一个"满足最低要求"的。
        // 「自动选择」的语义就是这个——优先度最高的那个，而不是版本最高的。
        if (preferredOrder is { Count: > 0 })
        {
            int Rank(string home)
            {
                int index = preferredOrder
                    .ToList()
                    .FindIndex(p => string.Equals(p, home, StringComparison.OrdinalIgnoreCase));

                return index < 0 ? int.MaxValue : index;
            }

            var ordered = candidates
                .Where(installation => installation.Major >= requiredMajor)
                .OrderBy(installation => Rank(installation.Home))
                .FirstOrDefault();

            if (ordered is not null)
            {
                return ordered;
            }
        }

        var exact = candidates
            .Where(installation => installation.Major == requiredMajor)
            .OrderByDescending(installation => installation.Version)
            .ThenBy(installation => installation.Source)
            .ToArray();

        if (exact.Length > 0)
        {
            return exact[0];
        }

        var higher = candidates
            .Where(installation => installation.Major > requiredMajor)
            .OrderBy(installation => installation.Major)
            .ThenByDescending(installation => installation.Version)
            .ThenBy(installation => installation.Source)
            .ToArray();

        return higher.Length > 0 ? higher[0] : null;
    }

    public static JavaArchitecture? ParseArchitecture(string? osArch)
    {
        if (string.IsNullOrWhiteSpace(osArch))
        {
            return null;
        }

        return osArch.Trim().ToLowerInvariant() switch
        {
            "x86_64" or "amd64" or "x64" => JavaArchitecture.X64,
            "x86" or "i386" or "i486" or "i586" or "i686" => JavaArchitecture.X86,
            "aarch64" or "arm64" => JavaArchitecture.Arm64,
            _ => JavaArchitecture.Unknown
        };
    }

    private static string CanonicalKey(string home) =>
        Path.TrimEndingDirectorySeparator(home).ToLowerInvariant();

    private static string? ResolveSymlinkedHome(string executablePath)
    {
        try
        {
            var target = File.ResolveLinkTarget(executablePath, returnFinalTarget: true);
            var finalPath = target?.FullName;
            if (finalPath is null)
            {
                return null;
            }

            var bin = Path.GetDirectoryName(finalPath);
            return bin is null ? null : Path.GetDirectoryName(bin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从 java.exe 的 PE 头读架构。release 文件里没有 OS_ARCH 时（Mojang 自带的运行时就是这样）
    /// 这是唯一可靠的来源——比"看路径里有没有 x86"准得多。
    /// </summary>
    private static JavaArchitecture InferArchitectureFromPe(string executablePath)
    {
        try
        {
            using var stream = File.OpenRead(executablePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
            {
                return JavaArchitecture.Unknown;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();

            if (peOffset <= 0 || peOffset + 6 > stream.Length)
            {
                return JavaArchitecture.Unknown;
            }

            stream.Position = peOffset;

            if (reader.ReadUInt32() != 0x00004550u) // "PE  "
            {
                return JavaArchitecture.Unknown;
            }

            return reader.ReadUInt16() switch
            {
                0x8664 => JavaArchitecture.X64,
                0x014c => JavaArchitecture.X86,
                0xAA64 => JavaArchitecture.Arm64,
                _ => JavaArchitecture.Unknown
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return JavaArchitecture.Unknown;
        }
    }

    /// <summary>把 Unknown 当成"没查到"，好接在 ?? 后面。</summary>
    private static JavaArchitecture? NonUnknown(JavaArchitecture architecture) =>
        architecture == JavaArchitecture.Unknown ? null : architecture;

    private static JavaArchitecture InferArchitectureFromPath(string home)
    {
        // 兜底：x86 的 Java 一定装在 "Program Files (x86)" 下。
        return home.Contains("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
            ? JavaArchitecture.X86
            : JavaArchitecture.Unknown;
    }

    private static bool ProbeVersion(string executable, out JavaVersion version)
    {
        version = JavaVersion.Unknown;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-version");

            if (!process.Start())
            {
                return false;
            }

            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return false;
            }

            // openjdk version "21.0.10" 2025-10-21
            var start = output.IndexOf("version ", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            var quoteBegin = output.IndexOf('"', start);
            if (quoteBegin < 0)
            {
                return false;
            }

            var quoteEnd = output.IndexOf('"', quoteBegin + 1);
            if (quoteEnd < 0)
            {
                return false;
            }

            return JavaVersion.TryParse(output[(quoteBegin + 1)..quoteEnd], out version);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<string> HomesFromPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            yield break;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try
            {
                full = Path.GetFullPath(directory.Trim().Trim('"'));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }

            // PATH 里的目录是 bin 目录，根目录在上一层。
            if (File.Exists(Path.Combine(full, ExecutableName)))
            {
                var parent = Path.GetDirectoryName(full);
                if (parent is not null)
                {
                    yield return parent;
                }
            }
        }
    }

    // 注册表是 Windows 专有 API。加这个标注后，平台分析器会检查所有调用点是否都过了
    // OperatingSystem.IsWindows() 判断——Discover 里确实过了，所以这里不需要 warning 抑制。
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> HomesFromRegistry()
    {
        // 64 位视图和 32 位视图都要看：32 位 JVM 只出现在 WOW6432Node 里。
        (RegistryHive Hive, string SubKey)[] roots =
        [
            (RegistryHive.LocalMachine, @"SOFTWARE\JavaSoft"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\JavaSoft")
        ];

        // 只认这三类。JavaSoft 下还有一堆 "Java Web Start"/"Java Update" 子键，JavaHome 是空的。
        string[] families = ["JDK", "Java Development Kit", "Java Runtime Environment"];

        foreach (var (hive, subKey) in roots)
        {
            foreach (var family in families)
            {
                foreach (var home in ReadRegistryHomes(hive, subKey + @"\" + family))
                {
                    yield return home;
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> ReadRegistryHomes(RegistryHive hive, string subKey)
    {
        RegistryKey? baseKey = null;
        try
        {
            baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        if (baseKey is null)
        {
            yield break;
        }

        using (baseKey)
        {
            string[] versions;
            try
            {
                versions = baseKey.GetSubKeyNames();
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var version in versions)
            {
                RegistryKey? versionKey = null;
                string? javaHome = null;

                try
                {
                    versionKey = baseKey.OpenSubKey(version);
                    javaHome = versionKey?.GetValue("JavaHome") as string;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }
                finally
                {
                    versionKey?.Dispose();
                }

                if (!string.IsNullOrWhiteSpace(javaHome))
                {
                    yield return javaHome;
                }
            }
        }
    }

    private static IEnumerable<string> HomesFromWellKnownDirectories()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 厂商会改名，这里只放「一眼能认出是 JDK 容器」的目录。
        (string Root, string Pattern)[] layouts =
        [
            (programFiles, "Java"),
            (programFiles, "Eclipse Adoptium"),
            (programFiles, "Eclipse Foundation"),
            (programFiles, "Microsoft"),
            (programFiles, "Zulu"),
            (programFiles, "Amazon Corretto"),
            (programFiles, "BellSoft"),
            (programFiles, "Semeru"),
            (programFiles, "RedHat"),
            (programFiles, "JetBrains"),
            (programFilesX86, "Java"),
            (programFilesX86, "Zulu"),
            (Path.Combine(localAppData, "Programs"), "Eclipse Adoptium"),
            (Path.Combine(localAppData, "Programs"), "Java"),
            (Path.Combine(localAppData, "Programs"), "Microsoft")
        ];

        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, pattern) in layouts)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var directory = Path.Combine(root, pattern);
            if (!seenRoots.Add(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<string> HomesFromMojangRuntimes()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            yield break;
        }

        var runtimeRoot = Path.Combine(appData, ".minecraft", "runtime");
        if (!Directory.Exists(runtimeRoot))
        {
            yield break;
        }

        string[] components;
        try
        {
            components = Directory.GetDirectories(runtimeRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var component in components)
        {
            // 两种布局都要认：
            //   扁平   runtime/<component>/bin/java.exe            （实测本机 java-runtime-epsilon 就是这样）
            //   分层   runtime/<component>/<platform>/<JVM 根>/bin/java.exe
            // 早先只扫了分层这种，于是扁平布局的 Java 25 被漏掉。
            yield return component;

            string[] platforms;
            try
            {
                platforms = Directory.GetDirectories(component);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var platform in platforms)
            {
                yield return platform;

                string[] nested;
                try
                {
                    nested = Directory.GetDirectories(platform);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var child in nested)
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>
    /// IDE 与其它启动器自带的 Java：用户平时不会把它们加进 PATH，但确实装在本机。
    /// </summary>
    /// <remarks>
    /// 和 PCL 的扫描结果对齐用的：<c>%USERPROFILE%\.jdks</c>（IntelliJ 下载的 JDK）、
    /// <c>%APPDATA%\.hmcl\java</c>（HMCL 管理的 Java，多一层平台目录）。
    /// 只扫这两类"一眼能认出"的固定位置，不做全盘搜索——那会慢到不可接受。
    /// </remarks>
    private static IEnumerable<string> HomesFromOtherTools()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        // IntelliJ：.jdks/<jdk-21.0.7> 直接就是 JVM 根
        if (!string.IsNullOrEmpty(userProfile))
        {
            foreach (var home in ChildrenOf(Path.Combine(userProfile, ".jdks")))
            {
                yield return home;
            }
        }

        if (string.IsNullOrEmpty(appData))
        {
            yield break;
        }

        // HMCL：.hmcl/java/<windows-x86_64>/<jdk>
        foreach (var platform in ChildrenOf(Path.Combine(appData, ".hmcl", "java")))
        {
            yield return platform;

            foreach (var home in ChildrenOf(platform))
            {
                yield return home;
            }
        }
    }

    private static IEnumerable<string> ChildrenOf(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            yield return child;
        }
    }

    internal static string FormatVersionForLog(JavaInstallation installation) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{installation.Version.Raw}/{installation.Architecture}/{installation.Source}");
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ML.Core.Launch;

/// <summary>启动结果归因。</summary>
public enum LaunchDiagnosis
{
    /// <summary>退出码 0，正常关闭。</summary>
    CleanExit,

    /// <summary>非零退出且命中崩溃特征。</summary>
    Crashed,

    /// <summary>JVM 根本没起来（Java 太老、mainClass 拼错、缺库）。</summary>
    JavaStartupFailure,

    /// <summary>超时被我们杀掉。对游戏来说这通常意味着"跑起来了且一直在跑"。</summary>
    TimedOut,

    /// <summary>外部取消（用户点了取消 / 宿主退出）。</summary>
    Killed
}

/// <summary>一次启动的观测结果。</summary>
public sealed record LaunchOutcome
{
    public required int ExitCode { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>前 120 行输出。启动是否真的走到了游戏初始化，看这里。</summary>
    public required IReadOnlyList<string> HeadOutput { get; init; }

    /// <summary>最后 200 行输出。崩溃原因基本都在尾部。</summary>
    public required IReadOnlyList<string> TailOutput { get; init; }

    public required bool TimedOut { get; init; }

    public required LaunchDiagnosis Diagnosis { get; init; }

    /// <summary>是否见到过游戏真正开始初始化的特征行（LWJGL / OpenAL / 设置用户）。</summary>
    public required bool SawStartupMarker { get; init; }
}

/// <summary>
/// 起 Java 进程、收输出、等退出，并给出归因。
/// </summary>
/// <remarks>
/// 归因只依赖输出特征与退出码，不做启发式猜测：猜错的归因比没有归因更糟，
/// 用户会照着错误的方向排查。
/// </remarks>
public static class GameProcessRunner
{
    private const int HeadLines = 120;
    private const int TailLines = 200;

    private static readonly string[] StartupFailureMarkers =
    [
        "UnsupportedClassVersionError",
        "Error: Could not create the Java Virtual Machine",
        "Error occurred during initialization of VM",
        "Could not find or load main class",
        "java.lang.ClassNotFoundException",
        "NoClassDefFoundError",
    ];

    private static readonly string[] CrashMarkers =
    [
        "Exception in thread",
        "Minecraft has crashed",
        "java.lang.OutOfMemoryError",
        "A fatal error has been detected",
        "FATAL ERROR in native method",
    ];

    private static readonly string[] StartupMarkers =
    [
        "Backend library:",
        "OpenAL initialized",
        "Sound engine started",
        "Setting user:",
        "LWJGL",
    ];

    public static async Task<LaunchOutcome> RunAsync(
        LaunchCommand command,
        TimeSpan? timeout = null,
        Action<string>? onLine = null,
        ProcessPriorityClass? priority = null,
        Action<int>? onStarted = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.JavaExecutable);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.JavaExecutable,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var head = new List<string>(HeadLines);
        var tail = new Queue<string>(TailLines);
        var gate = new object();
        var flattener = new Log4jXmlFlattener();
        bool sawStartupMarker = false;
        bool sawStartupFailure = false;
        bool sawCrash = false;

        // 控制台日志是 log4j XML（一个事件横跨多行），先压成一行再判特征、再记。
        // 顺序很重要：特征串（"Exception in thread"……）在 CDATA 里，不先压平就一个字都匹配不到。
        void Ingest(string? rawLine)
        {
            if (rawLine is null)
            {
                return;
            }

            lock (gate)
            {
                // Monitor 可重入，Record 内部的 lock 不会死锁。
                foreach (string line in flattener.Append(rawLine))
                {
                    Record(line);
                }
            }
        }

        void Record(string? line)
        {
            if (line is null)
            {
                return;
            }

            onLine?.Invoke(line);

            lock (gate)
            {
                if (head.Count < HeadLines)
                {
                    head.Add(line);
                }

                if (tail.Count == TailLines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);

                sawStartupMarker |= ContainsAny(line, StartupMarkers);
                sawStartupFailure |= ContainsAny(line, StartupFailureMarkers);
                sawCrash |= ContainsAny(line, CrashMarkers);
            }
        }

        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => Ingest(e.Data);
        process.ErrorDataReceived += (_, e) => Ingest(e.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动 Java 进程：{command.JavaExecutable}");
        }

        if (priority is { } wanted)
        {
            try
            {
                process.PriorityClass = wanted;
                onLine?.Invoke($"[ML] 已设置进程优先级：{process.PriorityClass}");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // 权限不够（高优先级在某些机器上要提权）不该让游戏起不来：记一句继续跑。
                onLine?.Invoke($"[ML] 设置进程优先级失败（{wanted}）：{ex.Message}");
            }
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 进程真的起来了：调用方靠这个回调知道"启动阶段结束了"（多开闸门就在这儿放行）。
        onStarted?.Invoke(process.Id);

        bool timedOut = false;
        bool killed = false;

        using var timeoutSource = timeout is { } limit
            ? new CancellationTokenSource(limit)
            : new CancellationTokenSource();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            killed = cancellationToken.IsCancellationRequested;
            timedOut = !killed;

            TryKillTree(process);

            // 杀掉之后还要把已缓冲的输出读干净，否则 TailOutput 会缺尾巴。
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 进程已经没了，正常。
            }
        }

        stopwatch.Stop();

        // 被腰斩的最后一个事件也要吐出来，否则崩溃现场恰好落在最后一行时会消失。
        lock (gate)
        {
            foreach (string line in flattener.Flush())
            {
                Record(line);
            }
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        LaunchDiagnosis diagnosis;
        if (killed)
        {
            diagnosis = LaunchDiagnosis.Killed;
        }
        else if (timedOut)
        {
            diagnosis = LaunchDiagnosis.TimedOut;
        }
        else if (exitCode == 0)
        {
            diagnosis = LaunchDiagnosis.CleanExit;
        }
        else if (sawStartupFailure)
        {
            diagnosis = LaunchDiagnosis.JavaStartupFailure;
        }
        else
        {
            // 非零退出但没命中具体特征，仍然算崩溃——总比报"未知"有用。
            diagnosis = LaunchDiagnosis.Crashed;
        }

        lock (gate)
        {
            return new LaunchOutcome
            {
                ExitCode = exitCode,
                Duration = stopwatch.Elapsed,
                HeadOutput = head,
                TailOutput = [.. tail],
                TimedOut = timedOut,
                Diagnosis = diagnosis,
                SawStartupMarker = sawStartupMarker,
            };
        }
    }

    private static bool ContainsAny(string line, string[] markers)
    {
        foreach (var marker in markers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // 已经退出。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 权限不足或进程已消失；交给后续 WaitForExit 收尾。
        }
    }
}

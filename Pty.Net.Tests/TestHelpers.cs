namespace Ghostflyby.Pty.Tests;

using System.Globalization;
using System.Text;

/// <summary>
/// Test-side helpers for driving a PTY session. Marker-based reading (<c>ReadUntil</c>)
/// is deliberately not part of the public library API — consumers build their own text
/// layer on <c>PtyProcess.Output</c> — so the tests share one here.
/// All blocking work uses the reader's async path with a cancelable token (the engine
/// cancels pending reads immediately), so no thread-pool thread is ever parked.
/// </summary>
internal static class TestBash
{
    /// <summary>Path to an interactive bash: /bin/bash on Unix, Git Bash's bash.exe on Windows.</summary>
    public static string BashPath { get; } = ResolveBashPath();

    private static string ResolveBashPath()
    {
#if WINDOWS
        return ResolveBash();
#else
        return "/bin/bash";
#endif
    }

    /// <summary>
    /// Starts an interactive bash. Runs with <c>--noprofile --norc --noediting -i</c> so
    /// the session is deterministic and does not pick up the user's rc files.
    /// <c>--noediting</c> disables readline, so <c>stty -echo</c> can suppress input echo
    /// (readline does its own echoing regardless). Note: the long options must come before
    /// <c>-i</c>, otherwise macOS bash 3.2 rejects the invocation.
    /// </summary>
    public static PtyProcess Start(string? workingDirectory = null, params string[]? arguments)
    {
        var args = arguments is { Length: > 0 }
            ? arguments
            : ["--noprofile", "--norc", "--noediting", "-i"];
        return PtyProcess.Start(BashPath, args, workingDirectory);
    }

#if WINDOWS
    /// <summary>Locates Git Bash's bash.exe (standard install paths first, then PATH).</summary>
    private static string ResolveBash()
    {
        var candidates = new List<string>
        {
            @"C:\Program Files\Git\bin\bash.exe",
            @"C:\Program Files (x86)\Git\bin\bash.exe",
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (dir.Length > 0)
                candidates.Add(Path.Combine(dir.Trim('"'), "bash.exe"));
        }
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(
            "Git Bash (bash.exe) was not found; the Windows test suite drives interactive bash sessions.");
    }
#endif

    /// <summary>File + args for a process that sleeps for <paramref name="seconds"/> without exiting on its own.</summary>
    public static (string File, string[] Args) SleepProcess(double seconds)
    {
#if WINDOWS
        // A fresh powershell per session is heavy (and its startup churn perturbs the
        // thread-pool accounting in the no-starvation tests); cmd + ping is much lighter.
        // ping 127.0.0.1 replies roughly once a second, so n pings ≈ n seconds.
        return ("cmd.exe", ["/c", $"ping -n {Math.Max(2, (int)seconds + 1)} 127.0.0.1 >nul"]);
#else
        return ("/bin/sleep", [seconds.ToString(CultureInfo.InvariantCulture)]);
#endif
    }

    /// <summary>File + args for a process that runs forever (busy child, used by dispose/exit tests).</summary>
    public static (string File, string[] Args) BusyProcess()
    {
#if WINDOWS
        return ("powershell.exe", ["-Command", "while($true){Start-Sleep -Milliseconds 100}"]);
#else
        return ("/bin/sh", ["-c", "cat < /dev/zero"]);
#endif
    }

    /// <summary>
    /// File + args for a short-lived child that exits on its own (~1 s).
    /// On Windows it uses cmd + ping (like <see cref="SleepProcess"/>) rather than a fresh
    /// powershell, whose multi-second startup latency under the parallel suite's load has
    /// blown the 5 s wait in the Exited/reaper tests.
    /// </summary>
    public static (string File, string[] Args) ShortLivedProcess()
    {
#if WINDOWS
        return ("cmd.exe", ["/c", "ping -n 2 127.0.0.1 >nul"]);
#else
        return ("/bin/sh", ["-c", "sleep 0.3"]);
#endif
    }

    /// <summary>
    /// Samples available worker threads repeatedly over <paramref name="window"/> and returns
    /// the maximum observed value. A genuine leak (e.g. parked blocking reads pinning one
    /// thread per session) suppresses every sample, so the max still catches it; transient
    /// startup churn from the parallel suite dips only isolated samples, which the max
    /// filters out.
    /// </summary>
    public static int MaxAvailableWorkers(TimeSpan window)
    {
        var deadline = DateTime.UtcNow + window;
        var max = 0;
        while (DateTime.UtcNow < deadline)
        {
            ThreadPool.GetAvailableThreads(out var available, out _);
            max = Math.Max(max, available);
            Thread.Sleep(25);
        }
        return max;
    }

    /// <summary>
    /// Reads from <paramref name="reader"/> until <paramref name="marker"/> appears in the
    /// accumulated text (inclusive) and returns everything read so far. Returns whatever
    /// was read if the child exits (EOF) before the marker shows up. Throws
    /// <see cref="TimeoutException"/> if the marker does not appear within
    /// <paramref name="timeout"/>.
    /// </summary>
    public static string ReadUntil(StreamReader reader, string marker, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var n = reader.ReadAsync(buf.AsMemory(), timeoutCts.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0)
                    return sb.ToString(); // EOF: the child exited
                sb.Append(buf, 0, n);
                // The marker can only have appeared within the newly appended chunk
                // (the text before it was already checked): scan only that window instead
                // of re-copying the whole accumulated buffer every iteration.
                if (sb.Length >= marker.Length)
                {
                    var start = Math.Max(0, sb.Length - n - marker.Length + 1);
                    if (sb.ToString(start, sb.Length - start).Contains(marker, StringComparison.Ordinal))
                        return sb.ToString();
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {timeout} waiting for '{marker}'. Got: {sb}");
        }
    }

    /// <summary>
    /// Reads and discards whatever the child produces for up to <paramref name="duration"/>
    /// (e.g. the echo of a command that just ran), then returns. Never parks a thread:
    /// each read window is capped by a cancelable token and the engine cancels pending
    /// reads immediately.
    /// </summary>
    public static void Drain(StreamReader reader, TimeSpan duration)
    {
        var buf = new char[4096];
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                var n = reader.ReadAsync(buf.AsMemory(), cts.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0)
                    return; // EOF
            }
            catch (OperationCanceledException)
            {
                // Nothing arrived within this 100ms window; keep draining until the deadline.
            }
        }
    }
}

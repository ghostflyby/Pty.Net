using System.Diagnostics;

namespace Ghostflyby.Pty.Tests;

/// <summary>
/// Exercises the async process-control surface: <see cref="PtyProcess.WaitForExitAsync(CancellationToken)"/>,
/// <see cref="PtyProcess.DisposeAsync"/> and the <see cref="PtyProcess.Exited"/> event, plus the
/// process-wide reaper that owns waitpid(2).
/// </summary>
public class PtyProcessAsyncTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // --- WaitForExitAsync -------------------------------------------------

    /// <summary>Timeout returns false around the deadline without blocking the calling thread.</summary>
    [Fact]
    public async Task WaitForExitAsync_TimeoutReturnsFalse()
    {
        var (file, args) = TestBash.SleepProcess(1000);
        using var p = PtyProcess.Start(file, args);

        var sw = Stopwatch.StartNew();
        var exited = await p.WaitForExitAsync(TimeSpan.FromMilliseconds(200)).WaitAsync(Timeout);
        sw.Stop();

        Assert.False(exited);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"timeout wait took {sw.Elapsed}");
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(100), $"returned too early: {sw.Elapsed}");
    }

    [Fact]
    public async Task WaitForExitAsync_ReturnsTrue_WhenChildExits()
    {
        using var bash = TestBash.Start();
        bash.Input.WriteLine("exit");

        Assert.True(await bash.WaitForExitAsync(Timeout).WaitAsync(Timeout));
        Assert.Equal(0, bash.ExitCode);
    }

    /// <summary>The child exits on its own; the reaper collects it and the wait completes.</summary>
    [Fact]
    public async Task WaitForExitAsync_CompletesOnExternalExit()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        await using var p = PtyProcess.Start(file, args);

        Assert.True(await p.WaitForExitAsync(Timeout).WaitAsync(Timeout));
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task WaitForExitAsync_Cancellation_ThrowsOce()
    {
        var (file, args) = TestBash.SleepProcess(1000);
        await using var p = PtyProcess.Start(file, args);
        using var cts = new CancellationTokenSource();

        var wait = p.WaitForExitAsync(System.Threading.Timeout.InfiniteTimeSpan, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait).WaitAsync(Timeout);
    }

    /// <summary>
    /// Pending WaitForExitAsync calls hold no thread: with many sessions parked waiting,
    /// the worker pool must not lose threads (the wait is a Task.Delay loop, not a
    /// Thread.Sleep), and every wait completes promptly once the child is killed.
    /// </summary>
    [Fact]
    public async Task WaitForExitAsync_HoldsNoThreadPoolThreads()
    {
        const int sessions = 32;
        var all = new PtyProcess[sessions];

        ThreadPool.GetAvailableThreads(out var workersBefore, out _);
        for (var i = 0; i < sessions; i++)
        {
            var (file, args) = TestBash.SleepProcess(1000);
            all[i] = PtyProcess.Start(file, args);
            _ = all[i].WaitForExitAsync(System.Threading.Timeout.InfiniteTimeSpan);
        }

        // Windows launches one child process per session (cmd/ping — lighter than
        // /bin/sleep's peer PowerShell, but still heavier than the Unix sleep), so let
        // the startup churn settle before measuring; the sessions stay alive (sleep
        // 1000 s) throughout, and the wait itself holds no thread. The parallel suite
        // also dips isolated samples, so take the max over a window afterwards: a real
        // leak suppresses every sample, while transient churn only dips some.
#if WINDOWS
        await Task.Delay(1500);
#else
        await Task.Delay(300);
#endif
        var workersDuring = TestBash.MaxAvailableWorkers(TimeSpan.FromSeconds(1));

        foreach (var p in all)
        {
            p.Kill();
            await p.WaitForExitAsync(Timeout).WaitAsync(Timeout);
            p.Dispose();
        }

        Assert.True(
            workersDuring >= workersBefore - 4,
            $"Available worker threads collapsed from {workersBefore} to {workersDuring}: " +
            "pending WaitForExitAsync waits must not occupy thread-pool threads.");
    }

    // --- DisposeAsync -----------------------------------------------------

    [Fact]
    public async Task DisposeAsync_CompletesForExitedChild()
    {
        var bash = TestBash.Start();
        bash.Input.WriteLine("exit");
        await bash.WaitForExitAsync(Timeout).WaitAsync(Timeout);

        await bash.DisposeAsync(); // must complete and not throw

        Assert.Equal(0, bash.ExitCode);
    }

    /// <summary>DisposeAsync on a busy child (writing to the pty in a loop) completes promptly.</summary>
    [Fact]
    public async Task DisposeAsync_BusyChild_Completes()
    {
        var (file, args) = TestBash.BusyProcess();
        var p = PtyProcess.Start(file, args);

        var sw = Stopwatch.StartNew();
        await p.DisposeAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"DisposeAsync took {sw.Elapsed}");
    }

    /// <summary>
    /// A child that ignores SIGHUP survives the terminate step inside
    /// <see cref="PtyProcess.DisposeAsync"/>: the bounded 2 s reap wait elapses with the
    /// child still alive, and DisposeAsync must return normally (no TimeoutException)
    /// while the process-wide reaper keeps watching the child in the background.
    /// Unix-only: Windows TerminateProcess kills the child regardless of signal
    /// handling, so the 2 s branch is unreachable there.
    /// </summary>
#if !WINDOWS
    [Fact]
    public async Task DisposeAsync_SurvivingChild_CompletesAfterBoundedWait()
    {
        // trap '' HUP makes the child ignore the hangup Dispose sends; sleep keeps it
        // alive well past the 2 s bounded wait. Wait for the trap to be installed before
        // disposing: a SIGHUP landing before `trap` runs would terminate the fresh shell
        // by default and the test would never reach the 2 s branch.
        var p = PtyProcess.Start("/bin/sh", ["-c", "trap '' HUP; sleep 30"]);
        try
        {
            await Task.Delay(300);
            var sw = Stopwatch.StartNew();
            await p.DisposeAsync(); // must complete, not throw TimeoutException
            sw.Stop();

            // The bounded window is 2 s; the Stopwatch may straddle the WaitAsync timer
            // by a sub-millisecond, so assert "the wait branch ran" with a comfortable
            // margin (1.5 s) rather than the exact 2 s — a trap-install failure would
            // return in ~0.3 s. The pre-fix behavior threw TimeoutException at 2 s.
            Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(1.5), $"returned too early: {sw.Elapsed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"DisposeAsync took {sw.Elapsed}");
        }
        finally
        {
            // Ensure the surviving child does not outlive the test: SIGKILL it; the
            // reaper collects it in the background.
            p.Kill();
        }
    }
#endif

    // --- Exited event -----------------------------------------------------

    [Fact]
    public async Task Exited_Fires_WithExitCode()
    {
        using var bash = TestBash.Start();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bash.Exited += (_, _) => tcs.TrySetResult(bash.ExitCode ?? -1);

        bash.Input.WriteLine("exit");

        Assert.Equal(0, await tcs.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Exited_FiresOnExternalDeath()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        using var p = PtyProcess.Start(file, args);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (_, _) => tcs.TrySetResult(p.ExitCode ?? -1);

        Assert.Equal(0, await tcs.Task.WaitAsync(Timeout));
    }

    /// <summary>A throwing handler must not kill the shared reaper thread (other sessions keep reaping).</summary>
    [Fact]
    public async Task Exited_HandlerException_DoesNotAffectOtherSessions()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        var p1 = PtyProcess.Start(file, args);
        var p2 = PtyProcess.Start(file, args);
        var tcs2 = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        p1.Exited += (_, _) => throw new InvalidOperationException("boom");
        p2.Exited += (_, _) => tcs2.TrySetResult(p2.ExitCode ?? -1);

        try
        {
            Assert.Equal(0, await tcs2.Task.WaitAsync(Timeout));
        }
        finally
        {
            p1.Dispose();
            p2.Dispose();
        }
    }

    /// <summary>The reaper sets ExitCode on its own — no explicit WaitForExit needed.</summary>
    [Fact]
    public async Task ExitCode_IsSetByReaper_WithoutExplicitWait()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        using var p = PtyProcess.Start(file, args);

        // The only wait here is for the observable outcome (ExitCode), which the
        // process-wide reaper produces by itself.
        var sw = Stopwatch.StartNew();
        while (p.ExitCode is null && sw.Elapsed < Timeout)
            await Task.Delay(10);

        Assert.NotNull(p.ExitCode);
        Assert.Equal(0, p.ExitCode);
    }
}

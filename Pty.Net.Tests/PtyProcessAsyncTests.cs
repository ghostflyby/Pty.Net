using System.Diagnostics;
using Ghostflyby.Pty;

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
        bash.StandardInput.WriteLine("exit");

        Assert.True(await bash.WaitForExitAsync(Timeout).WaitAsync(Timeout));
        Assert.Equal(0, bash.ExitCode);
    }

    /// <summary>The child exits on its own; the reaper collects it and the wait completes.</summary>
    [Fact]
    public async Task WaitForExitAsync_CompletesOnExternalExit()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        using var p = PtyProcess.Start(file, args);

        Assert.True(await p.WaitForExitAsync(Timeout).WaitAsync(Timeout));
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public async Task WaitForExitAsync_Cancellation_ThrowsOce()
    {
        var (file, args) = TestBash.SleepProcess(1000);
        using var p = PtyProcess.Start(file, args);
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
        // /bin/sleep's peer powershell, but still heavier than the Unix sleep), so let
        // the startup churn settle before measuring; the sessions stay alive (sleep
        // 1000 s) throughout, and the wait itself holds no thread.
#if WINDOWS
        await Task.Delay(1500);
#else
        await Task.Delay(300);
#endif
        ThreadPool.GetAvailableThreads(out var workersDuring, out _);

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
        bash.StandardInput.WriteLine("exit");
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

    // --- Exited event -----------------------------------------------------

    [Fact]
    public async Task Exited_Fires_WithExitCode()
    {
        using var bash = TestBash.Start();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bash.Exited += (_, _) => tcs.TrySetResult(bash.ExitCode ?? -1);

        bash.StandardInput.WriteLine("exit");

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

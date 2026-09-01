using System.Diagnostics;

namespace Ghostflyby.Pty.Tests;

/// <summary>
/// Exercises the async process-control surface: <see cref="PtyProcess.WaitForExitAsync(TimeSpan?, CancellationToken)"/>,
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
        var exited = await p.WaitForExitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);
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

        Assert.True(await bash.WaitForExitAsync(Timeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, bash.ExitCode);
    }

    /// <summary>The child exits on its own; the reaper collects it and the wait completes.</summary>
    [Fact]
    public async Task WaitForExitAsync_CompletesOnExternalExit()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        await using var p = PtyProcess.Start(file, args);

        Assert.True(await p.WaitForExitAsync(Timeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken));
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait).WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }

    // --- DisposeAsync -----------------------------------------------------

    [Fact]
    public async Task DisposeAsync_CompletesForExitedChild()
    {
        var bash = TestBash.Start();
        bash.Input.WriteLine("exit");
        await bash.WaitForExitAsync(Timeout, TestContext.Current.CancellationToken).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await bash.DisposeAsync(); // must complete and not throw

        Assert.Equal(0, bash.ExitCode);
    }

    /// <summary>DisposeAsync on a busy child (writing to the pty in a loop) completes promptly.</summary>
    [Fact]
    public async Task DisposeAsync_BusyChild_Completes()
    {
        var (file, args) = TestBash.BusyProcess();
        var p = PtyProcess.Start(file, args);
        // A busy child typically does not exit on the graceful signal (SIGHUP / Windows
        // CTRL_CLOSE_EVENT), so a short grace window keeps the test fast — it pins down
        // "dispose completes", not "the child exits cleanly".
        p.GracefulExitTimeout = TimeSpan.FromSeconds(1);

        var sw = Stopwatch.StartNew();
        await p.DisposeAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"DisposeAsync took {sw.Elapsed}");
    }

    /// <summary>
    /// A child that ignores SIGHUP survives the graceful window inside
    /// <see cref="PtyProcess.DisposeAsync"/> and is then force-killed: DisposeAsync
    /// returns promptly once the reaper collects it, with no child left running in the
    /// background. Unix-only: Windows TerminateProcess kills the child regardless of
    /// signal handling, so the graceful branch is unreachable there.
    /// </summary>
#if !WINDOWS
    [Fact]
    public async Task DisposeAsync_InteractiveShell_ClosesTerminalPromptly()
    {
        var p = TestBash.Start();
        p.GracefulExitTimeout = TimeSpan.FromSeconds(2);
        TestBash.ReadUntil(p.Output, "$", Timeout);

        var sw = Stopwatch.StartNew();
        await p.DisposeAsync();
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"DisposeAsync took {sw.Elapsed}");
        Assert.True(p.HasExited, "interactive shell must be reaped by dispose");
    }

    [Fact]
    public async Task DisposeAsync_SurvivingChild_CompletesAfterGracefulTimeout()
    {
        // trap '' HUP makes the child ignore the hangup Dispose sends; sleep keeps it
        // alive well past the graceful window. Wait for the trap to be installed before
        // disposing: a SIGHUP landing before `trap` runs would terminate the fresh shell
        // by default and the test would never reach the force-kill branch.
        var p = PtyProcess.Start("/bin/sh", ["-c", "trap '' HUP; sleep 30"]);
        p.GracefulExitTimeout = TimeSpan.FromSeconds(1);
        try
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            var sw = Stopwatch.StartNew();
            await p.DisposeAsync(); // graceful window elapses, then force-kill; completes, not TimeoutException
            sw.Stop();

            // The graceful window is 1 s; allow some scheduling slack on the upper bound.
            Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(700), $"returned too early: {sw.Elapsed}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"DisposeAsync took {sw.Elapsed}");
            Assert.True(p.HasExited, "child must be reaped by dispose");
        }
        finally
        {
            // Ensure the child does not outlive the test: SIGKILL it; the reaper
            // collects it in the background.
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
        bash.Exited += (code, _) => tcs.TrySetResult(code);

        bash.Input.WriteLine("exit");

        Assert.Equal(0, await tcs.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The Exited flow with nobody reading the terminal. Regression: on macOS a
    /// fork-spawned session leader parks mid-exit when its final tty output cannot
    /// drain to an unread pty master, so without the reaper's stuck-exit close this
    /// wait never completes. Allow ~3s for the reaper's stuck-exit grace window.
    /// </summary>
    [Fact]
    public async Task Exited_Fires_WithoutReadingOutput()
    {
        var p = TestBash.Start();
        try
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.Exited += (code, _) => tcs.TrySetResult(code);

            p.Input.WriteLine("exit");

            Assert.Equal(0, await tcs.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
        finally
        {
            await p.DisposeAsync();
        }
    }

    [Fact]
    public async Task Exited_FiresOnExternalDeath()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        using var p = PtyProcess.Start(file, args);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (code, _) => tcs.TrySetResult(code);

        Assert.Equal(0, await tcs.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
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
        p2.Exited += (code, _) => tcs2.TrySetResult(code);

        try
        {
            Assert.Equal(0, await tcs2.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        }
        finally
        {
            p1.Dispose();
            p2.Dispose();
        }
    }

    /// <summary>
    /// Handler isolation is per handler, not per event: with two subscribers on the
    /// SAME process, a throw from the first must not stop the second from seeing the
    /// exit (a whole-multicast try/catch would starve later subscribers).
    /// </summary>
    [Fact]
    public async Task Exited_HandlerException_IsolatesLaterHandlersOnSameProcess()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        using var p = PtyProcess.Start(file, args);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRan = 0;

        p.Exited += (_, _) =>
        {
            Interlocked.Increment(ref firstRan);
            throw new InvalidOperationException("boom");
        };
        p.Exited += (code, _) => second.TrySetResult(code);

        Assert.Equal(0, await second.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, Volatile.Read(ref firstRan));
    }

    [Fact]
    public async Task OnReaped_DuplicateNotification_PublishesOnlyOnce()
    {
        var (file, args) = TestBash.ShortLivedProcess();
        using var p = PtyProcess.Start(file, args);
        var calls = 0;
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (code, _) =>
        {
            Interlocked.Increment(ref calls);
            tcs.TrySetResult(code);
        };

        Assert.Equal(0, await tcs.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        p.OnReaped(42);

        Assert.Equal(0, p.ExitCode);
        Assert.Equal(1, Volatile.Read(ref calls));
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
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.NotNull(p.ExitCode);
        Assert.Equal(0, p.ExitCode);
    }
}

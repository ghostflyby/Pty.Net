namespace Ghostflyby.Pty.Tests;

/// <summary>
/// The two tests that assert on thread-pool worker availability, grouped into one
/// serialized collection. They measure a process-global resource (available worker
/// threads), so they must not race the rest of the parallel suite, which freely borrows
/// the same pool (spawning processes, doing I/O); running them with
/// <c>DisableParallelization</c> gives them exclusive access, so the measured counts are
/// perturbed only by each test's own spawns — which the re-baselined sampling inside the
/// test then settles. Each test starts and stops many sessions, so keeping the pair in
/// one collection also lets them run sequentially relative to each other.
/// </summary>
[Collection("thread-pool accounting")]
public class ThreadPoolAccountingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

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

        for (var i = 0; i < sessions; i++)
        {
            var (file, args) = TestBash.SleepProcess(1000);
            all[i] = PtyProcess.Start(file, args);
            _ = all[i].WaitForExitAsync(System.Threading.Timeout.InfiniteTimeSpan);
        }

        // Windows launches one child process per session (cmd/ping — lighter than
        // /bin/sleep's peer PowerShell, but still heavier than the Unix sleep), so let
        // the startup churn settle before measuring; the sessions stay alive (sleep
        // 1000 s) throughout, and the wait itself holds no thread. Baseline AFTER the
        // settle, immediately before the window, so the comparison measures the same
        // pool state (this test runs in a serialized collection, so the only churn is
        // its own spawns). A real leak (one thread pinned per parked wait) still
        // suppresses every sample in the max window.
#if WINDOWS
        await Task.Delay(1500);
#else
        await Task.Delay(300);
#endif
        ThreadPool.GetAvailableThreads(out var workersBefore, out _);
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

    /// <summary>
    /// A pending async read holds no thread: with many sessions parked in ReadAsync, the
    /// worker pool must not lose threads to them, and canceling every one completes
    /// immediately. (The regression this guards against — FileStream offloading blocking
    /// reads to pool threads — would drain ~one thread per session.)
    /// </summary>
    [Fact]
    public async Task PendingReads_DoNotConsumeThreadPoolAndCancelImmediately()
    {
        const int sessions = 32;
        var all = new PtyProcess[sessions];
        var reads = new Task<int>[sessions];
        var cts = new CancellationTokenSource[sessions];

        for (var i = 0; i < sessions; i++)
        {
            all[i] = TestBash.Start();
            // Drain the startup banner so the session is truly idle; a read parked here
            // must stay pending (the whole point of the test) instead of consuming it.
            TestBash.ReadUntil(all[i].Output, "$", Timeout);
            cts[i] = new CancellationTokenSource();
            reads[i] = all[i].BaseStream.ReadAsync(new byte[16], cts[i].Token).AsTask();
        }

        // Give any thread-pool offloading a chance to manifest. Baseline AFTER the
        // settle, immediately before the window, so the comparison measures the same
        // pool state (this test runs in a serialized collection, so the only churn is
        // its own spawns). A real leak (one thread pinned per parked read) suppresses
        // every sample in the max window.
        await Task.Delay(300);
        ThreadPool.GetAvailableThreads(out var workersBefore, out _);
        var workersDuring = TestBash.MaxAvailableWorkers(TimeSpan.FromSeconds(1));

        // Cancel everything; every read must abort promptly.
        var cancelAll = Task.WhenAll(Enumerable.Range(0, sessions).Select(i => CancelAndExpectOce(reads[i], cts[i])));
        await cancelAll.WaitAsync(Timeout);

        foreach (var p in all)
            p.Dispose();

        Assert.True(
            workersDuring >= workersBefore - 4,
            $"Available worker threads collapsed from {workersBefore} to {workersDuring}: " +
            "pending pty reads must not occupy thread-pool threads.");
    }

    private static async Task CancelAndExpectOce(Task<int> read, CancellationTokenSource cts)
    {
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(Timeout);
    }
}

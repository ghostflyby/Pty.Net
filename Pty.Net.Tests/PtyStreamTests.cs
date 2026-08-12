using System.Diagnostics;
using System.Text;

namespace Ghostflyby.Pty.Tests;

/// <summary>
/// Exercises the async/byte-level surface of <see cref="PtyStream"/>: partial reads,
/// immediate cancellation, prompt EOF, and — the reason this type exists — that pending
/// operations never consume thread-pool threads.
/// </summary>
public class PtyStreamTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private const string Done = "__DONE__";

    private readonly PtyProcess bash = TestBash.Start();

    public void Dispose() => bash.Dispose();

    private PtyStream Stream => bash.BaseStream;

    /// <summary>Turns off the tty line-discipline echo so command output is not mixed with echoed input.</summary>
    private void DisableEcho()
    {
        bash.StandardInput.WriteLine("stty -echo");
        TestBash.Drain(bash.StandardOutput, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Waits for the shell prompt and drains it, leaving the session idle.</summary>
    private void WaitForIdlePrompt()
    {
        TestBash.ReadUntil(bash.StandardOutput, "$", Timeout);
        TestBash.Drain(bash.StandardOutput, TimeSpan.FromMilliseconds(50));
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

        ThreadPool.GetAvailableThreads(out var workersBefore, out _);
        for (var i = 0; i < sessions; i++)
        {
            all[i] = TestBash.Start();
            // Drain the startup banner so the session is truly idle; a read parked here
            // must stay pending (the whole point of the test) instead of consuming it.
            TestBash.ReadUntil(all[i].StandardOutput, "$", Timeout);
            cts[i] = new CancellationTokenSource();
            reads[i] = all[i].BaseStream.ReadAsync(new byte[16], cts[i].Token).AsTask();
        }

        // Give any thread-pool offloading a chance to manifest, then measure the damage.
        await Task.Delay(300);
        ThreadPool.GetAvailableThreads(out var workersDuring, out _);

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

    /// <summary>Reading returns whatever is available promptly — not necessarily a full buffer.</summary>
    [Fact]
    public async Task ReadAsync_ReturnsAvailableBytesNotFullBuffer()
    {
        DisableEcho();
        bash.StandardInput.WriteLine($"printf 'short'; echo {Done}");

        var buf = new byte[1024];
        var n = await Stream.ReadAsync(buf).AsTask().WaitAsync(Timeout);

        // Only ~30 bytes were produced; a stream that waited for a full 1024-byte buffer
        // (blocking-read semantics) could never return this fast with this much data.
        Assert.InRange(n, 1, 64);
    }

    /// <summary>Data that is already available is read promptly even when a token is passed.</summary>
    [Fact]
    public async Task ReadAsync_ReadsDataThatWasAlreadyAvailable()
    {
        DisableEcho();
        bash.StandardInput.WriteLine($"printf 'ALREADY-THERE'; echo {Done}");
        await Task.Delay(100); // let the echo land in the pty buffer before reading

        using var cts = new CancellationTokenSource();
        var buf = new byte[256];
        var n = await Stream.ReadAsync(buf, cts.Token).AsTask().WaitAsync(Timeout);

        Assert.True(n > 0);
        Assert.Contains("ALREADY-THERE", Encoding.UTF8.GetString(buf, 0, n));
    }

    /// <summary>Cancellation of a pending read returns immediately (there is no thread to unpark).</summary>
    [Fact]
    public async Task ReadAsync_PendingReadCancelsImmediately()
    {
        WaitForIdlePrompt();

        Task<int> read;
        using var cts = new CancellationTokenSource();
        while (true)
        {
            read = Stream.ReadAsync(new byte[16], cts.Token).AsTask();
            await Task.Delay(50); // let it register as the pending operation
            if (!read.IsCompleted)
                break;
            await read; // a startup straggler landed first: drain it and re-arm
        }

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read).WaitAsync(Timeout);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"cancel took {sw.Elapsed}");
    }

    /// <summary>After the child exits, pending reads eventually complete with 0 (EOF), not an error or a hang.</summary>
    [Fact]
    public async Task ReadAsync_CompletesWithZeroAfterChildExit()
    {
        WaitForIdlePrompt();
        using var cts = new CancellationTokenSource();
        var read = Stream.ReadAsync(new byte[16], cts.Token).AsTask();

        bash.StandardInput.WriteLine("exit");

        // Partial reads may surface leftover output first; keep reading until EOF (0).
        var n = await read.WaitAsync(Timeout);
        while (n > 0)
            n = await Stream.ReadAsync(new byte[16], cts.Token).AsTask().WaitAsync(Timeout);

        Assert.Equal(0, n);
    }

    /// <summary>Synchronous read also surfaces EOF promptly instead of blocking forever.</summary>
    [Fact]
    public void Read_ReturnsZeroAfterChildExit()
    {
        WaitForIdlePrompt();
        bash.StandardInput.WriteLine("exit");
        bash.WaitForExit(Timeout);

        var n = Stream.Read(new byte[16]);
        Assert.Equal(0, n);
    }

    /// <summary>An async write through a real reader (cat echoing the input back) completes.</summary>
    [Fact]
    public async Task WriteAsync_Completes()
    {
        DisableEcho();
        bash.StandardInput.WriteLine($"cat; echo {Done}");
        TestBash.Drain(bash.StandardOutput, TimeSpan.FromMilliseconds(500)); // let cat start reading

        using var cts = new CancellationTokenSource();
        await bash.StandardInput.WriteAsync("hello-async-write\n".AsMemory(), cts.Token).WaitAsync(Timeout);
        await bash.StandardInput.WriteAsync("\x04".AsMemory(), default); // EOT ends cat

        var output = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);
        Assert.Contains("hello-async-write", output);
    }

    /// <summary>
    /// When the child stops reading its stdin, a big write fills the pty buffer and blocks;
    /// canceling it throws immediately (a partial advance is acceptable).
    /// The pty is put into non-canonical mode first, because canonical mode discards excess
    /// input instead of applying backpressure, which would let the write complete.
    /// Unix-only: it relies on stty termios (non-canonical mode). ConPTY's input queue is
    /// not termios-controlled, so this particular backpressure setup does not hold there.
    /// </summary>
#if !WINDOWS
    [Fact]
    public async Task WriteAsync_BlockedOnFullPtyBuffer_CancelsImmediately()
    {
        WaitForIdlePrompt();
        bash.StandardInput.WriteLine("stty -icanon min 1 time 0"); // non-canonical: bounded input queue
        TestBash.Drain(bash.StandardOutput, TimeSpan.FromMilliseconds(300));
        bash.StandardInput.WriteLine("exec sleep 1000"); // child stops reading stdin
        await Task.Delay(300);

        using var cts = new CancellationTokenSource();
        var big = new byte[1024 * 1024];
        Array.Fill(big, (byte)'z');
        var write = Stream.WriteAsync(big, cts.Token).AsTask();

        // Give the write a moment to fill the buffer and register as pending.
        await Task.Delay(300);
        Assert.False(write.IsCompleted);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write).WaitAsync(Timeout);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"cancel took {sw.Elapsed}");
    }
#endif

    /// <summary>Overlapping async reads on the same stream serialize instead of corrupting each other.</summary>
    [Fact]
    public async Task ConcurrentReadsOnOneStream_AllComplete()
    {
        DisableEcho();
        // 8 overlapping reads, 64 bytes each; the child then produces enough output for all.
        var reads = Enumerable.Range(0, 8)
            .Select(_ => Stream.ReadAsync(new byte[64], default).AsTask())
            .ToArray();
        await Task.Delay(200); // let them all register before output flows

        bash.StandardInput.WriteLine($"seq 1 500; echo {Done}"); // ~2 KB of output

        var results = await Task.WhenAll(reads).WaitAsync(Timeout);
        Assert.All(results, n => Assert.True(n > 0, "every overlapping read should get data"));
    }

    /// <summary>
    /// A pending read must not block a concurrent write on the same stream. The engine's
    /// per-(fd, direction) operation queue keeps reads and writes independent; a read
    /// parked waiting for POLLIN must not block a write waiting for POLLOUT.
    /// </summary>
    [Fact]
    public async Task PendingRead_DoesNotBlockConcurrentWrite()
    {
        WaitForIdlePrompt();
        DisableEcho();

        // Park a read with no data coming (bash is idle at the prompt).
        var readBuf = new byte[64];
        Task<int> read;
        while (true)
        {
            read = Stream.ReadAsync(readBuf, default).AsTask();
            await Task.Delay(100); // let it register as the pending operation
            if (!read.IsCompleted)
                break;
            await read; // stray output landed first: drain and re-arm so it is truly pending
        }

        // Now write: must dispatch on POLLOUT even while the read is pending.
        var write = Stream.WriteAsync(Encoding.UTF8.GetBytes("echo I-GOT-THIS\n"), default).AsTask();

        // The deadlock this guards against would hang both forever; WaitAsync turns it
        // into a prompt failure.
        await write.WaitAsync(Timeout);
        var n = await read.WaitAsync(Timeout);

        Assert.True(n > 0);
        Assert.Contains("I-GOT-THIS", Encoding.UTF8.GetString(readBuf, 0, n));
    }
}

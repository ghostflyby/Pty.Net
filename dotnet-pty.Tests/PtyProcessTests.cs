namespace dotnet_pty.Tests;

public class PtyProcessTests : IDisposable
{
    private const string Done = "__DONE__";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly PtyProcess bash = PtyProcess.StartBash();

    public void Dispose() => bash.Dispose();

    [Fact]
    public void StartsInteractiveBash_ShowsPrompt()
    {
        var output = bash.ReadUntil("$", Timeout);

        Assert.Contains("$", output);
    }

    [Fact]
    public void RunsSingleCommand_ReturnsOutput()
    {
        DisableEcho();

        bash.Write($"echo hello-from-pty; echo {Done}\n");
        var output = bash.ReadUntil(Done, Timeout);

        Assert.Contains("hello-from-pty", output);
    }

    [Fact]
    public void RunsMultipleCommands_CollectsEachOutput()
    {
        DisableEcho();

        bash.Write($"echo one-AAAA; echo {Done}\n");
        var one = bash.ReadUntil(Done, Timeout);

        bash.Write($"echo two-BBBB; echo {Done}\n");
        var two = bash.ReadUntil(Done, Timeout);

        bash.Write($"echo three-CCCC; echo {Done}\n");
        var three = bash.ReadUntil(Done, Timeout);

        Assert.Contains("one-AAAA", one);
        Assert.Contains("two-BBBB", two);
        Assert.Contains("three-CCCC", three);
    }

    [Fact]
    public void SessionSurvivesAcrossCommands_StatePersists()
    {
        DisableEcho();

        // Variables set by one command are still visible to the next one:
        // proof we are talking to the same interactive shell session.
        bash.Write("ANSWER=42; echo set-AAAA; echo " + Done + "\n");
        Assert.Contains("set-AAAA", bash.ReadUntil(Done, Timeout));

        bash.Write($"echo the-answer-is-$ANSWER; echo {Done}\n");
        var output = bash.ReadUntil(Done, Timeout);

        Assert.Contains("the-answer-is-42", output);
    }

    [Fact]
    public void Exit_TerminatesWithCodeZero()
    {
        // Wait until bash is fully up (prompt visible) before sending exit,
        // otherwise the early input can be lost during startup.
        bash.ReadUntil("$", Timeout);
        bash.Write("exit\n");

        Assert.True(bash.WaitForExit(Timeout));
        Assert.Equal(0, bash.ExitCode);
    }

    [Fact]
    public void StartsInSpecifiedWorkingDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pty-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var bash = PtyProcess.StartBash(dir);
            bash.Write($"pwd; echo {Done}\n");
            var output = bash.ReadUntil(Done, Timeout);

            Assert.Contains(dir, output);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void JobControl_BackgroundAndForegroundJobsWork()
    {
        DisableEcho();

        // Monitor (job control) must be on: this is the functional difference between a
        // proper pty session leader and a plain subprocess. It requires the child to have
        // a session with a controlling terminal (POSIX_SPAWN_SETSID = 0x0400 on macOS).
        bash.Write("set +H; echo __DONE__\n"); // disable history expansion for the $! below
        bash.ReadUntil("__DONE__", Timeout);

        // Background job reports its pid and is listed by jobs -l.
        bash.Write("sleep 0.5 & echo JOBPID=$!; echo __DONE__\n");
        var bg = bash.ReadUntil("__DONE__", Timeout);
        Assert.Contains("[1]", bg);
        Assert.Contains("JOBPID=", bg);

        bash.Write("jobs -l; echo __DONE__\n");
        var jobs = bash.ReadUntil("__DONE__", Timeout);
        Assert.Contains("[1]", jobs);
        Assert.Contains("sleep 0.5", jobs);
    }

    // --- async convenience methods ------------------------------------------

    [Fact]
    public async Task WriteAsync_And_ReadUntilAsync_RoundTrip()
    {
        DisableEcho();

        await bash.WriteAsync($"echo async-AAAA; echo {Done}\n", default);
        var output = await bash.ReadUntilAsync(Done, Timeout, default);

        Assert.Contains("async-AAAA", output);
    }

    [Fact]
    public async Task ReadUntilAsync_Timeout_ThrowsTimeoutException()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => bash.ReadUntilAsync("never-appears", TimeSpan.FromMilliseconds(300), default));
        Assert.Contains("never-appears", ex.Message);
    }

    [Fact]
    public async Task ReadUntilAsync_Cancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var task = bash.ReadUntilAsync("never-appears", Timeout, cts.Token);

        await Task.Delay(100);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(Timeout);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"cancel took {sw.Elapsed}");
    }

    [Fact]
    public async Task ReadUntilAsync_ReturnsEarlyWhenChildExits()
    {
        bash.ReadUntil("$", Timeout);
        bash.Write("exit\n");

        // Returns on EOF (child gone) instead of blocking until the timeout.
        var output = await bash.ReadUntilAsync("never-appears", Timeout, default);
        Assert.NotNull(output);

        Assert.True(bash.WaitForExit(Timeout)); // confirm the child actually exited
    }

    /// <summary>
    /// Turns off the tty line-discipline echo so command output is not mixed with
    /// the echoed input, then waits for the change to take effect.
    /// </summary>
    private void DisableEcho()
    {
        bash.Write("stty -echo\n");
        bash.ReadAvailable(TimeSpan.FromMilliseconds(200));
    }
}

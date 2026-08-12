namespace Ghostflyby.Pty.Tests;

using System.Text;

public class PtyProcessTests : IDisposable
{
    private const string Done = "__DONE__";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly PtyProcess bash = TestBash.Start();

    public void Dispose() => bash.Dispose();

    [Fact]
    public void StartsInteractiveBash_ShowsPrompt()
    {
        var output = TestBash.ReadUntil(bash.StandardOutput, "$", Timeout);

        Assert.Contains("$", output);
    }

    [Fact]
    public void RunsSingleCommand_ReturnsOutput()
    {
        DisableEcho();

        bash.StandardInput.WriteLine($"echo hello-from-pty; echo {Done}");
        var output = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

        Assert.Contains("hello-from-pty", output);
    }

    [Fact]
    public void RunsMultipleCommands_CollectsEachOutput()
    {
        DisableEcho();

        bash.StandardInput.WriteLine($"echo one-AAAA; echo {Done}");
        var one = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

        bash.StandardInput.WriteLine($"echo two-BBBB; echo {Done}");
        var two = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

        bash.StandardInput.WriteLine($"echo three-CCCC; echo {Done}");
        var three = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

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
        bash.StandardInput.WriteLine($"ANSWER=42; echo set-AAAA; echo {Done}");
        Assert.Contains("set-AAAA", TestBash.ReadUntil(bash.StandardOutput, Done, Timeout));

        bash.StandardInput.WriteLine($"echo the-answer-is-$ANSWER; echo {Done}");
        var output = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

        Assert.Contains("the-answer-is-42", output);
    }

    [Fact]
    public void Exit_TerminatesWithCodeZero()
    {
        // Wait until bash is fully up (prompt visible) before sending exit,
        // otherwise the early input can be lost during startup.
        TestBash.ReadUntil(bash.StandardOutput, "$", Timeout);
        bash.StandardInput.WriteLine("exit");

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
            using var bash = TestBash.Start(dir);
            // Echo off so the command's own text does not shadow the marker read.
            bash.StandardInput.WriteLine("stty -echo");
            TestBash.Drain(bash.StandardOutput, TimeSpan.FromMilliseconds(200));

#if WINDOWS
            // MSYS bash prints POSIX-style paths (/c/...) for pwd; cygpath -w converts
            // back to the Windows path the test compares against.
            bash.StandardInput.WriteLine($"cygpath -w \"$(pwd)\"; echo {Done}");
#else
            bash.StandardInput.WriteLine($"pwd; echo {Done}");
#endif
            var output = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

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
        bash.StandardInput.WriteLine($"set +H; echo {Done}"); // disable history expansion for the $! below
        TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

        // Background job reports its pid and is listed by jobs -l.
        bash.StandardInput.WriteLine($"sleep 0.5 & echo JOBPID=$!; echo {Done}");
        var bg = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);
        Assert.Contains("[1]", bg);
        Assert.Contains("JOBPID=", bg);

        bash.StandardInput.WriteLine($"jobs -l; echo {Done}");
        var jobs = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);
        Assert.Contains("[1]", jobs);
        Assert.Contains("sleep 0.5", jobs);
    }

    // --- window size -------------------------------------------------------

    /// <summary>
    /// The initial size from <see cref="PtyStartInfo"/> (default 120x30) is applied before
    /// the child starts, and <see cref="PtyProcess.Resize"/> propagates to the child:
    /// bash reads the pty's size and `stty size` reports it back.
    /// </summary>
    [Fact]
    public void Resize_ChildSeesNewWindowSize()
    {
        DisableEcho();

        bash.StandardInput.WriteLine($"stty size; echo {Done}");
        var initial = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);
        // "rows cols" — the StartInfo default is 120 columns x 30 rows.
        Assert.Matches(@"\b30\s+120\b", initial);

        bash.Resize(80, 24);

        bash.StandardInput.WriteLine($"stty size; echo {Done}");
        var resized = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);
        Assert.Matches(@"\b24\s+80\b", resized);
    }

#if !WINDOWS
    /// <summary>
    /// Resize on Unix is TIOCSWINSZ, which also delivers SIGWINCH to the child's
    /// foreground process group: a trap fires without the child having to read anything.
    /// Windows has no SIGWINCH (ConPTY propagates size through its own mechanism).
    /// </summary>
    [Fact]
    public void Resize_SendsSigwinch()
    {
        DisableEcho();

        // Ready marker proves the trap is installed before we resize.
        bash.StandardInput.WriteLine($"trap 'echo WINCH-AAAA' WINCH; echo ready-BBBB; echo {Done}");
        TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

        bash.Resize(100, 40);

        // The trap fires asynchronously; wait for its marker, then confirm the size too.
        var output = TestBash.ReadUntil(bash.StandardOutput, "WINCH-AAAA", Timeout);
        Assert.Contains("WINCH-AAAA", output);

        bash.StandardInput.WriteLine($"stty size; echo {Done}");
        Assert.Matches(@"\b40\s+100\b", TestBash.ReadUntil(bash.StandardOutput, Done, Timeout));
    }
#endif

    // --- async surface -----------------------------------------------------

    [Fact]
    public async Task WriteAsync_RoundTrip()
    {
        DisableEcho();

        await bash.StandardInput.WriteLineAsync($"echo async-AAAA; echo {Done}");
        var output = TestBash.ReadUntil(bash.StandardOutput, Done, Timeout);

        Assert.Contains("async-AAAA", output);
    }

    [Fact]
    public void ReadUntil_ReturnsEarlyWhenChildExits()
    {
        bash.StandardInput.WriteLine("exit");

        // The reader hits EOF and returns what was read instead of hanging until the timeout.
        var output = TestBash.ReadUntil(bash.StandardOutput, "never-appears", Timeout);
        Assert.False(string.IsNullOrEmpty(output));
    }

    [Fact]
    public void Kill_TerminatesChildWithSignal()
    {
        // A bare `sleep` has no reason to exit on its own; Kill must bring it down.
        var (file, args) = TestBash.SleepProcess(100);
        using var p = PtyProcess.Start(file, args);

        Assert.False(p.HasExited);
        p.Kill();

        Assert.True(p.WaitForExit(Timeout));
#if WINDOWS
        // ConPTY has no signals: Kill is TerminateProcess (exit code 1), not 128+SIGKILL.
        Assert.Equal(1, p.ExitCode);
#else
        // Killed by SIGKILL (9): exit code is 128 + 9.
        Assert.Equal(137, p.ExitCode);
#endif
    }

    [Fact]
    public void WaitForExit_NoTimeout_ReturnsAfterChildExits()
    {
        bash.StandardInput.WriteLine("exit");

        bash.WaitForExit(); // must not throw and must return once the child is reaped

        Assert.True(bash.HasExited);
    }

    /// <summary>
    /// Latin-1 I/O round-trip: a non-UTF-8 payload written via StandardInput must be
    /// encoded as configured, and the child's output must be decoded as configured
    /// (é is 0xE9 in Latin-1 but 0xC3 0xA9 in UTF-8, so a wrong encoding garbles it).
    /// </summary>
    [Fact]
    public void Encoding_ConfiguredViaStartInfo()
    {
        var latin1 = Encoding.Latin1; // ISO-8859-1
        using var p = PtyProcess.Start(new PtyStartInfo
        {
            FileName = TestBash.BashPath,
            ArgumentList = [ "--noprofile", "--norc", "--noediting", "-i" ],
            StandardInputEncoding = latin1,
            StandardOutputEncoding = latin1,
        });

        p.StandardInput.WriteLine("stty -echo");
        TestBash.Drain(p.StandardOutput, TimeSpan.FromMilliseconds(200));

        // 0xE9 is a single byte in Latin-1; if the writer used UTF-8 it would send
        // two bytes and bash would see a garbled command, and the marker never shows.
        p.StandardInput.WriteLine($"echo caf\u00e9; echo {Done}");
        var output = TestBash.ReadUntil(p.StandardOutput, Done, Timeout);

        Assert.Contains("caf\u00e9", output);
    }

    /// <summary>
    /// Turns off the tty line-discipline echo so command output is not mixed with the
    /// echoed input, and waits for the change to take effect (echo off is what keeps a
    /// command's own literal text out of the output, which otherwise short-circuits
    /// marker reads).
    /// </summary>
    private void DisableEcho()
    {
        bash.StandardInput.WriteLine("stty -echo");
        TestBash.Drain(bash.StandardOutput, TimeSpan.FromMilliseconds(200));
    }
}

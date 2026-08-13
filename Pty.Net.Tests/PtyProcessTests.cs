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
        var output = TestBash.ReadUntil(bash.Output, "$", Timeout);

        Assert.Contains("$", output);
    }

    [Fact]
    public void RunsSingleCommand_ReturnsOutput()
    {
        DisableEcho();

        bash.Input.WriteLine($"echo hello-from-pty; echo {Done}");
        var output = TestBash.ReadUntil(bash.Output, Done, Timeout);

        Assert.Contains("hello-from-pty", output);
    }

    [Fact]
    public void RunsMultipleCommands_CollectsEachOutput()
    {
        DisableEcho();

        bash.Input.WriteLine($"echo one-AAAA; echo {Done}");
        var one = TestBash.ReadUntil(bash.Output, Done, Timeout);

        bash.Input.WriteLine($"echo two-BBBB; echo {Done}");
        var two = TestBash.ReadUntil(bash.Output, Done, Timeout);

        bash.Input.WriteLine($"echo three-CCCC; echo {Done}");
        var three = TestBash.ReadUntil(bash.Output, Done, Timeout);

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
        bash.Input.WriteLine($"ANSWER=42; echo set-AAAA; echo {Done}");
        Assert.Contains("set-AAAA", TestBash.ReadUntil(bash.Output, Done, Timeout));

        bash.Input.WriteLine($"echo the-answer-is-$ANSWER; echo {Done}");
        var output = TestBash.ReadUntil(bash.Output, Done, Timeout);

        Assert.Contains("the-answer-is-42", output);
    }

    [Fact]
    public void Exit_TerminatesWithCodeZero()
    {
        // Wait until bash is fully up (prompt visible) before sending exit,
        // otherwise the early input can be lost during startup.
        TestBash.ReadUntil(bash.Output, "$", Timeout);
        bash.Input.WriteLine("exit");

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
            bash.Input.WriteLine("stty -echo");
            TestBash.Drain(bash.Output, TimeSpan.FromMilliseconds(200));

#if WINDOWS
            // MSYS bash prints POSIX-style paths (/c/...) for pwd; cygpath -w converts
            // back to the Windows path the test compares against.
            bash.Input.WriteLine($"cygpath -w \"$(pwd)\"; echo {Done}");
#else
            bash.Input.WriteLine($"pwd; echo {Done}");
#endif
            var output = TestBash.ReadUntil(bash.Output, Done, Timeout);

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
        bash.Input.WriteLine($"set +H; echo {Done}"); // disable history expansion for the $! below
        TestBash.ReadUntil(bash.Output, Done, Timeout);

        // Background job reports its pid and is listed by jobs -l.
        bash.Input.WriteLine($"sleep 0.5 & echo JOBPID=$!; echo {Done}");
        var bg = TestBash.ReadUntil(bash.Output, Done, Timeout);
        Assert.Contains("[1]", bg);
        Assert.Contains("JOBPID=", bg);

        bash.Input.WriteLine($"jobs -l; echo {Done}");
        var jobs = TestBash.ReadUntil(bash.Output, Done, Timeout);
        Assert.Contains("[1]", jobs);
        Assert.Contains("sleep 0.5", jobs);
    }

    // --- window size -------------------------------------------------------

#if !WINDOWS
    /// <summary>
    /// The initial size from <see cref="PtyStartInfo"/> (default 120x30) is applied before
    /// the child starts, and <see cref="PtyProcess.Resize"/> propagates to the child:
    /// bash reads the pty's size and `stty size` reports it back. The Windows path is
    /// covered by PtyWindowsTests.Resize_PropagatesThroughConPty — Git Bash's stty size
    /// is unreliable under ConPTY.
    /// </summary>
    [Fact]
    public void Resize_ChildSeesNewWindowSize()
    {
        DisableEcho();

        bash.Input.WriteLine($"stty size; echo {Done}");
        var initial = TestBash.ReadUntil(bash.Output, Done, Timeout);
        // "rows cols" — the StartInfo default is 120 columns x 30 rows.
        Assert.Matches(@"\b30\s+120\b", initial);

        bash.Resize(80, 24);

        bash.Input.WriteLine($"stty size; echo {Done}");
        var resized = TestBash.ReadUntil(bash.Output, Done, Timeout);
        Assert.Matches(@"\b24\s+80\b", resized);
    }
#endif

#if OSX
    /// <summary>
    /// Resize on Unix is TIOCSWINSZ, which also delivers SIGWINCH to the child's
    /// foreground process group: a trap fires without the child having to read anything.
    /// macOS-only: on Linux the size change reaches the child (stty size readback works)
    /// but SIGWINCH delivery to the interactive bash session is not reliable enough for a
    /// test gate, so the Linux side asserts the size propagation only.
    /// </summary>
    [Fact]
    public void Resize_SendsSigwinch()
    {
        DisableEcho();

        // Ready marker proves the trap is installed before we resize.
        bash.Input.WriteLine($"trap 'echo WINCH-AAAA' WINCH; echo ready-BBBB; echo {Done}");
        TestBash.ReadUntil(bash.Output, Done, Timeout);

        bash.Resize(100, 40);

        // The trap fires asynchronously; wait for its marker, then confirm the size too.
        var output = TestBash.ReadUntil(bash.Output, "WINCH-AAAA", Timeout);
        Assert.Contains("WINCH-AAAA", output);

        bash.Input.WriteLine($"stty size; echo {Done}");
        Assert.Matches(@"\b40\s+100\b", TestBash.ReadUntil(bash.Output, Done, Timeout));
    }
#endif

    // --- async surface -----------------------------------------------------

    [Fact]
    public async Task WriteAsync_RoundTrip()
    {
        DisableEcho();

        await bash.Input.WriteLineAsync($"echo async-AAAA; echo {Done}");
        var output = TestBash.ReadUntil(bash.Output, Done, Timeout);

        Assert.Contains("async-AAAA", output);
    }

    [Fact]
    public void ReadUntil_ReturnsEarlyWhenChildExits()
    {
        bash.Input.WriteLine("exit");

        // The reader hits EOF and returns what was read instead of hanging until the timeout.
        var output = TestBash.ReadUntil(bash.Output, "never-appears", Timeout);
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

    /// <summary>
    /// <see cref="PtyProcess.Interrupt"/> delivers Ctrl-C to the foreground process
    /// group: the interactive bash's foreground sleep is interrupted and the shell
    /// returns to its prompt, still alive. Unix-only — ConPTY's 0x03 forwarding is best
    /// effort, so the outcome is not asserted on Windows.
    /// </summary>
#if !WINDOWS
    [Fact]
    public async Task Interrupt_StopsForegroundProcess_ShellSurvives()
    {
        using var p = TestBash.Start();
        TestBash.ReadUntil(p.Output, "$", Timeout); // drain the startup prompt

        // Marker before the long-running foreground job: its output proves the job has
        // started (0x03 sent too early would hit the shell before exec and be ignored).
        p.Input.WriteLine("echo BEFORE; sleep 30; echo AFTER");
        TestBash.ReadUntil(p.Output, "BEFORE", Timeout);
        await Task.Delay(200); // let the sleep finish exec'ing
        p.Interrupt();         // Ctrl-C to the foreground group

        // The interrupted sleep never reaches `echo AFTER`; the shell prints a fresh
        // prompt once the job is reaped, and the shell itself must have survived
        // (HasExited false) — Interrupt does not terminate the session.
        TestBash.ReadUntil(p.Output, "$", Timeout);
        Assert.False(p.HasExited);
    }
#endif

    /// <summary>
    /// <see cref="PtyProcess.HangUp"/> sends SIGHUP, the terminal-hangup signal: an
    /// interactive shell exits cleanly instead of being force-killed. Unix-only — ConPTY
    /// has no terminal signal, so HangUp is a no-op on Windows.
    /// </summary>
#if !WINDOWS
    [Fact]
    public void HangUp_CausesShellToExit()
    {
        using var p = TestBash.Start();
        Assert.False(p.HasExited);

        p.HangUp();

        // SIGHUP lets the shell run its exit path; it must exit on its own.
        Assert.True(p.WaitForExit(Timeout));
    }
#endif

    [Fact]
    public void WaitForExit_NoTimeout_ReturnsAfterChildExits()
    {
        bash.Input.WriteLine("exit");

        bash.WaitForExit(); // must not throw and must return once the child is reaped

        Assert.True(bash.HasExited);
    }

    /// <summary>
    /// Latin-1 I/O round-trip: a non-UTF-8 payload written via <see cref="PtyProcess.Input"/> must be
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
            Arguments = ["--noprofile", "--norc", "--noediting", "-i"],
            InputEncoding = latin1,
            OutputEncoding = latin1,
        });

        p.Input.WriteLine("stty -echo");
        TestBash.Drain(p.Output, TimeSpan.FromMilliseconds(200));

        // 0xE9 is a single byte in Latin-1; if the writer used UTF-8 it would send
        // two bytes and bash would see a garbled command, and the marker never shows.
        p.Input.WriteLine($"echo caf\u00e9; echo {Done}");
        var output = TestBash.ReadUntil(p.Output, Done, Timeout);

        Assert.Contains("caf\u00e9", output);
    }

    /// <summary>
    /// Turns off the tty line-discipline echo so command output is not mixed with the
    /// echoed input, and waits for the change to take effect.
    /// (Echo off is what keeps a command's own literal text out of the output, which
    /// otherwise short-circuits marker reads.)
    /// </summary>
    private void DisableEcho()
    {
        bash.Input.WriteLine("stty -echo");
        TestBash.Drain(bash.Output, TimeSpan.FromMilliseconds(200));
    }
}

namespace Ghostflyby.Pty.Tests;

using System.Collections.Immutable;
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

    /// <summary>
    /// The child must be a session leader with the pty as its controlling terminal: that
    /// is what makes stdin a real terminal (isatty), and what makes the foreground process
    /// group equal to the shell's own group (tcgetpgrp). Inheriting a parent-opened fd
    /// never acquires a controlling terminal. Unix-only; ConPTY has no POSIX
    /// session/foreground-group semantics.
    /// </summary>
    /// <summary>
    /// The kernel-level controlling-terminal check, with no external dependencies: a
    /// process that has a ctty can open /dev/tty; without one the open fails with
    /// ENXIO. This is the hard Unix guarantee that every launch shape must satisfy —
    /// <see cref="Child_IsSessionLeaderWithControllingTerminal"/> adds the
    /// tcgetpgrp precision where python3 is available.
    /// </summary>
#if !WINDOWS
    [Fact]
    public void Shell_ChildOpensDevTty()
    {
        // The fixture bash IS the child under test: it is a session leader whose
        // stdio is the pty slave, so opening /dev/tty must succeed.
        DisableEcho();
        bash.Input.WriteLine("if : </dev/tty 2>/dev/null; then echo CTTY_OK; else echo CTTY_MISSING; fi; echo __DEVTTY_DONE__");

        var output = TestBash.ReadUntil(bash.Output, "__DEVTTY_DONE__", Timeout);

        Assert.Contains("CTTY_OK", output);
        Assert.DoesNotContain("CTTY_MISSING", output);
    }

    [Fact]
    public void Child_IsSessionLeaderWithControllingTerminal()
    {
        var (file, args) = TryPythonProbe();
        if (file is null)
            return; // python3 unavailable on this host; CI installs it (see ci.yml)

        using var p = PtyProcess.Start(file, args);
        var output = TestBash.ReadUntil(p.Output, "__CTTY_DONE__", Timeout);

        // "isatty=True sid=<n> pid=<n> pgrp=<n> tcgetpgrp=<n>"
        Assert.Contains("isatty=True", output);
        Assert.Matches(@"sid=(\d+)\s+pid=\1", output);          // session leader
        Assert.Matches(@"pgrp=(\d+)\s+tcgetpgrp=\1", output);   // foreground on the ctty
    }

    /// <summary>
    /// End-to-end confirmation from the shell's perspective: tty(1) resolves fd 0 to a
    /// terminal device path ("not a tty" otherwise). The deep controlling-terminal
    /// semantics (isatty + tcgetpgrp) are asserted by Child_IsSessionLeaderWithControllingTerminal;
    /// this only checks that an interactive shell is wired to a real terminal device.
    /// </summary>
    [Fact]
    public void Shell_SeesItsControllingTerminal()
    {
        DisableEcho();
        bash.Input.WriteLine($"tty; echo {Done}");
        var output = TestBash.ReadUntil(bash.Output, Done, Timeout);

        Assert.Matches(@"(/dev/ttys\d+|/dev/pts/\d+)", output);
    }

    private static (string? File, string[] Args) TryPythonProbe()
    {
        // Probe for python3 without letting a missing interpreter fail the whole suite:
        // the library must work on hosts without python3, but the ctty assertion needs it.
        try
        {
            using var probe = PtyProcess.Start("python3", ["--version"]);
            if (!probe.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                probe.Kill();
                probe.WaitForExit(TimeSpan.FromSeconds(5));
                return (null, []);
            }
            return ("python3",
            [
                "-c",
                "import os; print(f'isatty={os.isatty(0)} sid={os.getsid(0)} pid={os.getpid()} pgrp={os.getpgrp()} tcgetpgrp={os.tcgetpgrp(0)}'); print('__CTTY_DONE__')",
            ]);
        }
        catch (FileNotFoundException)
        {
            return (null, []);
        }
    }
#endif

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
    /// <see cref="PtyProcess.RequestClose"/> asks the terminal session to close: SIGHUP
    /// on Unix lets an interactive shell exit cleanly instead of being force-killed.
    /// Unix-only — on Windows it starts the async pseudo-console close (CTRL_CLOSE_EVENT),
    /// which is covered by the ConPTY smoke tests.
    /// </summary>
#if !WINDOWS
    [Fact]
    public void RequestClose_CausesShellToExit()
    {
        using var p = TestBash.Start();
        Assert.False(p.HasExited);

        p.RequestClose();

        // SIGHUP lets the shell run its exit path; it must exit on its own.
        Assert.True(p.WaitForExit(Timeout));
    }
#endif

    /// <summary>
    /// Explicit environment overrides are merged into the parent environment at launch:
    /// the child sees the overridden variable. (The null-value removal path is exercised
    /// implicitly by the parent-inheritance default.)
    /// </summary>
    [Fact]
    public void Environment_OverridesAreVisibleToChild()
    {
        var info = new PtyStartInfo(TestBash.BashPath)
        {
            Arguments = ["--noprofile", "--norc", "-c", "echo $PTY_TEST_OVERRIDE"],
            Environment = ImmutableDictionary<string, string?>.Empty.Add("PTY_TEST_OVERRIDE", "visible-42"),
        };

        using var p = PtyProcess.Start(info);
        var output = TestBash.ReadUntil(p.Output, "visible-42", Timeout);

        Assert.Contains("visible-42", output);
    }

    /// <summary>
    /// Windows environment variables are case-insensitive: overriding "SYSTEMROOT"
    /// (upper-cased) must replace the inherited "SystemRoot" value instead of adding a
    /// second variable to the environment block, whose case-insensitive lookup by
    /// CreateProcess is then undefined. Unix-only counterpart is the case-sensitive
    /// <see cref="Environment_OverridesAreVisibleToChild"/>.
    /// </summary>
#if WINDOWS
    [Fact]
    public void Environment_OverrideIsCaseInsensitiveOnWindows()
    {
        // SystemRoot is always present on Windows; override its case-variant and read it
        // back through cmd.exe. The inherited value must be replaced, not duplicated.
        var info = new PtyStartInfo("cmd.exe")
        {
            Arguments = ["/c", "echo %SYSTEMROOT%; echo __DONE__"],
            Environment = ImmutableDictionary<string, string?>.Empty.Add("SYSTEMROOT", "CUSTOM-ROOT"),
        };

        using var p = PtyProcess.Start(info);
        var output = TestBash.ReadUntil(p.Output, "__DONE__", Timeout);

        Assert.Contains("CUSTOM-ROOT", output);
    }
#endif

    /// <summary>
    /// <see cref="PtyStartInfo.InheritParentEnvironment"/> = false turns the environment
    /// into an allowlist: the listed variable is visible, while a host variable that is
    /// deliberately absent (set on the parent, uniquely named, not listed) must not leak
    /// into the child.
    /// </summary>
    [Fact]
    public void Environment_AllowlistHidesHostVariables()
    {
        const string hostVar = "PTY_TEST_HOST_ONLY_8F2A";
        var old = Environment.GetEnvironmentVariable(hostVar);
        Environment.SetEnvironmentVariable(hostVar, "present");
        try
        {
            var info = new PtyStartInfo(TestBash.BashPath)
            {
                Arguments = ["--noprofile", "--norc", "-c",
                    "echo visible=$ALLOWLIST_VISIBLE host=$PTY_TEST_HOST_ONLY_8F2A; echo __DONE__"],
                Environment = ImmutableDictionary<string, string?>.Empty.Add("ALLOWLIST_VISIBLE", "yes"),
                InheritParentEnvironment = false,
            };

            using var p = PtyProcess.Start(info);
            var output = TestBash.ReadUntil(p.Output, "__DONE__", Timeout);

            Assert.Contains("visible=yes", output);
            Assert.DoesNotContain("host=present", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(hostVar, old);
        }
    }

    /// <summary>
    /// Allowlist mode is strict: the library injects nothing (not even TERM). A TERM
    /// the child sees is either listed explicitly or set by the shell itself — bash
    /// falls back to TERM=dumb on macOS and Git Bash to xterm-256color on Windows.
    /// </summary>
    [Fact]
    public void Environment_Allowlist_DoesNotInjectTerm()
    {
        // Explicit TERM passes through verbatim: allowlist is fully caller-controlled.
        // This is the cross-platform guarantee (the library never overrides or adds).
        var explicitInfo = new PtyStartInfo(TestBash.BashPath)
        {
            Arguments = ["--noprofile", "--norc", "-c", "echo TERM=[$TERM]; echo __DONE__"],
            Environment = ImmutableDictionary<string, string?>.Empty.Add("TERM", "custom-term-1"),
            InheritParentEnvironment = false,
        };
        using (var p = PtyProcess.Start(explicitInfo))
        {
            var output = TestBash.ReadUntil(p.Output, "__DONE__", Timeout);
            Assert.Contains("TERM=[custom-term-1]", output);
        }

        // Empty allowlist, Unix-only negative assertion: the library's old implicit
        // value was xterm-256color, and Unix shells fall back to anything but that
        // (macOS bash sets "dumb"). Windows Git Bash's own fallback IS xterm-256color,
        // so the assertion has no discriminative power there — and the Windows launch
        // path never had injection logic to begin with (BuildEnvironmentBlock only
        // serializes what the caller passed).
#if !WINDOWS
        var emptyInfo = new PtyStartInfo(TestBash.BashPath)
        {
            Arguments = ["--noprofile", "--norc", "-c", "echo TERM=[$TERM]; echo __DONE__"],
            Environment = ImmutableDictionary<string, string?>.Empty,
            InheritParentEnvironment = false,
        };
        using (var p = PtyProcess.Start(emptyInfo))
        {
            var output = TestBash.ReadUntil(p.Output, "__DONE__", Timeout);
            Assert.DoesNotContain("xterm-256color", output);
        }
#endif
    }

    /// <summary>
    /// A bare file name (no slash) is resolved through PATH on every platform:
    /// posix_spawnp on Unix, CreateProcess on Windows. Guards the regression where Unix
    /// used plain posix_spawn and threw FileNotFoundException for a name like "bash".
    /// Each platform uses a name that is reliably on PATH (cmd.exe lives in System32;
    /// "bash" on the Windows runner resolves to an unpredictable WSL/other bash).
    /// </summary>
    [Fact]
    public void Start_ResolvesBareFileNameViaPath()
    {
#if WINDOWS
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo spawnp-ok && echo __DONE__"]);
#else
        using var p = PtyProcess.Start("bash", ["--noprofile", "--norc", "-c", "echo spawnp-ok; echo __DONE__"]);
#endif
        var output = TestBash.ReadUntil(p.Output, "__DONE__", Timeout);

        Assert.Contains("spawnp-ok", output);
    }

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

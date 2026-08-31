#if WINDOWS
using Ghostflyby.Pty;
using System.Diagnostics;
using System.Text;

namespace Ghostflyby.Pty.Tests;

/// <summary>
/// Minimal ConPTY smoke tests: exercise the Windows launch path (CreatePseudoConsole +
/// CreateProcessW) end to end with cmd.exe and PowerShell — independent of Git Bash, so a
/// failure here isolates a ConPTY plumbing bug from a Git Bash-in-ConPTY quirk. The
/// portable bash-driven suite covers the interactive-session semantics.
/// </summary>
public class PtyWindowsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>cmd.exe echoes a command through the ConPTY channel.</summary>
    [Fact]
    public void Cmd_EchoesThroughConPty()
    {
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo hello-windows-pty & echo DONE_CMD"]);
        var output = TestBash.ReadUntil(p.Output, "DONE_CMD", Timeout);

        Assert.Contains("hello-windows-pty", output);
        Assert.True(p.WaitForExit(Timeout));
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>PowerShell output and a non-zero exit code round-trip through ConPTY.</summary>
    [Fact]
    public void PowerShell_OutputAndExitCodeRoundTrip()
    {
        using var p = PtyProcess.Start("powershell.exe", ["-NoProfile", "-Command", "Write-Output 'ps-AAAA'; Write-Output 'ps-DONE'; exit 3"]);
        var output = TestBash.ReadUntil(p.Output, "ps-DONE", Timeout);

        Assert.Contains("ps-AAAA", output);
        Assert.True(p.WaitForExit(Timeout));
        Assert.Equal(3, p.ExitCode);
    }

    /// <summary>
    /// Windows exit codes are full 32-bit values. 0x80000000 (e.g. STATUS_INTEGER_OVERFLOW
    /// bubbling out of a program) collides with the old int.MinValue liveness sentinel —
    /// with that encoding HasExited stayed false forever. ExitCode must surface the code
    /// and HasExited must be true.
    /// </summary>
    [Fact]
    public void ExitCode_Full32BitValue_IsPublished()
    {
        // PowerShell's exit binding is 32-bit; [int]::MinValue reaches the process exit
        // code as 0x80000000.
        using var p = PtyProcess.Start("powershell.exe", ["-NoProfile", "-Command", "exit [int]::MinValue"]);

        Assert.True(p.WaitForExit(Timeout), "process with exit code 0x80000000 must be observed as exited");
        Assert.True(p.HasExited);
        Assert.Equal(int.MinValue, p.ExitCode);
    }

    /// <summary>Empty-string arguments must survive command-line marshaling (as `""`).</summary>
    [Fact]
    public void Start_EmptyArgument_IsPreserved()
    {
        // cmd.exe: `echo` receives three arguments; the middle one is empty. If the
        // empty argument were dropped, the marker would print mangled.
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo a", "", "c & echo EMPTY_OK"]);
        var output = TestBash.ReadUntil(p.Output, "EMPTY_OK", Timeout);

        Assert.Contains("EMPTY_OK", output);
        Assert.True(p.WaitForExit(Timeout));
    }

    /// <summary>Sizes above short.MaxValue are rejected instead of wrapping the COORD fields negative.</summary>
    [Fact]
    public void Resize_RejectsDimensionsAboveShortMaxValue()
    {
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo OVERSIZE_DONE"]);

        Assert.Throws<ArgumentOutOfRangeException>(() => p.Resize(32768, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => p.Resize(30, 32768));
        TestBash.ReadUntil(p.Output, "OVERSIZE_DONE", Timeout);
    }

    /// <summary>StartInfo sizes above short.MaxValue are rejected at launch time.</summary>
    [Fact]
    public void Start_RejectsInitialSizeAboveShortMaxValue()
    {
        var info = new PtyStartInfo("cmd.exe") { Arguments = ["/c", "echo x"], Column = 32768 };
        Assert.Throws<ArgumentOutOfRangeException>(() => PtyProcess.Start(info));
    }

    /// <summary>Root exit closes ConPTY, preserves its final output, and publishes EOF.</summary>
    [Fact]
    public async Task RootExit_PreservesFinalOutputAndCompletesWithEof()
    {
        const int payloadLength = 1_250_000;
        var exitTimeout = TimeSpan.FromSeconds(20);
        using var p = PtyProcess.Start(
            "powershell.exe",
            ["-NoProfile", "-Command", $"$s='FINAL-' + ('x' * {payloadLength}) + '-FRAME'; [Console]::Out.Write($s)"]);

        // No output is consumed before this wait. The payload exceeds the pump's normal
        // bounded queue, so exit can complete only if the exit-wait lease lifts the bound.
        Assert.True(await p.WaitForExitAsync(exitTimeout).WaitAsync(exitTimeout));

        using var cts = new CancellationTokenSource(exitTimeout);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await p.BaseStream.ReadAsync(buffer, cts.Token);
            if (count == 0)
                break;
            bytes.Write(buffer, 0, count);
        }

        var output = Encoding.UTF8.GetString(bytes.ToArray());
        Assert.Contains("FINAL-", output);
        Assert.Contains("-FRAME", output);
        // ClosePseudoConsole re-renders the visible tail of the console screen buffer
        // as its final frame, which can overlap the tail of the streamed payload — the
        // pump never duplicates (a single producer, consumed in order), but the byte
        // count can exceed the payload. Assert the payload is fully present instead.
        Assert.True(output.Count(c => c == 'x') >= payloadLength, "final output must contain the full payload");
    }

    /// <summary>
    /// The initial size from <see cref="PtyStartInfo"/> (default 120x30) is applied when
    /// the pseudo console is created, and <see cref="PtyProcess.Resize"/> propagates
    /// through ResizePseudoConsole: the child's console API sees both sizes.
    /// </summary>
    [Fact]
    public void Resize_PropagatesThroughConPty()
    {
        using var p = PtyProcess.Start(
            "powershell.exe",
            ["-NoProfile", "-Command",
             "$h=[Console]::WindowHeight; $w=[Console]::WindowWidth; [Console]::Out.Write($w.ToString()+','+$h.ToString()+'|A'); Start-Sleep -Seconds 5; $h=[Console]::WindowHeight; $w=[Console]::WindowWidth; [Console]::Out.Write($w.ToString()+','+$h.ToString()+'|B')"]);

        // PowerShell cold start is the slow step here — it alone can exceed the class
        // Timeout of 10 s under CI load (the first read also covers ConPTY setup), so
        // this test waits with a longer budget than the smoke tests above.
        var readTimeout = TimeSpan.FromSeconds(30);
        var first = TestBash.ReadUntil(p.Output, "|A", readTimeout);
        Assert.Contains("120,30|A", first);

        p.Resize(80, 24);

        var second = TestBash.ReadUntil(p.Output, "|B", readTimeout);
        Assert.Contains("80,24|B", second);
    }

    /// <summary>Configured Latin-1 facades transcode to/from ConPTY's UTF-8 transport.</summary>
    [Fact]
    public void ConfiguredLatin1_TranscodesBothDirections()
    {
        using var p = PtyProcess.Start(new PtyStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
            [
                "-NoProfile",
                "-Command",
                "$line=[Console]::In.ReadLine(); [Console]::Out.WriteLine($line); [Console]::Out.Write('LATIN-DONE')",
            ],
            InputEncoding = Encoding.Latin1,
            OutputEncoding = Encoding.Latin1,
        });

        p.Input.WriteLine("caf\u00e9");
        var output = TestBash.ReadUntil(p.Output, "LATIN-DONE", Timeout);

        Assert.Contains("caf\u00e9", output);
        Assert.True(p.WaitForExit(Timeout));
    }

    /// <summary>
    /// Disposing a live child on Windows must not deadlock: the graceful step starts the
    /// async ClosePseudoConsole (which sends CTRL_CLOSE_EVENT), the grace window elapses,
    /// and the force-kill unblocks that close. This smoke test pins the timing so the
    /// shared dispose path stays correct on both platforms.
    /// </summary>
    [Fact]
    public void Dispose_LiveChild_CompletesWithoutHang()
    {
        var p = PtyProcess.Start("powershell.exe", ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"]);
        try
        {
            p.GracefulExitTimeout = TimeSpan.FromSeconds(1);
            var sw = Stopwatch.StartNew();
            p.Dispose();
            sw.Stop();

            // The 1 s grace window plus a comfortable scheduling margin; a hang in the
            // close/kill ordering would blow well past this.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"Dispose took {sw.Elapsed}");
            Assert.True(p.HasExited, "child must be reaped by dispose");
        }
        finally
        {
            p.Kill(); // no-op if already reaped; guards against a premature failure
        }
    }
}
#endif

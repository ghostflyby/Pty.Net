#if WINDOWS
using Ghostflyby.Pty;
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
}
#endif

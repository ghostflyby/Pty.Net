#if WINDOWS
using Ghostflyby.Pty;

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
        var output = TestBash.ReadUntil(p.StandardOutput, "DONE_CMD", Timeout);

        Assert.Contains("hello-windows-pty", output);
        Assert.True(p.WaitForExit(Timeout));
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>PowerShell output and a non-zero exit code round-trip through ConPTY.</summary>
    [Fact]
    public void PowerShell_OutputAndExitCodeRoundTrip()
    {
        using var p = PtyProcess.Start("powershell.exe", ["-NoProfile", "-Command", "Write-Output 'ps-AAAA'; Write-Output 'ps-DONE'; exit 3"]);
        var output = TestBash.ReadUntil(p.StandardOutput, "ps-DONE", Timeout);

        Assert.Contains("ps-AAAA", output);
        Assert.True(p.WaitForExit(Timeout));
        Assert.Equal(3, p.ExitCode);
    }
}
#endif

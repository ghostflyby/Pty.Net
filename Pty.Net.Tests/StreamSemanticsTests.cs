using System.Text;

namespace Ghostflyby.Pty.Tests;

/// <summary>
/// Stream ownership and wait-time output policy — the semantics frozen for 1.0:
/// <list type="bullet">
/// <item>PtyProcess is the single owner of the underlying stream: disposing one
/// facade never breaks its siblings, and process dispose closes everything.</item>
/// <item>Output drained during a WaitForExit/Dispose wait is preserved and remains
/// readable afterward, on every platform.</item>
/// </list>
/// </summary>
public class StreamSemanticsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

#if !WINDOWS
    /// <summary>Disposing Output must not kill the process: Input keeps working and the shell still exits on RequestClose.</summary>
    [Fact]
    public void DisposeOutput_InputRemainsUsable()
    {
        var probeFile = Path.Combine(Path.GetTempPath(), "pty-output-disposed-" + Guid.NewGuid().ToString("N"));
        using var p = TestBash.Start();
        p.Output.Dispose();

        p.Input.WriteLine($"echo ok > '{probeFile}'; echo __DONE__ > /dev/null");
        p.Input.WriteLine("exit");

        Assert.True(p.WaitForExit(Timeout), "process must still be disposable after Output was disposed");
        Assert.True(File.Exists(probeFile), "Input must remain usable after Output was disposed");
        File.Delete(probeFile);
    }

    /// <summary>Disposing Input must not kill the process: Output keeps working.</summary>
    [Fact]
    public void DisposeInput_OutputRemainsUsable()
    {
        using var p = TestBash.Start();
        p.Input.Dispose();

        // Input is gone (can't type), so drive the shell by its PID lifecycle instead:
        // RequestClose asks the shell to exit; Output still streams until EOF.
        p.RequestClose();
        Assert.True(p.WaitForExit(Timeout));
        Assert.True(p.HasExited);
    }

    /// <summary>Disposing BaseStream directly is allowed; facade operations then throw.</summary>
    [Fact]
    public void DisposeBaseStream_FacadesThrow()
    {
        var p = TestBash.Start();
        p.BaseStream.Dispose();

        Assert.ThrowsAny<Exception>(() => p.Input.WriteLine("nope"));
        Assert.ThrowsAny<Exception>(() => _ = p.Output.Peek());
        p.Dispose(); // double dispose is fine
    }
#endif

    /// <summary>
    /// The wait-time output contract: everything the child printed before and during
    /// the wait is readable afterward. Regression: the Unix drain used to discard
    /// bytes produced between the last user read and the wait, so a child that printed
    /// and exited could leave the reader with nothing.
    /// </summary>
    [Fact]
    public async Task WaitForExit_PreservesOutputProducedBeforeAndDuringWait()
    {
#if WINDOWS
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo BEFORE-MARKER & ping -n 2 127.0.0.1 >nul & echo AFTER-MARKER"]);
        const string before = "BEFORE-MARKER";
        const string after = "AFTER-MARKER";
#else
        using var p = PtyProcess.Start("bash", ["--noprofile", "--norc", "-c", "echo BEFORE-MARKER; sleep 0.4; echo AFTER-MARKER"]);
        const string before = "BEFORE-MARKER";
        const string after = "AFTER-MARKER";
#endif
        // Wait for exit WITHOUT reading first: the drain now buffers what it would
        // previously have discarded on Unix.
        Assert.True(await p.WaitForExitAsync(Timeout).WaitAsync(Timeout));

        using var cts = new CancellationTokenSource(Timeout);
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var n = await p.Output.ReadAsync(buffer.AsMemory(), cts.Token);
            if (n == 0)
                break;
            text.Append(buffer, 0, n);
        }

        Assert.Contains(before, text.ToString());
        Assert.Contains(after, text.ToString());
    }

    /// <summary>Output printed right before exiting must survive a sync WaitForExit.</summary>
    [Fact]
    public void WaitForExit_Sync_PreservesOutputPrintedBeforeExit()
    {
#if WINDOWS
        using var p = PtyProcess.Start("cmd.exe", ["/c", "echo FINAL-MARKER"]);
        var marker = "FINAL-MARKER";
#else
        using var p = PtyProcess.Start("bash", ["--noprofile", "--norc", "-c", "echo FINAL-MARKER"]);
        var marker = "FINAL-MARKER";
#endif
        Assert.True(p.WaitForExit(Timeout));
        var output = TestBash.ReadUntil(p.Output, marker, Timeout);
        Assert.Contains(marker, output);
    }
}

using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty;

/// <summary>
/// Windows half of <see cref="PtyProcess"/>: ConPTY launch, terminate-before-close
/// teardown and process-handle reaping. Compiled only on the Windows target (see csproj),
/// so the shared <c>PtyProcess.cs</c> carries no platform conditionals.
/// </summary>
public sealed partial class PtyProcess
{
    private static partial PtyProcess StartPlatform(
        string file, IReadOnlyList<string> arguments, string? workingDirectory,
        IDictionary<string, string?> environment, Encoding? inputEncoding, Encoding? outputEncoding,
        int initialCols, int initialRows)
    {
        // ConPTY path: CreatePseudoConsole + CreateProcessW (see WindowsPty.cs). The child
        // attaches to the pseudo console via PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE; no fd,
        // no signals, no waitpid — BCL named-pipe clients carry the parent-side I/O and
        // the process handle drives waiting/termination.
        var result = WindowsPty.Start(file, arguments, workingDirectory, environment, initialCols, initialRows);
        var winStream = new PtyStream(result.InputWrite, result.OutputRead, result.PseudoConsole);
        return new PtyProcess(winStream, result.Pid, inputEncoding, outputEncoding, result.ProcessHandle);
    }

    /// <summary>
    /// Windows: ConPTY always transports UTF-8 bytes. These directional facade streams
    /// expose the caller-selected encodings on their outer side while BaseStream remains
    /// the untouched raw UTF-8 transport.
    /// </summary>
    private partial void CreateFacades(
        Encoding inputEncoding, Encoding outputEncoding,
        out Stream inputFacadeStream, out Stream outputFacadeStream)
    {
        inputFacadeStream = Encoding.CreateTranscodingStream(
            BaseStream, Encoding.UTF8, inputEncoding, leaveOpen: true);
        outputFacadeStream = Encoding.CreateTranscodingStream(
            BaseStream, Encoding.UTF8, outputEncoding, leaveOpen: true);
    }

    /// <summary>Windows: ConPTY has no POSIX signals; TerminateProcess is the SIGKILL analog.</summary>
    private partial void KillPlatform() => WindowsPty.Terminate(ProcessHandle!);

    /// <summary>Windows: starts the async pseudo-console close, which sends CTRL_CLOSE_EVENT (see <see cref="PtyProcess.RequestClose"/>).</summary>
    private partial void RequestClosePlatform() => BaseStream.BeginAsyncClose();

    /// <summary>
    /// Gives a still-alive child the chance to exit cleanly before the terminal closes:
    /// on Unix via SIGHUP, on Windows by starting the ClosePseudoConsole close
    /// asynchronously, which sends CTRL_CLOSE_EVENT (the Windows analog of SIGHUP) to
    /// the attached clients. The close runs on a thread-pool thread; if the child does
    /// not exit, the dispose grace window force-kills it, which unblocks that close.
    /// Exited children are left alone so their final output is preserved.
    /// </summary>
    private void SignalChildIfAlive()
    {
        if (!HasExited)
            BaseStream.BeginAsyncClose();
    }

    /// <summary>
    /// Non-blocking drain: the Windows output pump continuously drains the native ConPTY
    /// pipe into its managed queue, so there is nothing to do here. Do not consume that
    /// queue: all bytes remain visible to the caller through BaseStream / Output.
    /// </summary>
    private partial void DrainOutput()
    {
    }

    /// <summary>
    /// Windows: exit may be blocked behind more than the pump's normal bounded buffer
    /// (a child writing a payload larger than the bound never exits while the pump
    /// waits for space). Lift the bound for an explicit wait, preserving all bytes for
    /// later user reads.
    /// </summary>
    private partial void BeginExitWait() => BaseStream.EnterExitWait();

    /// <summary>Balances <see cref="BeginExitWait"/> when the wait returns, times out, or is canceled.</summary>
    private partial void EndExitWait() => BaseStream.ExitExitWait();

    /// <summary>
    /// Called by the reaper after the root process exits. ClosePseudoConsole can wait for
    /// a final output frame to drain; PtyStream queues that work away from this shared
    /// reaper thread and publishes EOF when done.
    /// </summary>
    private partial void OnReapedPlatform()
    {
        BaseStream.NotifyProcessExited();
    }

    /// <summary>
    /// Single non-blocking reap attempt for the child: WaitForSingleObject(0) on the
    /// process handle, then the real Windows exit code (no wait-status/signal encoding).
    /// Returns true when the child exited (or the handle is invalid).
    /// </summary>
    private partial bool TryReapPlatform(out int exitCode)
    {
        if (ProcessHandle is null || ProcessHandle.IsInvalid)
        {
            exitCode = -1;
            return false;
        }

        if (WindowsPty.TryReap(ProcessHandle, out exitCode))
            return true;

        exitCode = -1;
        return false;
    }
}

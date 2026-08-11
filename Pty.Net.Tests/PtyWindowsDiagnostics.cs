#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Ghostflyby.Pty;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: isolates the zero-output issue via direct handle + stream debug surface.</summary>
public class PtyWindowsDiagnostics
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [Fact]
    public async Task DumpLaunchAndIoState()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== diagnostic start ===");
        try
        {
            var p = PtyProcess.Start("cmd.exe", ["/c", "echo HELLO-MARKER"]);
            var stream = p.BaseStream;
            sb.AppendLine($"Start OK: pid={p.Pid} streamId={stream.DebugInstanceId}");

            // Wait for the reader to do its thing, then inspect the stream's internal state.
            await Task.Delay(1500);
            sb.AppendLine($"after 1.5s: bufferCount={stream.DebugBufferCount} waiters={stream.DebugReadWaiterCount} eof={stream.DebugReaderEof}");
            sb.AppendLine($"buffered text=[{stream.DebugBufferText}]");

            // Now a stream-level read with timeout.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buf = new byte[512];
            try
            {
                var n = await stream.ReadAsync(buf, cts.Token);
                sb.AppendLine($"stream ReadAsync: n={n} bytes=[{Encoding.UTF8.GetString(buf, 0, n)}]");
            }
            catch (OperationCanceledException)
            {
                sb.AppendLine("stream ReadAsync: TIMED OUT");
            }
            sb.AppendLine($"post-read: bufferCount={stream.DebugBufferCount} waiters={stream.DebugReadWaiterCount}");

            sb.AppendLine($"WaitForExit(2s)={p.WaitForExit(TimeSpan.FromSeconds(2))} exitCode={p.ExitCode}");
            p.Dispose();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"threw {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine("--- launch/reader trace ---");
        sb.AppendLine(WindowsPty.GetDebugLog());
        sb.AppendLine("=== diagnostic end ===");
        throw new Exception(sb.ToString());
    }
}
#endif

#if WINDOWS
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: directly reads/writes the ConPTY pipe handles to isolate the zero-output issue.</summary>
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
            var p = Ghostflyby.Pty.PtyProcess.Start("cmd.exe", ["/c", "echo HELLO-MARKER"]);
            sb.AppendLine($"Start OK: pid={p.Pid}");

            // Pull the pipe handles out of the stream via reflection.
            var streamType = p.BaseStream.GetType();
            var outReadField = streamType.GetField("outputRead", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var inWriteField = streamType.GetField("inputWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var outRead = (SafeFileHandle)outReadField.GetValue(p.BaseStream)!;
            using var inWrite = (SafeFileHandle)inWriteField.GetValue(p.BaseStream)!;
            sb.AppendLine($"handles: outRead={(long)outRead.DangerousGetHandle()} inWrite={(long)inWrite.DangerousGetHandle()} closed={outRead.IsClosed}");

            // Direct write to the child's stdin channel.
            var payload = Encoding.ASCII.GetBytes("echo FROM-DIRECT-WRITE\r\n");
            var okW = WriteFile(inWrite, payload, (uint)payload.Length, out var written, IntPtr.Zero);
            sb.AppendLine($"direct WriteFile: ok={okW} written={written} err={Marshal.GetLastWin32Error()}");

            // Direct blocking read (short, on a task with timeout).
            var buf = new byte[512];
            var readTask = Task.Run(() =>
            {
                var okR = ReadFile(outRead, buf, (uint)buf.Length, out var readN, IntPtr.Zero);
                return $"direct ReadFile: ok={okR} read={readN} err={Marshal.GetLastWin32Error()} bytes=[{Encoding.UTF8.GetString(buf, 0, (int)readN)}]";
            });
            var directResult = await readTask.WaitAsync(TimeSpan.FromSeconds(3));
            sb.AppendLine(directResult);

            // Stream-level read for comparison.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var sbuf = new byte[512];
            var n = await p.BaseStream.ReadAsync(sbuf, cts.Token);
            sb.AppendLine($"stream ReadAsync: n={n} bytes=[{Encoding.UTF8.GetString(sbuf, 0, n)}]");

            sb.AppendLine($"WaitForExit(2s)={p.WaitForExit(TimeSpan.FromSeconds(2))} exitCode={p.ExitCode}");
            p.Dispose();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"threw {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var asm = typeof(Ghostflyby.Pty.PtyProcess).Assembly;
            var type = asm.GetType("Ghostflyby.Pty.WindowsPty")!;
            var log = (string)type.GetMethod("GetDebugLog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            sb.AppendLine("--- launch/reader trace ---");
            sb.AppendLine(log);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"GetDebugLog failed: {ex.Message}");
        }

        sb.AppendLine("=== diagnostic end ===");
        throw new Exception(sb.ToString());
    }
}
#endif

#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Ghostflyby.Pty;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: library path — A (success) then B (sync read, bypassing the reader thread).</summary>
public class AaaPtyWindowsDiagnostics
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [Fact]
    public async Task AB_SyncReadForB()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== AB-sync-read start ===");

        // A: async read via reader thread (should succeed).
        PtyProcess? pA = null;
        try
        {
            pA = PtyProcess.Start("cmd.exe", ["/c", "echo AAA-OK"]);
            var a = await ReadAllAsync(pA).WaitAsync(TimeSpan.FromSeconds(8));
            sb.AppendLine($"A (async): got={a.Contains("AAA-OK")} len={a.Length}");
        }
        catch (Exception ex) { sb.AppendLine($"A threw {ex.GetType().Name}: {ex.Message}"); }

        // B: same, but read synchronously (bypass reader thread) with a timeout.
        PtyProcess? pB = null;
        try
        {
            pB = PtyProcess.Start("cmd.exe", ["/c", "echo BBB-OK"]);
            sb.AppendLine($"B started pid={pB.Pid}");

            // Dump the outR handle validity.
            var field = typeof(PtyStream).GetField("outputRead", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            using var outR = (SafeFileHandle)field.GetValue(pB.BaseStream)!;
            var valid = GetFileInformationByHandle(outR, out _);
            sb.AppendLine($"B outR valid={valid} err={Marshal.GetLastWin32Error()}");

            // Sync read on a background task with timeout.
            var readTask = Task.Run(() =>
            {
                var buf = new byte[512];
                try
                {
                    var n = pB.BaseStream.Read(buf, 0, buf.Length);
                    return $"B sync Read: n={n} bytes=[{Encoding.UTF8.GetString(buf, 0, n)}]";
                }
                catch (Exception ex)
                {
                    return $"B sync Read threw {ex.GetType().Name}: {ex.Message}";
                }
            });
            var result = await readTask.WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine(result);
            sb.AppendLine($"B WaitForExit(2s)={pB.WaitForExit(TimeSpan.FromSeconds(2))} exitCode={pB.ExitCode}");
        }
        catch (Exception ex) { sb.AppendLine($"B threw {ex.GetType().Name}: {ex.Message}"); }

        pA?.Dispose();
        pB?.Dispose();

        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }

    private static async Task<string> ReadAllAsync(PtyProcess p)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buf = new byte[512];
            try
            {
                var n = await p.BaseStream.ReadAsync(buf, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (Exception) { break; }
        }
        return sb.ToString();
    }
}
#endif

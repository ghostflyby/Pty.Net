#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: hand-written named-pipe ConPTY session (node-pty style), to check if named pipes avoid the anonymous-pipe zero-output on the second session.</summary>
public class AaaPtyWindowsDiagnostics
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateNamedPipeW(string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, IntPtr lpSecurityAttributes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ConnectNamedPipe(IntPtr hNamedPipe, IntPtr lpOverlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X; public short Y; }

    private const uint PIPE_ACCESS_INBOUND = 0x1;
    private const uint PIPE_ACCESS_OUTBOUND = 0x2;
    private const uint PIPE_TYPE_BYTE = 0x0;
    private const uint PIPE_WAIT = 0x0;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint OPEN_EXISTING = 3;

    [Fact]
    public async Task NamedPipeSecondSession()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== named-pipe-second start ===");

        // Session 1 (this one is just to consume the "first" slot; anonymous is fine).
        try
        {
            using var p1 = Ghostflyby.Pty.PtyProcess.Start("cmd.exe", ["/c", "echo ONE-OK"]);
            var one = await ReadAll(p1).WaitAsync(TimeSpan.FromSeconds(8));
            sb.AppendLine($"s1 (library, anonymous): got={one.Contains("ONE-OK")} len={one.Length}");
        }
        catch (Exception ex) { sb.AppendLine($"s1 threw {ex.GetType().Name}: {ex.Message}"); }

        // Session 2: hand-written with NAMED pipes.
        try
        {
            var pipeName = @"\\.\pipe\pty-namedpipe-" + Guid.NewGuid().ToString("N");

            // Input pipe: server side = PIPE_ACCESS_INBOUND (we read? no — ConPTY reads input, we write).
            // node-pty pattern: for the input pipe, the ConPTY gets the read side; we hold the write side.
            var inServer = CreateNamedPipeW(pipeName + "-in", PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 0x20000, 0x20000, 0, IntPtr.Zero);
            sb.AppendLine($"inServer={(long)inServer} err={Marshal.GetLastWin32Error()}");
            var connectTask = Task.Run(() => ConnectNamedPipe(inServer, IntPtr.Zero));
            using var inClient = CreateFileW(pipeName + "-in", GENERIC_READ, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            sb.AppendLine($"inClient valid={!inClient.IsInvalid} err={Marshal.GetLastWin32Error()}");
            await connectTask.WaitAsync(TimeSpan.FromSeconds(5));
            sb.AppendLine("in pipe connected");

            // Output pipe: server side = PIPE_ACCESS_INBOUND (we read), ConPTY writes.
            var outServer = CreateNamedPipeW(pipeName + "-out", PIPE_ACCESS_INBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 0x20000, 0x20000, 0, IntPtr.Zero);
            var connectTask2 = Task.Run(() => ConnectNamedPipe(outServer, IntPtr.Zero));
            using var outClient = CreateFileW(pipeName + "-out", GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            sb.AppendLine($"outClient valid={!outClient.IsInvalid} err={Marshal.GetLastWin32Error()}");
            await connectTask2.WaitAsync(TimeSpan.FromSeconds(5));
            sb.AppendLine("out pipe connected");

            // The ConPTY gets inClient (it reads input) and outClient (it writes output).
            // We hold inServer (write input) and outServer (read output).
            var rc = CreatePseudoConsole(new COORD { X = 120, Y = 30 }, inClient, outClient, 0, out var hPc);
            sb.AppendLine($"CreatePseudoConsole rc={rc} err={Marshal.GetLastWin32Error()}");
            if (rc != 0) return;

            // Reuse the library's launch for the child attach (it builds the attribute list etc.),
            // but we can't — the library creates its own pipes. Instead: just spawn cmd via library
            // is not possible without replacing pipes. So this diagnostic tests only whether the
            // named-pipe ConPTY itself works for reading (write a frame by spawning a child attached
            // to it — too complex without the attr plumbing here). Simplify: skip child, just check
            // the pipe plumbing by having cmd echo through it via the library's WindowsPty? Not possible.
            sb.AppendLine("named-pipe ConPTY created — child attach requires full plumbing; skipping output check");
            ClosePseudoConsole(hPc);
            CloseHandle(inServer);
            CloseHandle(outServer);
        }
        catch (Exception ex) { sb.AppendLine($"s2 threw {ex.GetType().Name}: {ex.Message}"); }

        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }

    private static async Task<string> ReadAll(Ghostflyby.Pty.PtyProcess p)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buf = new byte[512];
            try
            {
                var n = await p.BaseStream.ReadAsync(buf, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                if (n == 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            }
            catch (Exception) { break; }
        }
        return sb.ToString();
    }
}
#endif

#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Ghostflyby.Pty;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: MiniTerm-style hand-written ConPTY launch vs the library path, every step timeout-bounded.</summary>
public class AaaPtyWindowsDiagnostics
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEXW lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, uint dwAttributeCount, uint dwFlags, ref IntPtr lpSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, nuint attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X; public short Y; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOW { public uint cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public uint dwX; public uint dwY; public uint dwXSize; public uint dwYSize; public uint dwXCountChars; public uint dwYCountChars; public uint dwFillAttribute; public uint dwFlags; public ushort wShowWindow; public ushort cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [Fact]
    public async Task SideBySideMiniTermVsLibrary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== side-by-side start ===");

        // Path A: hand-written MiniTerm-style (independent P/Invoke).
        try
        {
            sb.AppendLine(await RunMiniTermStyle().WaitAsync(TimeSpan.FromSeconds(20)));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"MiniTerm-style threw {ex.GetType().Name}: {ex.Message}");
        }

        // Path B: the library.
        try
        {
            using var p = PtyProcess.Start("cmd.exe", ["/c", "echo HELLO-MARKER"]);
            sb.AppendLine($"library Start OK: pid={p.Pid}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buf = new byte[512];
            var n = await p.BaseStream.ReadAsync(buf, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(6));
            sb.AppendLine($"library ReadAsync: n={n} bytes=[{Encoding.UTF8.GetString(buf, 0, n)}]");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"library path threw {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine("=== side-by-side end ===");
        throw new Exception(sb.ToString());
    }

    private static Task<string> RunMiniTermStyle()
    {
        return Task.Run(() =>
        {
            var log = new StringBuilder();
            if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) return "CreatePipe in failed";
            if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) return "CreatePipe out failed";
            log.AppendLine($"mt pipes: inR={(long)inputRead.DangerousGetHandle()} inW={(long)inputWrite.DangerousGetHandle()} outR={(long)outputRead.DangerousGetHandle()} outW={(long)outputWrite.DangerousGetHandle()}");

            var rc = CreatePseudoConsole(new COORD { X = 120, Y = 30 }, inputRead, outputWrite, 0, out var hPc);
            log.AppendLine($"mt CreatePseudoConsole rc={rc} hPc={(long)hPc} err={Marshal.GetLastWin32Error()}");
            if (rc != 0) return $"CreatePseudoConsole failed rc={rc}";
            inputRead.Dispose();
            outputWrite.Dispose();

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            log.AppendLine($"mt attr size={size}");
            var attrList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
                return $"mt Initialize failed err={Marshal.GetLastWin32Error()}";
            if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                return $"mt Update failed err={Marshal.GetLastWin32Error()}";
            log.AppendLine("mt attr ok");

            var si = new STARTUPINFOEXW();
            si.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>();
            si.StartupInfo.dwFlags = 0x100;
            si.lpAttributeList = attrList;
            var cmdLine = new StringBuilder("\"cmd.exe\" /c echo HELLO-MARKER");
            var ok = CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref si, out var pi);
            log.AppendLine($"mt CreateProcessW ok={ok} pid={pi.dwProcessId} err={Marshal.GetLastWin32Error()}");
            if (!ok) return $"mt CreateProcessW failed";

            var buf = new byte[512];
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            var total = new StringBuilder();
            while (DateTime.UtcNow < deadline)
            {
                var readOk = ReadFile(outputRead, buf, (uint)buf.Length, out var readN, IntPtr.Zero);
                if (!readOk) { log.AppendLine($"mt ReadFile failed err={Marshal.GetLastWin32Error()}"); break; }
                if (readN == 0) { log.AppendLine("mt ReadFile: EOF (0 bytes)"); break; }
                total.Append(Encoding.UTF8.GetString(buf, 0, (int)readN));
                log.AppendLine($"mt ReadFile: read={readN}");
                if (total.Length > 200) break;
            }
            log.AppendLine($"mt output=[{total}]");
            ClosePseudoConsole(hPc);
            Marshal.FreeHGlobal(attrList);
            return log.ToString();
        });
    }
}
#endif

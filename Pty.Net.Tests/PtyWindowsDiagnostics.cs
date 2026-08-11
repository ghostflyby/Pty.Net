#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: hand-written (library-free) ConPTY, two sequential sessions.</summary>
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
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X; public short Y; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOW { public uint cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public uint dwX; public uint dwY; public uint dwXSize; public uint dwYSize; public uint dwXCountChars; public uint dwYCountChars; public uint dwFillAttribute; public uint dwFlags; public ushort wShowWindow; public ushort cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [Fact]
    public async Task HandwrittenTwoSessions()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== handwritten-two-sessions start ===");

        sb.AppendLine("-- session 1 --");
        try { sb.AppendLine(await Task.Run(() => RunSession("M1-OK")).WaitAsync(TimeSpan.FromSeconds(15))); }
        catch (Exception ex) { sb.AppendLine($"s1 threw {ex.GetType().Name}: {ex.Message}"); }

        sb.AppendLine("-- session 2 --");
        try { sb.AppendLine(await Task.Run(() => RunSession("M2-OK")).WaitAsync(TimeSpan.FromSeconds(15))); }
        catch (Exception ex) { sb.AppendLine($"s2 threw {ex.GetType().Name}: {ex.Message}"); }

        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }

    private static string RunSession(string marker)
    {
        var log = new StringBuilder();
        if (!CreatePipe(out var inR, out var inW, IntPtr.Zero, 0)) return "CreatePipe in failed";
        if (!CreatePipe(out var outR, out var outW, IntPtr.Zero, 0)) return "CreatePipe out failed";

        var rc = CreatePseudoConsole(new COORD { X = 120, Y = 30 }, inR, outW, 0, out var hPc);
        if (rc != 0) return $"CreatePseudoConsole rc={rc} err={Marshal.GetLastWin32Error()}";
        log.AppendLine($"CreatePseudoConsole ok hPc={(long)hPc}");

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var attr = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attr, 1, 0, ref size)) return $"attr init failed {Marshal.GetLastWin32Error()}";
        if (!UpdateProcThreadAttribute(attr, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) return $"attr update failed {Marshal.GetLastWin32Error()}";

        var si = new STARTUPINFOEXW();
        si.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>();
        si.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES
        si.lpAttributeList = attr;
        var cmdLine = new StringBuilder($"\"cmd.exe\" /c echo {marker}");
        var ok = CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref si, out var pi);
        if (!ok) return $"CreateProcessW failed err={Marshal.GetLastWin32Error()}";
        log.AppendLine($"CreateProcessW ok pid={pi.dwProcessId}");
        inR.Dispose();
        outW.Dispose();

        // Wait for child exit, then drain the output pipe.
        WaitForSingleObject(pi.hProcess, 5000);
        log.AppendLine("child exited");

        var buf = new byte[512];
        var total = new StringBuilder();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var readOk = ReadFile(outR, buf, (uint)buf.Length, out var readN, IntPtr.Zero);
            if (!readOk) { log.AppendLine($"ReadFile err={Marshal.GetLastWin32Error()}"); break; }
            if (readN == 0) { log.AppendLine("ReadFile EOF"); break; }
            total.Append(Encoding.UTF8.GetString(buf, 0, (int)readN));
            if (total.Length > 100) break;
        }
        log.AppendLine($"output=[{total}] gotMarker={total.ToString().Contains(marker)}");

        ClosePseudoConsole(hPc);
        Marshal.FreeHGlobal(attr);
        return log.ToString();
    }
}
#endif

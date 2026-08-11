#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using Ghostflyby.Pty;
using Microsoft.Win32.SafeHandles;

namespace Ghostflyby.Pty.Tests;

/// <summary>Temporary diagnostic: runs a hand-written MiniTerm-style ConPTY launch side by side with the library path.</summary>
public class AaaPtyWindowsDiagnostics
{
    // ---- independent MiniTerm-style P/Invoke (does not touch the library) ----
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

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW { public uint cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public uint dwX; public uint dwY; public uint dwXSize; public uint dwYSize; public uint dwXCountChars; public uint dwYCountChars; public uint dwFillAttribute; public uint dwFlags; public ushort wShowWindow; public ushort cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId; }

    [Fact]
    public async Task SideBySideMiniTermVsLibrary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== side-by-side ===");

        // --- Path A: hand-written MiniTerm-style launch ---
        try
        {
            var result = LaunchMiniTermStyle(sb);
            sb.AppendLine(result);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"MiniTerm-style threw {ex.GetType().Name}: {ex.Message}");
        }

        // --- Path B: the library ---
        try
        {
            using var p = PtyProcess.Start("cmd.exe", ["/c", "echo HELLO-MARKER"]);
            sb.AppendLine($"library Start OK: pid={p.Pid}");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var buf = new byte[512];
            var n = await p.BaseStream.ReadAsync(buf, cts.Token);
            sb.AppendLine($"library ReadAsync: n={n} bytes=[{Encoding.UTF8.GetString(buf, 0, n)}]");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"library path threw {ex.GetType().Name}: {ex.Message}");
        }

        sb.AppendLine("=== end ===");
        throw new Exception(sb.ToString());
    }

    private static string LaunchMiniTermStyle(StringBuilder log)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) return "CreatePipe in failed";
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) return "CreatePipe out failed";
        log.AppendLine($"mt pipes: inR={(long)inputRead.DangerousGetHandle()} inW={(long)inputWrite.DangerousGetHandle()} outR={(long)outputRead.DangerousGetHandle()} outW={(long)outputWrite.DangerousGetHandle()}");

        var rc = CreatePseudoConsole(new COORD { X = 120, Y = 30 }, inputRead, outputWrite, 0, out var hPc);
        log.AppendLine($"mt CreatePseudoConsole rc={rc} hPc={(long)hPc} err={Marshal.GetLastWin32Error()}");
        if (rc != 0) return $"CreatePseudoConsole failed rc={rc}";

        inputRead.Dispose();
        outputWrite.Dispose();
        log.AppendLine("mt closed conpty pipe ends");

        // Attribute list.
        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        log.AppendLine($"mt attr size={size}");
        var attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
            return $"mt Initialize failed err={Marshal.GetLastWin32Error()}";
        if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            return $"mt Update failed err={Marshal.GetLastWin32Error()}";
        log.AppendLine("mt attr ok");

        // STARTUPINFOEX + CreateProcess.
        var si = new STARTUPINFOEXW();
        si.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>();
        si.StartupInfo.dwFlags = 0x100 /* STARTF_USESTDHANDLES */;
        si.lpAttributeList = attrList;
        var cmdLine = new StringBuilder("\"cmd.exe\" /c echo HELLO-MARKER");
        var ok = CreateProcessW(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0x08000000 /* EXTENDED_STARTUPINFO_PRESENT */, IntPtr.Zero, null, ref si, out var pi);
        log.AppendLine($"mt CreateProcessW ok={ok} pid={pi.dwProcessId} err={Marshal.GetLastWin32Error()}");
        if (!ok) return $"mt CreateProcessW failed";

        // Read the output pipe.
        var buf = new byte[512];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        var total = new StringBuilder();
        while (DateTime.UtcNow < deadline)
        {
            var readOk = ReadFile(outputRead, buf, (uint)buf.Length, out var readN, IntPtr.Zero);
            if (!readOk) { log.AppendLine($"mt ReadFile failed err={Marshal.GetLastWin32Error()}"); break; }
            if (readN == 0) break;
            total.Append(Encoding.UTF8.GetString(buf, 0, (int)readN));
            if (total.Length > 200) break;
        }
        WaitForSingleObject(pi.hProcess, 5000);
        ClosePseudoConsole(hPc);
        Marshal.FreeHGlobal(attrList);
        return $"mt output=[{total}]";
    }
}
#endif

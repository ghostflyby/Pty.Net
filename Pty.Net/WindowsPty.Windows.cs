using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.System.Threading;
using static Windows.Win32.PInvoke;

// CA1416 (platform compatibility) is a structural false positive here: the analyzer only
// understands platform expressed via TFM/RID or [SupportedOSPlatform], not this repo's
// single-TFM + file-glob splitting, so it flags these Windows-only P/Invoke calls as
// "reachable on all platforms". The class below is compiled only on Windows (csproj
// glob), and the [SupportedOSPlatform] route is unusable because partial classes share
// annotations across files (public PtyProcess/PtyStream would become Windows-only).
// Scoped to this one file; every other file keeps warnings-as-errors.
#pragma warning disable CA1416

namespace Ghostflyby.Pty;

/// <summary>Result of a ConPTY-backed spawn and the resources transferred to the process wrapper.</summary>
internal sealed record WindowsPtyResult(
    NamedPipeClientStream InputWrite,
    NamedPipeClientStream OutputRead,
    ClosePseudoConsoleSafeHandle PseudoConsole,
    SafeProcessHandle ProcessHandle,
    int Pid);

/// <summary>
/// Windows/ConPTY launch path. CsWin32 supplies only the ConPTY and process-control APIs;
/// <see cref="System.IO.Pipes"/> creates and owns the byte channels used for parent-side I/O.
/// Windows-only: compiled only by the Windows target (see csproj).
/// </summary>
internal static partial class WindowsPty
{
    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 22 | PROC_THREAD_ATTRIBUTE_INPUT (0x20000).
    private const uint ProcThreadAttributePseudoConsole = 0x20016;
    private const int PipeBufferSize = 128 * 1024;

    /// <summary>True when the OS exports ConPTY (Windows 10 1809 / build 17763 or later).</summary>
    internal static bool IsSupported { get; } = DetectConPty();

    private static bool DetectConPty()
    {
        using var kernel32 = LoadLibrary("kernel32.dll");
        return !kernel32.IsInvalid && GetProcAddress(kernel32, "CreatePseudoConsole") != IntPtr.Zero;
    }

    /// <summary>
    /// Creates a pseudo console, spawns <paramref name="file"/> attached to it, and transfers
    /// ownership of the parent pipe ends, pseudo console, and process handle to the caller.
    /// </summary>
    internal static unsafe WindowsPtyResult Start(
        string file, string[] arguments, string? workingDirectory,
        IDictionary<string, string?> environment, int columns, int rows)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "ConPTY (CreatePseudoConsole) requires Windows 10 version 1809 (build 17763) or later.");

        NamedPipeServerStream? inputServer = null;
        NamedPipeServerStream? outputServer = null;
        NamedPipeClientStream? inputWrite = null;
        NamedPipeClientStream? outputRead = null;
        ClosePseudoConsoleSafeHandle? pseudoConsole = null;
        SafeProcessHandle? processHandle = null;

        try
        {
            // The server handles are synchronous because CreatePseudoConsole does not accept
            // overlapped handles. Only the parent-side clients perform I/O, so those handles are
            // asynchronous and PipeStream can use IOCP without dedicated reader/writer threads.
            var inputName = "pty-in-" + Guid.NewGuid().ToString("N");
            inputServer = CreateServer(inputName, PipeDirection.In);
            inputWrite = new NamedPipeClientStream(
                ".", inputName, PipeDirection.Out, PipeOptions.Asynchronous);

            var outputName = "pty-out-" + Guid.NewGuid().ToString("N");
            outputServer = CreateServer(outputName, PipeDirection.Out);
            outputRead = new NamedPipeClientStream(
                ".", outputName, PipeDirection.In, PipeOptions.Asynchronous);

            // A local client can connect before WaitForConnection is entered; the BCL
            // explicitly supports that ordering, so no worker task is needed for setup.
            inputWrite.Connect(5000);
            inputServer.WaitForConnection();
            outputRead.Connect(5000);
            outputServer.WaitForConnection();

            var hr = CreatePseudoConsole(
                new COORD { X = (short)columns, Y = (short)rows },
                inputServer.SafePipeHandle,
                outputServer.SafePipeHandle,
                0,
                out pseudoConsole);
            if (hr.Failed)
                throw new Win32Exception(hr.Value, $"CreatePseudoConsole failed: {hr}");
            if (pseudoConsole is null || pseudoConsole.IsInvalid)
                throw new Win32Exception("CreatePseudoConsole returned an invalid pseudo-console handle");

            var startupInfo = new STARTUPINFOEXW();
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>();
            startupInfo.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;

            var attrListPtr = IntPtr.Zero;
            var attrListInitialized = false;
            PROCESS_INFORMATION processInfo = default;
            var success = false;
            try
            {
                const uint attributeCount = 1;
                nuint size = 0;
                if (InitializeProcThreadAttributeList(default, attributeCount, ref size) || size == 0)
                    throw new Win32Exception("InitializeProcThreadAttributeList (size query) failed");

                attrListPtr = Marshal.AllocHGlobal(checked((int)size));
                startupInfo.lpAttributeList = new LPPROC_THREAD_ATTRIBUTE_LIST(attrListPtr);
                if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, attributeCount, ref size))
                    throw new Win32Exception("InitializeProcThreadAttributeList failed");
                attrListInitialized = true;

                if (!UpdateProcThreadAttribute(
                        startupInfo.lpAttributeList,
                        0,
                        ProcThreadAttributePseudoConsole,
                        (void*)pseudoConsole.DangerousGetHandle(),
                        (nuint)Marshal.SizeOf<IntPtr>(),
                        null,
                        null))
                    throw new Win32Exception("UpdateProcThreadAttribute failed");

                var commandLine = BuildCommandLine(file, arguments);
                var envBlock = BuildEnvironmentBlock(environment);
                fixed (char* cmdLinePtr = commandLine)
                fixed (char* cwdPtr = workingDirectory)
                fixed (byte* envPtr = envBlock)
                {
                    success = CreateProcess(
                        default,
                        (PWSTR)cmdLinePtr,
                        null,
                        null,
                        false,
                        PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT |
                        PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                        envPtr,
                        (PCWSTR)cwdPtr,
                        (STARTUPINFOW*)&startupInfo,
                        &processInfo);
                }
            }
            finally
            {
                if (attrListInitialized)
                    DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                if (attrListPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(attrListPtr);
            }

            if (!success)
                throw new Win32Exception($"CreateProcessW failed for '{file}'");

            // Microsoft requires the ConPTY channel handles to remain valid through
            // CreateProcess. The pseudo console has retained what it needs now, so release
            // our server copies; only the asynchronous parent clients remain owned here.
            inputServer.Dispose();
            inputServer = null;
            outputServer.Dispose();
            outputServer = null;

            processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
            if (processInfo.hThread != 0)
                new SafeFileHandle(processInfo.hThread, ownsHandle: true).Dispose();

            var result = new WindowsPtyResult(
                inputWrite!, outputRead!, pseudoConsole, processHandle, (int)processInfo.dwProcessId);

            inputWrite = null;
            outputRead = null;
            pseudoConsole = null;
            processHandle = null;
            return result;
        }
        finally
        {
            inputServer?.Dispose();
            outputServer?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            pseudoConsole?.Dispose();
            processHandle?.Dispose();
        }
    }

    private static NamedPipeServerStream CreateServer(string name, PipeDirection direction)
    {
        return new NamedPipeServerStream(
            name,
            direction,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            PipeBufferSize,
            PipeBufferSize);
    }

    /// <summary>Terminates the child (ConPTY has no signals).</summary>
    internal static void Terminate(SafeProcessHandle processHandle)
    {
        if (!processHandle.IsInvalid)
            TerminateProcess(processHandle, 1);
    }

    /// <summary>Non-blocking process-handle reap step with the real Windows exit code.</summary>
    internal static bool TryReap(SafeProcessHandle processHandle, out int exitCode)
    {
        exitCode = -1;
        if (processHandle.IsInvalid)
            return false;
        if (WaitForSingleObject(processHandle, 0) != 0)
            return false;
        exitCode = GetExitCodeProcess(processHandle, out var code) ? (int)code : -1;
        return true;
    }

    private static string BuildCommandLine(string file, string[] arguments)
    {
        var sb = new StringBuilder();
        AppendQuoted(sb, file);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendQuoted(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string token)
    {
        if (!token.Any(c => c is ' ' or '\t' or '"'))
        {
            sb.Append(token);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in token)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                backslashes = 0;
                sb.Append('"');
                continue;
            }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static byte[] BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var sb = new StringBuilder();
        foreach (var kv in environment.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (kv.Key.Contains('='))
                continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? string.Empty).Append('\0');
        }
        sb.Append('\0');
        return Encoding.Unicode.GetBytes(sb.ToString());
    }
}

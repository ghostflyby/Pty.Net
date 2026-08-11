#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Console;
using Windows.Win32.System.Threading;
using static Windows.Win32.PInvoke;

namespace Ghostflyby.Pty;

/// <summary>Result of a ConPTY-backed spawn: the pipe ends the library owns and the child's process handle/pid.</summary>
internal sealed record WindowsPtyResult(
    SafeFileHandle InputWrite,      // write user input to the child's stdin
    SafeFileHandle OutputRead,      // read the child's merged stdout+stderr
    ClosePseudoConsoleSafeHandle PseudoConsole,
    SafeProcessHandle ProcessHandle,
    int Pid);

/// <summary>
/// Windows/ConPTY launch path. ConPTY (<c>CreatePseudoConsole</c>) creates a virtual console
/// the child attaches to via <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c> in its STARTUPINFOEX;
/// two anonymous pipes carry the input/output between the parent and the console.
///
/// This is the only file that touches CsWin32-generated interop; PtyProcess/PtyStream/PtyReaper
/// reach Windows behavior through the small helpers here so the generated API surface stays
/// contained. All interop is generated as [LibraryImport] (build-task mode), so the AOT
/// analyzers stay clean.
/// </summary>
internal static partial class WindowsPty
{
    // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 22 (attribute number) | PROC_THREAD_ATTRIBUTE_INPUT (0x20000).
    // Not present in the CsWin32 metadata, so defined here — same value Microsoft's ConPTY
    // samples use. Using the wrong value makes CreateProcess silently ignore the attribute
    // and the child ends up with no console.
    private const uint ProcThreadAttributePseudoConsole = 0x20016;

    private const short DefaultCols = 120;
    private const short DefaultRows = 30;

    /// <summary>
    /// True when the OS provides ConPTY (Windows 10 1809 / build 17763 or later). Guarded by
    /// a load-time probe rather than an OS-version check so the library stays usable on
    /// older builds with a clear error instead of an EntryPointNotFoundException.
    /// </summary>
    internal static bool IsSupported { get; } = DetectConPty();

    private static bool DetectConPty()
    {
        var kernel32 = LoadLibrary("kernel32.dll");
        if (kernel32.IsInvalid)
            return false;
        return GetProcAddress(kernel32, "CreatePseudoConsole") != IntPtr.Zero;
    }

    /// <summary>
    /// Creates a pseudo console, spawns <paramref name="file"/> attached to it, and returns
    /// the retained pipe ends plus the child's process handle. Ownership of the returned
    /// handles transfers to the caller.
    /// </summary>
    internal static unsafe WindowsPtyResult Start(
        string file, string[] arguments, string? workingDirectory,
        IDictionary<string, string?> environment)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException(
                "ConPTY (CreatePseudoConsole) requires Windows 10 version 1809 (build 17763) or later.");

        // Two named pipe pairs (node-pty style). Anonymous pipes (CreatePipe) leave the
        // second-and-later ConPTY session in a process silently producing no output on
        // Windows Server / CI runners; node-pty's named pipes with 128KB buffers are
        // reliable for many sessions. The ConPTY gets the server ends; we read/write
        // through the client ends (CreateFileW).
        const uint pipeBufferSize = 128 * 1024;
        const uint pipeOpenMode = 0x0003; // PIPE_ACCESS_INBOUND | PIPE_ACCESS_OUTBOUND
        const uint pipeMode = 0x0000;     // PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT
        const uint openExisting = 3;

        var inName = @"\\.\pipe\pty-in-" + Guid.NewGuid().ToString("N");
        var outName = @"\\.\pipe\pty-out-" + Guid.NewGuid().ToString("N");

        var inServer = CreateNamedPipeW(inName, pipeOpenMode, pipeMode, 1, pipeBufferSize, pipeBufferSize, 30000, null);
        if (inServer.IsNull)
            throw new Win32Exception("CreateNamedPipeW (input) failed");
        var outServer = CreateNamedPipeW(outName, pipeOpenMode, pipeMode, 1, pipeBufferSize, pipeBufferSize, 30000, null);
        if (outServer.IsNull)
        {
            CloseHandle(inServer);
            throw new Win32Exception("CreateNamedPipeW (output) failed");
        }

        // Client ends for our I/O; CreateFileW blocks until ConnectNamedPipe pairs up.
        using var connectIn = Task.Run(() => ConnectNamedPipe(inServer, null));
        using var connectOut = Task.Run(() => ConnectNamedPipe(outServer, null));
        var inClient = CreateFileW(inName, 0x40000000 /* GENERIC_WRITE */, 0, null, openExisting, 0, default);
        if (inClient.IsInvalid)
        {
            CloseHandle(inServer);
            CloseHandle(outServer);
            throw new Win32Exception("CreateFileW (input client) failed");
        }
        var outClient = CreateFileW(outName, 0x80000000 /* GENERIC_READ */, 0, null, openExisting, 0, default);
        if (outClient.IsInvalid)
        {
            CloseHandle(inServer);
            CloseHandle(outServer);
            inClient.Dispose();
            throw new Win32Exception("CreateFileW (output client) failed");
        }
        try
        {
            if (!connectIn.Wait(TimeSpan.FromSeconds(5)) || !connectOut.Wait(TimeSpan.FromSeconds(5)))
                throw new Win32Exception("ConnectNamedPipe timed out");
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            CloseHandle(inServer);
            CloseHandle(outServer);
            inClient.Dispose();
            outClient.Dispose();
            throw new Win32Exception($"ConnectNamedPipe failed: {ex.InnerException.Message}");
        }
        Trace($"Start: inServer={(long)inServer.Value} outServer={(long)outServer.Value} inClient={(long)inClient.DangerousGetHandle()} outClient={(long)outClient.DangerousGetHandle()}");

        ClosePseudoConsoleSafeHandle? pseudoConsole = null;
        var attrListPtr = IntPtr.Zero;
        try
        {
            var hr = CreatePseudoConsole(
                new COORD { X = DefaultCols, Y = DefaultRows },
                inServer,
                outServer,
                0,
                out pseudoConsole);
            if (hr.Failed)
                throw new Win32Exception(hr.Value, $"CreatePseudoConsole failed: {hr}");
            Trace($"CreatePseudoConsole ok hPc={(long)pseudoConsole.DangerousGetHandle()}");

            // The pseudo console owns the server ends; the client ends carry our I/O.
            CloseHandle(inServer);
            CloseHandle(outServer);

            // STARTUPINFOEX with the pseudoconsole attribute. Get the required size with a
            // first call, allocate, then initialize.
            var startupInfo = new STARTUPINFOEXW();
            startupInfo.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>();
            // Both reference implementations (Microsoft MiniTerm, Porta.Pty) set
            // STARTF_USESTDHANDLES; without it the child's stdio is not wired to the
            // pseudoconsole pipes and the child's output never reaches them.
            startupInfo.StartupInfo.dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES;

            const uint attributeCount = 1;
            nuint size = 0;
            if (InitializeProcThreadAttributeList(default, attributeCount, ref size) || size == 0)
                throw new Win32Exception("InitializeProcThreadAttributeList (size query) failed");

            attrListPtr = Marshal.AllocHGlobal((int)size);
            startupInfo.lpAttributeList = new LPPROC_THREAD_ATTRIBUTE_LIST(attrListPtr);
            if (!InitializeProcThreadAttributeList(startupInfo.lpAttributeList, attributeCount, ref size))
                throw new Win32Exception("InitializeProcThreadAttributeList failed");

            if (!UpdateProcThreadAttribute(
                    startupInfo.lpAttributeList,
                    0,
                    ProcThreadAttributePseudoConsole,
                    (void*)pseudoConsole.DangerousGetHandle(),
                    (nuint)Marshal.SizeOf<IntPtr>(),
                    null,
                    null))
                throw new Win32Exception("UpdateProcThreadAttribute failed");

            // Command line: "app" + arguments. CreateProcessW takes a single mutable
            // Unicode string; arguments arrive already split, so they are joined with
            // space separators (each already quoted by the caller where needed).
            var commandLine = BuildCommandLine(file, arguments);

            // Unicode environment block: KEY=VALUE\0 pairs, ends with an extra \0.
            // TERM is injected (ConPTY child shells like bash in Git Bash use it).
            var envBlock = BuildEnvironmentBlock(environment);

            PROCESS_INFORMATION processInfo;
            var processHandle = default(SafeProcessHandle);
            var success = false;
            try
            {
                fixed (char* cmdLinePtr = commandLine)
                fixed (char* cwdPtr = workingDirectory)
                fixed (byte* envPtr = envBlock)
                {
                    success = CreateProcess(
                        default,   // lpApplicationName (the command line carries the exe)
                        (PWSTR)cmdLinePtr,
                        null,      // lpProcessAttributes
                        null,      // lpThreadAttributes
                        false,     // bInheritHandles — ConPTY wires the child's stdio itself
                        PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT |
                        PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                        envPtr,    // lpEnvironment
                        (PCWSTR)cwdPtr,
                        (STARTUPINFOW*)&startupInfo,
                        &processInfo);
                }
            }
            finally
            {
                // The attribute list is consumed by the child during CreateProcess; free it
                // immediately, matching Porta.Pty (the child duplicates what it needs).
                if (!startupInfo.lpAttributeList.IsNull)
                    DeleteProcThreadAttributeList(startupInfo.lpAttributeList);
                if (attrListPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(attrListPtr);
            }

            if (!success)
                throw new Win32Exception($"CreateProcessW failed for '{file}'");

            processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);
            if (processInfo.hThread != 0)
                new SafeFileHandle(processInfo.hThread, ownsHandle: true).Dispose();

            return new WindowsPtyResult(inClient, outClient, pseudoConsole, processHandle, (int)processInfo.dwProcessId);
        }
        catch
        {
            // Server ends are closed by the caller paths above on failure; here we only
            // own the client ends and the pseudo console.
            inClient.Dispose();
            outClient.Dispose();
            pseudoConsole?.Dispose();
            throw;
        }
    }

    /// <summary>Terminates the child (ConPTY has no signals; ClosePseudoConsole would also terminate the tree).</summary>
    internal static void Terminate(SafeProcessHandle processHandle)
    {
        if (!processHandle.IsInvalid)
            TerminateProcess(processHandle, 1);
    }

    /// <summary>
    /// Windows reaper step: non-blocking wait on the process handle; on exit, fetches the
    /// real exit code (no wait-status/signal encoding on Windows).
    /// </summary>
    internal static bool TryReap(SafeProcessHandle processHandle, out int exitCode)
    {
        exitCode = -1;
        if (processHandle.IsInvalid)
            return false;
        if (WaitForSingleObject(processHandle, 0) != 0) // WAIT_OBJECT_0
            return false;
        if (!GetExitCodeProcess(processHandle, out var code))
            exitCode = -1;
        else
            exitCode = (int)code;
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

    // Quotes a command-line token per Windows rules: wrap in quotes if it contains spaces
    // or quotes, escaping embedded quotes with backslashes (CreateProcessW parsing).
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
                continue; // invalid key
            sb.Append(kv.Key).Append('=').Append(kv.Value ?? string.Empty).Append('\0');
        }
        sb.Append('\0'); // the block is terminated by a final empty string
        return Encoding.Unicode.GetBytes(sb.ToString());
    }

    // TEMPORARY diagnostic helpers (removed once the multi-session issue is resolved).
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> DebugLog = new();
    private static void Trace(string message) => DebugLog.Enqueue(message);
    internal static string GetDebugLog() => string.Join("\n", DebugLog);
    internal static void Diag(string message) => DebugLog.Enqueue(message);
}
#endif

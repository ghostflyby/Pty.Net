namespace Ghostflyby.Pty.Tests;

using System.Runtime.CompilerServices;

/// <summary>
/// Platform-scoped facts for the single-source-set test suite: every test file compiles
/// on every OS, and the platform gate moves from <c>#if</c> to discovery-time skips.
/// A skipped test still shows up in the run (as skipped) on the platform-foreign OS,
/// instead of being invisible to the compiler there.
/// The optional caller-info parameters keep source locations working in MTP's Test
/// Explorer (xUnit3003): the compiler supplies them for a bare [WindowsFact] usage.
/// </summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only";
    }
}

/// <summary>Runs on macOS and Linux; skipped on Windows.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix only";
    }
}

/// <summary>Runs on macOS only.</summary>
internal sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsMacOS())
            Skip = "macOS only";
    }
}

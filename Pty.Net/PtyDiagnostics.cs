using System.Diagnostics;

namespace Ghostflyby.Pty;

/// <summary>
/// Opt-in diagnostics for process lifetime and reaper timing. Set
/// <c>PTY_REAPER_DIAG=1</c> before starting the process to enable it. Internal on
/// purpose: this is a troubleshooting aid, not a supported API surface.
/// </summary>
internal static class PtyDiagnostics
{
    /// <summary>
    /// Check this before building a message at hot call sites (the reaper's wait loop
    /// returns up to ten times per second per watched child) — the interpolated string
    /// would otherwise allocate on every iteration even with diagnostics disabled.
    /// </summary>
    internal static bool Enabled { get; } = string.Equals(Environment.GetEnvironmentVariable("PTY_REAPER_DIAG"), "1", StringComparison.Ordinal);

    internal static void Log(string message)
    {
        if (!Enabled)
            return;

        try
        {
            Console.Error.WriteLine($"[Pty.Net diag {DateTime.UtcNow:O} tid={Environment.CurrentManagedThreadId} " +
                                    $"tick={Stopwatch.GetTimestamp()}] {message}");
        }
        catch
        {
            // Diagnostics must never change process lifetime behavior.
        }
    }
}

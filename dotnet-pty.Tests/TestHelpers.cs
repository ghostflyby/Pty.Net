namespace dotnet_pty.Tests;

using System.Text;

/// <summary>
/// Test-side helpers for driving a PTY session. Marker-based reading (<c>ReadUntil</c>)
/// is deliberately not part of the public library API — consumers build their own text
/// layer on <c>PtyProcess.StandardOutput</c> — so the tests share one here.
/// All blocking work uses the reader's async path with a cancelable token (the engine
/// cancels pending reads immediately), so no thread-pool thread is ever parked.
/// </summary>
internal static class TestBash
{
    /// <summary>
    /// Starts an interactive bash. Runs with <c>--noprofile --norc --noediting -i</c> so
    /// the session is deterministic and does not pick up the user's rc files.
    /// <c>--noediting</c> disables readline, so <c>stty -echo</c> can suppress input echo
    /// (readline does its own echoing regardless). Note: the long options must come before
    /// <c>-i</c>, otherwise macOS bash 3.2 rejects the invocation.
    /// </summary>
    public static PtyProcess Start(string? workingDirectory = null, params string[]? arguments)
    {
        var args = arguments is { Length: > 0 }
            ? arguments
            : ["--noprofile", "--norc", "--noediting", "-i"];
        return PtyProcess.Start("/bin/bash", args, workingDirectory);
    }

    /// <summary>
    /// Reads from <paramref name="reader"/> until <paramref name="marker"/> appears in the
    /// accumulated text (inclusive) and returns everything read so far. Returns whatever
    /// was read if the child exits (EOF) before the marker shows up. Throws
    /// <see cref="TimeoutException"/> if the marker does not appear within
    /// <paramref name="timeout"/>.
    /// </summary>
    public static string ReadUntil(StreamReader reader, string marker, TimeSpan timeout)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var n = reader.ReadAsync(buf.AsMemory(), timeoutCts.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0)
                    return sb.ToString(); // EOF: the child exited
                sb.Append(buf, 0, n);
                // The marker can only have appeared within the newly appended chunk
                // (the text before it was already checked): scan only that window instead
                // of re-copying the whole accumulated buffer every iteration.
                if (sb.Length >= marker.Length)
                {
                    var start = Math.Max(0, sb.Length - n - marker.Length + 1);
                    if (sb.ToString(start, sb.Length - start).Contains(marker, StringComparison.Ordinal))
                        return sb.ToString();
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out after {timeout} waiting for '{marker}'. Got: {sb}");
        }
    }

    /// <summary>
    /// Reads and discards whatever the child produces for up to <paramref name="duration"/>
    /// (e.g. the echo of a command that just ran), then returns. Never parks a thread:
    /// each read window is capped by a cancelable token and the engine cancels pending
    /// reads immediately.
    /// </summary>
    public static void Drain(StreamReader reader, TimeSpan duration)
    {
        var buf = new char[4096];
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                var n = reader.ReadAsync(buf.AsMemory(), cts.Token).AsTask().GetAwaiter().GetResult();
                if (n == 0)
                    return; // EOF
            }
            catch (OperationCanceledException)
            {
                // Nothing arrived within this 100ms window; keep draining until the deadline.
            }
        }
    }
}

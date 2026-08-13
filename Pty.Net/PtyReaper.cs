namespace Ghostflyby.Pty;

/// <summary>
/// Process-wide reaper: the single owner of the exit wait for every <see cref="PtyProcess"/>.
/// The platform half runs a dedicated reaper thread and reports the child's exit to
/// <see cref="PtyProcess.OnReaped(int)"/>, which sets <see cref="PtyProcess.ExitCode"/>,
/// raises <see cref="PtyProcess.Exited"/> and completes the exit signal every
/// WaitForExit/Dispose wait observes.
///
/// Single-owner matters: if WaitForExit/Dispose each waited directly, two callers would
/// race for the same child — on Unix the first reap wins and the loser would see ECHILD
/// and overwrite the ExitCode with -1 (on Windows a single owned process handle makes
/// that impossible, but funneling the wait through one thread keeps the result
/// deterministic all the same). Other paths only observe ExitCode.
///
/// Signal safety (Unix): no SIGCHLD handler is installed (.NET's runtime installs its own
/// SIGCHLD on Unix; overlaying it is risky). Instead of polling waitpid every 10 ms, the
/// Unix reaper is event-driven: it waits on a pidfd (Linux, via epoll) or kqueue
/// EVFILT_PROC NOTE_EXIT (macOS), so an idle watched process holds no periodic wakeup
/// and an exiting child is collected the moment the kernel reports it (see
/// PtyReaper.Unix.cs). The Windows half keeps polling the process handle with a bounded
/// interval (see PtyReaper.Windows.cs). The same thread also makes the dispose-time
/// "wait up to 2 s" window non-fatal: even if it elapses while the child is still
/// alive, this reaper keeps watching, so a child can never be left as a zombie.
///
/// The per-process reap is a partial method implemented in PtyProcess.Start.Windows.cs
/// / PtyProcess.Start.Unix.cs, so this file carries no platform conditionals.
/// </summary>
internal static partial class PtyReaper
{
    /// <summary>Registers a <see cref="PtyProcess"/> for reaping; removed once it is reaped.</summary>
    public static void Watch(PtyProcess process) => WatchPlatform(process);

    /// <summary>Platform hook: the platform half starts its reaper thread and registers the process.</summary>
    private static partial void WatchPlatform(PtyProcess process);
}

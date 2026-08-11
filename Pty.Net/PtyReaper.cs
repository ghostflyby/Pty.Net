using System.Runtime.InteropServices;

namespace Ghostflyby.Pty;

/// <summary>
/// Process-wide reaper: the single owner of waitpid(2) for every <see cref="PtyProcess"/>.
/// One background thread scans the registered processes every 10 ms and calls
/// waitpid(WNOHANG); once a child is reaped it sets <see cref="PtyProcess.ExitCode"/>,
/// raises <see cref="PtyProcess.Exited"/> and unregisters.
///
/// Single-owner matters: if WaitForExit/Dispose each called waitpid directly, two
/// callers would race for the same child — the first reap wins and the loser would see
/// ECHILD and overwrite the ExitCode with -1. Funneling every waitpid through this
/// thread makes the exit result deterministic: other paths only observe ExitCode.
///
/// Signal safety: no SIGCHLD handler is installed (.NET's runtime installs its own
/// SIGCHLD on Unix; overlaying it is risky), so reaping is polled; a pidfd-based
/// event-driven reap is a future optimization on Linux. The reaper thread only polls
/// while at least one process is registered — with none, it idles on a monitor wait
/// and is woken by the first registration. The same thread also makes the dispose-time
/// "wait up to 2 s" window non-fatal: even if it elapses while the child is still
/// alive, this reaper keeps watching, so a child can never be left as a zombie.
/// </summary>
internal static class PtyReaper
{
    private const int PollIntervalMs = 10;

    private static readonly Lazy<ReaperThread> Reaper =
        new(() => new ReaperThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Registers a <see cref="PtyProcess"/> for reaping; removed once it is reaped.</summary>
    public static void Watch(PtyProcess process) => Reaper.Value.Watch(process);

    private sealed class ReaperThread
    {
        private readonly object sync = new();
        private readonly List<PtyProcess> watched = [];

        public ReaperThread()
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Pty.Net-reaper" };
            thread.Start();
        }

        public void Watch(PtyProcess process)
        {
            lock (sync)
            {
                watched.Add(process);
                Monitor.Pulse(sync); // wake the loop if it was idle-waiting for work
            }
        }

        private void Loop()
        {
            while (true)
            {
                List<PtyProcess> snapshot;
                lock (sync)
                {
                    if (watched.Count == 0)
                    {
                        // Nothing to reap: sleep until the first Watch, so the process-wide
                        // reaper thread is fully idle (no polling) between sessions.
                        Monitor.Wait(sync);
                        continue;
                    }

                    snapshot = [.. watched];
                }

                foreach (var p in snapshot)
                {
                    if (TryReap(p))
                    {
                        lock (sync)
                        {
                            watched.Remove(p);
                        }
                    }
                }

                Thread.Sleep(PollIntervalMs);
            }
        }

        /// <summary>
        /// Calls waitpid(WNOHANG) once, retrying on EINTR. Returns true once the child
        /// has been reaped (or is unreachable), at which point the process's ExitCode
        /// is set and <see cref="PtyProcess.Exited"/> raised.
        /// </summary>
        private static bool TryReap(PtyProcess p)
        {
            while (true)
            {
                var r = NativeMethods.waitpid(p.Pid, out var status, NativeMethods.WaitOptions.Wnohang);
                if (r > 0)
                {
                    p.OnReaped(PtyProcess.ExtractExitCode(status));
                    return true;
                }

                if (r == 0)
                    return false; // still running

                var err = Marshal.GetLastPInvokeError();
                if (err == NativeMethods.Eintr)
                    continue;

                // ECHILD (reaped elsewhere / not our child) or an unexpected error:
                // record the exit code as unknown instead of throwing here, which would
                // kill the shared reaper thread and leave every other session unreaped.
                p.OnReaped(-1);
                return true;
            }
        }
    }
}

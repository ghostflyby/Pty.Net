using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
#if LINUX
// Needed only by the eventfd wake-channel drain below (Unsafe.AsPointer), which the
// macOS build does not compile. Guarded so IDE "remove unused usings" cleanups — run
// under the macOS compile context, where this branch is absent — cannot see or delete it.
using System.Runtime.CompilerServices;
#endif

namespace Ghostflyby.Pty;

/// <summary>
/// Unix half of <see cref="PtyReaper"/>: one event-driven reaper thread waits on the
/// kernel's process-exit notification instead of polling waitpid(WNOHANG) every 10ms.
/// Linux watches a pidfd per process via epoll (the fd turns readable on exit); macOS
/// watches EVFILT_PROC | NOTE_EXIT on a kqueue. An idle watched process therefore holds
/// no periodic wakeup, and an exiting child is collected the moment the kernel reports
/// it — exit latency drops from a poll tick to microseconds, and the per-process poll
/// syscall cost vanishes entirely. Unix-only: compiled only by the non-Windows target
/// (see csproj).
///
/// Registration races are closed by construction: <c>WatchProcess</c> records the
/// process under a lock and queues it; the loop drains that queue before waiting, reaps
/// children that already exited, and only then registers the survivors for an exit event.
/// So a child that exits between spawn and registration is reaped by the drain's own
/// waitpid, and one that exits right after registration still fires the event — no
/// process can be missed. The wake channel (eventfd on Linux, EVFILT_USER on macOS)
/// interrupts the wait when a new process is queued.
///
/// A registration that fails while the child is still alive (transient fd pressure, an
/// old kernel without pidfd) moves the process to a retry list that is scanned in full
/// on a short bounded interval. Every retry-waiting process is re-tried each interval —
/// never a serial queue where one failing registration would stall the others — so a
/// persistent failure degrades the whole set to a slow scan instead of losing children.
/// </summary>
internal static partial class PtyReaper
{
    private static readonly Lazy<ReaperThread> Reaper =
        new(() => new ReaperThread(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static partial void WatchPlatform(PtyProcess process) => Reaper.Value.WatchProcess(process);

    private sealed class ReaperThread
    {
        private const int MaxEvents = 64;
        private const int Eintr = NativeMethods.Eintr;
        // Retry interval for processes whose registration failed: long enough that a
        // transient resource shortage usually clears, short enough that the child's exit
        // is still collected promptly. Only active while a retry is pending — otherwise
        // the wait blocks indefinitely.
        private const int RetryIntervalMs = 100;
        // macOS-only: EVFILT_USER ident for the self-wake channel (any non-conflicting id).
        private const nuint WakeIdent = 1;

        // Event source: an epoll instance on Linux, a kqueue on macOS.
        private readonly int eventFd;
#if LINUX
        // Linux-only: the eventfd used to wake the loop when a process is queued.
        private readonly int wakeFd;
#endif

        private readonly Lock sync = new();
        private readonly Dictionary<int, PtyProcess> byPid = [];
        // macOS-only: pids seen stuck mid-exit (tty teardown wait) -> first-seen tick.
        // After StuckExitGraceMs the reaper closes the pty master to end the wait.
        private readonly Dictionary<int, long> stuckExiting = [];
        // Grace window before the reaper closes the master on a stuck mid-exit: long
        // enough that a reader polling the output at a human cadence keeps draining,
        // short enough that "wait for Exited without reading" stays responsive.
        private const int StuckExitGraceMs = 2000;
        // Processes whose Watch arrived and are not yet registered. Drained every loop
        // iteration; a registration failure moves the process to retryWatch.
        private readonly Queue<PtyProcess> pendingWatch = [];
        // Processes whose registration failed while they were still alive. Scanned in
        // full every RetryIntervalMs: each is reaped if it has exited or re-registered
        // if it has not. A scan always touches every entry, so no process can starve
        // behind a persistently failing one.
        private readonly List<PtyProcess> retryWatch = [];

#if LINUX
        // Linux-only: pid -> pidfd, for unregistering a reaped process. Kept out of the
        // shared field block (where the macOS build would see it as never used).
        private readonly Dictionary<int, int> pidToFd = [];

        // epoll_event is packed (12 bytes) on x86_64 and natural (16 bytes) elsewhere
        // (see NativeMethods.EpollIsPacked); the reaper selects the variant at runtime.
        private readonly NativeMethods.EpollEvent[] linuxEvents = new NativeMethods.EpollEvent[MaxEvents];
        private readonly NativeMethods.EpollEventPacked[] linuxEventsPacked = new NativeMethods.EpollEventPacked[MaxEvents];
        // epoll_ctl(EPOLL_CTL_DEL) needs a non-null event pointer on older kernels but
        // ignores its contents; one reused struct per layout avoids per-reap construction.
        private NativeMethods.EpollEvent nullEpollEvent;
        private NativeMethods.EpollEventPacked nullEpollEventPacked;
#elif OSX
        private readonly NativeMethods.Kevent[] macEvents = new NativeMethods.Kevent[MaxEvents];
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif

        public ReaperThread()
        {
#if LINUX
            eventFd = NativeMethods.epoll_create1(NativeMethods.EpollCloexec);
            if (eventFd < 0)
                throw new IOException($"epoll_create1 failed: errno={Marshal.GetLastPInvokeError()}");

            wakeFd = NativeMethods.eventfd(0, NativeMethods.EfdCloexec | NativeMethods.EfdNonblock);
            if (wakeFd < 0)
                throw new IOException($"eventfd failed: errno={Marshal.GetLastPInvokeError()}");

            // Register the wake channel: its data slot stores 0 — pids are always >= 1,
            // so the loop can unambiguously tell a wake event from a process event.
            if (NativeMethods.EpollIsPacked)
            {
                var wakeP = new NativeMethods.EpollEventPacked { Events = NativeMethods.EpollIn, Data = 0 };
                if (NativeMethods.epoll_ctl_packed(eventFd, NativeMethods.EpollCtlAdd, wakeFd, ref wakeP) != 0)
                    throw new IOException($"epoll_ctl (wake) failed: errno={Marshal.GetLastPInvokeError()}");
            }
            else
            {
                var wake = new NativeMethods.EpollEvent { Events = NativeMethods.EpollIn, Data = 0 };
                if (NativeMethods.epoll_ctl(eventFd, NativeMethods.EpollCtlAdd, wakeFd, ref wake) != 0)
                    throw new IOException($"epoll_ctl (wake) failed: errno={Marshal.GetLastPInvokeError()}");
            }
#elif OSX
            eventFd = NativeMethods.kqueue();
            if (eventFd < 0)
                throw new IOException($"kqueue failed: errno={Marshal.GetLastPInvokeError()}");

            // EVFILT_USER self-wake channel; EV_CLEAR resets it after each delivery, so
            // the loop needs no explicit drain after processing a wake event.
            var wake = new NativeMethods.Kevent
            {
                Ident = WakeIdent,
                Filter = NativeMethods.EvfilUser,
                Flags = NativeMethods.EvAdd | NativeMethods.EvClear,
            };
            if (NativeMethods.kevent(eventFd, [wake], 1, null, 0, IntPtr.Zero) != 0)
                throw new IOException($"kevent (wake) failed: errno={Marshal.GetLastPInvokeError()}");
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif

            var thread = new Thread(Loop) { IsBackground = true, Name = "Pty.Net-reaper" };
            thread.Start();
        }

        public void WatchProcess(PtyProcess process)
        {
            PtyDiagnostics.Log($"watch enqueue pid={process.Pid}");
            lock (sync)
            {
                byPid[process.Pid] = process;
                pendingWatch.Enqueue(process);
            }
            Wake();
        }

        // ------------------------------------------------------------- loop

        [DoesNotReturn]
        private void Loop()
        {
            // An unhandled exception here fails the process fast with the wait error
            // rather than silently leaving every child unreaped (and every exit wait
            // hung forever).
            while (true)
            {
                DrainPendingWatch();
                ScanRetryWatch();
                var n = WaitForEvents();
                for (var i = 0; i < n; i++)
                {
                    if (IsWakeEvent(i))
                    {
                        // A new process is queued; the loop top drains it. On Linux the
                        // wake eventfd must also be drained here: it is level-triggered
                        // and every Wake() adds 1 to its counter, so without reading the
                        // counter back to zero the channel stays readable forever and the
                        // loop spins at 100% CPU from the first watched process on.
                        DrainWake();
                        continue;
                    }
                    ReapProcess(EventPid(i));
                }
#if OSX
                // Two macOS failure modes need a net below the knote:
                //   1. EVFILT_PROC/NOTE_EXIT can stay silent for a registered child that
                //      already exited (a plain waitpid(WNOHANG) collects it).
                //   2. A session-leader child holding the ctty can park mid-exit — still
                //      not waitpid-able — until the pty master is closed (see
                //      PtyProcess.IsStuckExiting). The bounded wait below plus the scan
                //      covers both: waitpid first, then a grace-windowed master close.
                // The scan runs only on the reaper thread, so the single-reaper
                // ownership of waitpid is preserved.
                ScanRegisteredProcesses();
#endif
            }
        }

        /// <summary>
        /// Registers every queued process: reaps those that already exited (the spawn-to-
        /// register window), registers the survivors for an exit event. Runs on the loop
        /// thread, so no registration can interleave with event dispatch. A registration
        /// that fails while the process is still alive moves it to <see cref="retryWatch"/>,
        /// which the loop scans on its own interval.
        /// </summary>
        private void DrainPendingWatch()
        {
            while (true)
            {
                PtyProcess? process;
                lock (sync)
                {
                    if (pendingWatch.Count == 0)
                        return;
                    process = pendingWatch.Dequeue();
                }
                PtyDiagnostics.Log($"watch drain pid={process.Pid}");

                // Already exited before registration: collect it now, never register.
                if (process.TryReap(out var code))
                {
                    PtyDiagnostics.Log($"watch drain reaped pid={process.Pid} code={code}");
                    stuckExiting.Remove(process.Pid);
                    process.OnReaped(code);
                    lock (sync)
                    {
                        byPid.Remove(process.Pid);
                    }
                    continue;
                }

                if (!Register(process))
                {
                    PtyDiagnostics.Log($"watch register deferred pid={process.Pid}");
                    lock (sync)
                    {
                        retryWatch.Add(process);
                    }
                }
            }
        }

        /// <summary>
        /// Re-tries every process whose registration failed: reap it if it has exited
        /// (the exit may have happened while no pidfd was registered), otherwise attempt
        /// registration again. Runs before each wait; the wait is bounded while this list
        /// is non-empty, so the scan repeats on <see cref="RetryIntervalMs"/>.
        /// </summary>
        private void ScanRetryWatch()
        {
            for (var i = retryWatch.Count - 1; i >= 0; i--)
            {
                var process = retryWatch[i];
                if (process.TryReap(out var code))
                {
                    stuckExiting.Remove(process.Pid);
                    process.OnReaped(code);
                    retryWatch.RemoveAt(i);
                    lock (sync)
                    {
                        byPid.Remove(process.Pid);
                    }
                    continue;
                }
                if (Register(process))
                    retryWatch.RemoveAt(i);
                // Still alive and still failing: leave it for the next scan.
            }
        }

        /// <summary>
        /// Registers <paramref name="process"/> for an exit event. Returns true when it is
        /// registered (or was collected); false when it is still alive but registration
        /// failed and the caller should retry later.
        /// </summary>
        private bool Register(PtyProcess process)
        {
            var pid = process.Pid;
            PtyDiagnostics.Log($"register begin pid={pid}");
#if LINUX
            // A retry may follow a previous pidfd_open on the same pid (registration
            // failed after the fd was created): close the stale fd before opening a new
            // one, or every retry would leak a fd (fatal under a low RLIMIT_NOFILE).
            lock (sync)
            {
                if (pidToFd.Remove(pid, out var stale))
                    NativeMethods.close(stale);
            }

            var pidfd = (int)NativeMethods.syscall(NativeMethods.PidfdOpenSyscallNumber, pid, 0);
            if (pidfd < 0)
            {
                // The child exited between the drain's waitpid and pidfd_open, or the
                // kernel predates pidfd (ENOSYS): reap directly when possible.
                return TryReapProcess(process);
            }
            lock (sync)
            {
                pidToFd[pid] = pidfd;
            }
            var registered = false;
            if (NativeMethods.EpollIsPacked)
            {
                var evP = new NativeMethods.EpollEventPacked { Events = NativeMethods.EpollIn, Data = (ulong)pid };
                registered = NativeMethods.epoll_ctl_packed(eventFd, NativeMethods.EpollCtlAdd, pidfd, ref evP) == 0;
            }
            else
            {
                var ev = new NativeMethods.EpollEvent { Events = NativeMethods.EpollIn, Data = (ulong)pid };
                registered = NativeMethods.epoll_ctl(eventFd, NativeMethods.EpollCtlAdd, pidfd, ref ev) == 0;
            }
            if (!registered)
            {
                lock (sync)
                {
                    pidToFd.Remove(pid);
                }
                NativeMethods.close(pidfd);
                return TryReapProcess(process);
            }
            return true;
#elif OSX
            var ev = new NativeMethods.Kevent
            {
                Ident = (nuint)pid,
                Filter = NativeMethods.EvfilProc,
                Flags = NativeMethods.EvAdd,
                Fflags = NativeMethods.NoteExit,
            };
            if (NativeMethods.kevent(eventFd, [ev], 1, null, 0, IntPtr.Zero) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                PtyDiagnostics.Log($"register kevent failed pid={pid} errno={errno}");
                // Same fallback as above: the child exited between waitpid and kevent.
                return TryReapProcess(process);
            }
            PtyDiagnostics.Log($"register kevent succeeded pid={pid}");

            // The kernel does not backfill an already-fired NOTE_EXIT: if the child exited
            // after the drain's waitpid but before the EV_ADD above, the knote registers on
            // a zombie and no event will ever be delivered. Re-check once after a successful
            // registration; if the child is gone now, unregister and collect it.
            if (process.TryReap(out var code))
            {
                PtyDiagnostics.Log($"register postcheck reaped pid={pid} code={code}");
                Unregister(pid);
                process.OnReaped(code);
                lock (sync)
                {
                    byPid.Remove(process.Pid);
                }
                return true;
            }
            return true;
#endif
        }

        /// <summary>
        /// Reaps <paramref name="process"/> when it has exited; true when collected.
        /// Called from the registration failure paths, where the child may have exited
        /// between the drain's waitpid and the failed registration attempt.
        /// </summary>
        private bool TryReapProcess(PtyProcess process)
        {
            if (!process.TryReap(out var code))
                return false;
            PtyDiagnostics.Log($"try-reap completed pid={process.Pid} code={code}");
            stuckExiting.Remove(process.Pid);
            process.OnReaped(code);
            lock (sync)
            {
                byPid.Remove(process.Pid);
            }
            return true;
        }

        /// <summary>
        /// Collects the process that fired an exit event. The kernel has confirmed the
        /// exit, so waitpid succeeds; a failure (extreme race) re-registers instead of
        /// losing the child.
        /// </summary>
        private void ReapProcess(int pid)
        {
            PtyDiagnostics.Log($"reap event pid={pid}");
            PtyProcess? process;
            lock (sync)
            {
                if (!byPid.Remove(pid, out process))
                    return;
            }

            Unregister(pid);

            if (process.TryReap(out var code))
            {
                PtyDiagnostics.Log($"reap event waitpid completed pid={pid} code={code}");
                stuckExiting.Remove(pid);
                process.OnReaped(code);
                return;
            }

            // The event fired but the wait did not collect the child (kernel/race
            // anomaly): re-register so the next event can finish the job.
            lock (sync)
            {
                byPid[pid] = process;
            }
            if (!Register(process))
            {
                lock (sync)
                {
                    retryWatch.Add(process);
                }
            }
        }

        /// <summary>
        /// macOS safety net: reaps every registered child that has already exited, whether
        /// or not its knote ever fired (see the loop comment). A child parked mid-exit on
        /// the tty teardown wait (see <see cref="PtyProcess.IsStuckExiting"/>) gets a grace
        /// window for a reader to drain the master; if none does, the master is closed to
        /// end the wait — the child has already finished exiting at that point. Runs on
        /// the reaper thread.
        /// </summary>
        private void ScanRegisteredProcesses()
        {
            PtyProcess[] snapshot;
            lock (sync)
            {
                if (byPid.Count == 0)
                    return;
                snapshot = [.. byPid.Values];
            }

            foreach (var process in snapshot)
            {
                if (!process.TryReap(out var code))
                {
                    if (process.IsStuckExiting())
                    {
                        var now = Environment.TickCount64;
                        lock (sync)
                        {
                            if (!stuckExiting.TryGetValue(process.Pid, out var firstSeen))
                                stuckExiting[process.Pid] = firstSeen = now;
                            if (now - firstSeen >= StuckExitGraceMs)
                            {
                                PtyDiagnostics.Log($"stuck-exit close-master pid={process.Pid} after={now - firstSeen}ms");
                                process.CloseTerminalForStuckExit();
                            }
                            else
                            {
                                PtyDiagnostics.Log($"stuck-exit pending pid={process.Pid} grace={now - firstSeen}ms");
                            }
                        }
                    }
                    continue;
                }
                stuckExiting.Remove(process.Pid);
                Unregister(process.Pid);
                if (process.OnReaped(code))
                {
                    lock (sync)
                    {
                        byPid.Remove(process.Pid);
                        retryWatch.Remove(process);
                    }
                }
            }
        }

        private void Unregister(int pid)
        {
#if LINUX
            int pidfd;
            lock (sync)
            {
                if (!pidToFd.Remove(pid, out pidfd))
                    return;
            }
            if (NativeMethods.EpollIsPacked)
                NativeMethods.epoll_ctl_packed(eventFd, NativeMethods.EpollCtlDel, pidfd, ref nullEpollEventPacked);
            else
                NativeMethods.epoll_ctl(eventFd, NativeMethods.EpollCtlDel, pidfd, ref nullEpollEvent);
            NativeMethods.close(pidfd);
#elif OSX
            var ev = new NativeMethods.Kevent
            {
                Ident = (nuint)pid,
                Filter = NativeMethods.EvfilProc,
                Flags = NativeMethods.EvDelete,
            };
            _ = NativeMethods.kevent(eventFd, [ev], 1, null, 0, IntPtr.Zero);
#endif
        }

        // -------------------------------------------------------- events

        private unsafe int WaitForEvents()
        {
            while (true)
            {
                // While a registration retry is pending, bound the wait so the retry list
                // gets its periodic scan; a clean state blocks indefinitely (no periodic
                // wakeups).
#if LINUX
                var retryPending = retryWatch.Count > 0;
                var timeout = retryPending ? RetryIntervalMs : -1;
                var n = NativeMethods.EpollIsPacked
                    ? NativeMethods.epoll_wait_packed(eventFd, linuxEventsPacked, MaxEvents, timeout)
                    : NativeMethods.epoll_wait(eventFd, linuxEvents, MaxEvents, timeout);
#elif OSX
                // Always bounded on macOS: a dropped NOTE_EXIT (see the loop's scan
                // comment) must not leave the wait blocked indefinitely.
                var ts = new NativeMethods.TimeSpec
                {
                    TvSec = RetryIntervalMs / 1000,
                    TvNsec = (RetryIntervalMs % 1000) * 1_000_000,
                };
                var timeoutPtr = (IntPtr)(&ts);
                var n = NativeMethods.kevent(eventFd, null, 0, macEvents, MaxEvents, timeoutPtr);
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif
                if (n >= 0)
                {
                    // Hot path: the bounded macOS wait returns up to ten times per
                    // second per watched child — build the message only when enabled.
                    if (PtyDiagnostics.Enabled)
                        PtyDiagnostics.Log($"wait events result={n}");
                    return n;
                }
                if (Marshal.GetLastPInvokeError() == Eintr)
                    continue;
                throw new IOException($"Pty.Net reaper wait failed: errno={Marshal.GetLastPInvokeError()}");
            }
        }

        private bool IsWakeEvent(int i)
        {
#if LINUX
            // The wake channel's data slot is 0 (see the constructor); pids are always
            // >= 1, so 0 unambiguously identifies the wake event.
            return NativeMethods.EpollIsPacked
                ? linuxEventsPacked[i].Data == 0
                : linuxEvents[i].Data == 0;
#elif OSX
            return macEvents[i].Ident == WakeIdent && macEvents[i].Filter == NativeMethods.EvfilUser;
#endif
        }

        private int EventPid(int i)
        {
#if LINUX
            return (int)(NativeMethods.EpollIsPacked ? linuxEventsPacked[i].Data : linuxEvents[i].Data);
#elif OSX
            return (int)macEvents[i].Ident;
#endif
        }

        /// <summary>Wakes the wait so the loop drains newly queued processes.</summary>
        private void Wake()
        {
#if LINUX
            // eventfd is non-blocking and only ever holds a counter, so this never blocks.
            ulong val = 1;
            unsafe
            {
                _ = NativeMethods.write(wakeFd, (IntPtr)Unsafe.AsPointer(ref val), (nuint)sizeof(ulong));
            }
#elif OSX
            // NOTE_TRIGGER on the registered EVFILT_USER wakes the loop; EV_CLEAR resets
            // the event, so repeated triggers are not coalesced into one delivery.
            var ev = new NativeMethods.Kevent
            {
                Ident = WakeIdent,
                Filter = NativeMethods.EvfilUser,
                Flags = 0,
                Fflags = NativeMethods.NoteTrigger,
            };
            while (true)
            {
                var result = NativeMethods.kevent(eventFd, [ev], 1, null, 0, IntPtr.Zero);
                if (result == 0)
                {
                    PtyDiagnostics.Log("wake succeeded");
                    return;
                }

                var errno = Marshal.GetLastPInvokeError();
                PtyDiagnostics.Log($"wake failed result={result} errno={errno}");
                if (errno != Eintr)
                    return;
            }
#endif
        }

        /// <summary>
        /// Reads the wake channel back to zero. Linux-only: the eventfd counter accumulates
        /// every <see cref="Wake"/> and the channel is level-triggered in the poll set, so
        /// the loop must drain it after each wake event, or it stays readable forever and
        /// the loop spins. macOS needs no drain — EVFILT_USER is registered with EV_CLEAR
        /// and resets on delivery.
        /// </summary>
        private void DrainWake()
        {
#if LINUX
            // The value is a dummy: reading the eventfd counter back to zero is all that
            // matters (the byte count is discarded).
            ulong val = 0;
            unsafe
            {
                _ = NativeMethods.read(wakeFd, (IntPtr)Unsafe.AsPointer(ref val), (nuint)sizeof(ulong));
            }
#endif
        }
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ghostflyby.Pty;

/// <summary>
/// Unix half of <see cref="PtyReaper"/>: one event-driven reaper thread waits on the
/// kernel's process-exit notification instead of polling waitpid(WNOHANG) every 10 ms.
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

        private readonly object sync = new();
        private readonly Dictionary<int, PtyProcess> byPid = [];
        // Processes whose Watch arrived and are not yet registered. Drained every loop
        // iteration; a registration failure moves the process to retryWatch.
        private readonly Queue<PtyProcess> pendingWatch = [];
        // Processes whose registration failed while they were still alive. Scanned in
        // full every RetryIntervalMs: each is reaped if it has exited or re-registered
        // if it has not. A scan always touches every entry, so no process can starve
        // behind a persistently failing one.
        private readonly List<PtyProcess> retryWatch = [];
        // Linux-only: pid -> pidfd, for unregistering a reaped process.
        private readonly Dictionary<int, int> pidToFd = [];

#if LINUX
        private readonly NativeMethods.EpollEvent[] linuxEvents = new NativeMethods.EpollEvent[MaxEvents];
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
            var wake = new NativeMethods.EpollEvent { Events = NativeMethods.EpollIn, Data = 0 };
            if (NativeMethods.epoll_ctl(eventFd, NativeMethods.EpollCtlAdd, wakeFd, ref wake) != 0)
                throw new IOException($"epoll_ctl (wake) failed: errno={Marshal.GetLastPInvokeError()}");
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
            lock (sync)
            {
                byPid[process.Pid] = process;
                pendingWatch.Enqueue(process);
            }
            Wake();
        }

        // ------------------------------------------------------------- loop

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
                        continue; // a new process is queued; the loop top drains it
                    ReapProcess(EventPid(i));
                }
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

                // Already exited before registration: collect it now, never register.
                if (process.TryReap(out var code))
                {
                    process.OnReaped(code);
                    lock (sync)
                    {
                        byPid.Remove(process.Pid);
                    }
                    continue;
                }

                if (!Register(process))
                {
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
#if LINUX
            // A retry may follow a previous pidfd_open on the same pid (registration
            // failed after the fd was created): close the stale fd before opening a new
            // one, or every retry would leak an fd (fatal under a low RLIMIT_NOFILE).
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
            var ev = new NativeMethods.EpollEvent { Events = NativeMethods.EpollIn, Data = (ulong)pid };
            if (NativeMethods.epoll_ctl(eventFd, NativeMethods.EpollCtlAdd, pidfd, ref ev) != 0)
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
                // Same fallback as above: the child exited between waitpid and kevent.
                return TryReapProcess(process);
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
            PtyProcess? process;
            lock (sync)
            {
                if (!byPid.TryGetValue(pid, out process))
                    return;
                byPid.Remove(pid);
            }

            Unregister(pid);

            if (process.TryReap(out var code))
            {
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

        private void Unregister(int pid)
        {
#if LINUX
            int pidfd;
            lock (sync)
            {
                if (!pidToFd.Remove(pid, out pidfd))
                    return;
            }
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
                var retryPending = retryWatch.Count > 0;
#if LINUX
                var timeout = retryPending ? RetryIntervalMs : -1;
                var n = NativeMethods.epoll_wait(eventFd, linuxEvents, MaxEvents, timeout);
#elif OSX
                // kevent's timeout is a struct timespec*; null means block indefinitely.
                var ts = new NativeMethods.TimeSpec
                {
                    TvSec = RetryIntervalMs / 1000,
                    TvNsec = (RetryIntervalMs % 1000) * 1_000_000,
                };
                var timeoutPtr = retryPending ? (IntPtr)(&ts) : IntPtr.Zero;
                var n = NativeMethods.kevent(eventFd, null, 0, macEvents, MaxEvents, timeoutPtr);
#else
#error "The Unix path supports macOS (define OSX) or Linux (define LINUX) only."
#endif
                if (n >= 0)
                    return n;
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
            return linuxEvents[i].Data == 0;
#elif OSX
            return macEvents[i].Ident == WakeIdent && macEvents[i].Filter == NativeMethods.EvfilUser;
#endif
        }

        private int EventPid(int i)
        {
#if LINUX
            return (int)linuxEvents[i].Data;
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
            _ = NativeMethods.kevent(eventFd, [ev], 1, null, 0, IntPtr.Zero);
#endif
        }

#if LINUX
        // epoll_ctl(EPOLL_CTL_DEL) needs a non-null event pointer on older kernels but
        // ignores its contents; one reused struct avoids per-reap construction.
        private NativeMethods.EpollEvent nullEpollEvent;
#endif
    }
}

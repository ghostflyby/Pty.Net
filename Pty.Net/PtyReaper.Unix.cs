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
        private readonly Queue<PtyProcess> pendingWatch = [];
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
        /// thread, so no registration can interleave with event dispatch.
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

                Register(process);
            }
        }

        private void Register(PtyProcess process)
        {
            var pid = process.Pid;
#if LINUX
            var pidfd = NativeMethods.pidfd_open(pid, 0);
            if (pidfd < 0)
            {
                // The child exited between the drain's waitpid and pidfd_open (or the
                // kernel predates pidfd): reap directly instead of registering.
                if (!TryReapOrRequeue(process))
                    Requeue(process);
                return;
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
                // Registration failed (e.g. the fd set is full): reap directly if the
                // child is gone, otherwise re-queue so a later attempt can register it.
                if (!TryReapOrRequeue(process))
                    Requeue(process);
            }
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
                if (!TryReapOrRequeue(process))
                    Requeue(process);
            }
#endif
        }

        /// <summary>Reaps <paramref name="process"/> when it has exited; true when collected.</summary>
        private bool TryReapOrRequeue(PtyProcess process)
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

        /// <summary>Returns a process whose registration failed back to the pending queue for a later attempt.</summary>
        private void Requeue(PtyProcess process)
        {
            lock (sync)
            {
                pendingWatch.Enqueue(process);
            }
            Wake(); // the loop may be blocked in the wait; wake it to drain the queue
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
            Register(process);
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

        private int WaitForEvents()
        {
            while (true)
            {
#if LINUX
                var n = NativeMethods.epoll_wait(eventFd, linuxEvents, MaxEvents, -1);
#elif OSX
                var n = NativeMethods.kevent(eventFd, null, 0, macEvents, MaxEvents, IntPtr.Zero);
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

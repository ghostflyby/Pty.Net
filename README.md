# Pty.Net

The multi-platform pty wrapper in pure C# with P/Invoke: drive interactive shells with a real terminal on Windows, macOS, and Linux.

[![CI](https://github.com/ghostflyby/Pty.Net/actions/workflows/ci.yml/badge.svg)](https://github.com/ghostflyby/Pty.Net/actions/workflows/ci.yml) · [![NuGet Version](https://img.shields.io/nuget/v/Ghostflyby.Pty)](https://www.nuget.org/packages/Ghostflyby.Pty) · License: [Apache-2.0](LICENSE)

## Features

- **Real pseudo-terminal sessions** — full-screen programs, job control, and terminal escape sequences behave as they do in a real terminal.
- **Cross-platform** — ConPTY on Windows; `posix_openpt` + fork/exec on macOS and Linux. Architecture-neutral managed IL (one package covers x64 and arm64).
- **Text and raw I/O** — `Input`/`Output` text facades over the raw `BaseStream`.
- **Deterministic termination** — a configurable graceful-close window, then a force kill; `Dispose` blocks until the cleanup has actually completed.
- **Exit notification with the terminal result** — `Exited` supplies the process after its normal exit code or Unix termination signal has been published.
- **AOT compatible** — no reflection, no dynamic loading; the pty stays out of the way of trimmed/published apps.

## Install

```sh
dotnet add package Ghostflyby.Pty
```

## Quick start

Launch an interactive shell and drive it like a user at a terminal:

```csharp
using Ghostflyby.Pty;

// An interactive bash session in a real pty. The bare name resolves through
// PATH on every platform, like Process.Start — pass bash.exe (Git for Windows)
// or powershell.exe on Windows.
using var bash = PtyProcess.Start("bash", ["--noprofile", "--norc", "-i"]);

// Write a command and read until a marker proves the output landed.
bash.Input.WriteLine("echo hello-from-pty; echo __DONE__");
var output = ReadUntil(bash.Output, "__DONE__", TimeSpan.FromSeconds(10));
                                                   // contains "hello-from-pty"
```

`ReadUntil` is a small helper — the pty emits a prompt and terminal control
sequences alongside the payload, so the reliable pattern is "read until a marker":

```csharp
static string ReadUntil(StreamReader reader, string marker, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    var text = "";
    while (!text.Contains(marker, StringComparison.Ordinal))
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException($"timed out waiting for '{marker}'");
        var buf = new char[4096];
        var n = reader.ReadAsync(buf, 0, buf.Length).AsTask().WaitAsync(timeout).GetAwaiter().GetResult();
        if (n == 0) break;                 // child exited
        text += new string(buf, 0, n);
    }
    return text;
}
```

## Launch configuration

`PtyStartInfo` mirrors the commonly used subset of `ProcessStartInfo` and accepts one
directly via its `ProcessStartInfo` constructor, so existing launch code ports by
changing only the factory call:

```csharp
using System.Collections.Immutable;
using Ghostflyby.Pty;

var info = new PtyStartInfo("/bin/sh")
{
    Arguments   = ["-c", "echo $GREETING"],
    Column      = 100,            // initial terminal width in characters
    Row         = 40,             // initial terminal height in characters
    Environment = ImmutableDictionary<string, string?>.Empty
                      .Add("GREETING", "hello"),   // overrides, merged into the parent env at launch
};

using var p = PtyProcess.Start(info);

// Resize the live terminal; the child re-lays out immediately (SIGWINCH / ConPTY).
p.Resize(120, 50);
```

Environment semantics: the child inherits the parent's environment by default; entries
in `Environment` override inherited variables, and a `null` value removes one.

## Termination

| Method | Unix | Windows |
|---|---|---|
| `RequestClose()` | `SIGHUP` | `CTRL_CLOSE_EVENT` (async) |
| `Kill()` | `SIGKILL` | `TerminateProcess` |
| `Dispose()` / `DisposeAsync()` | `SIGHUP` → wait → `SIGKILL` | `CTRL_CLOSE_EVENT` → wait → `TerminateProcess` |

- **`RequestClose`** asks the terminal session to close; the child decides how to
  handle it. Fire-and-forget, like `Kill`.
- **`Kill`** force-terminates without cleanup.
- **`Dispose`** blocks until the cleanup is actually done: it sends the graceful
  signal, waits `GracefulExitTimeout` (default 30 s, configurable) for the child to
  exit on its own, force-kills if it does not, then blocks until the reaper has
  collected the child. A child that ignores the graceful signal is terminated, never
  left running in the background.
- **`DisposeAsync`** has the same semantics without blocking a thread. Discard the
  returned task for fire-and-forget; the internal grace window and force-kill still
  complete the cleanup. To impose an outer deadline:
  `await p.DisposeAsync().AsTask().WaitAsync(5s)`.

A manual graceful-termination pattern works on both platforms:

```csharp
p.RequestClose();
if (!p.WaitForExit(TimeSpan.FromSeconds(5)))
    p.Kill();
```

## Waiting and exit notification

```csharp
p.Exited += process =>
{
    if (process.ExitCode is int code)
        Console.WriteLine($"child exited with {code}");
    else
        Console.WriteLine($"child terminated by signal {process.TerminationSignal}");
};

p.WaitForExit();                        // blocks until reaped
bool ok = p.WaitForExit(TimeSpan.FromSeconds(5));
bool ok2 = await p.WaitForExitAsync(TimeSpan.FromSeconds(5));   // thread-pool-free
await p.WaitForExitAsync();             // null timeout = wait indefinitely
```

While the child is running, `ExitCode` and `TerminationSignal` are both `null`.
After it is reaped, `HasExited` is `true` and exactly one is non-null: normal
exits populate `ExitCode`; Unix signal termination populates `TerminationSignal`
with the platform's native positive signal number (`SIGKILL` is 9 on the
supported platforms). Windows always reports an exit code. A shell can itself
exit normally with `128 + signal`; that remains an exit code and is distinct
from this library observing its direct child die from a signal.

`Exited` fires on the shared reaper thread after the terminal result is published;
the handler must not block, and exceptions it throws are swallowed. The event is
not replayed to late subscribers: await a wait method and inspect the properties
when the child may already have exited. Exit waits, including disposal, are
released before handlers run, so disposal does not wait for a handler to finish.

## Platform notes

- **Windows** needs Windows 10 1809 (build 17763) or later (ConPTY).
- **Architectures** — the package ships runtimes for win-x64, win-arm64, linux-x64,
  linux-arm64, osx-x64, and osx-arm64; the assemblies are architecture-neutral managed
  IL. CI runs the full suite natively on one runner per RID — Windows x64 and arm64,
  Linux x64 and arm64 (plus glibc and musl containers), and macOS arm64 and Intel —
  so every shipped RID is exercised. (The Windows arm64 runner is a GitHub public
  preview; `macos-15-intel` is GitHub's final Intel image, retiring August 2027.)
- The pty merges the child's **stdout and stderr** into the single `Output` stream —
  there is no separate stderr, as in a real terminal.

## Caveats

- `Input`/`Output` (text) and `BaseStream` (raw bytes) read the same channel; never
  mix both on the same direction.
- While `WaitForExit`/`WaitForExitAsync` run, output is drained so the child never
  blocks on a full pty buffer. The drained output is preserved and remains readable
  after the wait, on every platform — the trade-off is memory: a child producing
  pathological output volume while being waited on grows the buffer with its output.
  For such children, consume the output concurrently instead of waiting.
- The process owns the underlying stream: disposing `Input` or `Output` alone never
  breaks the other facade or the process; `Dispose`/`DisposeAsync` closes everything.
- `InheritParentEnvironment = false` (allowlist) is strict: the child receives exactly
  the listed variables, nothing is injected implicitly. Terminal-aware programs need
  `TERM` — add it explicitly, e.g. `Environment = new Dictionary<string, string?> { ["TERM"] = "xterm-256color" }`.

## License

[Apache License 2.0](LICENSE)

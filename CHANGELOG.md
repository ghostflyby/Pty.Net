# Changelog

Notable changes per release. Each released version also has a GitHub Release
(https://github.com/ghostflyby/Pty.Net/releases) and a tag; add a section here
as part of the release.

## Unreleased

- Publish pipeline: snupkg symbols + SourceLink (`PublishRepositoryUrl`),
  build-provenance attestation for the assembled package, and tag validation
  (tag commit must be a main ancestor with green CI; a version already on
  nuget.org is refused before any build — this also makes the no-`--skip-duplicate`
  push safe).
- `dotnet nuget push` no longer passes `--skip-duplicate`: a duplicate-version
  push now fails loudly instead of being silently dropped.
- `global.json` pins the .NET 10 SDK (floor 10.0.100, `latestFeature` roll-forward).
- `SECURITY.md` (private vulnerability reporting) and this changelog added.

## 0.3.0 — 2026-09-01

Unix spawn rebuilt around fork/exec with a real controlling terminal, plus the
reliability and packaging work that came out of stress-testing it:

- **Unix spawn**: `posix_openpt` + `fork()`; the child becomes a session leader
  (`setsid`) and reopens the pty slave without `O_NOCTTY` to obtain a genuine
  controlling terminal (macOS `posix_spawn` with `SETEXEC|CLOEXEC_DEFAULT`,
  Linux `execve` + `close_range(2)` sweep). Replaces `posix_spawnp`.
- **Fork robustness**: no-GC region around the fork survives force-termination;
  a child wedged by the fork-vs-GC race is detected via the exec-result timeout,
  its stack captured (Linux `/proc`, macOS `sample`) and the launch retried once.
- **Transient `posix_openpt` failures** under concurrent spawning are retried
  with backoff (Darwin leaks the raw negated errno, so detection is by return code).
- **Reaper**: macOS exit events get a `waitpid` safety-net scan; stuck-exit
  children get a grace window, then the master is closed to end the wait;
  `Kill` uses compare-and-exchange on `killRequested`; `Exited` handler
  exceptions are isolated; exits are published exactly once.
- **API semantics**: `PtyStartInfo` is a value type in practice — content-based
  equality/hash/operators; `ProcessStartInfo` conversion normalizes the working
  directory and preserves empty quoted arguments.
- **Streams**: output produced before/during `WaitForExit` is preserved and
  readable (replay buffer, sync and async); the process owns the underlying
  stream — disposing `Input`/`Output` alone never kills the other facade.
- **Environment**: strict allowlist; nothing is injected implicitly (callers
  must set `TERM` explicitly).
- **Windows**: full 32-bit exit codes (no `int.MinValue` sentinel), empty
  arguments preserved, resize/start sizes validated against `COORD`'s short range.
- **Package**: `ref/net10.0` + `runtimes/{rid}` layout with zero flat `lib/`
  entries; a restore matrix proves each supported RID gets exactly its own dll,
  unsupported RIDs get compile-only assets (nuget.org's 0.2.2 handed
  ios-arm64/browser-wasm a wrong-OS dll at runtime).
- Tests: controlling-terminal coverage (`/dev/tty`, session-leader + foreground
  process group, `tty(1)`), stream-semantics matrix, async exit notifications.

## 0.2.2 — 2026-08-18

- CI: package smoke split out of the publish workflow into a manual workflow
  (the feed's indexing delay makes in-lockstep smoke flaky by design).
- CI: first NuGet-valid package assembly (later reworked in 0.3.0).
- Dependency bumps (xunit.runner.visualstudio 4.0.0, Microsoft.NET.Test.Sdk
  18.9.0, upload-artifact v7, download-artifact v8).

## 0.2.1 — 2026-08-18

Published to nuget.org on 2026-08-18. **No tag was pushed at the time**, so the
exact source commit was not recorded; the back-filled `v0.2.1` tag anchors the
closest reconstructed commit (`84cb82a`, "assemble nupkg with valid NuGet
layout", same day). Treat this entry as approximate.

## 0.2.0 — 2026-08-15

- `PtyStartInfo.InheritParentEnvironment` for allowlist-style environments
  (overrides merged over the parent environment).
- Publish pipeline gained a package smoke test on the full OS matrix.
- `PublicAPI.Shipped.txt` is updated automatically after each release.

## 0.1.0 — 2026-08-13

Initial release: `PtyProcess`/`PtyStream` over ConPTY (Windows) and a
posix pty (macOS/Linux), reaper-based process lifetime, resize, encodings.

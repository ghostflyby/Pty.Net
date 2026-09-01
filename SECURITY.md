# Security policy

## Supported versions

| Version | Supported |
|---------|-----------|
| 0.3.x   | ✅        |
| < 0.3   | ❌ — please upgrade |

## Reporting a vulnerability

Please do **not** open a public issue for anything you believe is exploitable.

Use GitHub's private vulnerability reporting for this repository:
**Security → Report a vulnerability**. The channel is enabled and reaches the
maintainer directly; nobody else can see the report while it is triaged.

You can also enable coordinated disclosure yourself — if you notify us privately
first, we will credit you in the fix release notes unless you prefer otherwise.

## What to include

- The package version (`Ghostflyby.Pty`) and the OS/architecture.
- A minimal reproduction, ideally a small console program.
- Your assessment of the impact, if you have one.

## Expectations

- Triage within 7 days.
- A fix or a mitigation plan within 30 days for accepted reports.
- We publish a GitHub security advisory and credit the reporter on request.

## Scope notes

Ghostflyby.Pty spawns child processes on a pseudo-terminal and owns their
lifetime (signal delivery, fd/kqueue-epoll ownership, ConPTY handles). Of
particular interest:

- Any way to make the library execute a binary other than the one the caller
  named, or inject arguments/environment across the spawn boundary.
- Escaping the pty sandbox: the parent acquiring the child's controlling
  terminal, or the child gaining fds it must not receive.
- PID-reuse hazards in the reaper/kill paths.
- Passwords or tokens supplied by the caller leaking into logs, diagnostics, or
  child environments beyond the caller's explicit allowlist.

Out of scope: issues that require the caller to already execute arbitrary code
in the parent process, and social engineering of terminal users.

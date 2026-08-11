# Verifies the Linux (glibc) build of Pty.Net (package: Ghostflyby.Pty) inside a container.
#
# The library P/Invokes openpty + posix_spawn, which resolve to glibc here with
# different constants than macOS: POSIX_SPAWN_SETSID=0x80 (vs 0x0400 on Darwin),
# EAGAIN=11 (vs 35), POSIX_SPAWN_SETSIGDEF to reset inherited SIG_IGNs (macOS does
# that automatically), and fd isolation via per-fd addclose(3..1024) file actions
# (vs POSIX_SPAWN_CLOEXEC_DEFAULT). `docker build` runs the full xUnit suite, so a
# green build == the Linux implementation passes every test (fd isolation, signal
# isolation, 24-way concurrent sessions, job control, exit codes).

FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src

# Run tests as a non-root user: bash's default interactive prompt is '#' for root
# and '$' otherwise, and the test suite's ReadUntil("$") markers rely on the '$'.
RUN useradd -m testuser && chown -R testuser:testuser /src

USER testuser

# Restore first so package downloads are cached in an early layer.
# COPY runs as root even after USER, so --chown keeps the tree writable by testuser.
COPY --chown=testuser:testuser Pty.Net.slnx ./
COPY --chown=testuser:testuser Pty.Net/Pty.Net.csproj Pty.Net/
COPY --chown=testuser:testuser Pty.Net.Tests/Pty.Net.Tests.csproj Pty.Net.Tests/
RUN dotnet restore

# Copy sources (bin/obj/.git excluded via .dockerignore) and run the test suite.
COPY --chown=testuser:testuser . .
RUN dotnet test --no-restore --nologo

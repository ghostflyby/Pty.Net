using Ghostflyby.Pty;

// Consumes the real Ghostflyby.Pty package (from nuget.org via the package reference)
// the way a user would, then runs one real pty session. The publish workflow runs this
// on the same OS matrix as the test suite to prove the shipped package is usable on
// every advertised platform, including the architecture-specific P/Invoke paths.
// Bare-name resolution (fork/exec on Unix, CreateProcess on Windows) is exercised
// on purpose: it is part of the published behavior.

// Platform: one bare name that is reliably on PATH everywhere. Runtime-selected — the
// smoke program compiles identically on every OS (single source set). The Windows
// branch deliberately names cmd.exe, not bash: a bare "bash" on Windows resolves to
// C:\Windows\SYSTEM32\bash.exe (WSL), which has no distro on the runners.
string shell;
string[] shellArgs;
if (OperatingSystem.IsWindows())
{
    shell = "cmd.exe";
    shellArgs = ["/c", "echo smoke-ok & echo __DONE__"];
}
else
{
    shell = "bash";
    shellArgs = ["--noprofile", "--norc", "-c", "echo smoke-ok; echo __DONE__"];
}

using var p = PtyProcess.Start(shell, shellArgs);

var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
var text = "";
while (!text.Contains("__DONE__", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
{
    var buf = new char[256];
    var n = await p.Output.ReadAsync(buf, 0, buf.Length);
    if (n == 0)
        break;
    text += new string(buf, 0, n);
}

if (!text.Contains("smoke-ok", StringComparison.Ordinal))
{
    Console.Error.WriteLine($"SMOKE-FAIL: marker not found. Got: {text}");
    return 1;
}

p.RequestClose();
if (!p.WaitForExit(TimeSpan.FromSeconds(5)))
    p.Kill();

Console.WriteLine("SMOKE-OK");
return 0;

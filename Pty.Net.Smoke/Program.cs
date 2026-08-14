using Ghostflyby.Pty;

// Consumes the real Ghostflyby.Pty package (from nuget.org via the package reference)
// the way a user would, then runs one real pty session. The publish workflow runs this
// on the same OS matrix as the test suite to prove the shipped package is usable on
// every advertised platform, including the architecture-specific P/Invoke paths.
// Bare-name resolution (posix_spawnp on Unix, CreateProcess on Windows) is exercised
// on purpose: it is part of the published behavior.

// Platform: one bare name that is reliably on PATH everywhere.
#if WINDOWS
const string shell = "cmd.exe";
string[] shellArgs = ["/c", "echo smoke-ok & echo __DONE__"];
#else
const string shell = "bash";
string[] shellArgs = ["--noprofile", "--norc", "-c", "echo smoke-ok; echo __DONE__"];
#endif

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

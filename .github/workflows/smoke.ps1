# Shared post-assembly / post-publish smoke test: consumes a Ghostflyby.Pty nupkg the
# way a real user would, from a local source, and runs one real pty session. Fails the
# step (and, in the publish job, aborts the release) if the package cannot be restored,
# its RID-specific dll is not resolved for the current platform, or the session output
# is wrong. Parameterized over the package directory so the same script gates the
# assembled package before push and re-validates the published artifact from a release.
#
# Usage: pwsh -File .github/workflows/smoke.ps1 -SourceDir <dir containing *.nupkg>
param(
    [Parameter(Mandatory = $true)][string]$SourceDir
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# The smoke test must consume exactly the package passed in, never a stale copy: an
# earlier build of the same version could be sitting in the global packages cache.
# Clear it so the local source is authoritative. (CI runners start clean anyway.)
dotnet nuget locals global-packages --clear | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet nuget locals failed: $LASTEXITCODE" }

$nupkg = Get-ChildItem -Path $SourceDir -Filter '*.nupkg' | Select-Object -First 1
if (-not $nupkg) { throw "no .nupkg found in '$SourceDir'" }
$version = $nupkg.BaseName -replace '^Ghostflyby\.Pty\.', ''

New-Item -ItemType Directory -Force -Path 'smoke' | Out-Null
Push-Location 'smoke'
try {
    dotnet new console --framework net10.0 --output . --force | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet new failed: $LASTEXITCODE" }

    dotnet add package Ghostflyby.Pty --version $version --source (Resolve-Path $SourceDir) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet add package failed: $LASTEXITCODE" }

    @'
using Ghostflyby.Pty;

// Launch a real bash session through the package's pty and read until a marker.
using var p = PtyProcess.Start("bash", ["--noprofile", "--norc", "-c", "echo smoke-ok; echo __DONE__"]);
var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
var text = "";
while (!text.Contains("__DONE__") && DateTime.UtcNow < deadline)
{
    var buf = new char[256];
    var n = await p.Output.ReadAsync(buf, 0, buf.Length);
    if (n == 0) break;
    text += new string(buf, 0, n);
}
if (!text.Contains("smoke-ok"))
{
    Console.Error.WriteLine("SMOKE-FAIL: marker not found. Got: " + text);
    return 1;
}
p.RequestClose();
if (!p.WaitForExit(TimeSpan.FromSeconds(5)))
    p.Kill();
Console.WriteLine("SMOKE-OK");
return 0;
'@ | Set-Content -Path Program.cs

    dotnet run
    if ($LASTEXITCODE -ne 0) { throw "smoke run failed: $LASTEXITCODE" }
    Write-Host "smoke passed: Ghostflyby.Pty $version"
}
finally {
    Pop-Location
}

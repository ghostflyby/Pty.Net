namespace Ghostflyby.Pty.Tests;

/// <summary>
/// The deterministic exception contract of <see cref="PtyProcess.Start(PtyStartInfo)"/>:
/// every launch failure a caller can provoke deliberately maps to a named BCL exception,
/// not a platform-shaped one. The Unix errno translation and the Windows CreateProcessW
/// error-code translation throw the same types for the same mistakes, so these tests run
/// unchanged on every platform the suite covers.
/// </summary>
public class StartExceptionTests
{
    /// <summary>A path that is guaranteed not to exist, whichever platform we run on.</summary>
    private static string MissingPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"pty-{name}-{Guid.NewGuid():N}");

    // --- argument validation -------------------------------------------------

    [Fact]
    public void Start_StringOverload_NullFile_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PtyProcess.Start(null!, []));
    }

    [Fact]
    public void Start_StringOverload_NullArguments_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PtyProcess.Start("/bin/echo", null!));
    }

    [Fact]
    public void Start_StartInfoOverload_NullInfo_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PtyProcess.Start(null!));
    }

    [Fact]
    public void Start_StringOverload_EmptyFileName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PtyProcess.Start("", []));
    }

    [Fact]
    public void Start_StartInfoOverload_WhitespaceFileName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PtyProcess.Start(new PtyStartInfo { FileName = "   " }));
    }

    // --- launch failures ------------------------------------------------------

    /// <summary>A missing executable is FileNotFoundException, not a generic IOException.</summary>
    [Fact]
    public void Start_MissingExecutable_ThrowsFileNotFoundException()
    {
        var missing = MissingPath("exec");

        var ex = Assert.Throws<FileNotFoundException>(() => PtyProcess.Start(missing, []));

        Assert.Contains(missing, ex.Message);
    }

    /// <summary>A missing working directory is DirectoryNotFoundException. The directory is
    /// validated in the parent before the spawn, so this holds even when the executable is
    /// otherwise fine.</summary>
    [Fact]
    public void Start_MissingWorkingDirectory_ThrowsDirectoryNotFoundException()
    {
        var missing = MissingPath("workdir");

        var ex = Assert.Throws<DirectoryNotFoundException>(
            () => PtyProcess.Start(TestBash.BashPath, [], missing));

        Assert.Contains(missing, ex.Message);
    }
}

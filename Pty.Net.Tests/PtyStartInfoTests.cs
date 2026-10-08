using System.Collections.Immutable;
using System.Diagnostics;

namespace Ghostflyby.Pty.Tests;

/// <summary>
/// Value semantics of <see cref="PtyStartInfo"/>: equality, hash and the
/// <c>==</c>/<c>!=</c> operators compare by content — the collection properties
/// entry-by-entry, regardless of the concrete collection implementation.
/// </summary>
public class PtyStartInfoTests
{
    [Fact]
    public void Equality_ComparesCollectionContents()
    {
        var a = new PtyStartInfo("/bin/bash")
        {
            Arguments = ["-c", "echo hi"],
            Environment = ImmutableDictionary<string, string?>.Empty.Add("K", "v"),
        };
        var b = new PtyStartInfo("/bin/bash")
        {
            Arguments = ["-c", "echo hi"],
            Environment = new Dictionary<string, string?> { ["K"] = "v" },
        };

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_ArgumentOrderMatters()
    {
        var a = new PtyStartInfo("/bin/bash") { Arguments = ["a", "b"] };
        var b = new PtyStartInfo("/bin/bash") { Arguments = ["b", "a"] };

        Assert.True(a != b);
    }

    [Fact]
    public void Equality_EnvironmentValueDifferenceMatters()
    {
        var a = new PtyStartInfo("/bin/bash") { Environment = ImmutableDictionary<string, string?>.Empty.Add("K", "v") };
        var b = new PtyStartInfo("/bin/bash") { Environment = ImmutableDictionary<string, string?>.Empty.Add("K", "other") };

        Assert.True(a != b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equality_EncodingsCompareByCodePage()
    {
        var a = new PtyStartInfo("/bin/bash") { OutputEncoding = System.Text.Encoding.UTF8 };
        // A different Encoding instance with the same code page is the same encoding.
        var b = new PtyStartInfo("/bin/bash") { OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false) };

        Assert.True(a == b);
    }

    [Fact]
    public void Conversion_EmptyWorkingDirectory_FallsBackToCurrentDirectory()
    {
        // ProcessStartInfo's default WorkingDirectory is the empty string — copying it
        // verbatim would make StartCore reject the launch with DirectoryNotFoundException.
        var psi = new ProcessStartInfo("/bin/echo");
        Assert.Equal(string.Empty, psi.WorkingDirectory);

        var info = new PtyStartInfo(psi);

        Assert.Equal(Environment.CurrentDirectory, info.WorkingDirectory);
    }

    [Fact]
    public void Conversion_PreservesEmptyQuotedArguments()
    {
        var psi = new ProcessStartInfo("/bin/echo") { Arguments = "prog \"\" x" };

        var info = new PtyStartInfo(psi);

        Assert.Equal(["prog", "", "x"], info.Arguments);
    }
}

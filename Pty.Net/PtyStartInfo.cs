using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Ghostflyby.Pty;

/// <summary>
/// Describes how to launch a child process inside a pseudo-terminal.
/// <para>
/// Mirrors the commonly used subset of <see cref="ProcessStartInfo"/> and accepts one
/// directly via <see cref="PtyStartInfo(ProcessStartInfo)"/>, so existing launch code
/// ports by changing only the factory call. Features that do not map to a pty launch
/// (<c>RedirectStandardOutput</c>, <c>UseShellExecute</c>, …) are deliberately absent.
/// </para>
/// </summary>
public sealed record PtyStartInfo
{
    /// <summary>The executable to run, e.g. <c>/bin/bash</c> or <c>cmd.exe</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments passed to <see cref="FileName"/>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Initial working directory of the child; defaults to the parent's current directory.</summary>
    public string WorkingDirectory { get; init; } = System.Environment.CurrentDirectory;

    /// <summary>
    /// Encoding used to encode text written to <see cref="PtyProcess.Input"/>.
    /// <para>Defaults to UTF-8.</para>
    /// </summary>
    public Encoding InputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Encoding used to decode text read from <see cref="PtyProcess.Output"/>.
    /// <para>Defaults to UTF-8.</para>
    /// </summary>
    public Encoding OutputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>Initial terminal width in character columns. Defaults to 120.</summary>
    public int Cols { get; init; } = 120;

    /// <summary>Initial terminal height in character rows. Defaults to 30.</summary>
    public int Rows { get; init; } = 30;

    /// <summary>
    /// Environment variables passed to the child.
    /// <para>Defaults to a copy of the parent's environment.</para>
    /// </summary>
    public IDictionary<string, string?> Environment => env.Value;

    private readonly Lazy<IDictionary<string, string?>> env = new(SnapshotParentEnvironment);

    /// <summary>A snapshot of the parent's environment, shared by <see cref="PtyProcess"/> for launches without an explicit one.</summary>
    internal static Dictionary<string, string?> SnapshotParentEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value;
        return env;
    }

    /// <summary>Creates an empty launch description; set <see cref="FileName"/> before starting.</summary>
    public PtyStartInfo()
    {
    }

    /// <summary>Creates a launch description for <paramref name="fileName"/>.</summary>
    /// <param name="fileName">The executable to run (see <see cref="FileName"/>).</param>
    [SetsRequiredMembers]
    public PtyStartInfo(string fileName)
    {
        FileName = fileName;
    }

    /// <summary>
    /// Creates a launch description from <paramref name="psi"/>.
    /// <para>Copies the file name, arguments, working directory, environment and stream encodings.</para>
    /// </summary>
    /// <param name="psi">The <see cref="ProcessStartInfo"/> to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="psi"/> is null.</exception>
    [SetsRequiredMembers]
    public PtyStartInfo(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        FileName = psi.FileName;
        WorkingDirectory = psi.WorkingDirectory;
        if (psi.StandardInputEncoding is { } inputEncoding)
            InputEncoding = inputEncoding;
        if (psi.StandardOutputEncoding is { } outputEncoding)
            OutputEncoding = outputEncoding;
        foreach (var kv in psi.Environment)
            Environment[kv.Key] = kv.Value;
        Arguments = psi.ArgumentList.Count == 0 ? ParseArguments(psi.Arguments) : [.. psi.ArgumentList];
    }

    /// <summary>Resolves the effective argument list passed to the child.</summary>
    internal string[] ResolveArguments()
    {
        return [.. Arguments];
    }

    /// <summary>
    /// Splits a command line the way a shell splits words: whitespace separates, and
    /// single or double quotes group characters (including whitespace) into one word.
    /// No escape sequences or expansion — just the quoting that launch configs use.
    /// </summary>
    private static string[] ParseArguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var c in arguments)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);
            }
            else
                switch (c)
                {
                    case '\'' or '"':
                        quote = c;
                        break;
                    case ' ' or '\t' or '\n' or '\r':
                        if (current.Length > 0)
                        {
                            result.Add(current.ToString());
                            current.Clear();
                        }

                        break;
                    default:
                        current.Append(c);
                        break;
                }
        }

        if (current.Length > 0)
            result.Add(current.ToString());
        return [.. result];
    }
}

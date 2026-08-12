using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Ghostflyby.Pty;

/// <summary>
/// Describes how to launch a child process inside a pseudo-terminal. Mirrors the
/// commonly used subset of <see cref="ProcessStartInfo"/> (which is sealed and cannot
/// be extended); a <see cref="ProcessStartInfo"/> can be converted with
/// <see cref="PtyStartInfo(ProcessStartInfo)"/>, so existing launch code ports by
/// changing only the factory call. Everything the PTY launch does not map to
/// (<c>RedirectStandardOutput</c>, <c>UseShellExecute</c>, …) is deliberately absent.
/// </summary>
public sealed record PtyStartInfo
{
    /// <summary>The executable to run, e.g. <c>/bin/bash</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>Explicit argument list.</summary>
    public IReadOnlyList<string> ArgumentList { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>Initial working directory of the child; the parent's when null.</summary>
    public string WorkingDirectory { get; init; } = System.Environment.CurrentDirectory;

    /// <summary>
    /// Encoding used to encode text written to <see cref="PtyProcess.StandardInput"/>,
    /// like <see cref="ProcessStartInfo.StandardInputEncoding"/>. Null means UTF-8
    /// (the terminal default on both macOS and Linux).
    /// </summary>
    public Encoding StandardInputEncoding { get; init; } = Encoding.UTF8;

    /// <summary>
    /// Encoding used to decode the child's output read from <see cref="PtyProcess.StandardOutput"/>,
    /// like <see cref="ProcessStartInfo.StandardOutputEncoding"/>. Null means UTF-8.
    /// (A pty merges stdout and stderr, so there is no separate stderr encoding.)
    /// </summary>
    public Encoding? StandardOutputEncoding { get; init; } = Encoding.UTF8;

    private readonly Lazy<IDictionary<string, string?>> env = new(SnapshotParentEnvironment);

    /// <summary>
    /// Environment of the child. Lazily initialized to a copy of the parent's
    /// environment on first access (like <see cref="ProcessStartInfo.Environment"/>),
    /// so an untouched info inherits the current environment.
    /// </summary>
    public IDictionary<string, string?> Environment => env.Value;

    /// <summary>A snapshot of the parent's environment, shared by <see cref="PtyProcess"/> for launches without an explicit one.</summary>
    internal static Dictionary<string, string?> SnapshotParentEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            env[(string)e.Key] = (string?)e.Value;
        return env;
    }

    /// <summary>An empty launch description (set <see cref="FileName"/> before starting).</summary>
    public PtyStartInfo()
    {
    }

    [SetsRequiredMembers]
    public PtyStartInfo(string fileName)
    {
        FileName = fileName;
    }

    /// <summary>
    /// Copies <see cref="ProcessStartInfo.FileName"/>, <c>Arguments</c>/<c>ArgumentList</c>,
    /// <see cref="ProcessStartInfo.WorkingDirectory"/>, <c>Environment</c> and the standard
    /// stream encodings from <paramref name="psi"/>, so an existing
    /// <c>ProcessStartInfo</c> can be reused as-is for a PTY launch.
    /// </summary>
    [SetsRequiredMembers]
    public PtyStartInfo(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        FileName = psi.FileName;
        WorkingDirectory = psi.WorkingDirectory;
        if (psi.StandardInputEncoding is { } inputEncoding)
            StandardInputEncoding = inputEncoding;
        if (psi.StandardOutputEncoding is { } outputEncoding)
            StandardOutputEncoding = outputEncoding;
        foreach (var kv in psi.Environment)
            Environment[kv.Key] = kv.Value;
        ArgumentList = psi.ArgumentList.Count == 0 ? ParseArguments(psi.Arguments) : [.. psi.ArgumentList];
    }

    /// <summary>Resolves the effective argument list passed to the child.</summary>
    internal string[] ResolveArguments()
    {
        return [.. ArgumentList];
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
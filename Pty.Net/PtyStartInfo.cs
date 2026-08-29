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
/// <para>
/// Value semantics: <c>==</c>/<c>!=</c>, <see cref="Equals(PtyStartInfo?)"/> and
/// <see cref="GetHashCode"/> compare by content — the collection properties
/// (<see cref="Arguments"/>, <see cref="Environment"/>) entry-by-entry, not by
/// reference. Properties are typed as read-only interfaces so a builder can hand over
/// any collection implementation. This is a plain configuration object, not an
/// immutable value: mutate the properties freely while building a launch.
/// </para>
/// </summary>
public sealed class PtyStartInfo : IEquatable<PtyStartInfo>
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
    public int Column { get; init; } = 120;

    /// <summary>Initial terminal height in character rows. Defaults to 30.</summary>
    public int Row { get; init; } = 30;

    /// <summary>
    /// Environment variables for the child. Entries here override the inherited parent
    /// environment at launch; a null value removes the inherited variable. Defaults to
    /// empty, so the child inherits the parent's environment unchanged unless overridden.
    /// Read-only to keep launches reproducible: to add a variable, assign a new
    /// dictionary, e.g. <c>info.Environment = ImmutableDictionary.Create&lt;string, string?&gt;().Add("K", "v")</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = ImmutableDictionary<string, string?>.Empty;

    /// <summary>
    /// Whether the child inherits the parent's environment variables.
    /// <para>True (default): <see cref="Environment"/> is an override set merged over
    /// the parent's environment at launch. False: the child receives only the variables
    /// explicitly listed in <see cref="Environment"/> — an allowlist, for environments
    /// that must not leak host variables. (With no parent set to remove from, a null
    /// value in <see cref="Environment"/> then simply does nothing.)</para>
    /// </summary>
    public bool InheritParentEnvironment { get; init; } = true;

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
    /// <para>Copies the file name, arguments, working directory, environment and stream encodings.
    /// An empty <c>psi.WorkingDirectory</c> (ProcessStartInfo's default) is normalized to the
    /// current directory, because an empty path is not a launchable directory.</para>
    /// </summary>
    /// <param name="psi">The <see cref="ProcessStartInfo"/> to copy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="psi"/> is null.</exception>
    [SetsRequiredMembers]
    public PtyStartInfo(ProcessStartInfo psi)
    {
        ArgumentNullException.ThrowIfNull(psi);
        FileName = psi.FileName;
        WorkingDirectory = psi.WorkingDirectory is { Length: > 0 }
            ? psi.WorkingDirectory
            : System.Environment.CurrentDirectory;
        if (psi.StandardInputEncoding is { } inputEncoding)
            InputEncoding = inputEncoding;
        if (psi.StandardOutputEncoding is { } outputEncoding)
            OutputEncoding = outputEncoding;
        Environment = psi.Environment.ToImmutableDictionary(StringComparer.Ordinal);
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
    /// A quoted word that ends up empty is preserved as an empty argument
    /// (<c>prog ""</c> passes one empty argument). No escape sequences or expansion —
    /// just the quoting that launch configs use.
    /// </summary>
    private static string[] ParseArguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var hasWord = false;

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
                        hasWord = true; // the word exists even if the quotes close empty
                        break;
                    case ' ' or '\t' or '\n' or '\r':
                        if (hasWord)
                        {
                            result.Add(current.ToString());
                            current.Clear();
                            hasWord = false;
                        }

                        break;
                    default:
                        current.Append(c);
                        hasWord = true;
                        break;
                }
        }

        if (hasWord)
            result.Add(current.ToString());
        return [.. result];
    }

    // --- content-based value equality ---------------------------------------
    // A record's synthesized equality compares properties by reference, which is
    // useless for the IReadOnlyList/IReadOnlyDictionary properties above. These
    // overrides compare by content instead: collections entry-by-entry (Arguments in
    // order; Environment as a set of ordinal key/value pairs), encodings by code page,
    // the rest by value.

    /// <inheritdoc />
    public bool Equals(PtyStartInfo? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;

        return string.Equals(FileName, other.FileName, StringComparison.Ordinal)
            && InheritParentEnvironment == other.InheritParentEnvironment
            && Column == other.Column
            && Row == other.Row
            && InputEncoding.CodePage == other.InputEncoding.CodePage
            && OutputEncoding.CodePage == other.OutputEncoding.CodePage
            && string.Equals(WorkingDirectory, other.WorkingDirectory, StringComparison.Ordinal)
            && Arguments.SequenceEqual(other.Arguments, StringComparer.Ordinal)
            && EnvironmentContentsEqual(Environment, other.Environment);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as PtyStartInfo);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FileName, StringComparer.Ordinal);
        hash.Add(InheritParentEnvironment);
        hash.Add(Column);
        hash.Add(Row);
        hash.Add(InputEncoding.CodePage);
        hash.Add(OutputEncoding.CodePage);
        hash.Add(WorkingDirectory, StringComparer.Ordinal);
        foreach (var argument in Arguments)
            hash.Add(argument, StringComparer.Ordinal);
        // Order-insensitive: the dictionary is a set of overrides, not a sequence.
        var environmentHash = 0;
        foreach (var (key, value) in Environment)
            environmentHash = unchecked(environmentHash
                + (StringComparer.Ordinal.GetHashCode(key) * 397) ^ (value?.GetHashCode() ?? 0));
        hash.Add(environmentHash);
        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"PtyStartInfo {{ FileName = {FileName}, Arguments = [{string.Join(", ", Arguments)}], Column = {Column}, Row = {Row} }}";
    }

    /// <summary>Content-based value equality (collections entry-by-entry; see the type documentation).</summary>
    public static bool operator ==(PtyStartInfo? left, PtyStartInfo? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Content-based value inequality (collections entry-by-entry; see the type documentation).</summary>
    public static bool operator !=(PtyStartInfo? left, PtyStartInfo? right) =>
        !(left == right);

    private static bool EnvironmentContentsEqual(
        IReadOnlyDictionary<string, string?> left, IReadOnlyDictionary<string, string?> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var otherValue))
                return false;
            if (!string.Equals(value, otherValue, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

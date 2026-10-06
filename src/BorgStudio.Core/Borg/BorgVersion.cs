using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BorgStudio.Core.Borg;

/// <summary>
/// Version of the installed borg binary as reported by <c>borg --version</c>,
/// e.g. "borg 1.4.0", "borg 2.0.0b14" or "borg-linux64 1.2.8" (standalone builds print their file name).
/// </summary>
public sealed partial record BorgVersion(int Major, int Minor, int Patch, string? PreRelease = null)
{
    /// <summary>borg 2 reworked the CLI (repo-create, --repo, ...), so command building has to branch on this.</summary>
    public bool IsBorg2 => Major >= 2;

    public static bool TryParse(string? output, [NotNullWhen(true)] out BorgVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var firstLine = output.AsSpan().Trim();
        var lineEnd = firstLine.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
            firstLine = firstLine[..lineEnd];

        var match = VersionPattern().Match(firstLine.ToString());
        if (!match.Success)
            return false;

        var pre = match.Groups["pre"].Value;
        version = new BorgVersion(
            int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
            pre.Length == 0 ? null : pre);
        return true;
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}{PreRelease}";

    [GeneratedRegex(@"^\S+\s+(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<pre>\S*)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

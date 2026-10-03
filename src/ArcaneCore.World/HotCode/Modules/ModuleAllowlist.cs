using System.Globalization;

namespace ArcaneCore.World.HotCode.Modules;

/// <summary>The decision of <see cref="ModuleAllowlist.Check"/>.</summary>
public sealed record AllowlistVerdict(bool Allowed, string Detail);

/// <summary>
/// The module hash allowlist (<c>World:HotCode:Modules:Allowlist</c>): a text file of SHA-256 hashes, one per
/// line, of the module dlls that may be loaded (docs/areas/code-hot-reload.md).
/// <list type="bullet">
/// <item>An empty setting adds no restriction.</item>
/// <item>A configured setting is fail-closed: a missing, unreadable, oversize or malformed file refuses every
/// module (a malformed line is never skipped, or a typo would silently shrink the list the operator reviewed),
/// and so does a hash that is not listed.</item>
/// <item>Format: blank lines and lines starting with <c>#</c> are ignored; otherwise the first token is 64 hex
/// digits (either case) and anything after whitespace is a free label. A trailing <c>#</c> comment is just label text.</item>
/// <item>The file is read on every check, so approving a new build needs no restart.</item>
/// </list>
/// </summary>
public static class ModuleAllowlist
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int HashLength = 64;

    public static AllowlistVerdict Check(string? path, string sha256Hex)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new AllowlistVerdict(true, "no allowlist configured");
        }

        string[] lines;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Refuse($"the allowlist file could not be read (not found): {path}");
            }

            if (info.Length > MaxFileBytes)
            {
                return Refuse($"the allowlist file could not be read (larger than {MaxFileBytes / 1024} KB): {path}");
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Refuse($"the allowlist file could not be read ({ex.GetType().Name}): {path}");
        }

        bool listed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int end = line.AsSpan().IndexOfAny(' ', '\t');
            string token = end < 0 ? line : line[..end];
            if (!IsHash(token))
            {
                return Refuse($"the allowlist file is malformed at line {(i + 1).ToString(CultureInfo.InvariantCulture)} (expected 64 hex digits): {path}");
            }

            if (string.Equals(token, sha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                listed = true;
            }
        }

        return listed
            ? new AllowlistVerdict(true, "listed in the allowlist")
            : Refuse($"sha256 {sha256Hex} is not in the allowlist ({path})");

        static AllowlistVerdict Refuse(string detail) => new(false, detail);
    }

    private static bool IsHash(string token)
        => token.Length == HashLength && token.All(char.IsAsciiHexDigit);
}

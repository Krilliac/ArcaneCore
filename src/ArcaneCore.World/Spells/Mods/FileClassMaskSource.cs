using System.Globalization;
using ArcaneCore.Game.Spells.Mods;

namespace ArcaneCore.World.Spells.Mods;

/// <summary>
/// The class-mask overlay loaded from <c>Spells:Mods:ClassMaskFile</c>: text lines <c>spellId effectIndex mask</c> (mask as
/// <c>0x</c> hex or decimal, a trailing <c>#</c> comment and blank lines allowed), as <c>arcane-content-importer class-masks</c>
/// writes them. Fail closed: a malformed or duplicated line throws <see cref="InvalidDataException"/> naming the file and line,
/// because a silently skipped row would give a talent the wrong spells.
/// </summary>
public sealed class FileClassMaskSource : IClassMaskSource
{
    private readonly Dictionary<(uint Spell, int Effect), ulong> _masks;

    private FileClassMaskSource(Dictionary<(uint Spell, int Effect), ulong> masks) => _masks = masks;

    /// <summary>Rows loaded.</summary>
    public int Count => _masks.Count;

    /// <summary>Rows whose mask needs more than 32 bits.</summary>
    public int WideCount => _masks.Values.Count(m => m > uint.MaxValue);

    public ulong? TryGetMask(uint spellId, int effectIndex) => _masks.TryGetValue((spellId, effectIndex), out ulong mask) ? mask : null;

    public static FileClassMaskSource Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllLines(path), path);
    }

    public static FileClassMaskSource Parse(IEnumerable<string> lines, string origin)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var masks = new Dictionary<(uint, int), ulong>();
        int number = 0;
        foreach (string raw in lines)
        {
            number++;
            int comment = raw.IndexOf('#', StringComparison.Ordinal);
            string line = (comment >= 0 ? raw[..comment] : raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint spell)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int effect) || effect > 2
                || !TryParseMask(parts[2], out ulong mask))
            {
                throw new InvalidDataException($"{origin}: line {number}: expected 'spellId effectIndex(0-2) mask', got '{line}'");
            }

            if (!masks.TryAdd((spell, effect), mask))
            {
                throw new InvalidDataException($"{origin}: line {number}: duplicate row for spell {spell} effect {effect}");
            }
        }

        return new FileClassMaskSource(masks);
    }

    private static bool TryParseMask(string text, out ulong mask)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mask)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out mask);
}

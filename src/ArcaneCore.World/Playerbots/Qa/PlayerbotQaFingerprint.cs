using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// The stable identity of a finding: a canonical key built from the kind and the content ids (quest, entry, map, a 50-yard location cell), hashed
/// with SHA-256. Bot ids and names are never part of it, so the same problem met by ten bots is one finding, and the hash is the same in every
/// process (it is also the dedup marker in GitHub issues, so the key format is versioned: change <see cref="Version"/> with it).
/// Pure and thread-safe.
/// </summary>
internal static partial class PlayerbotQaFingerprint
{
    /// <summary>The key format version (the first part of every key).</summary>
    internal const string Version = "v1";

    /// <summary>The side of a location cell in yards; part of the key format.</summary>
    internal const int CellYards = 50;

    /// <summary>Hex characters of the fingerprint (the marker); the first <see cref="ShortChars"/> are the short id shown in chat.</summary>
    internal const int FullChars = 16;

    internal const int ShortChars = 8;

    [GeneratedRegex(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}|0[xX][0-9a-fA-F]+|\d+", RegexOptions.CultureInvariant)]
    private static partial Regex VolatilePattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    /// <summary>The cell index of a coordinate.</summary>
    internal static int Cell(float coordinate) => (int)Math.Floor(coordinate / CellYards);

    /// <summary>The canonical key of a signal.</summary>
    internal static string Key(QaSignal s)
    {
        string where = s.HasPosition
            ? string.Create(CultureInfo.InvariantCulture, $"|map={s.Map}|cell={Cell(s.X)},{Cell(s.Y)}")
            : string.Empty;
        return Version + "|" + (s.Kind switch
        {
            // A stall outside the bot's home is its own finding (QaCause.OutsideHome); the keys of the other stalls are unchanged.
            QaSignalKind.Stall => string.Create(CultureInfo.InvariantCulture, $"stall|goal={s.Goal}|quest={s.Quest}|target={s.Target}") + where
                + (s.OutsideHome ? "|outside" : string.Empty),
            QaSignalKind.SharedSetAside => s.Quest != 0
                ? string.Create(CultureInfo.InvariantCulture, $"setaside|quest={s.Quest}")
                : string.Create(CultureInfo.InvariantCulture, $"setaside|entry={s.Target}"),
            QaSignalKind.DeathLoop => "deathloop" + where,
            QaSignalKind.ActionFault => $"fault|{s.Goal}|{s.ExceptionType}|{s.FaultCode}|{Normalise(s.Detail)}",
            QaSignalKind.Quarantine => $"quarantine|{s.FaultCode}|{Normalise(s.Detail)}",
            QaSignalKind.Disabled => $"disabled|{s.FaultCode}|{Normalise(s.Detail)}",
            QaSignalKind.RelayDataMissing => s.Entry != 0
                ? string.Create(CultureInfo.InvariantCulture, $"relaydata|relay={s.Relay}|{s.Detail}|path={s.PathId}|entry={s.Entry}")
                : string.Create(CultureInfo.InvariantCulture, $"relaydata|relay={s.Relay}|{Normalise(s.Detail)}"),
            QaSignalKind.RelayScriptMissing => string.Create(CultureInfo.InvariantCulture, $"relayscript|relay={s.Relay}"),
            _ => throw new ArgumentOutOfRangeException(nameof(s)),
        });
    }

    /// <summary>The fingerprint (16 lower-case hex characters) of a signal.</summary>
    internal static string Of(QaSignal signal) => Hash(Key(signal));

    internal static string Hash(string key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..FullChars].ToLowerInvariant();

    internal static string ShortId(string id) => id.Length <= ShortChars ? id : id[..ShortChars];

    /// <summary>A message without guids, hex numbers, numbers and runs of white space, lower-cased and at most 160 characters.</summary>
    internal static string Normalise(string text)
    {
        string flat = WhitespacePattern().Replace(VolatilePattern().Replace(text, "#"), " ").Trim().ToLowerInvariant();
        return flat.Length <= 160 ? flat : flat[..160];
    }
}

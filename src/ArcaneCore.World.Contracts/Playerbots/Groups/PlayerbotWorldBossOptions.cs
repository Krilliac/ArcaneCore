using System.Globalization;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Groups:WorldBosses</c>: bot raids that muster for a world boss that stands up on a continent (Azuregos, Lord Kazzak,
/// the four Dragons of Nightmare while their event runs, and what the content rule derives, Teremus the Devourer among it; docs/areas/playbots-groups.md, World-boss raids). The
/// coordinator watches each catalogued boss, and once one has been up for <see cref="PlayerGraceMinutes"/> with no player at it, the bots of
/// one team near it are asked, a raid forms, walks to the boss, holds back while players have a claim on it, pulls and kills it. Every key is
/// live: <c>.reload config</c> changes this object in place and the coordinator reads it at every decision. The raid's willingness rolls are
/// the cooperation rolls (<see cref="PlayerbotCooperationOptions"/>); the size cap is <see cref="PlayerbotCooperationOptions.MaxRaidSize"/>.
/// </summary>
public sealed class PlayerbotWorldBossOptions
{
    /// <summary>Let bots raid world bosses (on by default). Off: no raid forms; raids under way finish.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between looks at the catalogued bosses (5..600).</summary>
    public int WatchSeconds { get; set; } = 30;

    /// <summary>
    /// Minutes a boss must have been seen up before the bots go for it, so a player gets the first chance (0..240; 0 opens at the first
    /// sighting). The clock restarts when the boss is killed or its grid unloads.
    /// </summary>
    public int PlayerGraceMinutes { get; set; } = 15;

    /// <summary>The distance, in yards, within which a real player counts as "at the boss" for the holds below (0..400; 0 turns the player hold off).</summary>
    public float PlayersNearYards { get; set; } = 100f;

    /// <summary>The number of real players within <see cref="PlayersNearYards"/> that holds the pull (1..40).</summary>
    public int PlayersNearCount { get; set; } = 3;

    /// <summary>The farthest from the boss, in yards, a bot may stand and still be asked to muster (300..20000). The raid walks there.</summary>
    public float MusterRadiusYards { get; set; } = 3000f;

    /// <summary>
    /// Levels below the lower of the boss's level and the player level cap that a bot may stand (0..20), so a level-60 bot qualifies for a
    /// level-63 boss at the default 2 (floor 58).
    /// </summary>
    public int LevelGap { get; set; } = 2;

    /// <summary>The fewest members a raid may start with (5..40; not above <see cref="DefaultRaidSize"/>).</summary>
    public int MinRaidSize { get; set; } = 30;

    /// <summary>The raid size asked for when a boss has no entry in <see cref="SizeOverrides"/> (5..40; the cooperation raid cap bounds it).</summary>
    public int DefaultRaidSize { get; set; } = 40;

    /// <summary>Raid sizes per boss, comma separated <c>entry:size</c> pairs, size 5..40 (for example <c>6109:25,12397:40</c>). Empty: every boss uses <see cref="DefaultRaidSize"/>.</summary>
    public string SizeOverrides { get; set; } = "";

    /// <summary>Seconds an open muster waits for enough willing bots before it gives up (30..1800).</summary>
    public int MusterWindowSeconds { get; set; } = 180;

    /// <summary>The most world-boss raids at once, server wide (0..8; 0 opens none).</summary>
    public int MaxConcurrentRaids { get; set; } = 1;

    /// <summary>The most raids per boss and UTC day that reached the walk to the boss (0..24; 0 opens none). A raid that never left the muster is not counted.</summary>
    public int MaxAttemptsPerDay { get; set; } = 3;

    /// <summary>Seconds before the same boss is mustered for again after a raid or a failed muster (60..86400).</summary>
    public int RetrySeconds { get; set; } = 1800;

    /// <summary>The distance from the boss, in yards, at which the raid gathers before the pull (20..200).</summary>
    public float RallyYards { get; set; } = 50f;

    /// <summary>The fraction of the raid that must stand at the rally before it pulls (0.5..1).</summary>
    public float PresentFraction { get; set; } = 0.8f;

    /// <summary>
    /// Seconds the raid waits at the rally for <see cref="PresentFraction"/> (10..900). Past it the raid pulls with whoever is there when at
    /// least <see cref="MinRaidSize"/> are, and gives up otherwise.
    /// </summary>
    public int PullWaitSeconds { get; set; } = 120;

    /// <summary>
    /// Seconds the raid holds back while somebody else has a claim on the boss (another's tap or fight, real players near) before it gives
    /// the attempt up as taken (10..1800). Waiting for its own members does not count.
    /// </summary>
    public int HoldSeconds { get; set; } = 180;

    /// <summary>Wipes the raid recovers from before it gives up (1..10).</summary>
    public int MaxWipes { get; set; } = 2;

    /// <summary>Minutes a raid may last from its formation (10..480).</summary>
    public int MaxRaidMinutes { get; set; } = 90;

    /// <summary>Teleport a straggler to the raid (off by default: members walk). The follow options' own teleport rules still apply when on.</summary>
    public bool CatchUpTeleport { get; set; }

    /// <summary>Add every rank-3 creature on a continent that is no faction leader, flagged friendly or event placed to the catalogue (on by default).</summary>
    public bool DeriveFromContent { get; set; } = true;

    /// <summary>
    /// Further bosses, comma separated: <c>entry</c> (the creature needs a spawn on map 0 or 1) or <c>entry@map</c> (map 0 or 1; the boss is
    /// placed by something other than a database spawn and is looked for on that map).
    /// </summary>
    public string IncludedEntries { get; set; } = "";

    /// <summary>Entries removed from the catalogue, comma separated; wins over every other source.</summary>
    public string ExcludedEntries { get; set; } = "";

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Groups:WorldBosses";
        Check(WatchSeconds, 5, 600, nameof(WatchSeconds));
        Check(PlayerGraceMinutes, 0, 240, nameof(PlayerGraceMinutes));
        CheckRange(PlayersNearYards, 0f, 400f, nameof(PlayersNearYards));
        Check(PlayersNearCount, 1, 40, nameof(PlayersNearCount));
        CheckRange(MusterRadiusYards, 300f, 20_000f, nameof(MusterRadiusYards));
        Check(LevelGap, 0, 20, nameof(LevelGap));
        Check(MinRaidSize, 5, 40, nameof(MinRaidSize));
        Check(DefaultRaidSize, 5, 40, nameof(DefaultRaidSize));
        if (MinRaidSize > DefaultRaidSize) throw new InvalidOperationException($"{section}: MinRaidSize must not exceed DefaultRaidSize.");
        Check(MusterWindowSeconds, 30, 1800, nameof(MusterWindowSeconds));
        Check(MaxConcurrentRaids, 0, 8, nameof(MaxConcurrentRaids));
        Check(MaxAttemptsPerDay, 0, 24, nameof(MaxAttemptsPerDay));
        Check(RetrySeconds, 60, 86_400, nameof(RetrySeconds));
        CheckRange(RallyYards, 20f, 200f, nameof(RallyYards));
        CheckRange(PresentFraction, 0.5f, 1f, nameof(PresentFraction));
        Check(PullWaitSeconds, 10, 900, nameof(PullWaitSeconds));
        Check(HoldSeconds, 10, 1800, nameof(HoldSeconds));
        Check(MaxWipes, 1, 10, nameof(MaxWipes));
        Check(MaxRaidMinutes, 10, 480, nameof(MaxRaidMinutes));
        foreach (string token in Tokens(SizeOverrides))
            if (!TryParseSizeOverride(token, out _, out _))
                throw new InvalidOperationException($"{section}: SizeOverrides entry '{token}' must be entry:size with a size of 5..40.");
        foreach (string token in Tokens(IncludedEntries))
            if (!TryParseIncluded(token, out _, out _))
                throw new InvalidOperationException($"{section}: IncludedEntries entry '{token}' must be an entry or entry@map with map 0 or 1.");
        foreach (string token in Tokens(ExcludedEntries))
            if (!TryParseEntry(token, out _))
                throw new InvalidOperationException($"{section}: ExcludedEntries entry '{token}' must be a creature entry number.");

        static void CheckRange(float value, float min, float max, string name)
        {
            if (!float.IsFinite(value) || value < min || value > max)
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{section}: {name} must be {min}..{max}."));
        }

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }

    /// <summary>A live-reload check: null when <paramref name="value"/> is within the range, else the problem.</summary>
    internal static string? CheckRange(float value, float min, float max)
        => float.IsFinite(value) && value >= min && value <= max ? null : string.Create(CultureInfo.InvariantCulture, $"must be {min}..{max}");

    /// <summary>A live-reload check of <see cref="SizeOverrides"/>.</summary>
    internal static string? CheckSizeOverrides(string? value)
        => Tokens(value).All(t => TryParseSizeOverride(t, out _, out _)) ? null : "must be entry:size pairs with a size of 5..40";

    /// <summary>A live-reload check of <see cref="IncludedEntries"/>.</summary>
    internal static string? CheckIncluded(string? value)
        => Tokens(value).All(t => TryParseIncluded(t, out _, out _)) ? null : "must be entries or entry@map with map 0 or 1";

    /// <summary>A live-reload check of <see cref="ExcludedEntries"/>.</summary>
    internal static string? CheckExcluded(string? value)
        => Tokens(value).All(t => TryParseEntry(t, out _)) ? null : "must be creature entry numbers";

    /// <summary>The non-empty, trimmed items of a comma separated list.</summary>
    public static IEnumerable<string> Tokens(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) yield break;
        foreach (string part in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) yield return part;
    }

    /// <summary>A creature entry: digits only, above zero.</summary>
    public static bool TryParseEntry(string token, out uint entry)
        => uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out entry) && entry != 0;

    /// <summary><c>entry:size</c> with a size of 5..40.</summary>
    public static bool TryParseSizeOverride(string token, out uint entry, out int size)
    {
        entry = 0;
        size = 0;
        int colon = token.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && TryParseEntry(token[..colon], out entry)
            && int.TryParse(token[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out size) && size is >= 5 and <= 40;
    }

    /// <summary><c>entry</c> (<paramref name="map"/> null) or <c>entry@map</c> with map 0 or 1.</summary>
    public static bool TryParseIncluded(string token, out uint entry, out uint? map)
    {
        entry = 0;
        map = null;
        int at = token.IndexOf('@', StringComparison.Ordinal);
        if (at < 0) return TryParseEntry(token, out entry);
        if (at == 0 || !TryParseEntry(token[..at], out entry)) return false;
        if (!uint.TryParse(token[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out uint continent) || continent > 1) return false;
        map = continent;
        return true;
    }
}

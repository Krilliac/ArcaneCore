using System.Globalization;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Groups:DungeonRuns</c>: free autonomous bots form a five-player group on their own for a dungeon that fits their
/// level and run it (docs/areas/playbots-groups.md, "Dungeon and raid runs"). The dungeon list and each dungeon's level band come from
/// the world database (<c>map_template</c>, <c>areatrigger_teleport</c>, the creatures' levels, <c>instance_encounter</c>); this section
/// tunes the rule. Every key is live: <c>.reload config</c> changes this object in place and the coordinator reads it at every decision.
/// </summary>
public sealed class PlayerbotRunOptions
{
    /// <summary>Let free bots start dungeon runs (on by default). Off: no new run starts; running ones finish. Raid runs also need <see cref="PlayerbotRaidRunOptions.Enabled"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The chance per minute (0..1) that a scan which finds enough fitting bots starts a run. 0 starts none; 1 starts one at once.</summary>
    public double StartChancePerMinute { get; set; } = 0.5;

    /// <summary>The most dungeon runs at once (0..16; 0 starts none).</summary>
    public int MaxConcurrentRuns { get; set; } = 2;

    /// <summary>Wipes a dungeon run survives; the run is given up at this many (1..20).</summary>
    public int MaxWipes { get; set; } = 3;

    /// <summary>The longest a dungeon run lasts, in minutes, from the moment the group forms (10..720).</summary>
    public int MaxRunMinutes { get; set; } = 90;

    /// <summary>The longest the group takes to gather and walk to the entrance, in minutes (5..240).</summary>
    public int TravelTimeoutMinutes { get; set; } = 30;

    /// <summary>Levels below the weakest boss the level band starts (0..20): the band's lower end is the higher of the entrance level and the weakest boss's level minus this.</summary>
    public int BandBelowBoss { get; set; } = 3;

    /// <summary>Levels above the strongest boss the level band ends (0..20).</summary>
    public int BandAboveBoss { get; set; } = 3;

    /// <summary>
    /// Level bands that replace the derived ones. Keys are a map id (<c>36</c>) or a map id and segment (<c>189:2</c>, the third wing of
    /// Scarlet Monastery, counting from 0); values <c>min-max</c> (1..63). Example: <c>"36": "15-22"</c>.
    /// </summary>
    public Dictionary<string, string> LevelBandOverrides { get; set; } = [];

    /// <summary>
    /// Teleport the group to the approach point of the entrance instead of walking there (off by default: the group travels together,
    /// fighting what is in the way). The run never teleports otherwise.
    /// </summary>
    public bool TeleportToEntrance { get; set; }

    /// <summary>A bot farther than this from the entrance (yards, 3-D) is not matched for the run (500..20000).</summary>
    public float MaxTravelYards { get; set; } = 6000f;

    /// <summary>Raid runs: <c>World:Playerbots:Groups:DungeonRuns:Raids</c>.</summary>
    public PlayerbotRaidRunOptions Raids { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Groups:DungeonRuns";
        if (CheckChance(StartChancePerMinute) is { } chance) throw new InvalidOperationException($"{section}: StartChancePerMinute {chance}.");
        if (CheckRange(MaxConcurrentRuns, 0, 16) is { } runs) throw new InvalidOperationException($"{section}: MaxConcurrentRuns {runs}.");
        if (CheckRange(MaxWipes, 1, 20) is { } wipes) throw new InvalidOperationException($"{section}: MaxWipes {wipes}.");
        if (CheckRange(MaxRunMinutes, 10, 720) is { } minutes) throw new InvalidOperationException($"{section}: MaxRunMinutes {minutes}.");
        if (CheckRange(TravelTimeoutMinutes, 5, 240) is { } travel) throw new InvalidOperationException($"{section}: TravelTimeoutMinutes {travel}.");
        if (CheckRange(BandBelowBoss, 0, 20) is { } below) throw new InvalidOperationException($"{section}: BandBelowBoss {below}.");
        if (CheckRange(BandAboveBoss, 0, 20) is { } above) throw new InvalidOperationException($"{section}: BandAboveBoss {above}.");
        if (CheckYards(MaxTravelYards) is { } yards) throw new InvalidOperationException($"{section}: MaxTravelYards {yards}.");
        if (CheckOverrides(LevelBandOverrides) is { } overrides) throw new InvalidOperationException($"{section}: LevelBandOverrides {overrides}.");
        if (Raids is null) throw new InvalidOperationException($"{section}:Raids is missing.");
        Raids.Validate();
    }

    /// <summary>Null when <paramref name="value"/> is within <paramref name="min"/>..<paramref name="max"/>, else the refusal text.</summary>
    internal static string? CheckRange(int value, int min, int max)
        => value >= min && value <= max ? null : string.Create(CultureInfo.InvariantCulture, $"must be {min}..{max}");

    internal static string? CheckChance(double value) => value is >= 0d and <= 1d ? null : "must be 0..1";

    internal static string? CheckYards(float value) => value is >= 500f and <= 20_000f ? null : "must be 500..20000";

    /// <summary>The refusal text for a band override map, or null: keys <c>map</c> or <c>map:segment</c>, values <c>min-max</c> within 1..63, min not above max.</summary>
    internal static string? CheckOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null) return "is missing";
        foreach ((string key, string value) in overrides)
        {
            if (!TryParseKey(key, out _, out _)) return $"key '{key}' must be a map id or 'map:segment'";
            if (!TryParseBand(value, out _, out _)) return $"value '{value}' of '{key}' must be 'min-max' within 1..63";
        }

        return null;
    }

    /// <summary>Parse <c>map</c> or <c>map:segment</c> (segment null for the whole map).</summary>
    internal static bool TryParseKey(string? key, out uint map, out int? segment)
    {
        map = 0;
        segment = null;
        if (string.IsNullOrWhiteSpace(key)) return false;
        string[] parts = key.Split(':');
        if (parts.Length is < 1 or > 2 || !uint.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out map)) return false;
        if (parts.Length == 1) return true;
        if (!int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index)) return false;
        segment = index;
        return true;
    }

    /// <summary>Parse <c>min-max</c> (both 1..63, min not above max).</summary>
    internal static bool TryParseBand(string? text, out byte min, out byte max)
    {
        min = max = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] parts = text.Split('-');
        if (parts.Length != 2 || !byte.TryParse(parts[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out min)
            || !byte.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out max)) return false;
        return min is >= 1 and <= 63 && max is >= 1 and <= 63 && min <= max;
    }
}

/// <summary>
/// <c>World:Playerbots:Groups:DungeonRuns:Raids</c>: free level-60 bots form a raid group for a raid instance when every bot meets the
/// raid's entry requirements, and run it. Live like the rest of the section.
/// </summary>
public sealed class PlayerbotRaidRunOptions
{
    /// <summary>Let free bots start raid runs (on by default; needs <see cref="PlayerbotRunOptions.Enabled"/> too).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Skip the raids' key item, attunement quest and condition requirements (off by default). On, bots are matched without them and the
    /// server lets the members of a bot raid run through the entrance without them. Only managed bots in a bot raid run, only through that
    /// raid's own entrance and never the entrance level: a real player, a bot outside a run and a dungeon run are checked as always.
    /// </summary>
    public bool SkipPrerequisites { get; set; }

    /// <summary>The most raid runs at once (0..4; 0 starts none).</summary>
    public int MaxConcurrentRaids { get; set; } = 1;

    /// <summary>Wipes a raid run survives; the run is given up at this many (1..20).</summary>
    public int MaxWipes { get; set; } = 5;

    /// <summary>The longest a raid run lasts, in minutes, from the moment the raid forms (10..1440).</summary>
    public int MaxRunMinutes { get; set; } = 240;

    /// <summary>Tanks per ten raid members (1..5): a raid of 40 takes 4 at 1, 8 at 2.</summary>
    public int TanksPerTen { get; set; } = 1;

    /// <summary>Healers as a percentage of the raid (10..50): a raid of 40 takes 10 at 25.</summary>
    public int HealerPercent { get; set; } = 25;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Groups:DungeonRuns:Raids";
        if (PlayerbotRunOptions.CheckRange(MaxConcurrentRaids, 0, 4) is { } raids) throw new InvalidOperationException($"{section}: MaxConcurrentRaids {raids}.");
        if (PlayerbotRunOptions.CheckRange(MaxWipes, 1, 20) is { } wipes) throw new InvalidOperationException($"{section}: MaxWipes {wipes}.");
        if (PlayerbotRunOptions.CheckRange(MaxRunMinutes, 10, 1440) is { } minutes) throw new InvalidOperationException($"{section}: MaxRunMinutes {minutes}.");
        if (PlayerbotRunOptions.CheckRange(TanksPerTen, 1, 5) is { } tanks) throw new InvalidOperationException($"{section}: TanksPerTen {tanks}.");
        if (PlayerbotRunOptions.CheckRange(HealerPercent, 10, 50) is { } healers) throw new InvalidOperationException($"{section}: HealerPercent {healers}.");
    }
}

/// <summary>
/// The level band overrides as one reload value: equal when both hold the same keys and bands (the reload compares values with
/// <see cref="object.Equals(object?)"/>).
/// </summary>
internal sealed class PlayerbotLevelBandOverrides(IReadOnlyDictionary<string, string> items) : IEquatable<PlayerbotLevelBandOverrides>
{
    public IReadOnlyDictionary<string, string> Items { get; } = items;

    public bool Equals(PlayerbotLevelBandOverrides? other)
        => other is not null && Items.Count == other.Items.Count
            && Items.All(pair => other.Items.TryGetValue(pair.Key, out string? value) && value == pair.Value);

    public override bool Equals(object? obj) => Equals(obj as PlayerbotLevelBandOverrides);

    public override int GetHashCode() => Items.Count;

    public override string ToString() => Items.Count == 0 ? "{}" : "{" + string.Join(", ", Items.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")) + "}";
}

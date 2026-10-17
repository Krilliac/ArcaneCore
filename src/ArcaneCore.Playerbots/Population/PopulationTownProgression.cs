using ArcaneCore.Game;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>One town of the progression: where it is (the spawn of <see cref="AnchorEntry"/> on <see cref="MapId"/>) and the levels it serves.</summary>
/// <param name="Key">The key stored in the home's place text (at most 14 characters, lower case).</param>
/// <param name="Team">The faction that uses it; null for a neutral town both factions use.</param>
/// <param name="AnchorEntry">The innkeeper, or where a town has none in the data the flight master, whose first spawn is the town centre.</param>
/// <param name="Capital">A faction capital (a master home), never a level-band town.</param>
internal sealed record TownRow(string Key, string Name, Team? Team, uint MapId, int MinLevel, int MaxLevel, uint AnchorEntry, bool Capital = false);

/// <summary>A town whose centre was found in the creature content.</summary>
internal readonly record struct ResolvedTown(TownRow Row, Vector3 Centre);

/// <summary>
/// What a town-stage home's place text says: <see cref="Active"/> is the town the bot is homed in now, <see cref="Master"/> its faction
/// capital and <see cref="Secondary"/> its level-band town (null when none could be found).
/// </summary>
internal readonly record struct TownPlace(string Active, string Master, string? Secondary)
{
    /// <summary>Whether the bot is staying at its capital (otherwise at its level-band town).</summary>
    internal bool AtCapital => string.Equals(Active, Master, StringComparison.Ordinal);
}

/// <summary>Where a town-stage home sits in the capital/secondary cycle, for the brain's idle rule.</summary>
internal enum TownStay : byte
{
    None,
    Secondary,
    Capital,
}

/// <summary>
/// The levelling towns of 1.12 and how a bot of the population moves between them (ArcaneCore's own rule, as the start homes are).
/// <para>
/// <b>Two homes.</b> After <c>FreshReleaseLevel</c> every bot has a <i>master</i> home, its faction capital (Stormwind or Orgrimmar for
/// <c>CapitalMajorSharePct</c> percent of a faction, otherwise one of the other capitals, the bot's own race's first), and a
/// <i>secondary</i> home, the town of its level band from <see cref="Rows"/>. Exactly one is the stored home (the active one); the
/// place text carries all three: <c>town:&lt;active&gt;;m=&lt;master&gt;;s=&lt;secondary&gt;</c>, so no schema changes, a restart
/// loses nothing but the stay clock (a fresh window then starts, so nothing flaps) and an operator's own home (any other place text) is
/// never touched. The sweep alternates them: a stay at the secondary (<c>SecondaryStayMinMinutes</c> to <c>SecondaryStayMaxMinutes</c>), a stay at the capital
/// (<c>CapitalStayMin/MaxMinutes</c>; the trainers, auction house, bank and vendors of the city are inside the capital home), and again.
/// </para>
/// <para>
/// <b>Bands.</b> A secondary is kept until the bot's level reaches the town's <see cref="TownRow.MaxLevel"/> (the hysteresis: no
/// re-homing on every sweep, none while the bands merely overlap); then a town whose band holds the level takes it: the least
/// populated one under <c>TownCap</c>, else the least populated. Bands follow the classic 1.12 zone level ranges, each row a hub of
/// the zone (innkeeper or flight master of the town, so the centre comes from the world data and not from coordinates here).
/// </para>
/// <para>Thread affinity: the static functions are pure. <see cref="Resolve"/> reads the creature content (world thread).</para>
/// </summary>
internal static class PopulationTownProgression
{
    internal const string Prefix = "town:";

    /// <summary>Highest level any row serves (a level-60 bot is served by rows with this maximum).</summary>
    internal const int TopLevel = 61;

    /// <summary>
    /// The 46 rows: the six capitals and the level-band towns from level 10 up. There are no rows for the 5-10 start-zone hubs
    /// (Goldshire, Dolanaar, Razor Hill, Bloodhoof): a bot is in the town stage from <c>FreshReleaseLevel</c> (10), at which a band that
    /// ends at 10 no longer serves it, so such a row could never be picked at the default FreshReleaseLevel. Kharanos and Brill (to 12) serve levels 10 and 11.
    /// </summary>
    internal static readonly IReadOnlyList<TownRow> Rows =
    [
        // ---- the six capitals (master homes). Anchor: the capital's innkeeper. ----
        new("stormwind", "Stormwind City", Team.Alliance, 0, 1, TopLevel, 6740, Capital: true),
        new("ironforge", "Ironforge", Team.Alliance, 0, 1, TopLevel, 5111, Capital: true),
        new("darnassus", "Darnassus", Team.Alliance, 1, 1, TopLevel, 6735, Capital: true),
        new("orgrimmar", "Orgrimmar", Team.Horde, 1, 1, TopLevel, 6929, Capital: true),
        new("undercity", "Undercity", Team.Horde, 0, 1, TopLevel, 6741, Capital: true),
        new("thunder-bluff", "Thunder Bluff", Team.Horde, 1, 1, TopLevel, 6746, Capital: true),

        // ---- Alliance, Eastern Kingdoms (classic 1.12 zone level ranges: Elwynn 1-10, Dun Morogh 1-10, Westfall 10-20, Loch Modan 10-20,
        // Redridge 15-25, Duskwood 18-30, Wetlands 20-30, Hillsbrad 20-30, Arathi 30-40, Stranglethorn 30-45, Hinterlands 40-50,
        // Searing Gorge / Blasted Lands 45-55, Western Plaguelands 51-58, Eastern Plaguelands 53-60) ----
        new("kharanos", "Kharanos", Team.Alliance, 0, 5, 12, 1247),
        new("sentinel-hill", "Sentinel Hill", Team.Alliance, 0, 10, 20, 8931),
        new("thelsamar", "Thelsamar", Team.Alliance, 0, 10, 20, 6734),
        new("lakeshire", "Lakeshire", Team.Alliance, 0, 15, 25, 6727),
        new("darkshire", "Darkshire", Team.Alliance, 0, 20, 30, 6790),
        new("menethil", "Menethil Harbor", Team.Alliance, 0, 20, 30, 1464),
        new("southshore", "Southshore", Team.Alliance, 0, 25, 35, 2352),
        new("refuge-pointe", "Refuge Pointe", Team.Alliance, 0, 30, 40, 2835),
        new("booty-bay", "Booty Bay", null, 0, 35, 45, 6807),
        new("aerie-peak", "Aerie Peak", Team.Alliance, 0, 40, 50, 7744),
        new("nethergarde", "Nethergarde Keep", Team.Alliance, 0, 45, 55, 8609),
        new("thorium-a", "Thorium Point", Team.Alliance, 0, 45, 55, 2941),
        new("chillwind", "Chillwind Camp", Team.Alliance, 0, 50, 60, 12596),
        new("lights-hope", "Light's Hope Chapel", null, 0, 53, TopLevel, 16256),

        // ---- Horde, Eastern Kingdoms (Tirisfal 1-12, Silverpine 10-20, Hillsbrad 18-30, Arathi 30-40, Stranglethorn 30-45,
        // Swamp of Sorrows 35-45, Badlands 35-45, Hinterlands 40-50, Searing Gorge 45-55) ----
        new("brill", "Brill", Team.Horde, 0, 5, 12, 5688),
        new("sepulcher", "The Sepulcher", Team.Horde, 0, 10, 20, 6739),
        new("tarren-mill", "Tarren Mill", Team.Horde, 0, 18, 30, 2388),
        new("hammerfall", "Hammerfall", Team.Horde, 0, 30, 40, 9501),
        new("grom-gol", "Grom'gol Base Camp", Team.Horde, 0, 30, 45, 5814),
        new("stonard", "Stonard", Team.Horde, 0, 35, 45, 6930),
        new("kargath", "Kargath", Team.Horde, 0, 35, 45, 9356),
        new("revantusk", "Revantusk Village", Team.Horde, 0, 40, 50, 14731),
        new("thorium-h", "Thorium Point", Team.Horde, 0, 45, 55, 3305),

        // ---- Alliance, Kalimdor (Teldrassil 1-10, Darkshore 10-20, Ashenvale 18-30, Dustwallow 35-45 (Theramore), Feralas 40-50,
        // Tanaris 40-50, Un'Goro 48-55, Winterspring 50-60, Silithus 55-60) ----
        new("auberdine", "Auberdine", Team.Alliance, 1, 10, 20, 6737),
        new("astranaar", "Astranaar", Team.Alliance, 1, 18, 30, 6738),
        new("theramore", "Theramore Isle", Team.Alliance, 1, 30, 45, 6272),
        new("feathermoon", "Feathermoon Stronghold", Team.Alliance, 1, 40, 50, 7736),

        // ---- Horde, Kalimdor (Durotar 1-10, Mulgore 1-10, Barrens 10-25, Stonetalon 15-27, Ashenvale 18-30, Thousand Needles 25-35,
        // Desolace 30-40, Dustwallow 35-45, Feralas 40-50) ----
        new("crossroads", "The Crossroads", Team.Horde, 1, 10, 25, 3934),
        new("camp-taurajo", "Camp Taurajo", Team.Horde, 1, 15, 25, 7714),
        new("splintertree", "Splintertree Post", Team.Horde, 1, 18, 30, 12196),
        new("sun-rock", "Sun Rock Retreat", Team.Horde, 1, 20, 30, 7731),
        new("freewind-post", "Freewind Post", Team.Horde, 1, 25, 35, 11116),
        new("shadowprey", "Shadowprey Village", Team.Horde, 1, 30, 40, 11106),
        new("brackenwall", "Brackenwall Village", Team.Horde, 1, 35, 45, 11899),
        new("camp-mojache", "Camp Mojache", Team.Horde, 1, 40, 50, 7737),

        // ---- neutral Kalimdor hubs both factions use (Barrens 10-25 Ratchet, Tanaris 40-50, Un'Goro 48-55, Winterspring 50-60, Silithus 55-60) ----
        new("ratchet", "Ratchet", null, 1, 20, 30, 6791),
        new("gadgetzan", "Gadgetzan", null, 1, 40, 50, 7733),
        new("marshals-ref", "Marshal's Refuge", null, 1, 48, 55, 10583),
        new("everlook", "Everlook", null, 1, 50, TopLevel, 11118),
        new("cenarion-hold", "Cenarion Hold", null, 1, 55, TopLevel, 15174),
    ];

    private static readonly Dictionary<string, TownRow> ByKey = Rows.ToDictionary(r => r.Key, StringComparer.Ordinal);

    /// <summary>The row of a key, or null.</summary>
    internal static TownRow? Find(string? key) => key is not null && ByKey.TryGetValue(key, out TownRow? row) ? row : null;

    /// <summary>Whether a row serves a team (its own faction, or a neutral town).</summary>
    internal static bool ServesTeam(TownRow row, Team team) => row.Team is null || row.Team == team;

    /// <summary>The capitals of a team.</summary>
    internal static IEnumerable<TownRow> CapitalsOf(Team team) => Rows.Where(r => r.Capital && r.Team == team);

    // ---- place text ----

    /// <summary>The place text <c>town:&lt;active&gt;;m=&lt;master&gt;[;s=&lt;secondary&gt;]</c>.</summary>
    internal static string PlaceOf(string active, string master, string? secondary)
        => Prefix + active + ";m=" + master + (secondary is null ? string.Empty : ";s=" + secondary);

    /// <summary>Reads a town-stage place text; false for any other (an operator's home, a start home, none) or a key that is not in <see cref="Rows"/>.</summary>
    internal static bool TryParsePlace(string? place, out TownPlace parsed)
    {
        parsed = default;
        if (place is null || !place.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string[] parts = place[Prefix.Length..].Split(';');
        string active = parts[0];
        string? master = null, secondary = null;
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("m=", StringComparison.Ordinal)) master = parts[i][2..];
            else if (parts[i].StartsWith("s=", StringComparison.Ordinal)) secondary = parts[i][2..];
            else return false;
        }

        if (master is null || Find(active) is null || Find(master) is not { Capital: true }) return false;
        if (secondary is not null && Find(secondary) is not { Capital: false }) return false;
        parsed = new TownPlace(active, master, secondary);
        return true;
    }

    /// <summary>
    /// Where the home with this place text sits in the cycle (None for any other home, and for a capital-only home: a bot with no secondary
    /// has no cycle, so its idle bot explores round the capital instead of lingering at the inn for good).
    /// </summary>
    internal static TownStay StayOf(string? place)
        => !TryParsePlace(place, out TownPlace parsed) || parsed.Secondary is null ? TownStay.None : parsed.AtCapital ? TownStay.Capital : TownStay.Secondary;

    // ---- resolving the centres ----

    /// <summary>
    /// The towns whose anchor NPC has a spawn on the town's map in <paramref name="content"/>, centred on the first such spawn. A town
    /// whose anchor has none is absent (the sweep then falls back and reports it). World thread (reads the content).
    /// </summary>
    internal static Dictionary<string, ResolvedTown> Resolve(CreatureContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var found = new Dictionary<string, ResolvedTown>(StringComparer.Ordinal);
        foreach (TownRow row in Rows)
        {
            IReadOnlyList<CreatureSpawn> spawns = content.GetSpawns(row.MapId, row.AnchorEntry);
            if (spawns.Count == 0 || !float.IsFinite(spawns[0].X) || !float.IsFinite(spawns[0].Y) || !float.IsFinite(spawns[0].Z)) continue;
            found[row.Key] = new ResolvedTown(row, new Vector3(spawns[0].X, spawns[0].Y, spawns[0].Z));
        }

        return found;
    }

    // ---- master (capital) assignment ----

    /// <summary>A stable 32-bit hash of a bot id (FNV-1a over its bytes; the same on every run, unlike a seeded hash code).</summary>
    internal static uint HashOf(Guid botId)
    {
        uint hash = 2166136261u;
        foreach (byte b in botId.ToByteArray()) hash = unchecked((hash ^ b) * 16777619u);
        return hash;
    }

    /// <summary>
    /// The master capital of a bot: with probability <paramref name="majorSharePct"/> percent (by the bot's id hash, so a bot always gets
    /// the same one) the faction's main capital (Stormwind or Orgrimmar), otherwise one of the other two, the bot's own race's first
    /// (human and orc and troll have none among them: then the hash picks). A capital for which <paramref name="usable"/> is false is skipped.
    /// Null when the team has none usable.
    /// </summary>
    internal static string? MasterFor(Team team, byte race, Guid botId, int majorSharePct, Func<string, bool> usable)
    {
        ArgumentNullException.ThrowIfNull(usable);
        TownRow[] all = [.. CapitalsOf(team).Where(r => usable(r.Key))];
        if (all.Length == 0) return null;
        TownRow major = CapitalsOf(team).First();
        TownRow[] minors = [.. all.Where(r => r.Key != major.Key)];
        uint hash = HashOf(botId);
        bool wantMajor = hash % 100u < (uint)Math.Clamp(majorSharePct, 0, 100);
        bool majorUsable = all.Any(r => r.Key == major.Key);
        if ((wantMajor && majorUsable) || minors.Length == 0) return majorUsable ? major.Key : all[0].Key;
        string? own = RaceCapital(race);
        if (own is not null && minors.FirstOrDefault(r => r.Key == own) is { } affinity) return affinity.Key;
        return minors[(int)((hash / 100u) % (uint)minors.Length)].Key;
    }

    /// <summary>The capital of a race's own people (a tiebreak only): dwarf and gnome Ironforge, night elf Darnassus, undead Undercity, tauren Thunder Bluff; null for human, orc and troll (the main capitals).</summary>
    internal static string? RaceCapital(byte race) => (Race)race switch
    {
        Race.Dwarf or Race.Gnome => "ironforge",
        Race.NightElf => "darnassus",
        Race.Undead => "undercity",
        Race.Tauren => "thunder-bluff",
        _ => null,
    };

    // ---- secondary (level-band) assignment ----

    /// <summary>Whether a secondary still serves the level: the hysteresis, a bot stays until its level reaches the town's maximum.</summary>
    internal static bool StillServes(TownRow row, int level, int margin = 0) => !row.Capital && level >= row.MinLevel && level < row.MaxLevel + margin;

    /// <summary>Whether <paramref name="level"/> is below the first band of every level-band town of the team on <paramref name="continent"/> (true also when it has none).</summary>
    internal static bool BelowBands(Team team, uint continent, int level)
        => !Rows.Any(r => !r.Capital && r.MapId == continent && ServesTeam(r, team) && r.MinLevel <= level);

    /// <summary>
    /// The level-band town for a bot: among the usable towns of the team whose band holds <paramref name="level"/>, on the bot's own
    /// continent first (another continent only when the own one has none), the least populated under <paramref name="cap"/> (0: no cap;
    /// when every candidate is at the cap the least populated still takes the bot, so none is left without), ties to the town with more
    /// levels left, then the key. Null when no town serves the level.
    /// </summary>
    internal static TownRow? PickSecondary(Team team, uint continent, int level, IReadOnlyDictionary<string, int> counts, Func<string, int> cap, Func<string, bool> usable)
    {
        ArgumentNullException.ThrowIfNull(cap);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(usable);
        TownRow[] serving = [.. Rows.Where(r => ServesTeam(r, team) && StillServes(r, level) && usable(r.Key))];
        TownRow[] own = [.. serving.Where(r => r.MapId == continent)];
        // Younger than every town of its own continent: no town, not one on another continent (a bot below the town table).
        if (own.Length == 0 && BelowBands(team, continent, level)) return null;
        TownRow[] pool = own.Length != 0 ? own : serving;
        if (pool.Length == 0) return null;
        TownRow[] open = [.. pool.Where(r => cap(r.Key) <= 0 || counts.GetValueOrDefault(r.Key) < cap(r.Key))];
        return (open.Length != 0 ? open : pool)
            .OrderBy(r => counts.GetValueOrDefault(r.Key)).ThenByDescending(r => r.MaxLevel).ThenBy(r => r.Key, StringComparer.Ordinal).First();
    }

    // ---- the cycle ----

    /// <summary>A stay between <paramref name="minMinutes"/> and <paramref name="maxMinutes"/> minutes, fixed per bot by its id hash, so a fleet does not move in step. Milliseconds.</summary>
    internal static long StayMs(int minMinutes, int maxMinutes, Guid botId)
        => (long)((minMinutes + (Math.Max(0, maxMinutes - minMinutes) * ((HashOf(botId) >> 8) % 1000u / 1000.0))) * 60_000.0);

    // ---- per-town caps ----

    /// <summary>The cap of a town: its entry in <see cref="PlayerbotPopulationOptions.TownCaps"/>, else <see cref="PlayerbotPopulationOptions.TownCap"/> (0: no cap).</summary>
    internal static int CapOf(PlayerbotPopulationOptions options, string key)
        => TryParseCaps(options.TownCaps, out Dictionary<string, int> caps) && caps.TryGetValue(key, out int cap) ? cap : options.TownCap;

    /// <summary>Reads a <c>key=cap,key=cap</c> list (<see cref="PlayerbotPopulationOptions.TryParseCaps"/>, which knows the level-band keys of <see cref="Rows"/>).</summary>
    internal static bool TryParseCaps(string? text, out Dictionary<string, int> caps) => PlayerbotPopulationOptions.TryParseCaps(text, out caps);

    /// <summary>Why a <c>TownCaps</c> value is refused, or null.</summary>
    internal static string? CheckCaps(string? text) => PlayerbotPopulationOptions.CheckCaps(text);

    /// <summary>
    /// The town to be homed in after the stay: from the capital to the secondary when its stay is over, from the secondary to the
    /// capital when its stay is over, and to the (new) secondary at once when the band moved while at the secondary. A
    /// bot with no secondary stays at the capital. <paramref name="secondaryChanged"/>: the band moved this sweep.
    /// </summary>
    internal static string NextActive(TownPlace current, string? secondary, bool secondaryChanged, long elapsedMs, long capitalStayMs, long secondaryStayMs)
    {
        if (secondary is null) return current.Master;
        if (current.Active == current.Master) return elapsedMs >= capitalStayMs ? secondary : current.Master;
        if (current.Active != secondary && !secondaryChanged) return current.Master; // an active town that is neither of the two: back to the capital
        if (secondaryChanged) return secondary;
        return elapsedMs >= secondaryStayMs ? current.Master : secondary;
    }

    // ---- travel ----

    /// <summary>
    /// Whether two points of Kalimdor lie on different sides of the water bots do not cross by themselves: Teldrassil (x above 8000, y
    /// above -200 on map 1) is an island reached by boat from Darkshore. Other maps and two points on one side: false.
    /// </summary>
    internal static bool SeparatedByWater(uint mapA, float xA, float yA, uint mapB, float xB, float yB)
        => mapA == 1 && mapB == 1 && OnTeldrassil(xA, yA) != OnTeldrassil(xB, yB);

    private static bool OnTeldrassil(float x, float y) => x > 8000f && y > -200f;

    /// <summary>Formats a home's level range for status lines.</summary>
    internal static string Describe(TownRow row)
        => string.Create(CultureInfo.InvariantCulture, $"{row.Name} ({row.MinLevel}-{row.MaxLevel})");
}

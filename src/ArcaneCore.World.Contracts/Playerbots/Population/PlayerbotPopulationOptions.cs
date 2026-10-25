using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>Which factions the population creates bots for (<see cref="PlayerbotPopulationOptions.Factions"/>).</summary>
public enum PopulationFactions
{
    /// <summary>Both, split by <see cref="PlayerbotPopulationOptions.AllianceSharePct"/>.</summary>
    Both,

    /// <summary>Alliance races only.</summary>
    Alliance,

    /// <summary>Horde races only.</summary>
    Horde,
}

/// <summary>What the population does with a bot that out-levelled its zone (<see cref="PlayerbotPopulationOptions.Relocation"/>).</summary>
public enum PopulationRelocation
{
    /// <summary>Leave it where it is; the bot travels on by its own goals.</summary>
    Off,

    /// <summary>Teleport it to a graveyard of a zone that fits its level.</summary>
    Teleport,
}

/// <summary>One level band of the population (<see cref="PlayerbotPopulationOptions.Bands"/>).</summary>
public sealed class PopulationBand
{
    /// <summary>The lowest level of the band (1..60).</summary>
    public int MinLevel { get; set; }

    /// <summary>The highest level of the band (the band's MinLevel..60).</summary>
    public int MaxLevel { get; set; }

    /// <summary>The band's share of the target: its weight over the sum of all weights (0..1000; 0 = no bots).</summary>
    public int Weight { get; set; }

    public PopulationBand Clone() => new() { MinLevel = MinLevel, MaxLevel = MaxLevel, Weight = Weight };

    public bool SameAs(PopulationBand other) => MinLevel == other.MinLevel && MaxLevel == other.MaxLevel && Weight == other.Weight;

    public bool Contains(int level) => level >= MinLevel && level <= MaxLevel;

    public override string ToString() => $"{MinLevel}-{MaxLevel}x{Weight}";
}

/// <summary>
/// <c>World:Playerbots:Population</c>: a target number of bots kept online and spread over the world by level band, held back by the
/// world's own tick time (docs/areas/playbots-population.md). Off by default. Every key is live through <c>.reload config</c>.
/// </summary>
public sealed class PlayerbotPopulationOptions
{
    /// <summary>
    /// The keys <see cref="TownCaps"/> accepts: the level-band towns of the pack's town table (<c>PopulationTownProgression.Rows</c>
    /// without the capitals). Here, not in the pack, so the core can refuse an unknown key at <c>.reload config</c>; a test keeps the two
    /// lists equal (<c>TownProgressionTests</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> LevelBandTownKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "kharanos", "sentinel-hill", "thelsamar", "lakeshire", "darkshire", "menethil", "southshore", "refuge-pointe", "booty-bay",
        "aerie-peak", "nethergarde", "thorium-a", "chillwind", "lights-hope",
        "brill", "sepulcher", "tarren-mill", "hammerfall", "grom-gol", "stonard", "kargath", "revantusk", "thorium-h",
        "auberdine", "astranaar", "theramore", "feathermoon",
        "crossroads", "camp-taurajo", "splintertree", "sun-rock", "freewind-post", "shadowprey", "brackenwall", "camp-mojache",
        "ratchet", "gadgetzan", "marshals-ref", "everlook", "cenarion-hold",
    };

    // Private: a public SectionName would make the config catalog treat this nested class as a second root section.
    private const string SectionName = PlayerbotOptions.SectionName + ":Population";

    /// <summary>The highest level of any band (the 1.12 level cap).</summary>
    public const int MaxLevel = 60;

    /// <summary>The most bands that may be configured.</summary>
    public const int MaxBands = 20;

    /// <summary>The most excluded zones that may be configured.</summary>
    public const int MaxExcludedZones = 256;

    /// <summary>The 1.12 class ids a bot may be created with (warrior, paladin, hunter, rogue, priest, shaman, mage, warlock, druid).</summary>
    public static readonly IReadOnlyList<uint> ValidClasses = [1, 2, 3, 4, 5, 7, 8, 9, 11];

    /// <summary>Run the population manager (off by default). Off: no bot is created, started, parked or moved by it.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The number of managed bots to keep running (0..5000). The effective target is the lower of this and <c>World:Playerbots:MaxBots</c>.
    /// Bots an operator created count towards it but are never stopped by the population: the running ones are taken off the top, and
    /// what is left (the population's own share) is what the bands split by weight. An operator's bot never counts in a band, so a
    /// band full of operator bots still gets its share of population bots. Above the target only the bots the population owns are
    /// parked. 0 parks every bot the population owns.
    /// </summary>
    public int TargetOnline { get; set; }

    /// <summary>Seconds of world time between two passes of the control loop (2..600).</summary>
    public int IntervalSeconds { get; set; } = 10;

    /// <summary>
    /// The world tick time budget in milliseconds (5..1000): the 95th percentile of the tick body duration over the last
    /// WindowTicks ticks. Above it the population stops adding bots and, with ParkWhenOverBudget, parks some.
    /// </summary>
    public int TickP95BudgetMs { get; set; } = 60;

    /// <summary>
    /// Adding bots resumes only while the tick p95 is below this percentage of TickP95BudgetMs (10..99). Between it and the
    /// budget the population holds, which keeps it from adding and parking in turns.
    /// </summary>
    public int ResumeBelowPct { get; set; } = 80;

    /// <summary>
    /// The ticks measured after every start, creation, park or relocation before the next decision (20..4096). A pass that finds fewer
    /// fresh ticks holds, so a decision never reacts to ticks from before the last change.
    /// </summary>
    public int WindowTicks { get; set; } = 200;

    /// <summary>Park (stop) the least useful bots the population owns while the tick p95 is over the budget (on by default). Off: only stop adding.</summary>
    public bool ParkWhenOverBudget { get; set; } = true;

    /// <summary>
    /// The consecutive fresh windows of WindowTicks ticks the p95 must stay over TickP95BudgetMs before a park for the budget, or under
    /// ResumeBelowPct of it before an add (1..20). 1 acts on a single window. A window between the two, a change, or a window on the
    /// other side starts the count again; a lowered target is honoured without waiting.
    /// </summary>
    public int ConfirmWindows { get; set; } = 2;

    /// <summary>
    /// Anti-churn: a bot the population started is not parked for the tick budget until it has run this many seconds of world time
    /// (0..3600; 0 = no minimum). Over the target such a bot is still parked, last.
    /// </summary>
    public int MinRunSeconds { get; set; } = 300;

    /// <summary>
    /// The population logs one information line for the passes that parked, started, created or moved bots, at most once per this many
    /// seconds of world time; the changes of the passes in between are added up into the next line (0..3600; 0 = a line for every such pass).
    /// </summary>
    public int ActionLogSeconds { get; set; } = 60;

    /// <summary>The most new bot characters created per minute of world time (0..600; 0 creates none and only restarts parked bots).</summary>
    public int MaxCreatePerMinute { get; set; } = 4;

    /// <summary>The most bots started or created in one pass (1..500).</summary>
    public int MaxStartsPerInterval { get; set; } = 4;

    /// <summary>The most bots parked in one pass (1..500).</summary>
    public int MaxParksPerInterval { get; set; } = 2;

    /// <summary>The factions bots are created for (Both by default).</summary>
    public PopulationFactions Factions { get; set; } = PopulationFactions.Both;

    /// <summary>With Factions Both, the share of new bots that are Alliance (0..100).</summary>
    public int AllianceSharePct { get; set; } = 50;

    /// <summary>The class ids new bots are created with (1.12 classes: 1 warrior, 2 paladin, 3 hunter, 4 rogue, 5 priest, 7 shaman, 8 mage, 9 warlock, 11 druid; non-empty, no duplicates).</summary>
    public uint[] Classes { get; set; } = [1, 2, 3, 4, 5, 7, 8, 9, 11];

    /// <summary>
    /// The level bands the target is split over: each has MinLevel, MaxLevel and Weight (at most 20; bands must not overlap and at least one
    /// weight must be above 0). A configured list replaces the default six bands of ten levels with equal weights. Without the boost
    /// content (see docs/areas/character-boost.md) only a band that contains level 1 is used.
    /// </summary>
    public PopulationBand[] Bands { get; set; } =
    [
        new() { MinLevel = 1, MaxLevel = 10, Weight = 1 },
        new() { MinLevel = 11, MaxLevel = 20, Weight = 1 },
        new() { MinLevel = 21, MaxLevel = 30, Weight = 1 },
        new() { MinLevel = 31, MaxLevel = 40, Weight = 1 },
        new() { MinLevel = 41, MaxLevel = 50, Weight = 1 },
        new() { MinLevel = 51, MaxLevel = 60, Weight = 1 },
    ];

    /// <summary>
    /// How many levels below a placement's floor a bot may be while the floor is at most ZoneLevelSlackMaxFloor (0..10). The floor is the
    /// level of the sub-area the graveyard stands in, or the zone's lowest sub-area level when the terrain does not know; a starter zone's
    /// is level 4 to 7, so the default 4 lets a level 1 bot start there. Above ZoneLevelSlackMaxFloor the slack shrinks by one level for
    /// every two floor levels, to 0. A bot below every place of its zone it could have been put is relocated like an out-levelled one.
    /// </summary>
    public int ZoneLevelSlack { get; set; } = 4;

    /// <summary>
    /// The highest placement floor that gets the whole ZoneLevelSlack (1..60). Above it the slack shrinks by one level for every two
    /// floor levels (default: 3 at floors 13-14, 2 at 15-16, 1 at 17-18, 0 from 19), so a level 38 bot is not placed at a level 41
    /// graveyard. 60 gives every floor the whole slack (the rule before the taper).
    /// </summary>
    public int ZoneLevelSlackMaxFloor { get; set; } = 12;

    /// <summary>
    /// A bot is not placed at a graveyard where most creatures hostile to its faction within CreatureSurveyRadius are more than this
    /// many levels above it (0..10), and is relocated from a zone where every graveyard of its faction is like that. Uses the creature
    /// survey; without creature data or faction templates nothing is surveyed and only the area floor counts.
    /// </summary>
    public int CreatureLevelMargin { get; set; } = 3;

    /// <summary>
    /// The radius in yards around each graveyard the creature survey counts hostile creature spawns in (0..300; 0 turns the survey off).
    /// The survey runs with the graveyard terrain lookup, off the world thread, when the atlas is built.
    /// </summary>
    public int CreatureSurveyRadius { get; set; } = 100;

    /// <summary>
    /// Zone capacity: a zone takes no new or relocated bot once its assigned bots reach the zone's level-fitting outdoor creature
    /// spawns (on the surface, not indoors or underground, hostile to the bot's faction) divided by this many (0..100; 0 turns the cap
    /// off). Keeps a small zone such as Deadwind Pass (47 surface spawns, 600 s respawn) from taking a hundred bots that find every
    /// creature dead. The spawn survey runs off the world thread; until it is in, no zone is capped.
    /// </summary>
    public int SpawnsPerBot { get; set; } = 3;

    /// <summary>How many levels a bot may be above the highest level of its zone before it is relocated (0..10).</summary>
    public int OutlevelMargin { get; set; } = 2;

    /// <summary>What happens to a bot that out-levelled its zone or is below it (Teleport by default).</summary>
    public PopulationRelocation Relocation { get; set; } = PopulationRelocation.Teleport;

    /// <summary>The most bots relocated in one pass (0..50).</summary>
    public int MaxRelocationsPerInterval { get; set; } = 2;

    /// <summary>
    /// Fresh-launch mode (off by default; live): new bots the population creates stay level 1 with their default starting kit at their
    /// race's native start location (no boost, no teleport), the race and class mix stays balanced across both factions, and the level
    /// bands, the zone atlas and relocation are bypassed (the target is one pool; bots level up on their own).
    /// </summary>
    public bool FreshLaunch { get; set; }

    /// <summary>
    /// Fresh launch: give each new bot its race's start area as a home (live, on by default). The home is a circle of
    /// FreshStartHomeRadius yards round the race's start position, clipped to the start zone, so a level 1 bot works the quests and
    /// creatures of its own valley and does not wander into the next zone's mobs or onto the Deeprun Tram. It graduates with the
    /// level: from FreshGraduateLevel the circle widens to FreshZoneHomeRadius (still clipped to the start zone), from
    /// FreshReleaseLevel the home becomes the town homes of TownProgression (with it off, the home is removed and the bot travels on
    /// by its own goals). Off: new bots get no home and no
    /// start-zone cap applies. The population's sweep also gives a running fresh-launch bot that has no home one for its level.
    /// </summary>
    public bool FreshStartHome { get; set; } = true;

    /// <summary>
    /// The radius in yards of the start home of a bot below FreshGraduateLevel (50..5000), zone-clipped. 1100 reaches the farthest turn-in
    /// of the class quests of the start areas (the dwarf priest's, 1078 yards from Coldridge Valley). Quests of everyone else are held to
    /// the start valley itself (400 to 540 yards, per area), which keeps the level 5 breadcrumbs to the next town for the graduated home.
    /// </summary>
    public int FreshStartHomeRadius { get; set; } = 1100;

    /// <summary>The level at which a start home widens to FreshZoneHomeRadius (2..60). A leave-home quest (see LeaveHomeLevelMargin) is allowed only from here.</summary>
    public int FreshGraduateLevel { get; set; } = 6;

    /// <summary>The radius in yards of the graduated home, between FreshGraduateLevel and FreshReleaseLevel (50..5000). 4500 covers every starter zone end-to-end from its level-1 spawn while ZoneClipped keeps the bot inside its start zone.</summary>
    public int FreshZoneHomeRadius { get; set; } = 4500;

    /// <summary>The level at which a graduated home is replaced by the town homes of the progression (TownProgression; removed when that is off) (2..60; at least FreshGraduateLevel, equal skips the widened home). 10 puts the bot in the town of the next zone as it finishes its start zone.</summary>
    public int FreshReleaseLevel { get; set; } = 10;

    /// <summary>
    /// The most bots one start area (Northshire, Coldridge Valley, Shadowglen, Valley of Trials, Camp Narache, Deathknell; the dwarf and
    /// gnome share one, as do the orc and troll) may hold under a start home (0..1000; 0 = no cap). A fresh-launch creation picks
    /// the least crowded area the chosen faction and class can start in; when every area is at the cap creation is deferred (the rush
    /// waits and says so) until bots graduate.
    /// </summary>
    public int FreshStartZoneCap { get; set; } = 40;

    /// <summary>
    /// A quest whose turn-in NPC is outside a graduated home (a leave-home quest) needs the bot to be at least its QuestLevel minus this
    /// many levels (0..10), and never below its MinLevel. A bot under a start home takes none; a bot with no home is not affected.
    /// </summary>
    public int LeaveHomeLevelMargin { get; set; }

    /// <summary>
    /// A bot below this level that has no home takes no Deeprun Tram or lift trip (0..60; 0 = no limit). The live fresh launch sent about
    /// 76 level 1 bots to Stormwind quests by the tram.
    /// </summary>
    public int TramMinLevel { get; set; } = 10;

    /// <summary>
    /// Town progression (on by default; needs FreshLaunch and FreshStartHome): from FreshReleaseLevel a bot has two homes, its faction
    /// capital (the master) and the town of its level band (the secondary, <c>PopulationTownProgression</c>), and lives at one
    /// of them at a time: SecondaryStayMin/MaxMinutes at the secondary, CapitalStayMin/MaxMinutes at the capital, and again. The secondary follows
    /// the level (the next town once the level reaches the cap of the band); a town whose centre cannot be found in the world data is
    /// reported once and the bot falls back to the towns that did resolve (or, with none, no home); a bot with no secondary that fits keeps
    /// no town home at all, so it explores instead of waiting at a capital. Off: a bot past FreshReleaseLevel has no home.
    /// </summary>
    public bool TownProgression { get; set; } = true;

    /// <summary>The radius in yards of a secondary town home (50..5000), zone-clipped. 4500 covers the whole zone so its quests stay allowed; an idle bot still lingers at the town centre.</summary>
    public int TownHomeRadius { get; set; } = 4500;

    /// <summary>The radius in yards of a capital home (50..5000), zone-clipped to the city: the trainers, auction house, bank and vendors of the city lie inside it.</summary>
    public int CapitalHomeRadius { get; set; } = 500;

    /// <summary>The most bots one level-band town (secondary) may hold (0..1000; 0 = no cap). Bots spread over the towns of their band, least populated first; when every town of the band is at the cap the least populated still takes the bot.</summary>
    public int TownCap { get; set; } = 60;

    /// <summary>The share in percent (0..100) of each faction whose master capital is its main one (Stormwind for the Alliance, Orgrimmar for the Horde); the rest go to the other two capitals, a bot's own race's first.</summary>
    public int CapitalMajorSharePct { get; set; } = 65;

    /// <summary>The shortest time in minutes (5..1440) a bot stays at its level-band town before it heads to the capital. Each bot's stay is fixed per bot (by its id) between this and SecondaryStayMaxMinutes, so a fleet does not move in step.</summary>
    public int SecondaryStayMinMinutes { get; set; } = 30;

    /// <summary>The longest time in minutes (5..1440; at least SecondaryStayMinMinutes) a bot stays at its level-band town.</summary>
    public int SecondaryStayMaxMinutes { get; set; } = 90;

    /// <summary>The shortest time in minutes (1..240) a bot stays at its capital (errands, then idling near the inn) before it heads back out.</summary>
    public int CapitalStayMinMinutes { get; set; } = 5;

    /// <summary>The longest time in minutes (1..240; at least CapitalStayMinMinutes) a bot stays at its capital.</summary>
    public int CapitalStayMaxMinutes { get; set; } = 15;

    /// <summary>
    /// Levels a bot stays at its level-band town past the maximum level of the town's band before the next town takes it (0..10; the
    /// hysteresis margin of the band change). 0: it moves when its level reaches the maximum.
    /// </summary>
    public int TownBandMargin { get; set; }

    /// <summary>
    /// Per-town caps that replace TownCap, as a comma-separated list of key=cap (for example "sentinel-hill=30,thelsamar=80"; the keys
    /// are those of PopulationTownProgression.Rows, caps 0..1000, 0 = no cap). Empty: TownCap for every town.
    /// </summary>
    public string TownCaps { get; set; } = string.Empty;

    /// <summary>
    /// The teleport of a town home (on by default): a bot whose new town lies on another continent, across the water of Teldrassil or
    /// farther than TownWalkYards takes the one return teleport (the Home teleport rules and cooldown) when it is homed there. Off:
    /// bots reach a town by their own means only (on foot, by taxi, and, with World:Playerbots:Travel on and a return mode other than
    /// Walk, by boat, zeppelin or mage portal: docs/areas/playbots.md, Travel); a town they cannot reach so is not assigned to that bot
    /// (it takes a town it can walk to; with none it keeps a home that is not a town home, or has none, and explores) and one Info
    /// anomaly per reason says so. The progression's own moves (the town cycle, a new band town, the Danger rule on the way to a town)
    /// then never teleport. The Home options' returns of a displaced bot still do, as for any home: from another map (OtherMap), a
    /// walk set aside as blocked (WalkBlocked), the Teleport return mode, an unsafe spirit-healer landing, and stalled ground.
    /// A bot on Teldrassil, whose only way off is a boat, therefore has no town secondary unless it may take the boat (or this option is on).
    /// </summary>
    public bool TownProgressionTeleport { get; set; } = true;

    /// <summary>
    /// The longest trip in yards (100..20000) a bot with a town home walks to it on the same continent; farther, or across the water of
    /// Teldrassil, or to another continent, it takes the one return teleport (the Home teleport rules and cooldown). 4500 yards is
    /// about 11 minutes on foot. With World:Playerbots:Travel on (and a return mode other than Walk), a home across the sea is travelled to
    /// by boat, zeppelin or portal first, and the teleport is the fallback when no journey can begin.
    /// </summary>
    public int TownWalkYards { get; set; } = 4500;

    /// <summary>
    /// Migration: the sweep also takes over the homes an operator set (any place text that is not the population's) for bots of the
    /// population, replacing them with the home of the bot's level (a level 1 bot with a stale Sentinel Hill home gets its start area).
    /// Off by default: operator homes are never touched. <c>.playerbot population adopt</c> does the same once, until no operator home
    /// is left. Homes an operator cleared (.playerbot home clear) stay cleared.
    /// </summary>
    public bool AdoptOperatorHomes { get; set; }

    /// <summary>
    /// The safety brake of <c>.playerbot population rush</c>: the rush pauses while the world tick p95 is above this many milliseconds
    /// in RushBrakeWindows consecutive windows of RushBrakeWindowSeconds seconds (50..10000). 0 (the default) means 1.5 times
    /// TickP95BudgetMs, so the brake follows the budget the population itself keeps.
    /// </summary>
    public int RushBrakeP95Ms { get; set; }

    /// <summary>The consecutive windows over the RushBrakeP95Ms ceiling that pause a rush, and under the resume line that lift the pause (1..20).</summary>
    public int RushBrakeWindows { get; set; } = 3;

    /// <summary>The length of one brake window in real seconds (1..60). The default 5 s with 3 windows pauses a rush after about 15 s over the ceiling.</summary>
    public int RushBrakeWindowSeconds { get; set; } = 5;

    /// <summary>The ceiling of the rush brake in milliseconds: RushBrakeP95Ms, or 1.5 times TickP95BudgetMs when that is 0.</summary>
    internal int RushCeilingMs() => RushBrakeP95Ms > 0 ? RushBrakeP95Ms : TickP95BudgetMs * 3 / 2;

    internal const int PlayerbotHomeRadiusMin = (int)ArcaneCore.Kernel.Characters.PlayerbotHomeBounds.MinRadius;

    internal const int PlayerbotHomeRadiusMax = (int)ArcaneCore.Kernel.Characters.PlayerbotHomeBounds.MaxRadius;

    internal static string? CheckRushCeiling(int value) => value == 0 ? null : CheckRange(value, 50, 10000);

    /// <summary>Area table zone ids bots are never placed in (at most 256, no duplicates). Capitals and cities are never used.</summary>
    public uint[] ExcludedZones { get; set; } = [];

    /// <summary>
    /// ConfigurationBinder appends array elements to a non-empty initialised array, so a configured list must start from an empty one
    /// (the same rule as <c>AllowedMaps</c>): the defaults of Classes and Bands are replaced, not extended.
    /// </summary>
    internal static void PrepareBinding(PlayerbotPopulationOptions options, IConfigurationSection playerbots)
    {
        foreach (IConfigurationSection child in playerbots.GetChildren())
        {
            if (!string.Equals(child.Key, "Population", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (IConfigurationSection key in child.GetChildren())
            {
                if (string.Equals(key.Key, nameof(Classes), StringComparison.OrdinalIgnoreCase)) options.Classes = [];
                else if (string.Equals(key.Key, nameof(Bands), StringComparison.OrdinalIgnoreCase)) options.Bands = [];
                else if (string.Equals(key.Key, nameof(ExcludedZones), StringComparison.OrdinalIgnoreCase)) options.ExcludedZones = [];
            }
        }
    }

    internal static string? CheckTarget(int value) => value is >= 0 and <= PlayerbotOptions.MaxBotsCeiling ? null : $"must be 0..{PlayerbotOptions.MaxBotsCeiling}";

    internal static string? CheckRange(int value, int min, int max) => value >= min && value <= max ? null : $"must be {min}..{max}";

    internal static string? CheckDefined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? null : "is not a defined value";

    internal static string? CheckClasses(IReadOnlyCollection<uint>? classes)
    {
        if (classes is null || classes.Count == 0) return "must name at least one class";
        if (classes.Any(c => !ValidClasses.Contains(c))) return "may only hold the 1.12 classes 1, 2, 3, 4, 5, 7, 8, 9 and 11";
        return classes.Distinct().Count() == classes.Count ? null : "must not repeat a class";
    }

    internal static string? CheckZones(IReadOnlyCollection<uint>? zones)
    {
        if (zones is null) return "is missing";
        if (zones.Count > MaxExcludedZones) return $"may hold at most {MaxExcludedZones} zones";
        return zones.Distinct().Count() == zones.Count ? null : "must not repeat a zone";
    }

    internal static string? CheckBands(IReadOnlyList<PopulationBand>? bands)
    {
        if (bands is null || bands.Count == 0) return "must hold at least one band";
        if (bands.Count > MaxBands) return $"may hold at most {MaxBands} bands";
        foreach (PopulationBand band in bands)
        {
            if (band is null) return "holds an empty band";
            if (band.MinLevel < 1 || band.MaxLevel > MaxLevel || band.MinLevel > band.MaxLevel)
                return $"has the band {band.MinLevel}-{band.MaxLevel}: levels must satisfy 1 <= MinLevel <= MaxLevel <= {MaxLevel}";
            if (band.Weight is < 0 or > 1000) return $"has the band {band.MinLevel}-{band.MaxLevel} with weight {band.Weight}: must be 0..1000";
        }

        for (int i = 0; i < bands.Count; i++)
            for (int j = i + 1; j < bands.Count; j++)
                if (bands[i].MinLevel <= bands[j].MaxLevel && bands[j].MinLevel <= bands[i].MaxLevel)
                    return $"has overlapping bands {bands[i].MinLevel}-{bands[i].MaxLevel} and {bands[j].MinLevel}-{bands[j].MaxLevel}";
        return bands.Any(b => b.Weight > 0) ? null : "needs at least one band with a weight above 0";
    }

    public void Validate()
    {
        string? problem =
            CheckTarget(TargetOnline) is { } t ? $"TargetOnline {t}"
            : CheckRange(IntervalSeconds, 2, 600) is { } i ? $"IntervalSeconds {i}"
            : CheckRange(TickP95BudgetMs, 5, 1000) is { } b ? $"TickP95BudgetMs {b}"
            : CheckRange(ResumeBelowPct, 10, 99) is { } r ? $"ResumeBelowPct {r}"
            : CheckRange(WindowTicks, 20, 4096) is { } w ? $"WindowTicks {w}"
            : CheckRange(ConfirmWindows, 1, 20) is { } cw ? $"ConfirmWindows {cw}"
            : CheckRange(MinRunSeconds, 0, 3600) is { } mr ? $"MinRunSeconds {mr}"
            : CheckRange(ActionLogSeconds, 0, 3600) is { } al ? $"ActionLogSeconds {al}"
            : CheckRange(MaxCreatePerMinute, 0, 600) is { } c ? $"MaxCreatePerMinute {c}"
            : CheckRange(MaxStartsPerInterval, 1, 500) is { } s ? $"MaxStartsPerInterval {s}"
            : CheckRange(MaxParksPerInterval, 1, 500) is { } p ? $"MaxParksPerInterval {p}"
            : CheckDefined(Factions) is { } f ? $"Factions {f}"
            : CheckRange(AllianceSharePct, 0, 100) is { } a ? $"AllianceSharePct {a}"
            : CheckClasses(Classes) is { } k ? $"Classes {k}"
            : CheckBands(Bands) is { } n ? $"Bands {n}"
            : CheckRange(ZoneLevelSlack, 0, 10) is { } z ? $"ZoneLevelSlack {z}"
            : CheckRange(ZoneLevelSlackMaxFloor, 1, MaxLevel) is { } zf ? $"ZoneLevelSlackMaxFloor {zf}"
            : CheckRange(CreatureLevelMargin, 0, 10) is { } cm ? $"CreatureLevelMargin {cm}"
            : CheckRange(CreatureSurveyRadius, 0, 300) is { } cr ? $"CreatureSurveyRadius {cr}"
            : CheckRange(SpawnsPerBot, 0, 100) is { } spb ? $"SpawnsPerBot {spb}"
            : CheckRange(OutlevelMargin, 0, 10) is { } o ? $"OutlevelMargin {o}"
            : CheckDefined(Relocation) is { } l ? $"Relocation {l}"
            : CheckRange(MaxRelocationsPerInterval, 0, 50) is { } m ? $"MaxRelocationsPerInterval {m}"
            : CheckZones(ExcludedZones) is { } x ? $"ExcludedZones {x}"
            : CheckRushCeiling(RushBrakeP95Ms) is { } rb ? $"RushBrakeP95Ms {rb}"
            : CheckRange(RushBrakeWindows, 1, 20) is { } rw ? $"RushBrakeWindows {rw}"
            : CheckRange(RushBrakeWindowSeconds, 1, 60) is { } rs ? $"RushBrakeWindowSeconds {rs}"
            : CheckRange(FreshStartHomeRadius, PlayerbotHomeRadiusMin, PlayerbotHomeRadiusMax) is { } hr ? $"FreshStartHomeRadius {hr}"
            : CheckRange(FreshGraduateLevel, 2, MaxLevel) is { } gl ? $"FreshGraduateLevel {gl}"
            : CheckRange(FreshZoneHomeRadius, PlayerbotHomeRadiusMin, PlayerbotHomeRadiusMax) is { } zr ? $"FreshZoneHomeRadius {zr}"
            : CheckRange(FreshReleaseLevel, 2, MaxLevel) is { } rl ? $"FreshReleaseLevel {rl}"
            : FreshReleaseLevel < FreshGraduateLevel ? "FreshReleaseLevel must be at least FreshGraduateLevel"
            : CheckRange(FreshStartZoneCap, 0, 1000) is { } sc ? $"FreshStartZoneCap {sc}"
            : CheckRange(LeaveHomeLevelMargin, 0, 10) is { } lm ? $"LeaveHomeLevelMargin {lm}"
            : CheckRange(TramMinLevel, 0, MaxLevel) is { } tl ? $"TramMinLevel {tl}"
            : CheckRange(TownHomeRadius, PlayerbotHomeRadiusMin, PlayerbotHomeRadiusMax) is { } thr ? $"TownHomeRadius {thr}"
            : CheckRange(CapitalHomeRadius, PlayerbotHomeRadiusMin, PlayerbotHomeRadiusMax) is { } chr ? $"CapitalHomeRadius {chr}"
            : CheckRange(TownCap, 0, 1000) is { } tc ? $"TownCap {tc}"
            : CheckRange(CapitalMajorSharePct, 0, 100) is { } cms ? $"CapitalMajorSharePct {cms}"
            : CheckRange(SecondaryStayMinMinutes, 5, 1440) is { } ssn ? $"SecondaryStayMinMinutes {ssn}"
            : CheckRange(SecondaryStayMaxMinutes, 5, 1440) is { } ssx ? $"SecondaryStayMaxMinutes {ssx}"
            : SecondaryStayMaxMinutes < SecondaryStayMinMinutes ? "SecondaryStayMaxMinutes must be at least SecondaryStayMinMinutes"
            : CheckRange(CapitalStayMinMinutes, 1, 240) is { } csn ? $"CapitalStayMinMinutes {csn}"
            : CheckRange(CapitalStayMaxMinutes, 1, 240) is { } csx ? $"CapitalStayMaxMinutes {csx}"
            : CapitalStayMaxMinutes < CapitalStayMinMinutes ? "CapitalStayMaxMinutes must be at least CapitalStayMinMinutes"
            : CheckRange(TownBandMargin, 0, 10) is { } tbm ? $"TownBandMargin {tbm}"
            : CheckCaps(TownCaps) is { } tcs ? $"TownCaps {tcs}"
            : CheckRange(TownWalkYards, 100, 20000) is { } twy ? $"TownWalkYards {twy}"
            : null;
        if (problem is not null) throw new InvalidOperationException($"{SectionName}: {problem}.");
    }

    /// <summary>Reads a <c>key=cap,key=cap</c> list of <see cref="TownCaps"/>; false for a key not in <see cref="LevelBandTownKeys"/>, a cap outside 0..1000, a duplicate or a malformed entry. Empty is valid.</summary>
    public static bool TryParseCaps(string? text, out Dictionary<string, int> caps)
    {
        caps = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return true;
        foreach (string entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = entry.Split('=', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !LevelBandTownKeys.Contains(pair[0])
                || !int.TryParse(pair[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int cap) || cap > 1000
                || !caps.TryAdd(pair[0], cap)) return false;
        }

        return true;
    }

    /// <summary>Why a <see cref="TownCaps"/> value is refused, or null.</summary>
    public static string? CheckCaps(string? text) => TryParseCaps(text, out _) ? null : "must be key=cap pairs (cap 0..1000) of known level-band town keys, separated by commas";
}

/// <summary>A list of ids as one reload value (equal when the sequences are equal), shown as the list.</summary>
internal sealed class PopulationIdList(uint[] items) : IEquatable<PopulationIdList>
{
    public uint[] Items { get; } = items;

    public bool Equals(PopulationIdList? other) => other is not null && Items.AsSpan().SequenceEqual(other.Items);

    public override bool Equals(object? obj) => Equals(obj as PopulationIdList);

    public override int GetHashCode() => Items.Length;

    public override string ToString() => "[" + string.Join(", ", Items) + "]";
}

/// <summary>The band list as one reload value (equal when every band is), shown as <c>min-maxXweight</c> entries.</summary>
internal sealed class PopulationBandList(PopulationBand[] items) : IEquatable<PopulationBandList>
{
    public PopulationBand[] Items { get; } = items;

    public bool Equals(PopulationBandList? other)
        => other is not null && Items.Length == other.Items.Length && Items.Zip(other.Items).All(pair => pair.First.SameAs(pair.Second));

    public override bool Equals(object? obj) => Equals(obj as PopulationBandList);

    public override int GetHashCode() => Items.Length;

    public override string ToString() => "[" + string.Join(", ", Items.Select(b => b.ToString())) + "]";
}

using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// Where a fresh-launch bot stands against its start home (<see cref="PopulationStartAreas.StageFor"/>): <see cref="Start"/> is the
/// circle round the race's start position, <see cref="Zone"/> the widened home of a bot that outgrew the valley, <see cref="None"/>
/// no home of the population's at all (the bot travels on by its own goals).
/// </summary>
internal enum StartHomeStage : byte
{
    None,
    Start,
    Zone,

    /// <summary>A graduated bot's town home (<see cref="PopulationTownProgression"/>): its capital or its level-band town.</summary>
    Town,
}

/// <summary>
/// The six starting areas of 1.12 and the start homes the population gives its fresh-launch bots (ArcaneCore's own rule; no reference
/// server anchors a bot to an area). A home is stored as an ordinary <see cref="ArcaneCore.Kernel.Characters.PlayerbotHome"/> whose
/// <c>Place</c> carries the stage and the area (<c>start:valley-of-trials</c>, <c>zone:valley-of-trials</c>), so no schema changes and
/// an operator's own home (any other place text) is never touched.
/// <para>
/// <b>Graduation.</b> The stage follows the bot's level (<see cref="StageFor"/>): below <c>FreshGraduateLevel</c> a circle of
/// <c>FreshStartHomeRadius</c> yards round the start position (the valley); from there to <c>FreshReleaseLevel</c> a circle of
/// <c>FreshZoneHomeRadius</c> yards clipped to the start zone, in which leave-home quests become possible
/// (<see cref="PlayerbotHomeArea.QuestAllowed"/>); from <c>FreshReleaseLevel</c> a town home (<see cref="PopulationTownProgression"/>; no home when <c>TownProgression</c> is off). Only the start stage counts towards
/// <c>FreshStartZoneCap</c>, so a graduated bot frees its place in the valley.
/// </para>
/// </summary>
internal static class PopulationStartAreas
{
    internal const string StartPrefix = "start:";
    internal const string ZonePrefix = "zone:";

    /// <summary>The six start areas, by key.</summary>
    internal static readonly IReadOnlyList<string> Areas = ["northshire", "coldridge", "shadowglen", "valley-of-trials", "camp-narache", "deathknell"];

    /// <summary>The start area of a race (human Northshire, dwarf and gnome Coldridge, night elf Shadowglen, orc and troll the Valley of Trials, tauren Camp Narache, undead Deathknell); null for an unknown race.</summary>
    internal static string? AreaOf(byte race) => (Race)race switch
    {
        Race.Human => "northshire",
        Race.Dwarf or Race.Gnome => "coldridge",
        Race.NightElf => "shadowglen",
        Race.Orc or Race.Troll => "valley-of-trials",
        Race.Tauren => "camp-narache",
        Race.Undead => "deathknell",
        _ => null,
    };

    /// <summary>The stage a home's place text names (None for an operator's home).</summary>
    internal static StartHomeStage StageOfPlace(string? place) => TryParse(place, out StartHomeStage stage, out _) ? stage : StartHomeStage.None;

    /// <summary>The continent a race starts on (1 Kalimdor: orc, troll, tauren, night elf; 0 Eastern Kingdoms), known without a query so a map the server does not allow is refused before one.</summary>
    internal static uint ContinentOf(byte race) => (Race)race is Race.Orc or Race.Troll or Race.Tauren or Race.NightElf ? 1u : 0u;

    /// <summary>
    /// How far from the start position the content of the start valley reaches, in yards: the largest distance of a turn-in or kill
    /// target of the valley's own (race-wide, level 1 to 5) quests. A start-stage home takes no quest that goes beyond it unless the
    /// quest is for a class (the class trainers' quests lead to the next hamlet by design). Measured on the live world data
    /// (<c>creature_spawn</c> nearest spawn of each NPC against <c>player_create_info</c>): Northshire 278 (breadcrumbs from 534),
    /// Valley of Trials 371 (700), Coldridge 358 (1073), Shadowglen 449 (531), Deathknell 383 (584), Camp Narache 297 (breadcrumb 1656
    /// at 559); each value is that maximum plus a margin that stays under the nearest breadcrumb. A quest that continues a chain
    /// (it has a prerequisite quest: Rites of the Earthmother, 526 and 599 yards from Camp Narache) may reach <see cref="ChainSlackYards"/> further.
    /// </summary>
    internal static float ValleyRadiusOf(string area) => area switch
    {
        "northshire" => 400f,
        "valley-of-trials" => 450f,
        "coldridge" => 450f,
        "shadowglen" => 500f,
        "deathknell" => 450f,
        "camp-narache" => 540f,
        _ => 0f,
    };

    /// <summary>How much further than the valley radius the turn-in of a quest with a prerequisite may be.</summary>
    internal const float ChainSlackYards = 120f;

    /// <summary>The valley radius of a start-stage home's place text; 0 for any other place (no valley gate).</summary>
    internal static float ValleyRadiusOfPlace(string? place) => TryParse(place, out StartHomeStage stage, out string area) && stage == StartHomeStage.Start ? ValleyRadiusOf(area) : 0f;

    /// <summary>The <c>Place</c> text of the home of <paramref name="stage"/> in <paramref name="area"/>.</summary>
    internal static string PlaceOf(string area, StartHomeStage stage) => (stage == StartHomeStage.Zone ? ZonePrefix : StartPrefix) + area;

    /// <summary>Reads a home's place text: false when it is not one of the population's homes (an operator's home, or none). A town home reads as <see cref="StartHomeStage.Town"/> with the town it is homed in as the area.</summary>
    internal static bool TryParse(string? place, out StartHomeStage stage, out string area)
    {
        stage = StartHomeStage.None;
        area = string.Empty;
        if (place is null) return false;
        if (PopulationTownProgression.TryParsePlace(place, out TownPlace town))
        {
            (stage, area) = (StartHomeStage.Town, town.Active);
            return true;
        }

        StartHomeStage found = place.StartsWith(StartPrefix, StringComparison.Ordinal) ? StartHomeStage.Start
            : place.StartsWith(ZonePrefix, StringComparison.Ordinal) ? StartHomeStage.Zone : StartHomeStage.None;
        if (found == StartHomeStage.None) return false;
        string key = place[(found == StartHomeStage.Start ? StartPrefix.Length : ZonePrefix.Length)..];
        if (!Areas.Contains(key)) return false;
        (stage, area) = (found, key);
        return true;
    }

    /// <summary>The stage a bot of <paramref name="level"/> belongs in: Start below the graduate level, Zone below the release level, then Town (None when <c>TownProgression</c> is off).</summary>
    internal static StartHomeStage StageFor(int level, PlayerbotPopulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return level < options.FreshGraduateLevel ? StartHomeStage.Start : level < options.FreshReleaseLevel ? StartHomeStage.Zone
            : options.TownProgression ? StartHomeStage.Town : StartHomeStage.None;
    }

    /// <summary>The radius in yards of the home of <paramref name="stage"/>.</summary>
    internal static float RadiusOf(StartHomeStage stage, PlayerbotPopulationOptions options)
        => stage == StartHomeStage.Zone ? options.FreshZoneHomeRadius : stage == StartHomeStage.Town ? options.TownHomeRadius : options.FreshStartHomeRadius;
}

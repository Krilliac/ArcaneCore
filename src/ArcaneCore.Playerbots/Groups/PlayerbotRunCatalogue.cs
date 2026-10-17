using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>One boss a run kills: a creature with a static spawn in the instance that completes an encounter.</summary>
/// <param name="Entry">The creature entry.</param>
/// <param name="Name">The encounter's name (DungeonEncounter.dbc).</param>
/// <param name="EncounterId">DungeonEncounter.dbc id.</param>
/// <param name="EncounterIndex">The encounter's position in the dungeon order.</param>
/// <param name="Final">The last encounter of its segment (routed last).</param>
/// <param name="Position">The first static spawn of the creature on the map.</param>
/// <param name="MinLevel">The template's lowest level.</param>
/// <param name="MaxLevel">The template's highest level.</param>
internal sealed record PlayerbotRunBoss(uint Entry, string Name, uint EncounterId, uint EncounterIndex, bool Final, Vector3 Position, byte MinLevel, byte MaxLevel);

/// <summary>
/// One runnable piece of an instance: a whole dungeon, one wing of a dungeon with several (Scarlet Monastery, Blackrock Spire, Dire
/// Maul, Stratholme) or a raid. <see cref="Bosses"/> are in route order, the final boss last.
/// </summary>
/// <param name="MapId">The instance map.</param>
/// <param name="MapName">Its name (<c>map_template</c>).</param>
/// <param name="Segment">The wing's index on the map, counting from 0.</param>
/// <param name="SegmentCount">The wings the map's encounter list has.</param>
/// <param name="Raid">The map is a raid (<see cref="MapType.Raid"/>).</param>
/// <param name="Size">The group the run takes: five at most for a dungeon, the player limit for a raid.</param>
/// <param name="EntranceTrigger">The area trigger on a continent whose teleport leads in.</param>
/// <param name="EntranceMap">The continent the trigger is on.</param>
/// <param name="Entrance">Where the trigger stands.</param>
/// <param name="InsideStart">Where the teleport puts the group inside.</param>
/// <param name="EntranceLevel">The teleport's required level.</param>
/// <param name="Bosses">The bosses to kill, in route order.</param>
/// <param name="Scripted">The names of the encounters that are not routed: summoned or event bosses without a static spawn, and kills credited by a spell.</param>
internal sealed record PlayerbotRunPlan(
    uint MapId,
    string MapName,
    int Segment,
    int SegmentCount,
    bool Raid,
    int Size,
    uint EntranceTrigger,
    uint EntranceMap,
    Vector3 Entrance,
    Vector3 InsideStart,
    byte EntranceLevel,
    IReadOnlyList<PlayerbotRunBoss> Bosses,
    IReadOnlyList<string> Scripted)
{
    /// <summary>The entries of <see cref="Bosses"/> (built once: the group AI asks every tick which creatures are never trash).</summary>
    public IReadOnlyList<uint> BossEntries { get; } = [.. Bosses.Select(b => b.Entry)];

    /// <summary>Whether <paramref name="entry"/> is one of the plan's bosses (dead or alive).</summary>
    public bool IsBoss(uint entry)
    {
        for (int i = 0; i < BossEntries.Count; i++)
            if (BossEntries[i] == entry) return true;

        return false;
    }

    /// <summary>The plan's identity: map and wing.</summary>
    public (uint Map, int Segment) Key => (MapId, Segment);

    /// <summary>
    /// The levels a bot must have to be matched: the higher of the entrance level and the weakest boss's level minus
    /// <see cref="PlayerbotRunOptions.BandBelowBoss"/>, up to the strongest boss's level plus <see cref="PlayerbotRunOptions.BandAboveBoss"/>;
    /// a <see cref="PlayerbotRunOptions.LevelBandOverrides"/> entry for the wing, else for the map, replaces both.
    /// </summary>
    public (byte Min, byte Max) Band(PlayerbotRunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (OverrideFor(options.LevelBandOverrides) is { } over) return over;
        int weakest = Bosses.Min(b => (int)b.MinLevel);
        int strongest = Bosses.Max(b => (int)b.MaxLevel);
        int min = Math.Max(EntranceLevel, weakest - options.BandBelowBoss);
        int max = strongest + options.BandAboveBoss;
        return ((byte)Math.Clamp(min, 1, 255), (byte)Math.Clamp(Math.Max(max, min), 1, 255));
    }

    private (byte, byte)? OverrideFor(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return null;
        (byte, byte)? map = null, wing = null;
        foreach ((string key, string value) in overrides)
        {
            if (!PlayerbotRunOptions.TryParseKey(key, out uint mapId, out int? segment) || mapId != MapId
                || !PlayerbotRunOptions.TryParseBand(value, out byte min, out byte max)) continue;
            if (segment is null) map = (min, max);
            else if (segment == Segment) wing = (min, max);
        }

        return wing ?? map;
    }

    /// <summary><c>Deadmines</c> or <c>Scarlet Monastery:2</c>.</summary>
    public string Label => SegmentCount > 1 ? string.Create(CultureInfo.InvariantCulture, $"{MapName}:{Segment}") : MapName;
}

/// <summary>An instance (or wing) the catalogue cannot offer, and why.</summary>
internal sealed record PlayerbotRunExclusion(uint MapId, string MapName, int Segment, string Reason);

/// <summary>
/// The dungeons and raids the bots can run, derived from the world data (docs/areas/playbots-groups.md): <c>map_template</c> says which
/// maps are instances and their size, <c>instance_encounter</c> (cmangos classic-db <c>instance_dungeon_encounters</c> joined to
/// <c>instance_encounters</c>, i.e. DungeonEncounter.dbc) which creature kills are the bosses, in which order and where the dungeon's wings end,
/// <c>creature</c> and <c>creature_template</c> where and how strong they are, and <c>areatrigger_teleport</c> the entrance and its level.
/// <para>
/// <b>Rank is not used</b> to find bosses: in this world database no five-player map but 229 has a rank-3 (world boss) spawn, and
/// Deadmines' bosses are rank 1 and 2. A boss is an encounter whose credit is the death of a creature that has a static spawn on the map;
/// encounters credited by a spell (Naxxramas' Four Horsemen) or by a creature that is only summoned or event-spawned (Sneed, Nefarian,
/// Ragnaros, Majordomo) are <see cref="PlayerbotRunPlan.Scripted"/>: reported, never routed. Spawns that only a spawn entry table or a pool
/// resolves (<c>Entry</c> 0) are not static and so count as scripted too.
/// </para>
/// <para>
/// <b>Wings.</b> An encounter marked <c>lastEncounterDungeon</c> ends a wing (Scarlet Monastery has four, Blackrock Spire two); encounters
/// after the last marker join the last wing, and the marked boss of a wing is routed last. A map with no marker is one wing.
/// </para>
/// <para>
/// The band and the run sizes are ArcaneCore's own rules (neither reference core groups bots): see <see cref="PlayerbotRunPlan.Band"/>.
/// Immutable; built on the world thread (or any, it only reads its inputs).
/// </para>
/// </summary>
internal sealed class PlayerbotRunCatalogue
{
    /// <summary>Exclusion reason: no area trigger on a continent leads into the instance (Blackwing Lair's only trigger is inside Blackrock Spire).</summary>
    public const string NoEntrance = "no-entrance-on-continent";

    /// <summary>Exclusion reason: none of the wing's encounters is a creature with a static spawn.</summary>
    public const string NoSpawnedBoss = "no-spawned-boss";

    /// <summary>Exclusion reason: the instance has no <c>instance_encounter</c> rows.</summary>
    public const string NoEncounterData = "no-encounter-data";

    /// <summary>Exclusion reason: the encounters name a map that is not a dungeon or raid.</summary>
    public const string MapNotInstance = "map-not-instance";

    /// <summary>The empty catalogue (no encounter data).</summary>
    public static PlayerbotRunCatalogue Empty { get; } = new([], []);

    private PlayerbotRunCatalogue(IReadOnlyList<PlayerbotRunPlan> plans, IReadOnlyList<PlayerbotRunExclusion> exclusions)
    {
        Plans = plans;
        Exclusions = exclusions;
    }

    /// <summary>The runnable pieces, ordered by map then wing.</summary>
    public IReadOnlyList<PlayerbotRunPlan> Plans { get; }

    /// <summary>What was left out and why, ordered by map then wing.</summary>
    public IReadOnlyList<PlayerbotRunExclusion> Exclusions { get; }

    /// <summary>The plan with the given key, or null.</summary>
    public PlayerbotRunPlan? Find(uint mapId, int segment) => Plans.FirstOrDefault(p => p.MapId == mapId && p.Segment == segment);

    /// <summary>Build the catalogue from the world data.</summary>
    public static PlayerbotRunCatalogue Build(
        MapRegistry maps,
        IEnumerable<AreaTriggerTemplate> triggers,
        IEnumerable<AreaTriggerTeleport> teleports,
        CreatureContent creatures,
        IReadOnlyList<InstanceEncounter> encounters)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(teleports);
        ArgumentNullException.ThrowIfNull(creatures);
        ArgumentNullException.ThrowIfNull(encounters);

        Dictionary<uint, AreaTriggerTemplate> triggerById = [];
        foreach (AreaTriggerTemplate trigger in triggers) triggerById.TryAdd(trigger.Id, trigger);
        ILookup<uint, AreaTriggerTeleport> teleportsInto = teleports.ToLookup(t => t.TargetMap);
        ILookup<uint, InstanceEncounter> byMap = encounters.ToLookup(e => e.MapId);

        var plans = new List<PlayerbotRunPlan>();
        var exclusions = new List<PlayerbotRunExclusion>();
        foreach (MapTemplate map in maps.All.Where(m => m.IsDungeon))
        {
            InstanceEncounter[] list = [.. byMap[map.Entry].OrderBy(e => e.EncounterIndex).ThenBy(e => e.EncounterId)];
            if (list.Length == 0)
            {
                exclusions.Add(new(map.Entry, map.Name, 0, NoEncounterData));
                continue;
            }

            List<InstanceEncounter[]> wings = Segments(list);
            for (int wing = 0; wing < wings.Count; wing++)
            {
                PlayerbotRunPlan? plan = BuildWing(maps, map, wing, wings.Count, wings[wing], triggerById, teleportsInto[map.Entry], creatures, out string? reason);
                if (plan is not null) plans.Add(plan);
                else exclusions.Add(new(map.Entry, map.Name, wing, reason!));
            }
        }

        foreach (uint mapId in byMap.Select(g => g.Key).Distinct().OrderBy(m => m))
            if (maps.Find(mapId) is not { IsDungeon: true })
                exclusions.Add(new(mapId, maps.Find(mapId)?.Name ?? mapId.ToString(CultureInfo.InvariantCulture), 0, MapNotInstance));

        return new PlayerbotRunCatalogue(
            [.. plans.OrderBy(p => p.MapId).ThenBy(p => p.Segment)],
            [.. exclusions.OrderBy(e => e.MapId).ThenBy(e => e.Segment)]);
    }

    /// <summary>Cut the ordered encounters after each marked last encounter; the rest after the last marker joins the last wing.</summary>
    internal static List<InstanceEncounter[]> Segments(IReadOnlyList<InstanceEncounter> ordered)
    {
        var wings = new List<List<InstanceEncounter>>();
        var current = new List<InstanceEncounter>();
        foreach (InstanceEncounter encounter in ordered)
        {
            current.Add(encounter);
            if (!encounter.LastEncounter) continue;
            wings.Add(current);
            current = [];
        }

        if (current.Count > 0)
        {
            if (wings.Count == 0) wings.Add(current);
            else wings[^1].AddRange(current);
        }

        return [.. wings.Select(w => w.ToArray())];
    }

    private static PlayerbotRunPlan? BuildWing(MapRegistry maps, MapTemplate map, int wing, int wings, InstanceEncounter[] encounters,
        Dictionary<uint, AreaTriggerTemplate> triggers, IEnumerable<AreaTriggerTeleport> into, CreatureContent creatures, out string? reason)
    {
        var bosses = new List<PlayerbotRunBoss>();
        var scripted = new List<string>();
        foreach (InstanceEncounter encounter in encounters)
        {
            if (!encounter.IsCreatureKill || creatures.GetSpawns(map.Entry, encounter.CreditEntry) is not { Count: > 0 } spawns
                || creatures.FindTemplate(encounter.CreditEntry) is not { } template)
            {
                scripted.Add(encounter.Name);
                continue;
            }

            CreatureSpawn first = spawns[0];
            bosses.Add(new PlayerbotRunBoss(encounter.CreditEntry, encounter.Name, encounter.EncounterId, encounter.EncounterIndex, encounter.LastEncounter,
                new Vector3(first.X, first.Y, first.Z), template.MinLevel, template.MaxLevel));
        }

        if (bosses.Count == 0)
        {
            reason = NoSpawnedBoss;
            return null;
        }

        // The final boss last; the rest in encounter order (stable).
        PlayerbotRunBoss[] route = [.. bosses.Where(b => !b.Final), .. bosses.Where(b => b.Final)];
        PlayerbotRunBoss anchor = route[0];

        // The entrance: a trigger on a continent whose teleport leads in, the one that lands nearest the wing's first boss.
        (AreaTriggerTeleport Teleport, AreaTriggerTemplate Trigger)? best = null;
        float bestDistance = float.MaxValue;
        foreach (AreaTriggerTeleport teleport in into.OrderBy(t => t.Id))
        {
            if (!triggers.TryGetValue(teleport.Id, out AreaTriggerTemplate? trigger) || maps.Find(trigger.MapId) is not { IsContinent: true }) continue;
            float distance = Vector3.Distance(new Vector3(teleport.TargetX, teleport.TargetY, teleport.TargetZ), anchor.Position);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = (teleport, trigger);
        }

        if (best is not { } entrance)
        {
            reason = NoEntrance;
            return null;
        }

        reason = null;
        return new PlayerbotRunPlan(map.Entry, map.Name, wing, wings, map.IsRaid, PlayerbotGroupContent.RunGroupSize(map),
            entrance.Trigger.Id, entrance.Trigger.MapId, new Vector3(entrance.Trigger.X, entrance.Trigger.Y, entrance.Trigger.Z),
            new Vector3(entrance.Teleport.TargetX, entrance.Teleport.TargetY, entrance.Teleport.TargetZ), entrance.Teleport.RequiredLevel,
            route, scripted);
    }
}

using ArcaneCore.Data.World.Instances;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// Compact RFC world for the manual-clock run. Map, encounters, teleports, creature entries and levels come from
/// classic-db z2815 (instance_template, instance_dungeon_encounters, instance_encounters,
/// areatrigger_teleport, creature and creature_template). Trigger volumes are from build-5875 AreaTrigger.dbc.
/// The three trash positions, scaled combat stats and graveyard are test fixtures, not imported-world content.
/// The map's linked zone is set to AreaTable.dbc's RFC area 2437 so the no-terrain ghost test can resolve its graveyard;
/// classic-db instance_template's linked_zone for RFC is 0.
/// </summary>
internal static class GroupRfcContent
{
    public const uint Map = RunFixtures.RagefireChasm;
    public const uint Entrance = 2230, Exit = 2226;
    public const uint Trogg = 11318, Shaman = 11319;
    public static readonly uint[] PackSpawns = [990801, 990802, 990803];
    public static readonly uint[] BossEntries = [11517, 11520, 11518, 11519];

    /// <summary>The test corridor runs south of the exit volume before turning toward the first boss.</summary>
    internal sealed class Pathfinder : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => mapId == Map && start.X > -20f && end.X < -80f
                ? new PathResult(PathType.Normal, [start, new Vector3(-20f, -20f, -15.5288f), end])
                : PathResult.StraightLine(start, end, PathType.Normal);
    }

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IMapDataStore>(new Maps());
        services.AddSingleton<IInstanceEncounterStore>(new Encounters());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
    }

    public static (CreatureTemplate Template, CreatureSpawn Spawn)[] Creatures()
    {
        CreatureContent source = RunFixtures.Creatures();
        var bosses = new List<(CreatureTemplate, CreatureSpawn)>();
        foreach (uint entry in BossEntries)
        {
            CreatureTemplate template = source.Templates.Single(t => t.Entry == entry);
            CreatureSpawn spawn = source.GetSpawns(Map, entry).Single();
            bosses.Add((Hostile(entry, template.Name, template.MinLevel, template.MaxLevel, 450, rank: 1), spawn));
        }

        // Positions are 20-25 yards beyond the teleport landing and within a ten-yard pack radius.
        // The entries and 13-15 level range are classic-db creature_template 11318/11319.
        return
        [
            .. bosses,
            (Hostile(Trogg, "Ragefire Trogg", 13, 15, 115), Pack(PackSpawns[0], Trogg, -20f, -8f)),
            (Hostile(Shaman, "Ragefire Shaman", 13, 15, 100), Pack(PackSpawns[1], Shaman, -23f, -5f)),
            (Hostile(Trogg, "Ragefire Trogg", 13, 15, 115), Pack(PackSpawns[2], Trogg, -24f, -10f)),
        ];
    }

    private static CreatureTemplate Hostile(uint entry, string name, byte min, byte max, uint health, uint rank = 0) => new()
    {
        Entry = entry, Name = name, Faction = 14, CreatureType = 7, MinLevel = min, MaxLevel = max,
        DisplayIds = [903], MinLevelHealth = health, MaxLevelHealth = health,
        MinMeleeDamage = 2f, MaxMeleeDamage = 3f, MeleeBaseAttackTime = 2000, Rank = rank,
    };

    private static CreatureSpawn Pack(uint guid, uint entry, float x, float y) => new()
    {
        Guid = guid, Entry = entry, MapId = Map, X = x, Y = y, Z = -15.5288f,
        SpawnTimeMinSeconds = 7200, SpawnTimeMaxSeconds = 7200,
    };

    private sealed class Encounters : IInstanceEncounterStore
    {
        public Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<InstanceEncounter>>([.. RunFixtures.Encounters().Where(e => e.MapId == Map)]);
    }

    private sealed class Maps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [.. MapRegistry.DefaultContinents,
                new MapTemplate(Map, 0, MapType.Instance, 2437, 10, 0, 1, 1816.76f, -4423.37f, "Ragefire Chasm", "")],
            [new AreaTemplate(2437, Map, 0, 927, 0, 0, "Ragefire Chasm", 0, 0)],
            [new AreaTriggerTemplate(Entrance, 1, 1818.4f, -4427.26f, -10.4478f, 0f, 21.69f, 11.83f, 21.22f, 0.576f, "Ragefire Chasm entrance"),
             new AreaTriggerTemplate(Exit, Map, 2.58019f, -0.013587f, -13.3668f, 0f, 30.69f, 12.19f, 25.56f, 0f, "Ragefire Chasm exit")],
            [new AreaTriggerTeleport(Entrance, "Ragefire Chasm - Entering", "", 8, Map, 0.797643f, -8.23429f, -15.5288f, 4.71239f),
             new AreaTriggerTeleport(Exit, "Ragefire Chasm - Exiting", "", 0, 1, 1814.99f, -4419.23f, -18.8151f, 1.91986f)],
            []));
    }

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new GraveyardContent(
                [new WorldSafeLoc(9908, 1, 1837f, -4427f, -10.4478f, 0f, "RFC test graveyard")],
                [new GraveyardLink(9908, 2437, 0)]));
    }
}

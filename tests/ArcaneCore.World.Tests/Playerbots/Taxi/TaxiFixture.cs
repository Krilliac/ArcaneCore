using ArcaneCore.Game;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

/// <summary>
/// Three taxi nodes on map 0 and a flight master beside the first one, which is where a new human bot stands (the Northshire start):
/// node 1 (the master's), node 2 250 yards east of it and node 3 250 yards north of node 2. Paths 1 to 2 (100 copper) and 2 to 3 (50
/// copper), each with waypoint rows for the server's flight system. Nothing is known to a new character (no starting taxi mask).
/// </summary>
internal sealed class TaxiFixture : ICreatureDataStore, INpcContentStore, IQuestContentStore
{
    /// <summary>Adds a quest whose giver and ender stands 12 yards past node 2 (registered as the world's quest content).</summary>
    public bool WithQuest { get; init; }

    public const uint QuestId = 8120;
    public const uint GiverEntry = 8102;
    public static readonly float GiverX = StartX + 262f;

    public const uint MasterEntry = 8101;
    public const uint Gryphon = 3837;
    public const uint GryphonDisplay = 1147;
    public const float StartX = -8949.95f, StartY = -132.49f, Z = 83.53f;
    public static readonly ObjectGuid MasterGuid = ObjectGuid.WithEntry(HighGuid.Unit, MasterEntry, 81001);

    public static readonly TaxiPathNodeCatalog PathNodes = new(
    [
        new TaxiPathNodeRecord(1, 10, 0, 0, StartX - 1f, StartY, Z, 0, 0),
        new TaxiPathNodeRecord(2, 10, 1, 0, StartX + 125f, StartY, Z + 8f, 0, 0),
        new TaxiPathNodeRecord(3, 10, 2, 0, StartX + 250f, StartY, Z, 0, 0),
        new TaxiPathNodeRecord(4, 11, 0, 0, StartX + 250f, StartY, Z, 0, 0),
        new TaxiPathNodeRecord(5, 11, 1, 0, StartX + 250f, StartY + 125f, Z + 8f, 0, 0),
        new TaxiPathNodeRecord(6, 11, 2, 0, StartX + 250f, StartY + 250f, Z, 0, 0),
    ]);

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
    {
        TaxiNodes =
        [
            new TaxiNode { Id = 1, MapId = 0, X = StartX - 1f, Y = StartY, Z = Z, MountAlliance = Gryphon, MountHorde = Gryphon },
            new TaxiNode { Id = 2, MapId = 0, X = StartX + 250f, Y = StartY, Z = Z, MountAlliance = Gryphon, MountHorde = Gryphon },
            new TaxiNode { Id = 3, MapId = 0, X = StartX + 250f, Y = StartY + 250f, Z = Z, MountAlliance = Gryphon, MountHorde = Gryphon },
        ],
        TaxiPaths =
        [
            new TaxiPath { Id = 10, FromNode = 1, ToNode = 2, Price = 100 },
            new TaxiPath { Id = 11, FromNode = 2, ToNode = 3, Price = 50 },
        ],
    });

    Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new QuestContent(
        [new QuestTemplate { Entry = QuestId, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Taxi quest" }],
        [new CreatureQuestRelation { Id = GiverEntry, Quest = QuestId }],
        [new CreatureQuestRelation { Id = GiverEntry, Quest = QuestId }]));

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken)
    {
        var templates = new List<CreatureTemplate>
        {
            new() { Entry = Gryphon, Name = "Gryphon", DisplayIds = [GryphonDisplay] },
            new() { Entry = MasterEntry, Name = "Test flight master", Faction = 900011, NpcFlags = (uint)NpcFlags.FlightMaster, DisplayIds = [49] },
        };
        var spawns = new List<CreatureSpawn>
        {
            new() { Guid = 81001, Entry = MasterEntry, MapId = 0, X = StartX + 2f, Y = StartY, Z = Z },
        };
        if (WithQuest)
        {
            templates.Add(new CreatureTemplate { Entry = GiverEntry, Name = "Test quest giver", Faction = 900011, NpcFlags = 2, DisplayIds = [49] });
            spawns.Add(new CreatureSpawn { Guid = 81002, Entry = GiverEntry, MapId = 0, X = GiverX, Y = StartY, Z = Z });
        }

        return Task.FromResult(new CreatureContent(templates, spawns, [], [], []));
    }
}

internal sealed class TaxiServices : IWorldTestServices
{
    public static readonly AsyncLocal<TaxiFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        if (fixture.WithQuest) services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton(TaxiFixture.PathNodes);
        services.AddSingleton<ICharacterQuestStore>(new MemoryQuestStore());
        services.AddSingleton(new FactionTemplateCatalog([
            new FactionTemplateRecord(1, 1, 0, 1, 0, 0),
            new FactionTemplateRecord(900011, 0, 0, 8, 0, 0),
        ]));
    }
}

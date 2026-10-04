using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Tests.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// The quest and NPC content stores of a reload test: plain lists the test edits between the first load and
/// the reload (the same way an operator edits the database), read again by every load. Registered for the
/// host that is started while <see cref="ReloadContentTestServices.Current"/> holds it.
/// </summary>
internal sealed class ReloadContentFixture : IQuestContentStore, INpcContentStore
{
    public List<QuestTemplate> Templates { get; } = [];

    public List<CreatureQuestRelation> Starters { get; } = [];

    public List<CreatureQuestRelation> Enders { get; } = [];

    public List<NpcGossip> NpcGossips { get; } = [];

    public List<GossipMenu> GossipMenus { get; } = [];

    public List<GossipMenuOption> GossipMenuOptions { get; } = [];

    public List<NpcText> NpcTexts { get; } = [];

    public List<VendorItem> VendorItems { get; } = [];

    public List<TrainerSpell> TrainerSpells { get; } = [];

    public List<PointOfInterest> PointsOfInterest { get; } = [];

    /// <summary>Thrown by the next loads when set (a database that went away).</summary>
    public Exception? Failure { get; set; }

    /// <summary>Character quest rows the journal tests seed (the characters database double).</summary>
    public MemoryQuestStore Characters { get; } = new();

    public ManualQuestClock Clock { get; } = new();

    public static QuestTemplate Quest(uint entry, string title, uint killCount = 5) => new()
    {
        Entry = entry,
        Method = 2,
        QuestLevel = 7,
        MinLevel = 1,
        Title = title,
        Details = "Details of " + title,
        Objectives = "Objectives of " + title,
        ReqCreatureOrGOId1 = 100,
        ReqCreatureOrGOCount1 = killCount,
    };

    public Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default)
        => Failure is { } failure
            ? Task.FromException<QuestContent>(failure)
            : Task.FromResult(new QuestContent([.. Templates], [.. Starters], [.. Enders]));

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Failure is { } failure
            ? Task.FromException<NpcContent>(failure)
            : Task.FromResult(NpcContent.Empty with
            {
                NpcGossips = [.. NpcGossips],
                GossipMenus = [.. GossipMenus],
                GossipMenuOptions = [.. GossipMenuOptions],
                NpcTexts = [.. NpcTexts],
                VendorItems = [.. VendorItems],
                TrainerSpells = [.. TrainerSpells],
                PointsOfInterest = [.. PointsOfInterest],
            });
}

/// <summary>Registers <see cref="ReloadContentFixture"/> in the host started while it is current (async-local, so tests stay isolated).</summary>
internal sealed class ReloadContentTestServices : IWorldTestServices
{
    public static readonly AsyncLocal<ReloadContentFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture)
        {
            return;
        }

        services.AddSingleton<IQuestContentStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<ICharacterQuestStore>(fixture.Characters);
        services.AddSingleton<TimeProvider>(fixture.Clock);
    }

    public static WorldTestHost Start(ReloadContentFixture fixture)
    {
        Current.Value = fixture;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            Current.Value = null;
        }
    }
}

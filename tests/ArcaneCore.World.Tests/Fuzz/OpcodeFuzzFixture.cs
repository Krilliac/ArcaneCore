using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Loot;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// The content behind the opcode handler fuzz's fixture creature and chest, so the service handlers get past their "nothing to offer" early
/// returns: vendor stock (limited and unlimited), trainer spells, two quests the creature starts and ends, two taxi nodes with a path
/// between them (one at the creature), and loot for the chest and for the creature. The npc and quest stores are registered through
/// <see cref="HandlerFuzzContentServices"/> for the one host the fuzz starts; the loot goes to the chest's test context
/// (<see cref="Loot"/>). Thread affinity: immutable after construction, read by the host's startup loaders.
/// </summary>
internal sealed class HandlerFuzzFixtureContent : INpcContentStore, IQuestContentStore
{
    internal const uint PotionEntry = 910001;
    internal const uint SwordEntry = 910002;
    internal const uint BagEntry = 910003;
    internal const uint KillQuest = 910401;
    internal const uint DeliveryQuest = 910402;
    internal const uint TrainerSpellId = 991001;
    internal const uint NearTaxiNode = 910601;
    internal const uint FarTaxiNode = 910602;
    internal const uint ChestLootId = 910500;
    internal const uint CreatureLootId = 910501;

    /// <summary>The loot tables: the chest's row (the potion) and the fixture creature's (a sword row and a gold range).</summary>
    public LootContent Loot { get; } = new(
        [
            (LootTableKind.GameObject, new LootStoreRow(ChestLootId, PotionEntry, 100f, 0, 1, 3)),
            (LootTableKind.Creature, new LootStoreRow(CreatureLootId, SwordEntry, 100f, 0, 1, 1)),
        ],
        [new CreatureLootInfo(HandlerFuzzKit.FixtureCreatureEntry, CreatureLootId, 0, 5, 10)]);

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(NpcContent.Empty with
        {
            VendorItems =
            [
                new VendorItem { Entry = HandlerFuzzKit.FixtureCreatureEntry, Item = PotionEntry, MaxCount = 0, IncrTime = 0, Slot = 0 },
                new VendorItem { Entry = HandlerFuzzKit.FixtureCreatureEntry, Item = SwordEntry, MaxCount = 3, IncrTime = 60, Slot = 1 },
                new VendorItem { Entry = HandlerFuzzKit.FixtureCreatureEntry, Item = BagEntry, MaxCount = 0, IncrTime = 0, Slot = 2 },
            ],
            TrainerSpells =
            [
                new TrainerSpell { Entry = HandlerFuzzKit.FixtureCreatureEntry, Spell = TrainerSpellId, SpellCost = 1 },
            ],
            TaxiNodes =
            [
                new TaxiNode { Id = NearTaxiNode, MapId = 0, X = -8946f, Y = -132f, Z = 83.5f, Name = "Fuzz Landing", MountHorde = 2224, MountAlliance = 1147 },
                new TaxiNode { Id = FarTaxiNode, MapId = 0, X = -8700f, Y = -300f, Z = 90f, Name = "Fuzz Far Landing", MountHorde = 2224, MountAlliance = 1147 },
            ],
            TaxiPaths =
            [
                new TaxiPath { Id = 910701, FromNode = NearTaxiNode, ToNode = FarTaxiNode, Price = 10 },
                new TaxiPath { Id = 910702, FromNode = FarTaxiNode, ToNode = NearTaxiNode, Price = 10 },
            ],
        });

    Task<QuestContent> IQuestContentStore.LoadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new QuestContent(
            [
                new QuestTemplate
                {
                    Entry = KillQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Fuzz Kill", Details = "Kill something.", Objectives = "Slay it.",
                    RequestItemsText = "Well?", OfferRewardText = "Thanks.", ReqCreatureOrGOId1 = 1, ReqCreatureOrGOCount1 = 1, RewOrReqMoney = 50,
                    RewItemId1 = PotionEntry, RewItemCount1 = 1,
                },
                new QuestTemplate
                {
                    Entry = DeliveryQuest, Method = 2, MinLevel = 1, QuestLevel = 1, Title = "Fuzz Delivery", Details = "Bring a potion.", Objectives = "Bring it.",
                    RequestItemsText = "Got it?", OfferRewardText = "Pick one.", ReqItemId1 = PotionEntry, ReqItemCount1 = 2,
                    RewChoiceItemId1 = SwordEntry, RewChoiceItemCount1 = 1, RewChoiceItemId2 = BagEntry, RewChoiceItemCount2 = 1,
                },
            ],
            [
                new CreatureQuestRelation { Id = HandlerFuzzKit.FixtureCreatureEntry, Quest = KillQuest },
                new CreatureQuestRelation { Id = HandlerFuzzKit.FixtureCreatureEntry, Quest = DeliveryQuest },
            ],
            [
                new CreatureQuestRelation { Id = HandlerFuzzKit.FixtureCreatureEntry, Quest = KillQuest },
                new CreatureQuestRelation { Id = HandlerFuzzKit.FixtureCreatureEntry, Quest = DeliveryQuest },
            ]));
}

/// <summary>Registers <see cref="Current"/> as the npc, quest and character-quest stores of a host started while it is set (async-local, like the other fixtures' services).</summary>
internal sealed class HandlerFuzzContentServices : IWorldTestServices
{
    public static readonly AsyncLocal<HandlerFuzzFixtureContent?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } content)
        {
            return;
        }

        services.AddSingleton<INpcContentStore>(content);
        services.AddSingleton<IQuestContentStore>(content);
        services.AddSingleton<ICharacterQuestStore>(new ArcaneCore.World.Tests.Npc.MemoryQuestStore());
    }
}

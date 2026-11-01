using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Loot;

/// <summary>
/// A loot row's condition sees the loot target as its source (vmangos LootItem::AllowedForPlayer, LootMgr.cpp:370-373, and the reference
/// row check at LootMgr.cpp:1226), so a <see cref="ConditionFlags.SwapTargets"/> row tests the loot target as the condition's target. The
/// battleground insignia reference loot (3023-3028) is built this way: the dead player's team picks the row, not the looter's.
/// </summary>
public sealed class LootConditionSourceTests
{
    private const uint HordeVictimCondition = 21;
    private const uint AllianceVictimCondition = 22;
    private const uint ReferenceTable = 3023;
    private const uint HordeItem = ItemTestData.RecruitsShirt;
    private const uint AllianceItem = ItemTestData.WornWoodenShield;
    private const byte SwapTargets = (byte)ConditionFlags.SwapTargets;

    private static readonly ConditionRecord[] Conditions =
    [
        new(HordeVictimCondition, 6, 67, 0, 0, 0, SwapTargets),
        new(AllianceVictimCondition, 6, 469, 0, 0, 0, SwapTargets),
    ];

    private static LootService Service()
    {
        var evaluator = new ConditionEvaluator(ConditionTable.Build(Conditions), new ConditionContext { IsGameEventActive = _ => false });
        (LootTableKind, LootStoreRow)[] rows =
        [
            (LootTableKind.Reference, Row(ReferenceTable, HordeItem, 100, condition: HordeVictimCondition)),
            (LootTableKind.Reference, Row(ReferenceTable, AllianceItem, 100, condition: AllianceVictimCondition)),
        ];
        return new LootService(new LootContent(rows, []), random: new Random(7))
        {
            Items = ItemStore,
            Conditions = (player, source, id) => evaluator.IsSatisfiedWithSource(id, player, source),
        };
    }

    private static Player PlayerOf(uint guid, Race race) => TestWorld.CreatePlayer(guid, 0, 0, Player(guid).Session, race: race);

    private static LootBag Generate(LootService service, Player looter, Player? victim)
        => service.Generate(default, LootSourceKind.Bones, LootType.Insignia, LootTableKind.Reference, ReferenceTable, [looter], lootTarget: victim);

    [Fact]
    public void SwappedTargetRow_TestsTheLootTarget_NotTheLooter()
    {
        LootService service = Service();
        Player horde = PlayerOf(1, Race.Orc);
        Player alliance = PlayerOf(2, Race.Human);

        // The Horde looter strips an Alliance body: only the row whose swapped target is Alliance drops.
        Assert.Equal(AllianceItem, Assert.Single(Generate(service, horde, alliance).Items).ItemId);
        Assert.Equal(HordeItem, Assert.Single(Generate(service, alliance, horde).Items).ItemId);
    }

    [Fact]
    public void SwappedTargetRow_WithoutALootTarget_DoesNotDrop()
    {
        LootService service = Service();
        Player horde = PlayerOf(1, Race.Orc);

        Assert.Empty(Generate(service, horde, null).Items);
    }

    [Fact]
    public void SourceReachesTheCallback()
    {
        LootService service = Service();
        Player looter = PlayerOf(1, Race.Orc);
        Player victim = PlayerOf(2, Race.Human);
        WorldObject? seen = null;
        service.Conditions = (_, source, _) =>
        {
            seen = source;
            return true;
        };

        Generate(service, looter, victim);

        Assert.Same(victim, seen);
    }
}

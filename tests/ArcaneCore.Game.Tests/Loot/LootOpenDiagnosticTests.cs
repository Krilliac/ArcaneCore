using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.Loot;

public sealed class LootOpenDiagnosticTests
{
    private const uint WolfLoot = 299;
    private const uint SkinLoot = 7299;

    [Fact]
    public void RefusedForeignFarAndMissingOpens_ReportReasonsWithoutChangingBag()
    {
        Rig rig = CreateRig();
        (Player killer, _) = rig.Join(1);
        (Player stranger, FakeSession strangerSession) = rig.Join(2, 1, 0);
        Creature wolf = rig.Kill(killer);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.NotNull(bag);
        (uint gold, int items, ulong? owner, bool closed, ObjectGuid[] recipients) = Snapshot(bag);

        LootResult foreign = rig.Loot.Open(stranger, wolf.Guid);
        LootOpenDiagnostic foreignEvidence = rig.Loot.DescribeOpen(stranger, wolf.Guid, foreign);
        Assert.Equal(LootResult.NotAllowed, foreign);
        Assert.False(foreignEvidence.IsRecipient);
        Assert.False(foreignEvidence.HasSomethingForPlayer);
        Assert.True(foreignEvidence.BagFound);
        Assert.Equal(wolf.Guid.Value, foreignEvidence.SourceGuid);
        Assert.True(foreignEvidence.SameMap);
        Assert.Equal(CreatureDeathState.Corpse, foreignEvidence.CreatureDeathState);
        Assert.True(foreignEvidence.CorpseDecayMs > 0);
        Assert.Equal(9, Assert.Single(Packets(strangerSession, WorldOpcode.SmsgLootReleaseResponse)).Payload.Length);
        AssertSnapshot((gold, items, owner, closed, recipients), bag);

        killer.Relocate(30, 0, 83.5f, 0, 0);
        LootResult far = rig.Loot.Open(killer, wolf.Guid);
        LootOpenDiagnostic farEvidence = rig.Loot.DescribeOpen(killer, wolf.Guid, far);
        Assert.Equal(LootResult.TooFar, far);
        Assert.Equal((LootResult.TooFar, true, true), (farEvidence.Result, farEvidence.BagFound, farEvidence.HasSomethingForPlayer));
        AssertSnapshot((gold, items, owner, closed, recipients), bag);

        LootResult missing = rig.Loot.Open(killer, new ObjectGuid(0xF130000000001234));
        LootOpenDiagnostic missingEvidence = rig.Loot.DescribeOpen(killer, new ObjectGuid(0xF130000000001234), missing);
        Assert.Equal(LootResult.NotFound, missing);
        Assert.False(missingEvidence.BagFound);
        Assert.Null(missingEvidence.SameMap);
        Assert.Empty(missingEvidence.RecipientGuids);
        AssertSnapshot((gold, items, owner, closed, recipients), bag);
    }

    [Fact]
    public void ExistingSourceWithoutBag_StillReportsWorldStateWithoutChangingSource()
    {
        Rig rig = CreateRig();
        (Player player, _) = rig.Join(1);
        Creature wolf = rig.Creatures.Creatures.Single(c => c.Entry == WolfEntry);
        uint health = wolf.Health;
        LootResult result = rig.Loot.Open(player, wolf.Guid);
        LootOpenDiagnostic evidence = rig.Loot.DescribeOpen(player, wolf.Guid, result);

        Assert.Equal(LootResult.NotFound, result);
        Assert.False(evidence.BagFound);
        Assert.True(evidence.SourceIsInWorld);
        Assert.True(evidence.SameMap);
        Assert.Equal(CreatureDeathState.Alive, evidence.CreatureDeathState);
        Assert.Equal(wolf.X, evidence.SourceX);
        Assert.Equal(health, wolf.Health);
        Assert.Null(rig.Loot.FindLoot(wolf.Guid));
    }

    [Fact]
    public void SuccessfulOpen_DiagnosticIsReadOnlyAndWindowStillOpens()
    {
        Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.Kill(player);
        LootBag bag = rig.Loot.FindLoot(wolf.Guid)!;
        Assert.NotNull(bag);
        (uint gold, int items, ulong? owner, bool closed, ObjectGuid[] recipients) before = Snapshot(bag);

        LootResult result = rig.Loot.Open(player, wolf.Guid);
        LootOpenDiagnostic evidence = rig.Loot.DescribeOpen(player, wolf.Guid, result);

        Assert.Equal(LootResult.Ok, result);
        Assert.True(evidence.BagFound);
        Assert.True(evidence.SameMap);
        Assert.Equal(CreatureDeathState.Corpse, evidence.CreatureDeathState);
        Assert.True(evidence.IsRecipient);
        Assert.True(evidence.HasSomethingForPlayer);
        Assert.Equal((LootResult.Ok, before.gold, before.items), (evidence.Result, evidence.Gold, evidence.ItemCount));
        Assert.True(player.UnitFlags.HasFlag(UnitFlags.Looting));
        Assert.Contains(Packets(session, WorldOpcode.SmsgLootResponse), packet => packet.Payload.Length >= 14);
        AssertSnapshot(before, bag);
    }

    private static (uint Gold, int Items, ulong? Owner, bool Closed, ObjectGuid[] Recipients) Snapshot(LootBag bag)
        => (bag.Gold, bag.Items.Count, bag.Owner.IsEmpty ? null : bag.Owner.Value, bag.IsClosed, [.. bag.Recipients]);

    private static void AssertSnapshot((uint Gold, int Items, ulong? Owner, bool Closed, ObjectGuid[] Recipients) expected, LootBag bag)
    {
        var actual = Snapshot(bag);
        Assert.Equal((expected.Gold, expected.Items, expected.Owner, expected.Closed),
            (actual.Gold, actual.Items, actual.Owner, actual.Closed));
        Assert.Equal(expected.Recipients, actual.Recipients);
    }

    private sealed class Rig
    {
        public required WorldRuntime World { get; init; }
        public required Map Map { get; init; }
        public required CreatureMapSystem Creatures { get; init; }
        public required LootService Loot { get; init; }
        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            World.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return (player, session);
        }
        public Creature Kill(Player killer)
        {
            Creature creature = Creatures.Creatures.Single(c => c.Entry == WolfEntry);
            Map.Combat.DealDamage(killer, creature, creature.Health, direct: false);
            Assert.Equal(CreatureDeathState.Corpse, creature.DeathState);
            World.RunTick(50);
            return creature;
        }
    }

    private static Rig CreateRig()
    {
        CreatureContent creatures = Content([Template(), Template(300)], [Spawn(1, WolfEntry, 3, 0), Spawn(2, 300, -3, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(creatures);
        var rows = new LootContent(
            [(LootTableKind.Creature, Row(WolfLoot, ItemTestData.ToughJerky, 100, minOrRef: 3, max: 3)),
                (LootTableKind.Skinning, Row(SkinLoot, Hide, 100))],
            [new CreatureLootInfo(WolfEntry, WolfLoot, SkinLoot, 10, 10)]);
        var loot = new LootService(rows, random: new Random(11)) { Items = ItemStore, Quests = new FakeQuestJournal(), Groups = new FakeGroups() };
        map.Combat.UnitKilled += (killer, victim) => loot.OnCreatureKilled(killer, victim);
        return new Rig { World = world, Map = map, Creatures = system, Loot = loot };
    }
}

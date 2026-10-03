using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// The skinning lifecycle of a corpse (vmangos Creature::SetDeathState Creature.cpp:2274-2277, IsSkinnableBy Creature.h:308,
/// AllLootRemovedFromCorpse Creature.cpp:3355-3395, Player::SendLoot Player.cpp:7914-7928): skinnable from death, the tapper's 5 s head start,
/// a skinned and emptied corpse decays at once, partly taken skin loot can be reopened.
/// </summary>
public sealed class SkinningLifecycleTests
{
    private const uint WolfLoot = 299;
    private const uint SkinLoot = 7299;
    private const uint BareEntry = 300;

    private sealed class Rig : IDisposable
    {
        public required WorldRuntime World { get; init; }
        public required Map Map { get; init; }
        public required CreatureMapSystem Creatures { get; init; }
        public required LootService Loot { get; init; }

        public Creature Wolf => Creatures.Creatures.Single(c => c.Entry == WolfEntry);

        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            World.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return (player, session);
        }

        public Creature KillWolf(Player killer)
        {
            Creature wolf = Wolf;
            Map.Combat.DealDamage(killer, wolf, wolf.Health, direct: false);
            Assert.Equal(CreatureDeathState.Corpse, wolf.DeathState);
            return wolf;
        }

        public void Dispose() => World.Dispose();
    }

    private static Rig CreateRig(IEnumerable<(LootTableKind, LootStoreRow)>? rows = null, uint skin = SkinLoot)
    {
        CreatureContent creatures = Content([Template(), Template(BareEntry)], [Spawn(1, WolfEntry, 3, 0), Spawn(2, BareEntry, -3, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(creatures);
        var content = new LootContent(rows ??
            [
                (LootTableKind.Creature, Row(WolfLoot, ItemTestData.ToughJerky, 100)),
                (LootTableKind.Skinning, Row(SkinLoot, Hide, 100)),
                (LootTableKind.Skinning, Row(SkinLoot, ItemTestData.ToughJerky, 100)),
            ],
            [new CreatureLootInfo(WolfEntry, WolfLoot, skin, 10, 10)]);
        var loot = new LootService(content, random: new Random(11)) { Items = ItemStore };
        map.Combat.UnitKilled += (killer, victim) => loot.OnCreatureKilled(killer, victim);
        return new Rig { World = world, Map = map, Creatures = system, Loot = loot };
    }

    private static void LootOut(Rig rig, Player player, Creature wolf)
    {
        rig.Loot.Open(player, wolf.Guid);
        rig.Loot.TakeItem(player, 0);
        rig.Loot.TakeMoney(player);
        rig.Loot.Release(player, wolf.Guid);
    }

    [Fact]
    public void ACorpse_IsSkinnableFromDeath_WhenItHasASkinningTemplateAndALootRecipient()
    {
        using Rig rig = CreateRig();
        (Player player, _) = rig.Join(1);

        Creature wolf = rig.KillWolf(player);

        Assert.True(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));   // not only after it was looted out
        Assert.False(rig.Loot.IsCorpseLooted(wolf));
    }

    [Fact]
    public void ASkinningIdWithoutATemplate_IsNeverSkinnable()
    {
        using Rig rig = CreateRig(rows: [(LootTableKind.Creature, Row(WolfLoot, ItemTestData.ToughJerky, 100))]);
        (Player player, _) = rig.Join(1);

        Assert.False(rig.KillWolf(player).UnitFlags.HasFlag(UnitFlags.Skinnable)); // vmangos LootTemplates_Skinning.HaveLootFor
    }

    [Fact]
    public void TheTappersHaveA5SecondHeadStart_ThenEveryoneMaySkin()
    {
        using Rig rig = CreateRig();
        (Player killer, _) = rig.Join(1);
        (Player stranger, _) = rig.Join(2, 1, 0);
        Creature wolf = rig.KillWolf(killer);

        Assert.True(rig.Loot.IsSkinnableBy(killer, wolf));
        Assert.False(rig.Loot.IsSkinnableBy(stranger, wolf));

        rig.World.RunTick(4900);
        Assert.False(rig.Loot.IsSkinnableBy(stranger, wolf));
        rig.World.RunTick(100);
        Assert.Equal(0u, wolf.SkinningForOthersMs);
        Assert.True(rig.Loot.IsSkinnableBy(stranger, wolf));
    }

    [Fact]
    public void LootingTheCorpseOut_RestartsTheHeadStart_AndLeavesTheSkinnableCorpseDecayTimeAlone()
    {
        using Rig rig = CreateRig();
        (Player killer, _) = rig.Join(1);
        Creature wolf = rig.KillWolf(killer);
        rig.World.RunTick(6000);
        Assert.Equal(0u, wolf.SkinningForOthersMs);
        uint decayBefore = wolf.CorpseDecayMs;

        LootOut(rig, killer, wolf);

        Assert.True(rig.Loot.IsCorpseLooted(wolf));
        Assert.Equal(Creature.SkinningForOthersDefaultMs, wolf.SkinningForOthersMs);
        Assert.True(wolf.UnitFlags.HasFlag(UnitFlags.Skinnable));
        Assert.True(wolf.CorpseDecayMs >= decayBefore - 100); // untouched: vmangos only shortens a corpse that is not (or no longer) skinnable
    }

    [Fact]
    public void ASkinnedCorpse_DecaysAtOnce_WhenItsSkinLootIsTakenOut()
    {
        using Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        LootOut(rig, player, wolf);

        Assert.Equal(LootResult.Ok, rig.Loot.OpenSkinning(player, wolf));
        Assert.True(wolf.LootedForSkin);
        rig.Loot.TakeItem(player, 0);
        rig.Loot.TakeItem(player, 1);
        rig.Loot.Release(player, wolf.Guid);

        Assert.Equal(0u, wolf.CorpseDecayMs);
        Assert.Equal(0u, wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & LootService.UnitDynFlagLootable);
        Assert.Equal(LootResult.NotLootable, rig.Loot.OpenSkinning(player, wolf)); // once per corpse
    }

    [Fact]
    public void PartlyTakenSkinLoot_StaysLootable_AndCanBeReopened()
    {
        using Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        LootOut(rig, player, wolf);
        rig.Loot.OpenSkinning(player, wolf);
        Assert.NotEqual(0u, wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & LootService.UnitDynFlagLootable); // set so the loot can be reopened

        rig.Loot.TakeItem(player, 0);
        rig.Loot.Release(player, wolf.Guid);
        session.Clear();

        Assert.Equal(LootResult.Ok, rig.Loot.Open(player, wolf.Guid));
        ParsedLoot reopened = ParsedLoot.Parse(Assert.Single(Packets(session, ArcaneCore.Protocol.WorldOpcode.SmsgLootResponse)).Payload);
        ParsedLootItem left = Assert.Single(reopened.Items);
        Assert.Equal(1, left.Slot);
        Assert.NotEqual(0u, wolf.GetUInt32(UpdateFields.UnitDynamicFlags) & LootService.UnitDynFlagLootable);
        Assert.True(wolf.CorpseDecayMs > 0);   // not gone while loot remains
    }

    [Fact]
    public void ACorpseWithoutSkinningLoot_DecaysSoonerOnceLootedOut_LikeBefore()
    {
        using Rig rig = CreateRig(rows: [(LootTableKind.Creature, Row(WolfLoot, ItemTestData.ToughJerky, 100))]);
        (Player player, _) = rig.Join(1);
        Creature wolf = rig.KillWolf(player);
        uint before = wolf.CorpseDecayMs;

        LootOut(rig, player, wolf);

        Assert.True(wolf.CorpseDecayMs < before);
    }
}
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// Pick Pocket: the cast checks (vmangos Spell.cpp:6062-6071), the loot window of a living creature (Player.cpp:7833-7872), the money formula
/// <c>10 * (urand(0, mobLevel/2) + urand(0, playerLevel/2)) * rate</c>, the one-attempt state (quest items only for a second pickpocketer) and
/// the release rules (LootHandler.cpp:567-600). Fixtures follow classic-db: a humanoid with pocket loot id 1 and a quest row (negative chance).
/// </summary>
public sealed class PickpocketLootTests
{
    private const uint ThiefEntry = 7001;
    private const uint NoPocketsEntry = 7002;
    private const uint ThiefLevel = 30;
    private const uint PocketLoot = 1;
    private const uint SpellId = 921;
    private const uint Coin = ArcaneCore.Game.Tests.ItemTestData.ToughJerky;

    /// <summary>The upper end of every range: urand(0, n) always n.</summary>
    private sealed class MaxRandom : Random
    {
        public override int Next(int minValue, int maxValue) => maxValue - 1;
    }

    private sealed class CreatureResolver(CreatureMapSystem creatures) : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid)
            => guid.IsEmpty ? null : reference.Guid == guid ? reference : (Unit?)creatures.FindCreature(guid) ?? reference.Map?.FindPlayer(guid);
    }

    private sealed class Rig : IDisposable
    {
        public required WorldRuntime World { get; init; }
        public required Map Map { get; init; }
        public required CreatureMapSystem Creatures { get; init; }
        public required LootService Loot { get; init; }
        public required PickpocketLoot Pockets { get; init; }
        public required SpellSystem Spells { get; init; }
        public required FakeQuestJournal Quests { get; init; }
        public required FakeGroups Groups { get; init; }
        public uint Now { get; set; } = 1000;

        public Creature Thief => Creatures.Creatures.Single(c => c.Entry == ThiefEntry);

        public Creature NoPockets => Creatures.Creatures.Single(c => c.Entry == NoPocketsEntry);

        public (Player Player, FakeSession Session) Join(uint guid, float x = 0, float y = 0)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            World.AddPlayer(player);
            World.RunTick(50);
            session.Clear();
            return (player, session);
        }

        public SpellCastResult Cast(Player player, Unit target)
            => Spells.CastSpell(player, SpellId, SpellCastTargets.ForUnit(target.Guid), triggered: false);

        public void Dispose() => World.Dispose();
    }

    private static Rig CreateRig(IEnumerable<(LootTableKind, LootStoreRow)>? rows = null, uint pocketId = PocketLoot)
    {
        CreatureContent content = Content(
            [Template(ThiefEntry, b => b.MinLevel = b.MaxLevel = (byte)ThiefLevel), Template(NoPocketsEntry)],
            [Spawn(1, ThiefEntry, 3, 0), Spawn(2, NoPocketsEntry, -3, 0)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content);
        var loot = new LootContent(
            rows ??
            [
                (LootTableKind.Pickpocketing, Row(PocketLoot, Coin, 100, minOrRef: 2, max: 2)),
                (LootTableKind.Pickpocketing, Row(PocketLoot, QuestItem, -100)),
            ],
            [], [], [new KeyValuePair<uint, uint>(ThiefEntry, pocketId)]);
        var quests = new FakeQuestJournal();
        var groups = new FakeGroups();
        var service = new LootService(loot, random: new Random(3)) { Items = ItemStore, Quests = quests, Groups = groups };
        var pockets = new PickpocketLoot(service, new MaxRandom());
        map.Combat.UnitKilled += (killer, victim) => service.OnCreatureKilled(killer, victim);
        map.Combat.UnitKilled += pockets.OnCreatureKilled;

        var rig = new Rig { World = world, Map = map, Creatures = system, Loot = service, Pockets = pockets, Quests = quests, Groups = groups,
            Spells = null! };
        SpellInfo spell = SpellTestKit.Spell(SpellId, SpellTestKit.Effect(SpellEffectName.Pickpocket, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        var spells = new SpellSystem(new SpellStore([spell], [], []), () => rig.Now, units: new CreatureResolver(system), random: new Random(1));
        new PickpocketSpells(_ => pockets).Register(spells);
        return new Rig { World = world, Map = map, Creatures = system, Loot = service, Pockets = pockets, Quests = quests, Groups = groups, Spells = spells };
    }

    private static ParsedLoot Window(FakeSession session)
        => ParsedLoot.Parse(Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgLootResponse)));

    // --- cast checks ----------------------------------------------------------------------

    [Fact]
    public void ACreatureWithoutPocketLoot_AnswersTargetNoPockets()
    {
        using Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);

        Assert.Equal(SpellCastResult.TargetNoPockets, rig.Cast(player, rig.NoPockets));

        Assert.Empty(SpellTestKit.Packets(session, WorldOpcode.SmsgLootResponse));
        Assert.Equal(0, rig.Pockets.TrackedCreatures);
    }

    [Fact]
    public void AnotherPlayer_AndAPlayersCreature_AreBadTargets()
    {
        using Rig rig = CreateRig();
        (Player player, _) = rig.Join(1);
        (Player other, _) = rig.Join(2, 1, 0);
        Assert.Equal(SpellCastResult.BadTargets, rig.Cast(player, other)); // not a creature (cast against the other player)

        rig.Pockets.IsPlayerOwned = c => ReferenceEquals(c, rig.Thief);
        Assert.Equal(SpellCastResult.BadTargets, rig.Cast(player, rig.Thief));
    }

    [Fact]
    public void PickpocketingInCombat_IsAllowed_In112()
    {
        // vmangos Spell.cpp:5466-5470 has the TARGET_IN_COMBAT refusal behind a build check that excludes 1.12.
        using Rig rig = CreateRig();
        (Player player, _) = rig.Join(1);
        rig.Thief.UnitFlags |= UnitFlags.InCombat;

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(player, rig.Thief));
    }

    // --- the first pick ------------------------------------------------------------------

    [Fact]
    public void FirstPick_OpensTheTableLoot_AndTheLevelBasedMoney_AsWireType2()
    {
        using Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast(player, rig.Thief));

        ParsedLoot window = Window(session);
        Assert.Equal((rig.Thief.Guid.Value, LootType.Pickpocketing), (window.Guid, window.Type));
        // MaxRandom: a = 30 / 2 = 15, b = 1 / 2 = 0 -> 10 * 15 = 150 copper.
        Assert.Equal(150u, window.Gold);
        ParsedLootItem item = Assert.Single(window.Items);       // the quest row is hidden (nobody needs it)
        Assert.Equal((Coin, 2u, LootSlotType.AllowLoot), (item.ItemId, item.Count, item.SlotType));
        Assert.Equal(1, rig.Pockets.TrackedCreatures);
    }

    [Fact]
    public void Money_FollowsMobLevelAndPlayerLevel_AndTheMoneyRate()
    {
        using Rig rig = CreateRig();
        rig.Loot.Options.MoneyRate = 2.0f;
        (Player player, FakeSession session) = rig.Join(1);
        player.Level = 20; // b = 10

        rig.Cast(player, rig.Thief);

        Assert.Equal(10u * (15 + 10) * 2, Window(session).Gold);
    }

    [Fact]
    public void ThePickpocketMoney_IsNotSharedWithTheGroup_TheItemsAreThePickersAlone()
    {
        using Rig rig = CreateRig();
        (Player rogue, _) = rig.Join(1);
        (Player mate, FakeSession mateSession) = rig.Join(2, 1, 0);
        rig.Groups.Create(LootMethod.FreeForAll, rogue, mate);
        rig.Cast(rogue, rig.Thief);
        uint before = rogue.Money;

        Assert.True(rig.Loot.TakeMoney(rogue));

        Assert.Equal(150u, rogue.Money - before); // not 75
        Assert.Equal(0u, mate.Money);
        Assert.Empty(SpellTestKit.Packets(mateSession, WorldOpcode.SmsgLootMoneyNotify));
    }

    [Fact]
    public void TakingTheItems_GoesIntoTheBags_AndReleasingAFullyLootedWindowClearsIt()
    {
        using Rig rig = CreateRig();
        (Player player, _) = rig.Join(1);
        rig.Cast(player, rig.Thief);

        Assert.Equal(InventoryResult.Ok, rig.Loot.TakeItem(player, 0));
        Assert.True(rig.Loot.TakeMoney(player));
        Assert.Equal(2u, player.Inventory.GetItemCount(Coin));
        rig.Loot.Release(player, rig.Thief.Guid);

        Assert.Null(rig.Loot.FindLoot(rig.Thief.Guid));
        Assert.Null(rig.Loot.OpenLootOf(player));
    }

    // --- the one attempt ---------------------------------------------------------------------

    [Fact]
    public void TheSameLooterReopens_WhatIsLeft_WithoutNewMoney()
    {
        using Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        rig.Cast(player, rig.Thief);
        rig.Loot.TakeMoney(player);
        rig.Loot.Release(player, rig.Thief.Guid); // the item is left
        session.Clear();

        rig.Cast(player, rig.Thief);

        ParsedLoot window = Window(session);
        Assert.Equal(0u, window.Gold);
        Assert.Equal(Coin, Assert.Single(window.Items).ItemId);
    }

    [Fact]
    public void ASecondPickpocketer_SeesOnlyTheQuestItemsTheyNeed_AndNoMoney()
    {
        using Rig rig = CreateRig();
        (Player first, _) = rig.Join(1);
        (Player second, FakeSession secondSession) = rig.Join(2, 1, 0);
        rig.Quests.Needs.Add((second.Guid, QuestItem));
        rig.Cast(first, rig.Thief);

        rig.Cast(second, rig.Thief);

        ParsedLoot window = Window(secondSession);
        Assert.Equal(0u, window.Gold);
        ParsedLootItem quest = Assert.Single(window.Items);
        Assert.Equal(QuestItem, quest.ItemId); // the table's ordinary coin is not regenerated
    }

    [Fact]
    public void AfterTheLootWasTakenCompletely_ARepickShowsQuestItemsOnly()
    {
        using Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        rig.Quests.Needs.Add((player.Guid, QuestItem));
        rig.Cast(player, rig.Thief);
        rig.Loot.TakeItem(player, 0);
        rig.Loot.TakeMoney(player);
        rig.Loot.Release(player, rig.Thief.Guid);
        session.Clear();

        rig.Cast(player, rig.Thief);

        ParsedLoot window = Window(session);
        Assert.Equal(0u, window.Gold);
        Assert.Equal(QuestItem, Assert.Single(window.Items).ItemId);
    }

    // --- the creature dies / the window ------------------------------------------------------

    [Fact]
    public void KillingThePickpocketedCreature_ForgetsTheState_AndClosesTheWindow()
    {
        using Rig rig = CreateRig();
        (Player player, FakeSession session) = rig.Join(1);
        rig.Cast(player, rig.Thief);
        session.Clear();

        rig.Map.Combat.DealDamage(player, rig.Thief, rig.Thief.Health, direct: false);

        Assert.Equal(0, rig.Pockets.TrackedCreatures);
        Assert.Single(SpellTestKit.Packets(session, WorldOpcode.SmsgLootReleaseResponse)); // the pocket window is closed
        Assert.Null(rig.Loot.OpenLootOf(player));
        LootBag corpse = rig.Loot.FindLoot(rig.Thief.Guid)!;
        Assert.Equal(LootType.Corpse, corpse.Type); // the corpse loot replaced the pocket loot
    }

    [Fact]
    public void ThePickersDistance_IsCheckedWhileTheCreatureLives()
    {
        using Rig rig = CreateRig();
        (Player player, _) = rig.Join(1);
        rig.Cast(player, rig.Thief);
        player.Relocate(60, 0, player.Z, 0, 0);

        Assert.NotEqual(InventoryResult.Ok, rig.Loot.TakeItem(player, 0));
    }

    [Fact]
    public void AnotherCreatureDying_DoesNotSweepAnOpenPocketWindow()
    {
        using Rig rig = CreateRig();
        (Player player, _) = rig.Join(1);
        rig.Cast(player, rig.Thief);
        Assert.NotNull(rig.Loot.FindLoot(rig.Thief.Guid));

        // Prune() runs at every corpse generation and used to treat any registered live creature as a stale corpse.
        rig.Map.Combat.DealDamage(player, rig.NoPockets, rig.NoPockets.Health, direct: false);

        Assert.NotNull(rig.Loot.FindLoot(rig.Thief.Guid));
        Assert.NotNull(rig.Loot.OpenLootOf(player));
    }
}
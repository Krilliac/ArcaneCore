using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// The 10% durability loss of a death by a creature (vmangos Unit::Kill, Unit.cpp:1190-1202): not when a player (or a unit
/// that acts for one) tapped the kill, not in a battleground. The self-damage deaths take their own loss
/// (Player::EnvironmentalDamage, Player.cpp:755-770; EnvironmentalDamageTests).
/// </summary>
public sealed class PlayerDeathDurabilityTests
{
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(30, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Alterac Valley", ""),
        ],
        [], [], [], []);

    /// <summary>A creature-like unit that acts for a player (a pet, IPlayerControlledUnit).</summary>
    private sealed class PetStandIn : Unit, ICombatCreature, IPlayerControlledUnit
    {
        public PetStandIn(Player owner)
            : base(ObjectGuid.WithEntry(HighGuid.Unit, 2, 900), Game.TypeId.Unit, TypeMask.Object | TypeMask.Unit, UpdateFields.UnitEnd)
        {
            ControllingPlayer = owner;
            Level = 60;
            MaxHealth = Health = 1000;
        }

        public Player? ControllingPlayer { get; }

        public bool IsInEvadeMode => false;

        public bool CanParry => true;

        public bool CanBlock => true;

        public bool CanCrush => true;

        public bool IsWorldBoss => false;

        public bool RegeneratesHealth => true;

        public void OnAttackedBy(Unit attacker)
        {
        }

        public void OnJustDied(Unit? killer)
        {
        }
    }

    private static (WorldRuntime World, Player Victim, FakeSession Session, Item Sword) Setup(uint mapId = 0)
    {
        (WorldRuntime world, _, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
        WorldMaps.Of(world).Load(Content);
        world.GetMap(mapId).Combat.Hooks = hooks;
        var session = new FakeSession(1);
        Player victim = CombatTestKit.AddPlayer(world, 1, 100, 100, session, mapId: mapId);
        Wire(victim.Inventory);
        victim.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData { Guid = 4000, Entry = WornShortsword, Durability = 20 })]);
        session.Clear();
        return (world, victim, session, victim.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!);
    }

    private static bool Told(FakeSession session) => session.Sent.Any(p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);

    [Fact]
    public void ACreatureKill_TakesTenPercentOfTheWornItems_AndTellsTheClientOnce()
    {
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup();
        using (world)
        {
            var creature = new CombatTestUnit();
            creature.Spawn(world.GetMap(0), 100, 101);

            victim.Map!.Combat.Kill(creature, victim);

            Assert.Equal(18u, sword.Durability); // max(1, int(20 x 0.10)) = 2
            Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
            Assert.Empty(session.Sent.Single(p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath).Payload);
        }
    }

    [Fact]
    public void WithNoKiller_ItIsTakenToo()
    {
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup();
        using (world)
        {
            victim.Map!.Combat.Kill(null, victim);

            Assert.Equal(18u, sword.Durability);
            Assert.True(Told(session));
        }
    }

    [Fact]
    public void APlayerKill_TakesNothing()
    {
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup();
        using (world)
        {
            Player killer = CombatTestKit.AddPlayer(world, 2, 101, 100, new FakeSession(2));

            victim.Map!.Combat.Kill(killer, victim);

            Assert.Equal(20u, sword.Durability);
            Assert.False(Told(session));
        }
    }

    [Fact]
    public void AKillByAUnitThatActsForAPlayer_TakesNothing()
    {
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup();
        using (world)
        {
            Player owner = CombatTestKit.AddPlayer(world, 2, 101, 100, new FakeSession(2));

            victim.Map!.Combat.Kill(new PetStandIn(owner), victim);

            Assert.Equal(20u, sword.Durability);
            Assert.False(Told(session));
        }
    }

    [Fact]
    public void AKillByAPetWithoutAnOwner_StillCounts_AsACreatureKill()
    {
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup();
        using (world)
        {
            victim.Map!.Combat.Kill(new PetStandIn(null!), victim);

            Assert.Equal(18u, sword.Durability);
            Assert.True(Told(session));
        }
    }

    [Fact]
    public void ASelfKill_IsLeftToTheEnvironmentalPath_NoSecondLoss()
    {
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup();
        using (world)
        {
            victim.Map!.Combat.Kill(victim, victim);

            Assert.Equal(20u, sword.Durability);
            Assert.False(Told(session));
        }
    }

    [Fact]
    public void InABattleground_NothingIsLost()
    {
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup(mapId: 30);
        using (world)
        {
            var creature = new CombatTestUnit();
            creature.Spawn(world.GetMap(30), 100, 101);

            victim.Map!.Combat.Kill(creature, victim);

            Assert.Equal(20u, sword.Durability);
            Assert.False(Told(session));
        }
    }

    [Fact]
    public void WithDurabilityLossDisabled_TheItemsKeepTheirPoints_ButTheClientIsStillTold()
    {
        // The packet is unconditional in vmangos; DurabilityLoss.Enable only gates DurabilityPointsLoss (Player.cpp:4866).
        (WorldRuntime world, Player victim, FakeSession session, Item sword) = Setup();
        using (world)
        {
            victim.Inventory.Options.DurabilityLossEnable = false;

            victim.Map!.Combat.Kill(null, victim);

            Assert.Equal(20u, sword.Durability);
            Assert.True(Told(session));
        }
    }
}

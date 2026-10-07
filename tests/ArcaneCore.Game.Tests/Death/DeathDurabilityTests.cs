using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Duel;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.Death;

public sealed class DeathDurabilityTests
{
    [Fact]
    public void CreatureLethalDamage_WearsOnlyEquipment_AndSendsOneEmptyPacketToVictim()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, Item backpack) = AddVictim(world);
        var watcherSession = new FakeSession(2);
        CombatTestKit.AddPlayer(world, 2, 1, 0, watcherSession);
        Creature creature = AddCreature(victim);
        world.RunTick(1);
        session.Clear();
        watcherSession.Clear();

        victim.Map!.Combat.DealDamage(creature, victim, victim.Health);

        Assert.Equal(DeathState.JustDied, victim.Combat.DeathState);
        Assert.Equal(18u, equipment.Durability);
        Assert.Equal(20u, backpack.Durability);
        Assert.False(victim.Combat.PvpDeath);
        Assert.Empty(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath).Payload);
        Assert.DoesNotContain(watcherSession.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
        victim.Map!.Combat.DealDamage(creature, victim, 1000);
        victim.Map!.Combat.Kill(creature, victim);
        victim.Map!.Combat.KillPlayer(victim);
        Assert.Equal(18u, equipment.Durability);
        Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Fact]
    public void DisabledDurability_StillSendsTheSourceDeathNotification()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        victim.Inventory.Options.DurabilityLossEnable = false;
        victim.Map!.Combat.DealDamage(AddCreature(victim), victim, victim.Health);
        Assert.Equal(20u, equipment.Durability);
        Assert.Empty(Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath).Payload);
    }

    [Fact]
    public void NonLethalCreatureDamage_DoesNotApplyDeathWear()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        victim.Inventory.Options.DurabilityLossChanceDamage = 0; // isolate death wear from random nonlethal hit wear
        victim.Map!.Combat.DealDamage(AddCreature(victim), victim, 1);
        Assert.Equal(20u, equipment.Durability);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayerAndSelfKills_DoNotApplyOrdinaryDeathWear(bool self)
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        Player killer = self ? victim : CombatTestKit.AddPlayer(world, 2, 1, 0, new FakeSession(2));
        victim.Map!.Combat.DealDamage(killer, victim, victim.Health);
        Assert.Equal(20u, equipment.Durability);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealCreatureOwnerAndCharmerLinks_SuppressOrdinaryDeathWear(bool charmed)
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        Player owner = CombatTestKit.AddPlayer(world, 2, 1, 0, new FakeSession(2));
        Creature killer = AddCreature(victim);
        if (charmed)
        {
            killer.SetUInt64(UpdateFields.UnitFieldCharmedby, owner.Guid.Value);
        }
        else
        {
            killer.SetOwnerGuid(owner.Guid);
        }
        Assert.Same(owner, killer.GetCharmerOrOwnerPlayerOrSelf());

        victim.Map!.Combat.DealDamage(killer, victim, victim.Health);

        Assert.Equal(20u, equipment.Durability);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Fact]
    public void UnresolvedOwner_DoesNotFabricateAPlayerTap()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        Creature killer = AddCreature(victim);
        killer.SetOwnerGuid(new ObjectGuid(999));
        Assert.Null(killer.GetCharmerOrOwnerPlayerOrSelf());
        victim.Map!.Combat.DealDamage(killer, victim, victim.Health);
        Assert.Equal(18u, equipment.Durability);
        Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Fact]
    public void SettlementBlocksDeathAndWear_UntilCallerRetries()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        Creature killer = AddCreature(victim);
        Guid operation = Guid.NewGuid();
        Assert.True(victim.BeginQuestSettlement(operation));
        Assert.Equal(0u, victim.Map!.Combat.DealDamage(killer, victim, victim.Health));
        victim.Map!.Combat.Kill(killer, victim);
        Assert.Equal(DeathState.Alive, victim.Combat.DeathState);
        Assert.Equal(20u, equipment.Durability);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
        Assert.True(victim.EndQuestSettlement(operation));
        victim.Map!.Combat.DealDamage(killer, victim, victim.Health);
        Assert.Equal(18u, equipment.Durability);
    }

    [Fact]
    public void EnvironmentalDeath_ChargesEquipmentOnlyOnce()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, victim.Health);
        Assert.Equal(18u, equipment.Durability);
        Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Fact]
    public void DuelOpponentClampsWithoutWear_ThirdCreatureKillAppliesWear()
    {
        using var rig = new DuelRig();
        LoadEquipment(rig.B);
        rig.Challenge();
        rig.AcceptAndStart();
        rig.SessionB.Clear();
        rig.Map.Combat.DealDamage(rig.A, rig.B, rig.B.Health);
        Assert.Equal(1u, rig.B.Health);
        Assert.Equal(20u, rig.B.Inventory.GetItem(0xff, InventorySlots.MainHand)!.Durability);
        Assert.DoesNotContain(rig.SessionB.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
        rig.Map.Combat.DealDamage(AddCreature(rig.B), rig.B, 1);
        Assert.Equal(18u, rig.B.Inventory.GetItem(0xff, InventorySlots.MainHand)!.Durability);
        Assert.Single(rig.SessionB.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    private static (Player Player, FakeSession Session, Item Equipment, Item Backpack) AddVictim(WorldRuntime world)
    {
        var session = new FakeSession(1);
        Player victim = CombatTestKit.AddPlayer(world, 1, 0, 0, session);
        LoadEquipment(victim);
        Item backpack = Give(victim.Inventory, WornShortsword);
        session.Clear();
        return (victim, session, victim.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!, backpack);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallerAndSourceSpellCanSuppressDeathWear(bool spellExemption)
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        SpellInfo? spell = spellExemption ? new SpellInfo { Id = 963001, AttributesEx3 = 0x20 } : null;
        victim.Map!.Combat.DealDamage(AddCreature(victim), victim, victim.Health, durabilityLoss: spellExemption, threatSpell: spell);
        Assert.Equal(DeathState.JustDied, victim.Combat.DeathState);
        Assert.Equal(20u, equipment.Durability);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Fact]
    public void BattlegroundMapTemplate_SuppressesOrdinaryDeathWear()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        WorldMaps.Of(world).Load(new MapContent(
            [new MapTemplate(0, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Death Wear Battleground", "")],
            [], [], [], []));
        (Player victim, FakeSession session, Item equipment, _) = AddVictim(world);
        victim.Map!.Combat.DealDamage(AddCreature(victim), victim, victim.Health);
        Assert.Equal(DeathState.JustDied, victim.Combat.DeathState);
        Assert.Equal(20u, equipment.Durability);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    private static void LoadEquipment(Player player)
    {
        Wire(player.Inventory);
        player.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand,
            new ItemInstanceData { Guid = 4000, Entry = WornShortsword, Durability = 20 })]);
    }

    private static Creature AddCreature(Player victim)
    {
        var template = new CreatureTemplate { Entry = 963000, Name = "Death Wear", MinLevel = 1, MaxLevel = 1,
            MinLevelHealth = 100, MaxLevelHealth = 100, DisplayIds = [903], Faction = 35 };
        var creature = new Creature(963000, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        creature.Relocate(victim.X + 1, victim.Y, victim.Z, 0, 0);
        creature.MapId = victim.MapId;
        victim.Map!.AddObject(creature);
        victim.Map!.Combat.Track(creature);
        return creature;
    }
}

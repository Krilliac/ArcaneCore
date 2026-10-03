using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>Player::EnvironmentalDamage (vmangos Player.cpp:713-775) and SMSG_ENVIRONMENTAL_DAMAGE_LOG (Combat.cpp:105-127).</summary>
public sealed class EnvironmentalDamageTests
{
    private sealed class FakeMitigation : IEnvironmentalDamageMitigation
    {
        public HashSet<EnvironmentalSchool> Immune { get; } = [];

        public EnvironmentalMitigation Result { get; set; }

        public List<EnvironmentalSchool> Calculated { get; } = [];

        public bool IsImmune(Player player, EnvironmentalSchool school) => Immune.Contains(school);

        public EnvironmentalMitigation Calculate(Player player, EnvironmentalSchool school, uint damage)
        {
            Calculated.Add(school);
            return Result;
        }
    }

    private static (WorldRuntime World, Player Victim, FakeSession VictimSession, FakeSession WatcherSession) Setup(AccountSecurity security = AccountSecurity.Player)
    {
        (WorldRuntime world, _, _, _) = CombatTestKit.CreateWorld();
        var victimSession = new FakeSession(1, security);
        var watcherSession = new FakeSession(2);
        Player victim = CombatTestKit.AddPlayer(world, 1, 100, 100, victimSession);
        CombatTestKit.AddPlayer(world, 2, 101, 100, watcherSession);
        world.RunTick(50); // the two players see each other
        victimSession.Clear();
        watcherSession.Clear();
        return (world, victim, victimSession, watcherSession);
    }

    private static byte[] ExpectedLog(ulong guid, byte type, uint damage, uint absorb = 0, int resist = 0)
    {
        var w = new PacketWriter(21);
        w.WriteUInt64(guid);
        w.WriteByte(type);
        w.WriteUInt32(damage);
        w.WriteUInt32(absorb);
        w.WriteInt32(resist);
        return w.ToArray();
    }

    [Fact]
    public void TheLog_IsGuidTypeDamageAbsorbResist_ToTheVictimAndTheObservers()
    {
        (WorldRuntime world, Player victim, FakeSession mine, FakeSession theirs) = Setup();

        uint dealt = EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, 50);

        Assert.Equal(50u, dealt);
        byte[] expected = [1, 0, 0, 0, 0, 0, 0, 0, /* type */ 3, /* damage */ 50, 0, 0, 0, /* absorb */ 0, 0, 0, 0, /* resist */ 0, 0, 0, 0];
        Assert.Equal(expected, ExpectedLog(1, 3, 50));
        Assert.Equal(expected, Assert.Single(mine.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Payload);
        Assert.Equal(expected, Assert.Single(theirs.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Payload);
    }

    [Fact]
    public void FallToVoid_IsLoggedAsFall()
    {
        (WorldRuntime world, Player victim, FakeSession mine, _) = Setup();

        EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.FallToVoid, 10);

        Assert.Equal(ExpectedLog(1, 2, 10), Assert.Single(mine.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Payload);
    }

    [Fact]
    public void ADeadPlayerAndAGameMaster_TakeNothing_AndNothingIsSent()
    {
        (WorldRuntime world, Player victim, FakeSession mine, FakeSession theirs) = Setup(AccountSecurity.GameMaster);
        victim.SetGameMaster(true);

        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Fall, 500));
        Assert.Equal(1000u, victim.Health);
        Assert.Empty(mine.Sent);
        Assert.Empty(theirs.Sent);

        victim.SetGameMaster(false);
        victim.Map!.Combat.Kill(null, victim);
        mine.Clear();
        theirs.Clear();
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Fall, 500));
        Assert.Empty(mine.Sent);
        Assert.Empty(theirs.Sent);
    }

    [Fact]
    public void NonLethalDamage_IsSelfDamage_NoCombatNoDurabilityLoss()
    {
        (WorldRuntime world, Player victim, FakeSession mine, _) = Setup();

        EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Drowning, 50);

        Assert.Equal(950u, victim.Health);
        Assert.Equal(0u, (uint)(victim.UnitFlags & UnitFlags.InCombat));
        Assert.True(victim.IsAlive);
        Assert.DoesNotContain(mine.Sent, p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath);
    }

    [Fact]
    public void LethalDamage_KillsTheVictim_LosesTenPercentDurability_AndSaysSo()
    {
        (WorldRuntime world, Player victim, FakeSession mine, _) = Setup();
        Wire(victim.Inventory);
        victim.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData { Guid = 4000, Entry = WornShortsword, Durability = 20 })]);
        var kills = new List<(Unit? Killer, Unit Victim)>();
        victim.Map!.Combat.UnitKilled += (killer, dead) => kills.Add((killer, dead));
        mine.Clear();

        uint dealt = EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, 5000);

        Assert.Equal(5000u, dealt);
        Assert.Equal(0u, victim.Health);
        Assert.False(victim.IsAlive);
        Assert.Equal((victim, victim), Assert.Single(kills)); // self kill: killer == victim, no kill hook for the party
        // vmangos Unit::Kill: the tap of a self kill is the victim itself, so SetPvPDeath(pPlayerTap != nullptr) is true (Unit.cpp:1180).
        Assert.True(victim.Combat.PvpDeath);
        Assert.Equal(18u, victim.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability); // max(1, int(20 x 0.10)) = 2 lost

        WorldOpcode[] order = [.. mine.Sent.Select(p => p.Opcode).Where(o => o is WorldOpcode.SmsgEnvironmentaldamagelog or WorldOpcode.SmsgDurabilityDamageDeath)];
        Assert.Equal([WorldOpcode.SmsgEnvironmentaldamagelog, WorldOpcode.SmsgDurabilityDamageDeath], order);
        Assert.Empty(mine.Sent.Single(p => p.Opcode == WorldOpcode.SmsgDurabilityDamageDeath).Payload);
    }

    [Fact]
    public void Immunity_EndsItWithoutAPacket_PerSchool()
    {
        (WorldRuntime world, Player victim, FakeSession mine, _) = Setup();
        var mitigation = new FakeMitigation();
        LocomotionEnvironment.RegisterMitigation(world, mitigation);

        mitigation.Immune.Add(EnvironmentalSchool.Fire);
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, 100));
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Fire, 100));
        Assert.DoesNotContain(mine.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog);
        Assert.Equal(1000u, victim.Health);

        Assert.Equal(100u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Fall, 100)); // physical immunity is a different school

        mitigation.Immune.Clear();
        mitigation.Immune.Add(EnvironmentalSchool.Physical);
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Exhausted, 100));
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Drowning, 100));
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Fall, 100));
        Assert.Equal(900u, victim.Health);

        mitigation.Immune.Clear();
        mitigation.Immune.Add(EnvironmentalSchool.Nature);
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Slime, 100));
    }

    [Fact]
    public void FireAndSlimeAreReduced_ButFallDrowningAndFatigueAreNeverAbsorbed()
    {
        (WorldRuntime world, Player victim, FakeSession mine, _) = Setup();
        var mitigation = new FakeMitigation { Result = new EnvironmentalMitigation(Absorb: 10, Resist: 5) };
        LocomotionEnvironment.RegisterMitigation(world, mitigation);

        // vmangos: damage 100 - (absorb 10 + resist 5) = 85, both shown in the log.
        Assert.Equal(85u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, 100));
        Assert.Equal(ExpectedLog(1, 3, 85, 10, 5), mine.Sent.Last(p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Payload);
        Assert.Equal(85u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Slime, 100));
        Assert.Equal([EnvironmentalSchool.Fire, EnvironmentalSchool.Nature], mitigation.Calculated);

        mitigation.Calculated.Clear();
        Assert.Equal(100u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Fall, 100));
        Assert.Equal(100u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Drowning, 100));
        Assert.Equal(100u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Exhausted, 100));
        Assert.Empty(mitigation.Calculated);
    }

    [Fact]
    public void ANegativeResist_AddsToTheDamage_AndAbsorbingEverythingDealsNothing()
    {
        (WorldRuntime world, Player victim, FakeSession mine, _) = Setup();
        var mitigation = new FakeMitigation { Result = new EnvironmentalMitigation(Absorb: 0, Resist: -20) };
        LocomotionEnvironment.RegisterMitigation(world, mitigation);

        Assert.Equal(120u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, 100)); // Player.cpp:752 "bonus"

        mitigation.Result = new EnvironmentalMitigation(Absorb: 500, Resist: 0);
        mine.Clear();
        Assert.Equal(0u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, 100));
        Assert.Equal(880u, victim.Health);
        Assert.Equal(ExpectedLog(1, 3, 0, 500, 0), Assert.Single(mine.Sent, p => p.Opcode == WorldOpcode.SmsgEnvironmentaldamagelog).Payload);
    }

    [Fact]
    public void WithoutAMitigation_NothingIsReduced()
    {
        (WorldRuntime world, Player victim, _, _) = Setup();
        Assert.Same(NoEnvironmentalMitigation.Instance, LocomotionEnvironment.MitigationFor(world));
        Assert.Equal(100u, EnvironmentalDamage.Apply(world, victim, EnvironmentalDamageType.Lava, 100));
    }
}

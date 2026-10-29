using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// The spell system's answers for environmental and fall damage: vmangos Player::EnvironmentalDamage (Player.cpp:713-758) with
/// IsImmuneToDamage (Unit.cpp:5404-5439) and CalculateDamageAbsorbAndResist (Unit.cpp:1920-2202), and the fall's
/// MOD_DAMAGE_PERCENT_TAKEN multiplier (Player.cpp:20854-20855).
/// </summary>
public sealed class SpellEnvironmentalMitigationTests
{
    private const uint FireImmunity = 961_001;
    private const uint HarmfulFireImmunity = 961_002;
    private const uint PhysicalImmunity = 961_003;
    private const uint FireDamageImmunity = 961_004;
    private const uint FireWard = 961_005;
    private const uint NatureWard = 961_006;
    private const uint PhysicalTakenMinus50 = 961_007;
    private const uint PhysicalTakenPlus20 = 961_008;
    private const uint FireTakenMinus50 = 961_009;

    private static readonly int Fire = (int)SpellSchoolMasks.Of(SpellSchool.Fire);
    private static readonly int Nature = (int)SpellSchoolMasks.Of(SpellSchool.Nature);
    private static readonly int Physical = (int)SpellSchoolMasks.Of(SpellSchool.Normal);

    private static SpellTestKit Kit() => new(
        RuleTestKit.Grant(FireImmunity, AuraType.SchoolImmunity, 0, Fire),
        RuleTestKit.Grant(HarmfulFireImmunity, AuraType.SchoolImmunity, 0, Fire) with { Attributes = SpellAttributes.AuraIsDebuff },
        RuleTestKit.Grant(PhysicalImmunity, AuraType.SchoolImmunity, 0, Physical),
        RuleTestKit.Grant(FireDamageImmunity, AuraType.DamageImmunity, 0, Fire),
        RuleTestKit.Grant(FireWard, AuraType.SchoolAbsorb, 30, Fire),
        RuleTestKit.Grant(NatureWard, AuraType.SchoolAbsorb, 30, Nature),
        RuleTestKit.Grant(PhysicalTakenMinus50, AuraType.ModDamagePercentTaken, -50, Physical),
        RuleTestKit.Grant(PhysicalTakenPlus20, AuraType.ModDamagePercentTaken, 20, Physical),
        RuleTestKit.Grant(FireTakenMinus50, AuraType.ModDamagePercentTaken, -50, Fire));

    /// <summary>A level 60 player with 1000 health in a world whose environmental damage uses the kit's spell system and the 1.12 rules.</summary>
    private static (Player Player, FakeSession Session, SpellEnvironmentalMitigation Mitigation) Setup(SpellTestKit kit)
    {
        kit.System.CombatRules = new VanillaSpellCombatRules();
        var mitigation = new SpellEnvironmentalMitigation(kit.System);
        LocomotionEnvironment.RegisterMitigation(kit.World, mitigation);
        LocomotionEnvironment.RegisterFallModifiers(kit.World, mitigation);
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.Level = 60;
        player.MaxHealth = 1000;
        player.Health = 1000;
        return (player, session, mitigation);
    }

    private static void Resistance(Player player, SpellSchool school, int value) => player.SetInt32(UpdateFields.UnitFieldResistances + (int)school, value);

    private static byte[] ExpectedLog(ulong guid, EnvironmentalDamageType type, uint damage, uint absorb, int resist)
        => EnvironmentalDamage.BuildLog(guid, type, damage, absorb, resist);

    private static List<byte[]> Logs(FakeSession session) => SpellTestKit.Packets(session, WorldOpcode.SmsgEnvironmentaldamagelog);

    [Fact]
    public void Immunity_IsTheDamageImmunityOfTheSchool_AndEndsTheDamageWithoutALog()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session, SpellEnvironmentalMitigation mitigation) = Setup(kit);

        RuleTestKit.Apply(kit, player, FireImmunity);
        Assert.Equal((true, false, false), (mitigation.IsImmune(player, EnvironmentalSchool.Fire), mitigation.IsImmune(player, EnvironmentalSchool.Nature),
            mitigation.IsImmune(player, EnvironmentalSchool.Physical)));
        Assert.Equal(0u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Lava, 100));
        Assert.Empty(Logs(session));

        kit.System.RemoveAuras(player, FireImmunity);
        RuleTestKit.Apply(kit, player, FireDamageImmunity); // SPELL_AURA_DAMAGE_IMMUNITY, whatever its polarity (Unit.cpp:5409-5415)
        Assert.True(mitigation.IsImmune(player, EnvironmentalSchool.Fire));

        RuleTestKit.Apply(kit, player, PhysicalImmunity);
        Assert.Equal(0u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Exhausted, 100));
        Assert.Empty(Logs(session));
        Assert.Equal(1000u, player.Health);
    }

    [Fact]
    public void AHarmfulSchoolImmunity_DoesNotStopTheLava_ButAbsorbsAllOfIt()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session, SpellEnvironmentalMitigation mitigation) = Setup(kit);
        Resistance(player, SpellSchool.Fire, 400);
        RuleTestKit.Apply(kit, player, HarmfulFireImmunity);

        // Not immune to harmless damage (Unit.cpp:5430), but IsImmuneToSchoolMask absorbs everything before the resist (:1929-1933).
        Assert.False(mitigation.IsImmune(player, EnvironmentalSchool.Fire));
        Assert.Equal(new EnvironmentalMitigation(100, 0), mitigation.Calculate(player, EnvironmentalSchool.Fire, 100));
        Assert.Equal(0u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Lava, 100));
        Assert.Equal(ExpectedLog(player.Guid.Value, EnvironmentalDamageType.Lava, 0, 100, 0), Assert.Single(Logs(session)));
    }

    [Fact]
    public void FireResistance_IsResistedWithThePlayerAsItsOwnCaster()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session, SpellEnvironmentalMitigation mitigation) = Setup(kit);

        // 400 x 0.15 / 60 = 1.0, capped at a 0.75 chance: the 75% step (SpellCaster.cpp:917-922; quarter-step outcome).
        Resistance(player, SpellSchool.Fire, 400);
        Assert.Equal(25u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Fire, 100));
        Assert.Equal(ExpectedLog(player.Guid.Value, EnvironmentalDamageType.Fire, 25, 0, 75), Assert.Single(Logs(session)));

        // Nature resistance does nothing for fire; slime is nature (Player.cpp:730-736).
        Resistance(player, SpellSchool.Fire, 0);
        Resistance(player, SpellSchool.Nature, 400);
        Assert.Equal(new EnvironmentalMitigation(0, 0), mitigation.Calculate(player, EnvironmentalSchool.Fire, 100));
        Assert.Equal(new EnvironmentalMitigation(0, 75), mitigation.Calculate(player, EnvironmentalSchool.Nature, 100));
    }

    [Fact]
    public void ANegativeResistanceOnAPlayer_CountsAsZero_NoBonusDamage()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session, _) = Setup(kit);

        // A curse can take the field below 0, but a player has no negative base resistance, so the total counts as 0 (Patch 1.9.0).
        Resistance(player, SpellSchool.Fire, -150);

        Assert.Equal(100u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Lava, 100));
        Assert.Equal(900u, player.Health);
    }

    [Fact]
    public void WithoutTheResistRoll_OnlyTheAbsorbsApply()
    {
        using SpellTestKit kit = Kit();
        (Player player, _, SpellEnvironmentalMitigation mitigation) = Setup(kit);
        kit.System.CombatRules = SpellCombatRules.Neutral; // offers no ISchoolResistRoll
        Resistance(player, SpellSchool.Fire, 400);
        RuleTestKit.Apply(kit, player, FireWard);

        Assert.Equal(new EnvironmentalMitigation(30, 0), mitigation.Calculate(player, EnvironmentalSchool.Fire, 100));
    }

    [Fact]
    public void TheWard_AbsorbsWhatTheResistLeaves_AndOnlyItsOwnSchool()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session, _) = Setup(kit);
        Resistance(player, SpellSchool.Fire, 400);
        RuleTestKit.Apply(kit, player, FireWard);
        RuleTestKit.Apply(kit, player, NatureWard);

        // 75 resisted, the ward absorbs the remaining 25 of its 30 (Unit.cpp:1946-1953 before :2187).
        Assert.Equal(0u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Lava, 100));
        Assert.Equal(ExpectedLog(player.Guid.Value, EnvironmentalDamageType.Lava, 0, 25, 75), Logs(session)[^1]);
        Assert.True(kit.System.HasAura(player, FireWard));

        // The 5 left absorbs 5 of the next 25; the nature ward does not touch fire.
        Assert.Equal(20u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Lava, 100));
        Assert.False(kit.System.HasAura(player, FireWard));
        Assert.True(kit.System.HasAura(player, NatureWard));

        Resistance(player, SpellSchool.Fire, 0);
        Assert.Equal(70u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Slime, 100));
        Assert.False(kit.System.HasAura(player, NatureWard));
        Assert.Equal(1000u - 20u - 70u, player.Health);
    }

    [Fact]
    public void FallDrowningAndFatigue_AreNeverAbsorbed()
    {
        using SpellTestKit kit = Kit();
        (Player player, _, _) = Setup(kit);
        SpellInfo physicalWard = RuleTestKit.Grant(961_010, AuraType.SchoolAbsorb, 30, Physical);
        kit.System.Store = new SpellStore([.. kit.Store.All, physicalWard], [], []);
        RuleTestKit.Apply(kit, player, physicalWard.Id);

        Assert.Equal(100u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Drowning, 100)); // Player.cpp:743-750
        Assert.True(kit.System.HasAura(player, physicalWard.Id));
    }

    [Fact]
    public void TheFallMultiplier_IsTheProductOfThePhysicalDamageTakenPercentAuras()
    {
        using SpellTestKit kit = Kit();
        (Player player, _, SpellEnvironmentalMitigation mitigation) = Setup(kit);
        Assert.Equal(1.0f, mitigation.TakenDamageMultiplier(player));

        RuleTestKit.Apply(kit, player, PhysicalTakenMinus50);
        RuleTestKit.Apply(kit, player, PhysicalTakenPlus20);
        RuleTestKit.Apply(kit, player, FireTakenMinus50); // another school: not counted (misc mask NORMAL, Unit.cpp:3068-3072)

        Assert.Equal(0.6f, mitigation.TakenDamageMultiplier(player)); // (100 - 50) / 100 x (100 + 20) / 100
    }

    // --- through the fall observer ---------------------------------------------------------

    private static void Send(SpellTestKit kit, Player player, WorldOpcode opcode, MovementFlags flags, float z, uint fallTime = 0)
    {
        MovementInfo previous = player.Movement;
        var incoming = new MovementInfo { Flags = flags, Time = 1, X = player.X, Y = player.Y, Z = z, FallTime = fallTime };
        var context = new MovementObserverContext(player, kit.World, opcode);
        MovementObservers.Before(context, in previous, ref incoming);
        player.ApplyClientMovement(incoming, 1);
        MovementObservers.After(context, in previous);
    }

    private static void Fall(SpellTestKit kit, Player player, float startZ, float endZ)
    {
        Send(kit, player, WorldOpcode.MsgMoveJump, MovementFlags.Jumping, startZ);
        Send(kit, player, WorldOpcode.MsgMoveHeartbeat, MovementFlags.Jumping | MovementFlags.FallingFar, (startZ + endZ) / 2);
        Send(kit, player, WorldOpcode.MsgMoveFallLand, MovementFlags.None, endZ, 3000);
    }

    [Fact]
    public void AFall_IsHalvedByAPhysicalDamageTakenAura_AndStoppedByPhysicalImmunity()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session, _) = Setup(kit);
        player.MaxHealth = 3000;
        player.Health = 3000;
        RuleTestKit.Apply(kit, player, PhysicalTakenMinus50);

        Fall(kit, player, 100, 60); // 40 yards: 0.4774 of 3000 = 1432, halved

        Assert.Equal(716u, BitConverter.ToUInt32(Assert.Single(Logs(session)), 9));
        Assert.Equal(3000u - 716u, player.Health);

        RuleTestKit.Apply(kit, player, PhysicalImmunity);
        Fall(kit, player, 100, 60);
        Assert.Single(Logs(session));
        Assert.Equal(3000u - 716u, player.Health);
    }
}

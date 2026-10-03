using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// Crowd-control aura state: unit flags derived from live auras, cast interruption, stand-up and loot release on stun,
/// root, and the logout interplay (vmangos SpellAuras.cpp:3444-3463 fear/confuse, :3502-3545 disarm, :3548-3640 stun,
/// :3859-3890 silence, :5625-5640 pacify; Unit.cpp:9112-9174 ModConfuseSpell).
/// </summary>
public sealed class CcStateTests
{
    private const uint Silence = 920_001;
    private const uint SilenceTwo = 920_002;
    private const uint Pacify = 920_003;
    private const uint PacifySilence = 920_004;
    private const uint Disarm = 920_005;
    private const uint Fear = 920_006;
    private const uint Confuse = 920_007;
    private const uint Stun = 920_008;
    private const uint StunTwo = 920_009;
    private const uint Root = 920_010;
    private const uint NoFlee = 920_011;
    private const uint SilenceableCast = 920_020;
    private const uint PlainCast = 920_021;
    private const uint HostileSilence = 920_030;
    private const uint HostileStun = 920_031;
    private const uint HostileFear = 920_032;
    private const uint HostilePacify = 920_033;

    private static SpellInfo Hostile(uint id, AuraType aura) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, aura))
        with { Duration = new SpellDuration(20_000, 0, 20_000), SpellVisual = 1, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellInfo Cast(uint id, uint prevention) =>
        RuleTestSupport.Magic(id, SpellSchool.Fire) with
        {
            CastTime = new SpellCastTime(3000, 0, 0),
            PreventionType = prevention,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        RuleTestSupport.Grant(Silence, AuraType.ModSilence, 0),
        RuleTestSupport.Grant(SilenceTwo, AuraType.ModSilence, 0),
        RuleTestSupport.Grant(Pacify, AuraType.ModPacify, 0),
        RuleTestSupport.Grant(PacifySilence, AuraType.ModPacifySilence, 0),
        RuleTestSupport.Grant(Disarm, AuraType.ModDisarm, 0),
        RuleTestSupport.Grant(Fear, AuraType.ModFear, 0),
        RuleTestSupport.Grant(Confuse, AuraType.ModConfuse, 0),
        RuleTestSupport.Grant(Stun, AuraType.ModStun, 0),
        RuleTestSupport.Grant(StunTwo, AuraType.ModStun, 0),
        RuleTestSupport.Grant(Root, AuraType.ModRoot, 0),
        RuleTestSupport.Grant(NoFlee, AuraType.PreventsFleeing, 0),
        Cast(SilenceableCast, 1),
        Cast(PlainCast, 0),
        Hostile(HostileSilence, AuraType.ModSilence),
        Hostile(HostileStun, AuraType.ModStun),
        Hostile(HostileFear, AuraType.ModFear),
        Hostile(HostilePacify, AuraType.ModPacify));

    private static (Player Caster, Player Victim) Pair(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(victim, SilenceableCast, PlainCast);
        return (caster, victim);
    }

    [Fact]
    public void Silence_SetsTheFlag_UntilTheLastSilenceAuraIsGone()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);

        RuleTestSupport.Apply(kit, victim, Silence);
        RuleTestSupport.Apply(kit, victim, SilenceTwo);
        Assert.True((victim.UnitFlags & UnitFlags.Silenced) != 0);
        kit.System.RemoveAuras(victim, Silence);
        Assert.True((victim.UnitFlags & UnitFlags.Silenced) != 0);
        kit.System.RemoveAuras(victim, SilenceTwo);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Silenced);
    }

    [Fact]
    public void Pacify_SetsAndClearsTheFlag_AndPacifySilenceDrivesBoth()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);

        RuleTestSupport.Apply(kit, victim, Pacify);
        Assert.True((victim.UnitFlags & UnitFlags.Pacified) != 0);
        kit.System.RemoveAuras(victim, Pacify);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Pacified);

        RuleTestSupport.Apply(kit, victim, PacifySilence);
        Assert.Equal(UnitFlags.Pacified | UnitFlags.Silenced, victim.UnitFlags & (UnitFlags.Pacified | UnitFlags.Silenced));
        RuleTestSupport.Apply(kit, victim, Silence);
        kit.System.RemoveAuras(victim, Silence);
        Assert.True((victim.UnitFlags & UnitFlags.Silenced) != 0); // the pacify+silence aura still holds the silence
        kit.System.RemoveAuras(victim, PacifySilence);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & (UnitFlags.Pacified | UnitFlags.Silenced));
    }

    [Fact]
    public void Disarm_SetsAndClearsTheDisarmedFlag()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);

        RuleTestSupport.Apply(kit, victim, Disarm);
        Assert.True((victim.UnitFlags & UnitFlags.Disarmed) != 0);
        kit.System.RemoveAuras(victim, Disarm);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Disarmed);
    }

    [Fact]
    public void FearAndConfuse_SetTheirFlags_FearIsBlockedByPreventsFleeing()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);

        RuleTestSupport.Apply(kit, victim, Fear);
        RuleTestSupport.Apply(kit, victim, Confuse);
        Assert.Equal(UnitFlags.Fleeing | UnitFlags.Confused, victim.UnitFlags & (UnitFlags.Fleeing | UnitFlags.Confused));
        kit.System.RemoveAuras(victim, Fear);
        Assert.Equal(UnitFlags.Confused, victim.UnitFlags & (UnitFlags.Fleeing | UnitFlags.Confused));
        kit.System.RemoveAuras(victim, Confuse);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & (UnitFlags.Fleeing | UnitFlags.Confused));

        RuleTestSupport.Apply(kit, victim, NoFlee);
        RuleTestSupport.Apply(kit, victim, Fear);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Fleeing); // vmangos Unit::SetFeared: PREVENTS_FLEEING wins
    }

    [Fact]
    public void Totems_IgnoreFearAndConfuse()
    {
        using SpellTestKit kit = Kit();
        CreatureTemplate template = CreatureTestSupport.Template(CreatureTestSupport.WolfEntry, b => b.Rank = 0) with { CreatureType = 11 };
        var totem = new Creature(1, template, null, CreatureTestSupport.Content([template], []), new Random(1));

        RuleTestSupport.Apply(kit, totem, Fear);
        RuleTestSupport.Apply(kit, totem, Confuse);

        Assert.Equal(UnitFlags.None, totem.UnitFlags & (UnitFlags.Fleeing | UnitFlags.Confused));
    }

    [Fact]
    public void Stun_SetsTheFlagAndRoots_UntilTheLastStunIsGone()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);

        RuleTestSupport.Apply(kit, victim, Stun);
        RuleTestSupport.Apply(kit, victim, StunTwo);
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0);
        Assert.True(victim.IsRooted);
        Assert.True(kit.System.IsRooted(victim));
        kit.System.RemoveAuras(victim, Stun);
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0);
        Assert.True(victim.IsRooted);
        kit.System.RemoveAuras(victim, StunTwo);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned);
        Assert.False(victim.IsRooted);
        Assert.False(kit.System.IsRooted(victim));
    }

    [Fact]
    public void Stun_DoesNotApplyToAPlayerOnATaxi()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);
        victim.UnitFlags |= UnitFlags.TaxiFlight;

        RuleTestSupport.Apply(kit, victim, Stun);

        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned);
        Assert.False(victim.IsRooted);
    }

    [Fact]
    public void Root_RootsWithoutStunning_AndStaysWhileAStunHoldsIt()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);

        RuleTestSupport.Apply(kit, victim, Root);
        Assert.True(victim.IsRooted);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned);
        RuleTestSupport.Apply(kit, victim, Stun);
        kit.System.RemoveAuras(victim, Root);
        Assert.True(victim.IsRooted); // the stun still roots
        kit.System.RemoveAuras(victim, Stun);
        Assert.False(victim.IsRooted);
    }

    [Fact]
    public void Stun_StandsAPlayerUp_ReleasesLoot_ButNotWhenMounted()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);
        var released = new List<Player>();
        kit.System.ReleaseLoot = released.Add;
        victim.SetStandState(StandState.Sit);

        RuleTestSupport.Apply(kit, victim, Stun);

        Assert.Equal(StandState.Stand, victim.StandState);
        Assert.Equal([victim], released);

        kit.System.RemoveAuras(victim, Stun);
        victim.SetStandState(StandState.Sit);
        victim.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 1234);
        RuleTestSupport.Apply(kit, victim, Stun);
        Assert.Equal(StandState.Sit, victim.StandState); // vmangos: only when not mounted
    }

    [Fact]
    public void LogoutStun_IsASeparateSource_FromTheStunAura()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);

        victim.BeginLogout(kit.Now);
        RuleTestSupport.Apply(kit, victim, Stun);
        kit.System.RemoveAuras(victim, Stun);
        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0); // the logout still holds it
        Assert.True(victim.IsRooted);

        victim.CancelLogout();
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned);
        Assert.False(victim.IsRooted);
    }

    [Fact]
    public void CancelLogout_KeepsAStunAndARootHeldByAuras()
    {
        using SpellTestKit kit = Kit();
        (_, Player victim) = Pair(kit);
        RuleTestSupport.Apply(kit, victim, Stun);
        victim.BeginLogout(kit.Now);

        victim.CancelLogout();

        Assert.True((victim.UnitFlags & UnitFlags.Stunned) != 0);
        Assert.True(victim.IsRooted);
        kit.System.RemoveAuras(victim, Stun);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned);
        Assert.False(victim.IsRooted);
    }

    [Fact]
    public void Silence_InterruptsOnlyCastsWithTheSilencePreventionType()
    {
        using SpellTestKit kit = Kit();
        (Player other, Player victim) = Pair(kit);
        kit.System.HandleCastRequest(victim, SilenceableCast, SpellCastTargets.ForUnit(other.Guid));
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
        RuleTestSupport.Apply(kit, victim, Silence);
        Assert.Null(kit.System.GetState(victim.Guid)!.CurrentCast);

        kit.System.RemoveAuras(victim, Silence);
        kit.System.HandleCastRequest(victim, PlainCast, SpellCastTargets.ForUnit(other.Guid));
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
        RuleTestSupport.Apply(kit, victim, Silence);
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast); // PreventionType 0 is not silenced
    }

    [Fact]
    public void Pacify_InterruptsNothing()
    {
        using SpellTestKit kit = Kit();
        (Player other, Player victim) = Pair(kit);
        kit.System.HandleCastRequest(victim, SilenceableCast, SpellCastTargets.ForUnit(other.Guid));

        RuleTestSupport.Apply(kit, victim, Pacify);

        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
    }

    [Theory]
    [InlineData(HostileStun)]
    [InlineData(HostileFear)]
    public void StunAndFear_FromAnotherUnit_InterruptAnyCast(uint spell)
    {
        using SpellTestKit kit = Kit();
        (Player other, Player victim) = Pair(kit);
        kit.System.HandleCastRequest(victim, PlainCast, SpellCastTargets.ForUnit(other.Guid));
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);

        kit.System.CastSpell(other, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.Null(kit.System.GetState(victim.Guid)!.CurrentCast);
    }

    [Fact]
    public void ASelfAppliedStun_DoesNotInterruptTheCast()
    {
        using SpellTestKit kit = Kit();
        (Player other, Player victim) = Pair(kit);
        kit.System.HandleCastRequest(victim, PlainCast, SpellCastTargets.ForUnit(other.Guid));

        RuleTestSupport.Apply(kit, victim, Stun); // caster == target (SpellAuras.cpp:3573-3576)

        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
    }

    [Fact]
    public void FlagsFromAnotherCaster_ClearWhenTheHolderExpires()
    {
        using SpellTestKit kit = Kit();
        (Player other, Player victim) = Pair(kit);

        kit.System.CastSpell(other, HostileSilence, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        kit.System.CastSpell(other, HostilePacify, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.Equal(UnitFlags.Silenced | UnitFlags.Pacified, victim.UnitFlags & (UnitFlags.Silenced | UnitFlags.Pacified));

        kit.Advance(21_000);

        Assert.Equal(UnitFlags.None, victim.UnitFlags & (UnitFlags.Silenced | UnitFlags.Pacified));
    }
}

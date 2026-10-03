using System.Buffers.Binary;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Retail pushback, interrupt and school lockout (vmangos Spell::Delayed / DelayedChannel Spell.cpp:7465-7545, Unit.cpp:900-947, EffectInterruptCast SpellEffects.cpp:3560-3600, LockOutSpells SpellCaster.cpp:2527-2534).</summary>
public sealed class PushbackInterruptTests
{
    private const uint Bolt = 970_001;
    private const uint Channel = 970_002;
    private const uint ChannelActionCancel = 970_003;
    private const uint InstantFire = 970_004;
    private const uint NoPrevention = 970_005;
    private const uint FrostSilenceable = 970_006;
    private const uint Kick = 970_010;
    private const uint ResistAll = 970_020;
    private const uint ResistPartial = 970_021;

    private static SpellInfo Base(uint id, int castMs, uint prevention, SpellInterruptFlags interrupt, SpellSchool school = SpellSchool.Fire) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy))
        with
        {
            School = school,
            DamageClass = SpellDamageClass.Magic,
            CastTime = new SpellCastTime(castMs, 0, 0),
            PreventionType = prevention,
            InterruptFlags = interrupt,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellInfo Channeled(uint id, SpellAuraInterruptFlags channelFlags) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
        with
        {
            AttributesEx = SpellAttributesEx.IsChanneled,
            Duration = new SpellDuration(8000, 0, 8000),
            SpellVisual = 1,
            School = SpellSchool.Fire,
            PreventionType = 1,
            ChannelInterruptFlags = channelFlags,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        Base(Bolt, 3000, 1, SpellInterruptFlags.DamagePushback),
        Channeled(Channel, (SpellAuraInterruptFlags)SpellChannelInterruptFlags.Delay),
        Channeled(ChannelActionCancel, SpellAuraInterruptFlags.Action),
        Base(InstantFire, 0, 1, SpellInterruptFlags.DamagePushback),
        Base(NoPrevention, 3000, 0, SpellInterruptFlags.DamagePushback),
        Base(FrostSilenceable, 3000, 1, SpellInterruptFlags.DamagePushback, SpellSchool.Frost),
        SpellTestKit.Spell(Kick, SpellTestKit.Effect(SpellEffectName.InterruptCast, 0, SpellImplicitTarget.UnitEnemy))
            with { Duration = new SpellDuration(4000, 0, 4000), School = SpellSchool.Normal, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        RuleTestSupport.Grant(ResistAll, AuraType.ResistPushback, 100),
        RuleTestSupport.Grant(ResistPartial, AuraType.ResistPushback, 35));

    private static List<byte[]> Sent(FakeSession session, WorldOpcode opcode) =>
        [.. session.Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    private static uint LastUInt(byte[] payload) => BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(payload.Length - 4));

    private static (Player Attacker, Player Caster, FakeSession Session, SpellCast Cast) Casting(SpellTestKit kit, uint spell)
    {
        (Player attacker, _) = kit.AddPlayer(1);
        (Player caster, FakeSession session) = kit.AddPlayer(2, 2);
        kit.Spellbook.Teach(caster, spell);
        kit.System.HandleCastRequest(caster, spell, spell is Channel or ChannelActionCancel ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(attacker.Guid));
        SpellCast cast = kit.System.GetState(caster.Guid)!.CurrentCast!;
        session.Clear();
        return (attacker, caster, session, cast);
    }

    [Fact]
    public void ConsecutiveHits_DelayByTheDecayingTable_1000_800_600_400_200_200()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, FakeSession session, SpellCast cast) = Casting(kit, Bolt);

        for (int hit = 0; hit < 6; hit++)
        {
            cast.Timer = 1; // far from the cap so each delay is the table value
            kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        }

        Assert.Equal([1000u, 800u, 600u, 400u, 200u, 200u], Sent(session, WorldOpcode.SmsgSpellDelayed).Select(LastUInt));
    }

    [Fact]
    public void TheDelayIsCappedToTheRemainingBar()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, FakeSession session, SpellCast cast) = Casting(kit, Bolt);
        cast.Timer = 2500;

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);

        Assert.Equal(3000, cast.Timer);
        Assert.Equal([500u], Sent(session, WorldOpcode.SmsgSpellDelayed).Select(LastUInt));
    }

    [Fact]
    public void ThePushbackPacket_GoesToTheCasterOnly()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, _, SpellCast cast) = Casting(kit, Bolt);
        (_, FakeSession bystander) = kit.AddPlayer(3, 3);
        bystander.Clear();
        cast.Timer = 1;

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);

        Assert.Empty(Sent(bystander, WorldOpcode.SmsgSpellDelayed));
    }

    [Fact]
    public void ACreatureCaster_IsNeverPushedBackOrInterruptedByDamage()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        Creature creature = FoundationTests.MakeCreature(0, 30);
        SpellInfo bolt = kit.Store.Get(Bolt)! with { InterruptFlags = SpellInterruptFlags.DamagePushback | SpellInterruptFlags.DamageCancels };
        var cast = new SpellCast(bolt, creature, SpellCastTargets.ForUnit(attacker.Guid), triggered: false, 3000, 0, 0);
        cast.Timer = 1000;
        kit.System.StateOf(creature).CurrentCast = cast;

        kit.System.OnDamageTaken(creature, attacker, 50, periodic: false);

        Assert.Equal(1000, cast.Timer);
        Assert.NotEqual(SpellCastState.Finished, cast.State);
    }

    [Fact]
    public void PeriodicDamage_NeverPushesBack_AndZeroDamageNeitherDoes()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, FakeSession session, SpellCast cast) = Casting(kit, Bolt);
        cast.Timer = 1;

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: true);
        kit.System.OnDamageTaken(caster, attacker, 0, periodic: false);

        Assert.Equal(1, cast.Timer);
        Assert.Empty(Sent(session, WorldOpcode.SmsgSpellDelayed));
    }

    [Fact]
    public void ResistPushback_100_NeverDelays_AndAFractionResistsOnlyThatOften()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, FakeSession session, SpellCast cast) = Casting(kit, Bolt);
        RuleTestSupport.Apply(kit, caster, ResistAll);
        cast.Timer = 1;
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        Assert.Equal(1, cast.Timer);
        Assert.Empty(Sent(session, WorldOpcode.SmsgSpellDelayed));

        kit.System.RemoveAuras(caster, ResistAll);
        RuleTestSupport.Apply(kit, caster, ResistPartial);
        kit.System.Random = new ScriptedRandom(34, 35);
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false); // roll 34 < 35: resisted
        Assert.Equal(1, cast.Timer);
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false); // roll 35: delayed, and the first delay is still 1000
        Assert.Equal(1001, cast.Timer);
    }

    [Fact]
    public void TheTalentNotLoseCastingTimeModifier_FeedsTheResistChance()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, _, SpellCast cast) = Casting(kit, Bolt);
        kit.System.SpellModifiers = new MagicHitChanceTests.AddModifier(SpellModOp.NotLoseCastingTime, 100f); // 100 + 100 - 100 = 100%
        cast.Timer = 1;

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);

        Assert.Equal(1, cast.Timer);
    }

    [Fact]
    public void AChannelWithTheDelayFlag_LosesTheSameDecayingDelay_AndOnlyDirectDamageDelaysIt()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, FakeSession session, SpellCast cast) = Casting(kit, Channel);
        Assert.Equal(8000, cast.Timer);

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: true);
        Assert.Equal(8000, cast.Timer);
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);
        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);

        Assert.Equal(6200, cast.Timer); // 8000 - 1000 - 800
        Assert.Equal(6200, kit.System.GetAuras(caster).Single(h => h.Spell.Id == Channel).Duration);
        Assert.Equal(2, Sent(session, WorldOpcode.MsgChannelUpdate).Count);
    }

    [Fact]
    public void AChannelDelayedToNothing_Stops()
    {
        using SpellTestKit kit = Kit();
        (Player attacker, Player caster, _, SpellCast cast) = Casting(kit, Channel);
        cast.Timer = 600;

        kit.System.OnDamageTaken(caster, attacker, 5, periodic: false);

        Assert.Null(kit.System.GetState(caster.Guid)!.CurrentCast);
    }

    // --- interrupt and lockout ---------------------------------------------------------------------

    [Fact]
    public void Kick_InterruptsACastWithAnInterruptableBar_AndLocksTheSchoolOnce()
    {
        using SpellTestKit kit = Kit();
        (Player kicker, Player victim, _, _) = Casting(kit, Bolt);

        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.Null(kit.System.GetState(victim.Guid)!.CurrentCast);
        Assert.True(kit.System.IsSchoolLocked(victim, SpellSchool.Fire));

        kit.Advance(2000);
        kit.System.Spellbook!.LearnSpell(victim, Bolt);
        kit.System.CastSpell(victim, Bolt, SpellCastTargets.ForUnit(kicker.Guid), triggered: true); // triggered: ignores the lockout, no CurrentCast
        kit.System.StateOf(victim).CurrentCast = new SpellCast(kit.Store.Get(Bolt)!, victim, SpellCastTargets.ForUnit(kicker.Guid), false, 3000, 0, 0);
        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true); // a second kick must not extend the lockout
        kit.Advance(2100);
        Assert.False(kit.System.IsSchoolLocked(victim, SpellSchool.Fire)); // 4 s from the first kick
    }

    [Fact]
    public void Kick_DoesNothingToInstantSpellsOrSpellsWithoutSilencePrevention()
    {
        using SpellTestKit kit = Kit();
        (Player kicker, Player victim, _, _) = Casting(kit, NoPrevention);
        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
        Assert.False(kit.System.IsSchoolLocked(victim, SpellSchool.Fire));

        // An instant spell (cast time 0) occupying the slot is not interrupted either.
        SpellInfo instant = kit.Store.Get(InstantFire)!;
        kit.System.StateOf(victim).CurrentCast = new SpellCast(instant, victim, SpellCastTargets.ForUnit(kicker.Guid), false, 0, 0, 0);
        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast);
    }

    [Fact]
    public void Kick_NeedsThePushbackFlagForACast_AndTheActionFlagForAChannel()
    {
        using SpellTestKit kit = Kit();
        (Player kicker, Player victim, _, _) = Casting(kit, Bolt);
        SpellInfo noPushback = kit.Store.Get(Bolt)! with { InterruptFlags = SpellInterruptFlags.None };
        kit.System.StateOf(victim).CurrentCast = new SpellCast(noPushback, victim, SpellCastTargets.ForUnit(kicker.Guid), false, 3000, 0, 0);
        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast); // not interruptible

        kit.System.Spellbook!.LearnSpell(victim, Channel);
        kit.System.StateOf(victim).CurrentCast = null;
        kit.System.HandleCastRequest(victim, Channel, SpellCastTargets.ForSelf());
        Assert.Equal(SpellCastState.Casting, kit.System.GetState(victim.Guid)!.CurrentCast!.State);
        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.NotNull(kit.System.GetState(victim.Guid)!.CurrentCast); // delay-flag channel: no action-cancels flag

        kit.System.Spellbook.LearnSpell(victim, ChannelActionCancel);
        kit.System.HandleCastRequest(victim, ChannelActionCancel, SpellCastTargets.ForSelf());
        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);
        Assert.Null(kit.System.GetState(victim.Guid)!.CurrentCast);
    }

    [Fact]
    public void TheLockout_BlocksOnlySilencePreventionSpellsOfTheSchool()
    {
        using SpellTestKit kit = Kit();
        (Player kicker, Player victim, _, _) = Casting(kit, Bolt);
        kit.Spellbook.Teach(victim, NoPrevention, FrostSilenceable, InstantFire);
        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleCastRequest(victim, Bolt, SpellCastTargets.ForUnit(kicker.Guid)));
        Assert.Equal(SpellCastResult.NotReady, kit.System.HandleCastRequest(victim, InstantFire, SpellCastTargets.ForUnit(kicker.Guid)));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(victim, NoPrevention, SpellCastTargets.ForUnit(kicker.Guid))); // PreventionType 0
        kit.System.CancelCast(victim, 0);
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(victim, FrostSilenceable, SpellCastTargets.ForUnit(kicker.Guid))); // other school
    }

    [Fact]
    public void ThePlayer_GetsACooldownForEveryKnownSpellOfTheLockedSchool()
    {
        using SpellTestKit kit = Kit();
        (Player kicker, Player victim, FakeSession session, _) = Casting(kit, Bolt);
        kit.Spellbook.Teach(victim, NoPrevention, FrostSilenceable, InstantFire);
        session.Clear();

        kit.System.CastSpell(kicker, Kick, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

        byte[] packet = Assert.Single(Sent(session, WorldOpcode.SmsgSpellCooldown));
        var entries = new List<(uint Spell, uint Ms)>();
        for (int offset = 8; offset + 8 <= packet.Length; offset += 8)
        {
            entries.Add((BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset)), BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(offset + 4))));
        }

        Assert.Equal([(Bolt, 4000u), (InstantFire, 4000u), (NoPrevention, 4000u)], entries.OrderBy(e => e.Spell)); // every fire spell, not the frost one
    }

    private sealed class SilenceImmune : ICreatureImmunityProvider
    {
        public uint MechanicImmuneMask(Unit unit) => SpellMechanics.Mask(SpellMechanic.Silence);

        public uint SchoolImmuneMask(Unit unit) => 0;
    }

    [Fact]
    public void ACreatureImmuneToSilence_IgnoresTheLockout()
    {
        using SpellTestKit kit = Kit();
        (Player kicker, _) = kit.AddPlayer(1);
        Creature creature = FoundationTests.MakeCreature(0, 30);
        kit.System.CreatureImmunities = new SilenceImmune();
        SpellInfo bolt = kit.Store.Get(Bolt)!;
        var cast = new SpellCast(bolt, creature, SpellCastTargets.ForUnit(kicker.Guid), false, 3000, 0, 0);
        kit.System.StateOf(creature).CurrentCast = cast;

        kit.System.LockOut(creature, SpellSchoolMasks.Of(SpellSchool.Fire), 4000, bolt);

        Assert.False(kit.System.IsSchoolLocked(creature, SpellSchool.Fire));
    }
}

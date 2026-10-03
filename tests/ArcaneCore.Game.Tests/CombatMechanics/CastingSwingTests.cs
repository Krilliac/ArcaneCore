using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// Ranged lane S03: the swing timer while casting and the melee timer reset of a cast. Retail (vmangos
/// Unit::UpdateMeleeAttackingState, Unit.cpp:415-421) returns before it touches any timer while a non-melee spell is
/// cast, so the swing happens as soon as the cast ends; and Spell::cast (Spell.cpp:3805-3810) resets the main-hand
/// (and off-hand) timers after a non-triggered spell whose interrupt flags carry SPELL_INTERRUPT_FLAG_COMBAT (0x08)
/// unless Ex2 0x20000 DO_NOT_RESET_COMBAT_TIMERS. The spell shapes copy classic-db spell_template (Throw 2764:
/// InterruptFlags 15, cat 76; Arcane Shot 3044: Ex2 0x20000).
/// The clock is manual: <see cref="Rig.Step"/> advances the spell system and the world tick together.
/// </summary>
public sealed class CastingSwingTests
{
    private const uint ResetSpell = 930101;      // instant, InterruptFlags 15 (the Throw shape without the weapon checks)
    private const uint NoResetSpell = 930102;    // same with Ex2 0x20000 (the Arcane Shot shape)
    private const uint CombatFreeSpell = 930103; // instant, no combat interrupt bit

    private static SpellInfo Instant(uint id, SpellInterruptFlags flags, uint ex2 = 0) => Spell(id, Effect(SpellEffectName.Heal, 1)) with
    {
        InterruptFlags = flags,
        AttributesEx2 = (SpellAttributesEx2)ex2,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private sealed class Rig : IDisposable
    {
        public Rig(CombatOptions? options = null)
        {
            Kit = new SpellTestKit(
                Instant(ResetSpell, (SpellInterruptFlags)15),
                Instant(NoResetSpell, (SpellInterruptFlags)15, 0x20000),
                Instant(CombatFreeSpell, SpellInterruptFlags.Movement));
            (Caster, Session) = Kit.AddPlayer(1);
            (Target, _) = Kit.AddPlayer(2, 3, 0);
            Target.Health = 5000;
            Kit.Spellbook.Teach(Caster, CastBolt, ChannelSpell, ResetSpell, NoResetSpell, CombatFreeSpell);
            Caster.SetUInt32(UpdateFields.UnitFieldMaxpower1 + 1, 1000);
            SpellSystem.SetPower(Caster, PowerType.Rage, 100); // CastBolt costs 50 rage in the kit
            CombatEnvironment.Register(Kit.World, new CombatEnvironment(options ?? new CombatOptions(), null, new SpellSystemMeleeHooks(Kit.System)));
            Kit.World.RunTick(0);
            Session.Clear();
        }

        public SpellTestKit Kit { get; }

        public Player Caster { get; }

        public FakeSession Session { get; }

        public Player Target { get; }

        public MapCombat Combat => Caster.Map!.Combat;

        public int WhiteSwings => Session.Sent.Count(p => p.Opcode == WorldOpcode.SmsgAttackerstateupdate);

        public uint MainHandTimer => Caster.Combat.GetAttackTimer(WeaponAttackType.BaseAttack);

        /// <summary>Advance the spell clock and the world tick by <paramref name="ms"/> in steps of <paramref name="step"/>.</summary>
        public void Step(uint ms, uint step)
        {
            for (uint done = 0; done < ms; done += step)
            {
                uint diff = Math.Min(step, ms - done);
                Kit.Now += diff;
                Kit.System.Update(diff);
                Kit.World.RunTick(diff);
            }
        }

        public void Dispose() => Kit.Dispose();
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(50u)]
    public void Retail_ACastDoesNotConsumeTheSwing_ItFiresOnceTheCastEnds(uint step)
    {
        using var rig = new Rig();
        rig.Kit.System.HandleCastRequest(rig.Caster, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid)); // 2000 ms on the cast bar
        rig.Combat.Attack(rig.Caster, rig.Target);

        rig.Step(1500, step);

        Assert.Equal(0, rig.WhiteSwings);
        Assert.Equal(0u, rig.MainHandTimer); // still ready: nothing was consumed

        rig.Step(700, step); // the bolt ends at 2000 ms

        Assert.True(rig.WhiteSwings >= 1, "the swing must fire within a tick or two after the cast ended");
        Assert.Equal(1, rig.WhiteSwings);
    }

    [Theory]
    [InlineData(100u)]
    [InlineData(50u)]
    public void Retail_AChannelDoesNotConsumeTheSwingEither(uint step)
    {
        using var rig = new Rig();
        rig.Kit.System.HandleCastRequest(rig.Caster, ChannelSpell, SpellCastTargets.ForSelf()); // 5000 ms channel
        rig.Combat.Attack(rig.Caster, rig.Target);

        rig.Step(4000, step);

        Assert.Equal(0, rig.WhiteSwings);
        Assert.Equal(0u, rig.MainHandTimer);

        rig.Step(1500, step);
        Assert.Equal(1, rig.WhiteSwings);
    }

    [Fact]
    public void LegacyConsumeSwing_KeepsTheOldBehaviour_TheSwingIsLostAndTheTimerRestarts()
    {
        using var rig = new Rig(new CombatOptions { CastingConsumesSwing = true });
        rig.Kit.System.HandleCastRequest(rig.Caster, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid));
        rig.Combat.Attack(rig.Caster, rig.Target);

        rig.Step(100, 50);

        Assert.Equal(0, rig.WhiteSwings);
        Assert.True(rig.MainHandTimer > 0u, "the legacy path consumes the swing timer while casting");
    }

    [Fact]
    public void MeleeCastingBlocksSwingOff_StillLetsTheSwingThrough()
    {
        using var rig = new Rig(new CombatOptions { MeleeCastingBlocksSwing = false });
        rig.Kit.System.HandleCastRequest(rig.Caster, CastBolt, SpellCastTargets.ForUnit(rig.Target.Guid));
        rig.Combat.Attack(rig.Caster, rig.Target);

        rig.Step(100, 50);

        Assert.Equal(1, rig.WhiteSwings);
    }

    // --- cast resets the melee timer ------------------------------------------------------------

    private static uint CastAndReadTimer(Rig rig, uint spell, bool triggered)
    {
        rig.Caster.Combat.SetAttackTimer(WeaponAttackType.BaseAttack, 500);
        rig.Kit.System.CastSpell(rig.Caster, spell, SpellCastTargets.ForSelf(), triggered);
        return rig.MainHandTimer;
    }

    [Fact]
    public void ACombatFlaggedCast_ResetsTheMainHandTimerToTheFullDelay()
    {
        using var rig = new Rig();
        rig.Caster.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2400);

        Assert.Equal(2400u, CastAndReadTimer(rig, ResetSpell, triggered: false));
    }

    [Fact]
    public void TheResetSeesTheHastedField_NotTheBase()
    {
        using var rig = new Rig();
        rig.Caster.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2400);
        rig.Caster.Combat.ApplyAttackTimePercentMod(WeaponAttackType.BaseAttack, 20f, apply: true);

        uint timer = CastAndReadTimer(rig, ResetSpell, triggered: false);

        Assert.InRange(timer, 1999u, 2000u); // 2400 / 1.2
    }

    [Fact]
    public void NoResetWhen_TriggeredEx2DoNotResetOrNoCombatInterruptBit()
    {
        using var rig = new Rig();
        rig.Caster.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2400);

        Assert.Equal(500u, CastAndReadTimer(rig, ResetSpell, triggered: true));
        Assert.Equal(500u, CastAndReadTimer(rig, NoResetSpell, triggered: false));
        Assert.Equal(500u, CastAndReadTimer(rig, CombatFreeSpell, triggered: false));
    }

    [Fact]
    public void CastResetsMeleeSwingOff_DisablesTheReset()
    {
        using var rig = new Rig(new CombatOptions { CastResetsMeleeSwing = false });
        rig.Caster.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2400);

        Assert.Equal(500u, CastAndReadTimer(rig, ResetSpell, triggered: false));
    }

    [Fact]
    public void TheRangedTimerIsNeverResetByACast()
    {
        using var rig = new Rig();
        rig.Caster.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2800);
        rig.Caster.Combat.SetAttackTimer(WeaponAttackType.RangedAttack, 700);

        rig.Kit.System.CastSpell(rig.Caster, ResetSpell, SpellCastTargets.ForSelf(), triggered: false);

        Assert.Equal(700u, rig.Caster.Combat.GetAttackTimer(WeaponAttackType.RangedAttack)); // the ranged variant is commented out in vmangos (Spell.cpp:4371)
    }
}

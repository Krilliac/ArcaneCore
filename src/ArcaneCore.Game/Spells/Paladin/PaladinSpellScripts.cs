using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>
/// Judgement (20271, SCRIPT_EFFECT; vmangos <c>Spell::EffectScriptEffect</c>, SpellEffects.cpp:4502-4529): the caster's seal (a seal whose third
/// effect is the dummy aura naming its judgement: "all seals have judgement's aura dummy spell id in 2 effect") is cancelled and the judgement it
/// names (that effect's simple value, base points plus base dice) is cast at the living target, triggered. The spell itself needs
/// AURA_STATE_JUDGEMENT on the caster, which a seal sets (<see cref="PaladinAuraRules"/>).
/// </summary>
[SpellScript(PaladinSpells.Judgement)]
public sealed class JudgementScript : ISpellScript
{
    /// <summary>The seal effect that names the judgement.</summary>
    public const int JudgementEffectIndex = 2;

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Effect.Effect != SpellEffectName.ScriptEffect || !context.Target.IsAlive)
        {
            return;
        }

        SpellSystem system = context.System;
        Unit caster = context.Caster;
        uint judgement = 0;
        foreach (SpellAuraHolder holder in system.GetAuras(caster).ToArray())
        {
            if (holder.IsRemoved || !PaladinSpells.IsSeal(holder.Spell) || holder.Auras[JudgementEffectIndex] is not { Type: AuraType.Dummy })
            {
                continue;
            }

            // "must be calculated base at raw base points in spell proto, GetModifier()->m_value for S.Righteousness modified by SPELLMOD_DAMAGE"
            int named = (int)holder.Spell.SimpleValue(JudgementEffectIndex);
            if (named <= 1)
            {
                continue;
            }

            judgement = (uint)named;
            system.RemoveAuras(caster, holder.Spell.Id, AuraRemoveMode.Cancel);
            break;
        }

        system.CastSpell(caster, judgement, SpellCastTargets.ForUnit(context.Target.Guid), triggered: true);
    }
}

/// <summary>
/// Judgement of Command, the dummy (20425, 20961, 20962, 20967, 20968; vmangos scripts/spells/spell_paladin.cpp:56-75): effect 0's base points
/// name the damage rank, which the caster casts at the target, triggered.
/// </summary>
[SpellScript(20425, 20961, 20962, 20967, 20968)]
public sealed class JudgementOfCommandDummyScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Effect.Effect != SpellEffectName.Dummy)
        {
            return;
        }

        // spell->m_currentBasePoints[0]: the simple value of the effect.
        uint damageSpell = (uint)Math.Max(0, (int)context.Spell.SimpleValue(0));
        if (context.System.Store.Get(damageSpell) is not null)
        {
            context.System.CastSpell(context.Caster, damageSpell, SpellCastTargets.ForUnit(context.Target.Guid), triggered: true);
        }
    }
}

/// <summary>
/// Judgement of Command, the damage (20467, 20963, 20964, 20965, 20966; vmangos spell_paladin.cpp:37-54): effect 0's damage is halved unless the
/// target is stunned, then the done and taken spell damage bonuses are applied although the spell is a melee one (the script calls
/// SpellDamageBonusDone and SpellDamageBonusTaken itself). A value modifier: the effect value is what vmangos' script leaves in <c>spell->damage</c>.
/// </summary>
public sealed class JudgementOfCommandDamage : ISpellValueModifier
{
    public static readonly uint[] Ranks = [20467, 20963, 20964, 20965, 20966];

    private readonly SpellSystem _spells;

    public JudgementOfCommandDamage(SpellSystem spells) => _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
    {
        if (kind != SpellValueKind.EffectValue || context.EffectIndex != 0 || context.Target is not { } target
            || Array.IndexOf(Ranks, context.Spell.Id) < 0)
        {
            return value;
        }

        // "base damage halved if target not stunned" (UNIT_STATE_STUNNED | UNIT_STATE_PENDING_STUNNED).
        float damage = (target.UnitFlags & UnitFlags.Stunned) != 0 ? value : (int)(value * 0.5f);
        if (_spells.AmountModifier is { } bonus)
        {
            // SpellDamageBonusDone / SpellDamageBonusTaken route a melee class spell to the weapon formulas; the script wants the spell ones.
            damage = bonus.Modify(SpellAmountStage.DirectDamage, context.Caster, target, context.Spell with { DamageClass = SpellDamageClass.Magic }, 0, damage, 1);
        }

        return (int)damage;
    }
}

/// <summary>
/// Holy Shock (20473, 20929, 20930; vmangos spell_paladin.cpp:77-125): a hostile target must be in front of the caster (1.4 yards or closer
/// needs no facing) or the cast fails UNIT_NOT_INFRONT; the dummy casts the rank's heal on a friendly target and its damage on any other, triggered.
/// </summary>
[SpellScript(20473, 20929, 20930)]
public sealed class HolyShockScript : ISpellScript
{
    /// <summary>Rank → (damage, heal) spell (spell_paladin.cpp:100-115).</summary>
    public static readonly IReadOnlyDictionary<uint, (uint Hurt, uint Heal)> Spells = new Dictionary<uint, (uint, uint)>
    {
        [20473] = (25912, 25914),
        [20929] = (25911, 25913),
        [20930] = (25902, 25903),
    };

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Target is { } target && !ReferenceEquals(target, context.Caster) && !context.System.Relations.IsFriendly(context.Caster, target)
            && !IsFacing(context.Caster, target))
        {
            return SpellCastResult.UnitNotInfront;
        }

        return SpellCastResult.CastOk;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || !Spells.TryGetValue(context.Spell.Id, out (uint Hurt, uint Heal) ids))
        {
            return;
        }

        uint spell = context.System.Relations.IsFriendly(context.Caster, context.Target) ? ids.Heal : ids.Hurt;
        context.System.CastSpell(context.Caster, spell, SpellCastTargets.ForUnit(context.Target.Guid), triggered: true);
    }

    /// <summary>vmangos WorldObject::IsFacingTarget (Object.cpp:1986-1989): within 1.4 yards in 2D, or in the front half circle.</summary>
    private static bool IsFacing(Unit caster, Unit target)
    {
        float dx = target.X - caster.X;
        float dy = target.Y - caster.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy)) < CombatConstants.NoFacingChecksDistance || MapCombat.HasInArc(caster, target, MathF.PI);
    }
}

/// <summary>
/// The paladin bubbles (Divine Shield 642, 1020; Divine Protection 498, 5573; Blessing of Protection 1022, 5599, 10278; vmangos
/// spell_paladin.cpp:210-225, <c>OnAfterHit</c>): after the spell hit a target, the caster puts Forbearance (25771) on it, triggered. Forbearance
/// is MECHANIC_IMMUNITY to INVULNERABILITY (25), the mechanic of every bubble, so the target cannot be bubbled again while it lasts
/// (<see cref="PositiveSpellImmunityCheck"/> refuses the cast).
/// </summary>
public sealed class ForbearanceObserver : ISpellCastObserver
{
    public const uint Forbearance = 25771;

    public static readonly uint[] Bubbles = [642, 1020, 498, 5573, 1022, 5599, 10278];

    private readonly SpellSystem _spells;

    public ForbearanceObserver(SpellSystem spells) => _spells = spells ?? throw new ArgumentNullException(nameof(spells));

    public void OnTargetOutcome(SpellCast cast, SpellTargetOutcome outcome)
    {
        if (outcome.Miss == SpellMissInfo.None && Array.IndexOf(Bubbles, cast.Spell.Id) >= 0 && _spells.Store.Get(Forbearance) is not null)
        {
            _spells.CastSpell(cast.Caster, Forbearance, SpellCastTargets.ForUnit(outcome.Target.Guid), triggered: true);
        }
    }
}

/// <summary>
/// vmangos Spell::CheckCast (Spell.cpp:5634-5636): a positive spell whose target is immune to it fails TARGET_AURASTATE (a bubble on a unit with
/// Forbearance; a buff on a unit immune to its school). The target is the explicit unit target, or the caster for a self spell.
/// </summary>
public sealed class PositiveSpellImmunityCheck : ISpellCastCheck
{
    /// <summary>Final, not Target: the target phase runs only for spells that need a unit target, and a self spell's target is its caster.</summary>
    public SpellCheckPhase Phase => SpellCheckPhase.Final;

    public int Order => SpellCastCheckOrder.TargetAuraState;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        Unit target = context.Target ?? context.Caster;
        return context.Spell.IsPositive && Rules.Immunity.ImmunityRules.IsImmuneToSpell(context.System, target, context.Spell, ReferenceEquals(target, context.Caster))
            ? SpellCastResult.TargetAurastate
            : SpellCastResult.CastOk;
    }
}

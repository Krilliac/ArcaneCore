using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_APPLY_AREA_AURA_FRIEND (128) and SPELL_EFFECT_APPLY_AREA_AURA_ENEMY (129): vmangos routes both to <c>Spell::EffectApplyAreaAura</c>
/// (SpellEffects.cpp:188-189, body :2323-2332), which puts an <c>AreaAura</c> on the unit target with the caster as source; <c>AreaAura::Update</c>
/// (SpellAuras.cpp:592-840) then hands the aura to the other units around the caster. The 1.12 Spell.dbc has one user of these, the test spell
/// Sam's Test Aura (26659, effect 129 with a periodic damage aura at the caster); 128 has none.
/// <list type="bullet">
/// <item>FRIEND (<c>AnyFriendlyUnitInObjectRangeCheck</c>, GridNotifiers.h:808-820): every alive unit within the effect radius that the caster is friendly to,
/// the caster included.</item>
/// <item>ENEMY (<c>AnyAoETargetUnitInObjectRangeCheck</c>, GridNotifiers.h:944-967): every unit within the radius the caster may attack, and never the
/// caster (the constructor turns the caster's own aura into aura NONE, SpellAuras.cpp:441-445, so it has no effect on it; the caster still holds that
/// holder, which does the spreading). A unit that already holds an aura of the spell at the effect index, whoever cast it, gets no second one
/// (SpellAuras.cpp:757-784).</item>
/// </list>
/// <para>
/// LIMITS: the units around the caster are decided once, when the effect runs; vmangos re-evaluates them on every update (a unit that walks into the
/// radius later gets the aura, one that leaves, dies or turns hostile loses it). Each unit's copy then runs its own duration. The target selection
/// is the port's friendly and attackable relations (<see cref="SpellSystem.Relations"/>), without vmangos's <c>CanSeeInWorld</c> and
/// <c>IsImmuneToAoe</c> filters, and the lower-rank selection of <c>SelectAuraRankForLevel</c> is not applied (it only changes party and explicit-target buffs).
/// </para>
/// </summary>
public sealed class AreaAuraFriendEnemyEffects : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.ApplyAreaAuraFriend, static context => context.System.EffectApplyAreaAuraRange(context, enemy: false));
        system.RegisterEffect(SpellEffectName.ApplyAreaAuraEnemy, static context => context.System.EffectApplyAreaAuraRange(context, enemy: true));
    }
}

public sealed partial class SpellSystem
{
    internal void EffectApplyAreaAuraRange(SpellEffectContext context, bool enemy)
    {
        // vmangos EffectApplyAreaAura (SpellEffects.cpp:2323-2332): the aura goes on any live unit target. Only the caster's own aura then looks around
        // for the others (AreaAura::Update, SpellAuras.cpp:592-600); an aura on another target is a plain aura of the spell (its upkeep, dropping off
        // when the caster dies or leaves the radius, is not modelled).
        Unit caster = context.Caster;
        Unit? target = context.Target;
        if (target is null || !target.IsAlive)
        {
            return;
        }

        if (!ReferenceEquals(target, caster))
        {
            EffectApplyAura(context);
            return;
        }

        SpellEffectName kind = enemy ? SpellEffectName.ApplyAreaAuraEnemy : SpellEffectName.ApplyAreaAuraFriend;
        if (!enemy)
        {
            EffectApplyAura(context); // the caster is in range of itself
        }

        // One spread per cast, from the first effect of this kind, so a unit gets a single holder with all of the spell's effects of the kind.
        IReadOnlyList<SpellEffectInfo> effects = context.Spell.Effects;
        int first = -1;
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i].Effect == kind)
            {
                first = i;
                break;
            }
        }

        if (first != context.EffectIndex || caster.Map is not { } map || !caster.IsAlive)
        {
            return;
        }

        int duration = context.Cast.State == SpellCastState.Casting ? context.Cast.Timer : context.Cast.Duration;
        if (enemy)
        {
            // The caster's own ENEMY aura exists to spread and does nothing to the caster: aura type NONE (AreaAura::AreaAura, SpellAuras.cpp:441-445).
            AddAreaHolder(context, kind, caster, caster, duration, selfNone: true);
        }

        float radius = ModFloat(caster, context.Spell, SpellModOp.Radius, context.Effect.Radius); // vmangos AreaAura::AreaAura: SPELLMOD_RADIUS
        if (radius <= 0)
        {
            return;
        }

        var objects = new List<WorldObject>();
        map.Grids.CollectObjects(caster.X, caster.Y, radius, objects);
        var seen = new HashSet<Unit>(ReferenceEqualityComparer.Instance) { caster };
        foreach (WorldObject obj in objects)
        {
            if (obj is not Unit unit || !unit.IsInWorld || !ReferenceEquals(unit.Map, map) || !seen.Add(unit) || !unit.IsAlive)
            {
                continue;
            }

            float dx = unit.X - caster.X;
            float dy = unit.Y - caster.Y;
            float dz = unit.Z - caster.Z;
            float reach = radius + unit.BoundingRadius;
            if ((dx * dx) + (dy * dy) + (dz * dz) > reach * reach
                || !(enemy ? Relations.IsHostile(caster, unit) : Relations.IsFriendly(caster, unit)))
            {
                continue;
            }

            if ((context.Spell.AttributesEx3 & AreaAuraOnlyOnPlayer) != 0 && unit is not Player)
            {
                continue; // vmangos AreaAura::Update (SpellAuras.cpp:785-786)
            }

            // Area auras do not stack in the generic case (SpellAuras.cpp:757-784): any holder of the spell id with an aura at this effect index, whoever
            // cast it, keeps the unit from getting another.
            if (GetAuras(unit).Any(h => !h.IsRemoved && h.Spell.Id == context.Spell.Id && h.Auras.OfType<SpellAura>().Any(a => a.EffectIndex == first)))
            {
                continue;
            }

            AddAreaHolder(context, kind, unit, caster, duration, selfNone: false);
        }
    }

    private void AddAreaHolder(SpellEffectContext context, SpellEffectName kind, Unit unit, Unit caster, int duration, bool selfNone)
    {
        IReadOnlyList<SpellEffectInfo> effects = context.Spell.Effects;
        var holder = new SpellAuraHolder(context.Spell, unit, caster.Guid, caster.Level,
            _auraCasterOwners.GetValue(caster, static c => new AuraCasterOwner(c)), duration);
        for (int i = 0; i < effects.Count; i++)
        {
            SpellEffectInfo effect = effects[i];
            if (effect.Effect != kind || effect.AuraType == AuraType.None)
            {
                continue;
            }

            if (selfNone)
            {
                holder.SetAura(new SpellAura(i, AuraType.None, 0, 0, effect.MiscValue, unit.PowerType));
                continue;
            }

            if (!AuraHandlers.ContainsKey(effect.AuraType))
            {
                ReportUnsupported(context.Cast, i, "aura", (uint)effect.AuraType);
            }

            var aura = new SpellAura(i, effect.AuraType, SnapshotAuraAmount(new SpellEffectContext(this, context.Cast, unit, i, context.Value)),
                ModifiedAmplitude(caster, context.Spell, effect), effect.MiscValue, unit.PowerType);
            aura.PeriodicTimer = PeriodicTiming.InitialTimer(context.Spell, aura);
            holder.SetAura(aura);
        }

        if (!holder.IsEmpty)
        {
            AddAuraHolder(holder);
        }
    }
}

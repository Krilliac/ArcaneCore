using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>A script for the periodic trigger-spell aura of one spell (vmangos Aura::TriggerSpell, a switch on the aura's spell id).</summary>
public delegate void PeriodicTriggerScript(SpellSystem system, SpellAuraHolder holder, SpellAura aura);

public sealed partial class SpellSystem
{
    private readonly Dictionary<uint, PeriodicTriggerScript> _periodicTriggerScripts = [];
    private CustomValueScope? _customValues;
    private bool _customValueModifierInstalled;

    /// <summary>
    /// vmangos SpellCaster::CastCustomSpell (SpellCaster.cpp:2279-2330) for an instant, server-side cast: the spell is cast
    /// triggered with explicit base points for effects 0 to 2 (null keeps the spell's own). The explicit value replaces the
    /// effect's base points like <c>m_currentBasePoints</c> does in CalculateSpellEffectValue (SpellCaster.cpp:1147-1215):
    /// with a die of 0 or 1 sides it is the final value, otherwise the die is still rolled on top. Level and spell
    /// modifier terms stay. The override lives only for the synchronous cast (instant, which every triggered cast is);
    /// nested casts of other spells are not affected. <paramref name="castItem"/> is vmangos' <c>castItem</c> argument: the cast runs with it
    /// as its <see cref="SpellCast.CastItem"/>, as an item-triggered cast (no item-use check, none of its charges spent).
    /// </summary>
    public SpellCastResult CastCustomSpell(Unit caster, uint spellId, SpellCastTargets targets, int? basePoints0, int? basePoints1 = null, int? basePoints2 = null,
        Items.Item? castItem = null)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(targets);
        SpellInfo? spell = Store.Get(spellId);
        if (spell is null)
        {
            return SpellCastResult.NotFound;
        }

        if (!_customValueModifierInstalled)
        {
            // First in the chain: vmangos applies the spell mods after the base points (SpellCaster.cpp:1181-1196).
            _valueModifiers = [new CustomValueModifier(this), .. _valueModifiers];
            _customValueModifierInstalled = true;
        }

        CustomValueScope? outer = _customValues;
        _customValues = new CustomValueScope(caster, spell, [basePoints0, basePoints1, basePoints2]);
        try
        {
            // A script's cast with an item is triggered from it, not a use of it (no item-use check, no charge).
            return Prepare(caster, spell, targets, triggered: true, castItem: castItem, itemTriggeredCast: castItem is not null);
        }
        finally
        {
            _customValues = outer;
        }
    }

    /// <summary>
    /// Whether the active <see cref="CastCustomSpell"/> of <paramref name="spell"/> by <paramref name="caster"/> gives every effect in
    /// <paramref name="effectMask"/> an explicit value that is final (die of 0 or 1 sides) and not above 0, so the effect does nothing by design (vmangos
    /// CastCustomSpell: Frenzied Regeneration's tick heals <c>rage * amount / 10</c>, which is 0 at 0 rage). False outside such a cast, for another caster
    /// (the scope is matched like <see cref="CustomValueModifier"/> does), for an effect the cast leaves at the spell's own value, and for an empty mask.
    /// It evaluates only the spell's base and custom values (<see cref="SpellMath.CalculateEffectValue"/>), not the full value-modifier chain (flat and percent
    /// spell mods, auras), so a mod could still lift a non-positive value above 0: it is conservative only for spells that no such mod touches. Read by the
    /// playerbot QA monitor from <see cref="ISpellCastObserver.OnTargetOutcome"/>, which runs inside the synchronous cast (<see cref="Prepare"/> inside the
    /// try of <see cref="CastCustomSpell"/>), so the scope is still set. Thread: the scope is one plain field of the system, like the one
    /// <see cref="CustomValueModifier"/> reads; it is consistent only while casts do not overlap. The code-default stage gates serialize the Packets and
    /// Heartbeat stages and unmarked updaters (MapStageGates), but this was not verified for every cast path, and <c>World:Parallel:Ungated</c> can lift it.
    /// </summary>
    public bool CustomValueIsNonPositive(Unit caster, SpellInfo spell, int effectMask)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spell);
        if (effectMask == 0 || _customValues is not { } scope || !ReferenceEquals(scope.Caster, caster) || scope.Spell.Id != spell.Id)
        {
            return false;
        }

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            if ((effectMask & (1 << i)) == 0)
            {
                continue;
            }

            SpellEffectInfo effect = spell.Effects[i];
            if (i >= 3 || scope.BasePoints[i] is not { } explicitBasePoints || effect.DieSides is < 0 or > 1 || effect.DicePerLevel != 0)
            {
                return false;
            }

            SpellEffectInfo replaced = effect with { BasePoints = explicitBasePoints - effect.BaseDice };
            if (SpellMath.CalculateEffectValue(spell, replaced, CasterLevelOf(scope.Caster), Random) > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Add the script of the periodic trigger-spell aura of <paramref name="spellId"/>. The script replaces what the aura
    /// tick would cast (vmangos Aura::TriggerSpell switches on the aura's spell id); a second script for the same spell is a
    /// startup error.
    /// </summary>
    public void RegisterPeriodicTriggerScript(uint spellId, PeriodicTriggerScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!_periodicTriggerScripts.TryAdd(spellId, script))
        {
            throw new InvalidOperationException($"spell {spellId} already has a periodic trigger script");
        }
    }

    private sealed record CustomValueScope(Unit Caster, SpellInfo Spell, int?[] BasePoints);

    /// <summary>Replaces the base points of the effect values of the active <see cref="CastCustomSpell"/> (pure: reads, never consumes).</summary>
    private sealed class CustomValueModifier(SpellSystem system) : ISpellValueModifier
    {
        public int Modify(SpellValueKind kind, in SpellValueContext context, int value)
        {
            if (kind != SpellValueKind.EffectValue || system._customValues is not { } scope
                || !ReferenceEquals(scope.Caster, context.Caster) || scope.Spell.Id != context.Spell.Id
                || context.EffectIndex is < 0 or >= 3 || scope.BasePoints[context.EffectIndex] is not { } explicitBasePoints)
            {
                return value;
            }

            SpellEffectInfo effect = context.Spell.Effects[context.EffectIndex];
            SpellEffectInfo replaced = effect with { BasePoints = explicitBasePoints - effect.BaseDice };
            return SpellMath.CalculateEffectValue(context.Spell, replaced, system.CasterLevelOf(context.Caster), system.Random);
        }
    }
}

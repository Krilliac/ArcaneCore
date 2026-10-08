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

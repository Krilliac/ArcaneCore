namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private readonly Dictionary<(uint Spell, int EffectIndex), float> _areaRadiusOverrides = [];

    /// <summary>
    /// Override one spell effect's radius when selecting units for a target map. This is the fixed-radius
    /// part of vmangos SpellScript::OnSetTargetMap; the caster's SPELLMOD_RADIUS still applies afterward.
    /// An effect has one owner, so duplicate registrations fail at startup.
    /// </summary>
    public void RegisterSpellAreaRadiusOverride(uint spell, int effectIndex, float radius)
    {
        if (spell == 0) throw new ArgumentOutOfRangeException(nameof(spell));
        if (effectIndex is < 0 or >= SpellConstants.MaxEffects) throw new ArgumentOutOfRangeException(nameof(effectIndex));
        if (!float.IsFinite(radius) || radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));

        if (!_areaRadiusOverrides.TryAdd((spell, effectIndex), radius))
        {
            throw new InvalidOperationException($"Spell {spell} effect {effectIndex} already has an area radius override.");
        }
    }

    private readonly Dictionary<uint, uint> _maxTargetOverrides = [];

    /// <summary>
    /// Override a spell's MaxAffectedTargets (the <c>unMaxTargets</c> part of vmangos SpellScript::OnSetTargetMap, for example
    /// EmeraldDragonsDreamFogScript: Dream Fog 24781 hits one target). A spell has one owner, so a duplicate registration fails at startup.
    /// </summary>
    public void RegisterSpellMaxTargetsOverride(uint spell, uint maxTargets)
    {
        if (spell == 0) throw new ArgumentOutOfRangeException(nameof(spell));
        if (maxTargets == 0) throw new ArgumentOutOfRangeException(nameof(maxTargets));
        if (!_maxTargetOverrides.TryAdd(spell, maxTargets))
        {
            throw new InvalidOperationException($"Spell {spell} already has a max targets override.");
        }
    }

    /// <summary>The spell's MaxAffectedTargets after any <see cref="RegisterSpellMaxTargetsOverride"/> (0 = no cap).</summary>
    internal uint MaxTargetsOf(SpellInfo spell)
        => _maxTargetOverrides.TryGetValue(spell.Id, out uint max) ? max : spell.MaxAffectedTargets;

    private float TargetMapRadius(SpellInfo spell, SpellEffectInfo effect, int effectIndex)
        => _areaRadiusOverrides.TryGetValue((spell.Id, effectIndex), out float radius) ? radius : effect.Radius;
}

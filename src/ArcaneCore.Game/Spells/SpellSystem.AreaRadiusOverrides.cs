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

    private float TargetMapRadius(SpellInfo spell, SpellEffectInfo effect, int effectIndex)
        => _areaRadiusOverrides.TryGetValue((spell.Id, effectIndex), out float radius) ? radius : effect.Radius;
}

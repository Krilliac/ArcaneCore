using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>Calculate spell threat through the caster's school-scoped modifiers exactly once.</summary>
    public float? CalculateSpellThreat(Unit caster, Unit victim, SpellInfo spell, float threat, bool helpful = false)
        => SpellThreat.Calculate(this, caster, victim, spell, threat, helpful);

    /// <summary>Apply an explicit spell threat effect through the shared school modifier path.</summary>
    public void AddSpellThreat(Unit caster, Unit victim, SpellInfo spell, float threat)
        => SpellThreat.Add(this, caster, victim, spell, threat);

    /// <summary>Apply live ModThreat school modifiers to a threat amount without creating a threat entry.</summary>
    public float ApplyTotalThreatModifier(Unit caster, uint schoolMask, float threat)
        => SpellThreat.ApplySchoolModifier(this, caster, schoolMask, threat);
}

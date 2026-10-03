using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>What an effect's cast check gets (vmangos Spell::CheckCast, the per-effect switch at Spell.cpp:5900-6100).</summary>
/// <param name="System">The spell system running the check (its <see cref="SpellSystem.Random"/> serves rolls).</param>
/// <param name="Caster">The caster.</param>
/// <param name="Spell">The spell being cast.</param>
/// <param name="EffectIndex">The effect being checked.</param>
/// <param name="Targets">The explicit target block of the cast (unit, game object, item, location).</param>
/// <param name="UnitTarget">The resolved explicit unit target, if any.</param>
/// <param name="Triggered">A triggered cast.</param>
/// <param name="Strict">
/// True for the check when the cast is prepared, false for the re-check when it lands (vmangos <c>CheckCast(strict)</c>).
/// </param>
public sealed record SpellEffectCheckContext(
    SpellSystem System,
    Unit Caster,
    SpellInfo Spell,
    int EffectIndex,
    SpellCastTargets Targets,
    Unit? UnitTarget,
    bool Triggered,
    bool Strict)
{
    public SpellEffectInfo Effect => Spell.Effects[EffectIndex];
}

/// <summary>A per-effect cast check: <see cref="SpellCastResult.CastOk"/> lets the cast go on.</summary>
public delegate SpellCastResult SpellEffectCheck(SpellEffectCheckContext context);

public sealed partial class SpellSystem
{
    private readonly Dictionary<SpellEffectName, SpellEffectCheck> _effectChecks = [];

    /// <summary>
    /// Install the cast check of an effect (seam for the areas that own an effect's rules: the skills area registers
    /// OPEN_LOCK, OPEN_LOCK_ITEM and SKINNING). The check runs for every effect of that kind a spell has, after the
    /// target rules and before the power check; the first refusal is the cast result. A later registration replaces
    /// the earlier one.
    /// </summary>
    public void RegisterEffectCheck(SpellEffectName effect, SpellEffectCheck check)
        => _effectChecks[effect] = check ?? throw new ArgumentNullException(nameof(check));

    private SpellCastResult CheckEffects(Unit caster, SpellInfo spell, SpellCastTargets targets, Unit? unitTarget, bool triggered, bool strict)
    {
        if (_effectChecks.Count == 0)
        {
            return SpellCastResult.CastOk;
        }

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo effect = spell.Effects[i];
            if (!effect.IsEmpty && _effectChecks.TryGetValue(effect.Effect, out SpellEffectCheck? check))
            {
                SpellCastResult result = check(new SpellEffectCheckContext(this, caster, spell, i, targets, unitTarget, triggered, strict));
                if (result != SpellCastResult.CastOk)
                {
                    return result;
                }
            }
        }

        return SpellCastResult.CastOk;
    }
}

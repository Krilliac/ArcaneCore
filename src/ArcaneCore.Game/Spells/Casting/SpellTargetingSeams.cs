using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Resolves corpse spell targets (<see cref="SpellCastTargetFlags.CorpseAlly"/> and
/// <see cref="SpellCastTargetFlags.CorpseEnemy"/>) from a cast's target packet on the caster's map
/// (vmangos SpellCastTargets::Update, Spell.cpp:178-193).
/// </summary>
public interface ISpellCorpseTargetResolver
{
    Corpse? ResolveCorpse(Unit caster, ObjectGuid corpseGuid);

    Corpse? ResolveCorpse(Unit caster, SpellCastTargets targets)
        => (targets.Mask & (SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.CorpseEnemy)) != 0
            && !targets.Corpse.IsEmpty
            ? ResolveCorpse(caster, targets.Corpse)
            : null;
}

/// <summary>
/// Default <see cref="ISpellCorpseTargetResolver"/> backed by the caster's active <see cref="Maps.Map"/>.
/// </summary>
public sealed class MapCorpseTargetResolver : ISpellCorpseTargetResolver
{
    public static readonly MapCorpseTargetResolver Instance = new();

    public Corpse? ResolveCorpse(Unit caster, ObjectGuid corpseGuid)
    {
        ArgumentNullException.ThrowIfNull(caster);
        return !corpseGuid.IsEmpty ? caster.Map?.FindObject(corpseGuid) as Corpse : null;
    }
}

public sealed partial class SpellSystem
{
    /// <summary>
    /// Seam for resolving explicit corpse targets (<c>TARGET_FLAG_CORPSE_ALLY</c> / <c>TARGET_FLAG_CORPSE_ENEMY</c>).
    /// </summary>
    public ISpellCorpseTargetResolver CorpseTargets { get; set; } = MapCorpseTargetResolver.Instance;

    /// <summary>
    /// Resolves a corpse target from <paramref name="targets"/> on <paramref name="caster"/>'s map when a corpse target flag is set.
    /// </summary>
    public Corpse? ResolveCorpseTarget(Unit caster, SpellCastTargets targets)
        => CorpseTargets.ResolveCorpse(caster, targets);
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Quests.Adapters;

/// <summary>The <see cref="IQuestSpellCaster"/> over the real spell system: a triggered self cast.</summary>
public sealed class SpellSystemQuestCaster(SpellSystem spells) : IQuestSpellCaster
{
    public bool CastOnSelf(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        return spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true) == SpellCastResult.CastOk;
    }
}

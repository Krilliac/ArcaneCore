using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Quests.Adapters;

/// <summary>The <see cref="IQuestSpellCaster"/> over the real spell system: a triggered self cast.</summary>
public sealed class SpellSystemQuestCaster(SpellSystem spells) : IQuestSpellCaster
{
    /// <summary>Every EffectMiscValue of a QuestComplete effect in the spell store, read once (the quest support gate caches it).</summary>
    public IReadOnlyCollection<uint> QuestsCompletedBySpells
    {
        get
        {
            // The spell store is replaced as a whole when spells are loaded or reloaded: recompute only then.
            SpellStore store = spells.Store;
            if (!ReferenceEquals(store, _store))
            {
                _completed = [.. store.All.SelectMany(spell => spell.Effects)
                    .Where(effect => effect.Effect == SpellEffectName.QuestComplete && effect.MiscValue > 0)
                    .Select(effect => (uint)effect.MiscValue)];
                _store = store;
            }

            return _completed;
        }
    }

    private SpellStore? _store;
    private HashSet<uint> _completed = [];

    public bool CastOnSelf(Player player, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(player);
        return spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true) == SpellCastResult.CastOk;
    }
}

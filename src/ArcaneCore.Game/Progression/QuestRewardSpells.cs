using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Progression;

/// <summary>
/// The reward-spell part of vmangos/core 4b3d241 Player::RewardQuest: RewSpellCast (else RewSpell)
/// is cast triggered. Spells that teach, create items or target a single (friendly) unit are cast
/// by the quest ender on the player; anything else the player casts on itself.
/// </summary>
public static class QuestRewardSpells
{
    /// <summary>The unit that casts <paramref name="spell"/> for a reward: the quest ender when it can, else the player.</summary>
    public static Unit ResolveCaster(Player player, Unit? questGiver, SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(spell);
        bool giverCasts = questGiver is { IsInWorld: true } && ReferenceEquals(questGiver.Map, player.Map)
            && (spell.HasEffect(SpellEffectName.LearnSpell) || spell.HasEffect(SpellEffectName.CreateItem)
                || spell.Effects.Any(e => e.TargetA is SpellImplicitTarget.Unit or SpellImplicitTarget.UnitFriend));
        return giverCasts ? questGiver! : player;
    }

    public static SpellCastResult Cast(SpellSystem spells, Player player, Unit? questGiver, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(spells);
        ArgumentNullException.ThrowIfNull(player);
        if (spells.Store.Get(spellId) is not { } spell)
        {
            return SpellCastResult.NotFound;
        }

        Unit caster = ResolveCaster(player, questGiver, spell);
        return ReferenceEquals(caster, player)
            ? spells.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true)
            : spells.CastSpell(caster, spellId, SpellCastTargets.ForUnit(player.Guid), triggered: true);
    }
}

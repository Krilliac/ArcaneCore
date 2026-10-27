using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Warlock;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// The Soul Shard tap condition read from the loot tap (vmangos Aura::HandleChannelDeathItem, SpellAuras.cpp:2860-2861, through
/// Creature::IsTappedBy, Creature.cpp:1615-1632, which <see cref="LootService.IsTappedBy"/> follows). <paramref name="lootOf"/> finds the loot
/// service of the victim's map. A creature with no recorded tap (a scripted kill that never called <see cref="LootService.OnCreatureDamaged"/>)
/// is untapped, as in vmangos: no shard, unless <paramref name="untappedCountsAsTapped"/> (SpellRules:SoulShardUntappedCountsAsTapped) keeps
/// the shard for it. A creature on a map without a loot service counts as tapped, as it did before any source was installed.
/// </summary>
public sealed class LootSoulShardTapSource(Func<Creature, LootService?> lootOf, bool untappedCountsAsTapped = false) : ISoulShardTapSource
{
    public bool IsTappedBy(Creature victim, Player player)
    {
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(player);
        if (victim.LootTapPlayerGuid.IsEmpty && victim.LootTapGroup is null)
        {
            return untappedCountsAsTapped;
        }

        return lootOf(victim) is not { } loot || loot.IsTappedBy(player, victim);
    }
}

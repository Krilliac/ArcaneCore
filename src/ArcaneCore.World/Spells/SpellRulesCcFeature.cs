using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Wires the crowd-control rules to the areas they reach into: a stun, fear or confuse closes the loot
/// window of a player (vmangos HandleAuraModStun / ModConfuseSpell → DoLootRelease). The feature sits in
/// <c>ArcaneCore.World.Spells</c> so it attaches after <see cref="SpellFeature"/> (features attach by ordinal full name).
/// </summary>
public sealed class SpellRulesCcFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        services.GetRequiredService<SpellFeature>().System.ReleaseLoot = ReleaseOpenLoot;
    }

    private void ReleaseOpenLoot(Player player)
    {
        LootService? loot = player.Map is { } map ? services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot : null;
        if (loot?.OpenLootOf(player) is { } bag)
        {
            loot.Release(player, bag.Source);
        }
    }
}

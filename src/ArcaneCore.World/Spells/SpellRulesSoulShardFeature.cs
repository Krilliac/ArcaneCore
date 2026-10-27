using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.World.Features;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// Installs the Soul Shard tap source (<see cref="SoulShardRules.UseTapSource"/>), so Drain Soul and Shadowburn only make a shard from a
/// creature the warlock (their pet, or their group) tapped: vmangos Aura::HandleChannelDeathItem, SpellAuras.cpp:2860-2861. The tap is read from
/// the map's loot service, found the way <see cref="SpellRulesCcFeature"/> finds it. Sits in <c>ArcaneCore.World.Spells</c> so it attaches
/// after <see cref="SpellFeature"/>.
/// </summary>
public sealed class SpellRulesSoulShardFeature(IServiceProvider services) : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        SoulShardRules.UseTapSource(services.GetRequiredService<SpellFeature>().System, new LootSoulShardTapSource(LootOf));
    }

    private LootService? LootOf(Creature creature)
        => creature.Map is { } map ? services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot : null;
}
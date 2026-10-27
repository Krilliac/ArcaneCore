using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.SpellRules;

/// <summary>
/// The world installs a Soul Shard tap source, so a shard needs a victim the warlock tapped (vmangos Aura::HandleChannelDeathItem,
/// SpellAuras.cpp:2860-2861, via Creature::IsTappedBy, Creature.cpp:1615-1632).
/// </summary>
public sealed class SoulShardTapSourceFeatureTests
{
    [Fact]
    public async Task SoulShardFeature_InstallsTheLootTapSource()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("SHARDPLAYER", "Shardplayer");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Shardplayer")!;
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            Assert.IsAssignableFrom<ISoulShardTapSource>(SoulShardRules.TapSourceOf(spells.System));
        });
    }
}
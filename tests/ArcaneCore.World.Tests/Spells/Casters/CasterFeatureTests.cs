using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Spells.Casters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells.Casters;

public sealed class CasterFeatureTests
{
    [Fact]
    public void CasterFeature_IsDiscoveredAsAWorldFeature()
        => Assert.Contains(typeof(CasterFeature), WorldFeatures.FeatureTypes);

    [Fact]
    public async Task HostedSpellSystem_HasTheCasterHandlersInstalled()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CASTERFEAT", "Casterfeat");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Casterfeat")!;
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            Assert.True(spells.System.HasAuraHandler(AuraType.ModPowerCostSchoolPct));
            Assert.True(spells.System.HasAuraHandler(AuraType.ModPowerCostSchool));
        });
    }
}

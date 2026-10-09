using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Npc;

public sealed class TaxiNetworkFeatureTests
{
    [Fact]
    public void LateSpellLoadRebuildsTaxiMaskWithoutScriptOnlyPath()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var feature = new QuestNpcFeature(services, services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<QuestNpcFeature>.Instance);
        var content = NpcContent.Empty with
        {
            TaxiNodes = [new TaxiNode { Id = 1 }, new TaxiNode { Id = 2 }, new TaxiNode { Id = 3 }],
            TaxiPaths = [new TaxiPath { Id = 10, FromNode = 1, ToNode = 2 },
                new TaxiPath { Id = 11, FromNode = 3, ToNode = 2 }],
        };
        feature.Services.ReplaceNpcs(new NpcStore(content));
        Assert.True(feature.Services.Npcs.IsNetworkNode(1));

        var spells = new SpellStore([new SpellInfo
        {
            Id = 1,
            Effects = [new SpellEffectInfo { Effect = SpellEffectName.SendTaxi, MiscValue = 10 }],
        }], [], []);
        feature.RefreshTaxiNetwork(spells);

        Assert.False(feature.Services.Npcs.IsNetworkNode(1));
        Assert.True(feature.Services.Npcs.IsNetworkNode(3));
    }
}

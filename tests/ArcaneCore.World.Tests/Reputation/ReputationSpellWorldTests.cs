using ArcaneCore.Game.Reputation;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

/// <summary>The reputation feature wires the live service into the world's spell system (reputation effect, forced reactions, gain auras).</summary>
public sealed class ReputationSpellWorldTests
{
    [Fact]
    public async Task TheSpellSystemReachesTheLiveService_AndGainAurasFeedTheGainModifier()
    {
        ReputationTestServices.Current.Value = new MemoryReputationStore();
        WorldTestHost host;
        try { host = WorldTestHost.Start(); }
        finally { ReputationTestServices.Current.Value = null; }

        await using (host)
        {
            ReputationService service = host.WorldServices.GetRequiredService<ArcaneCore.World.Reputation.ReputationFeature>().Service;
            SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
            Assert.Same(service, ReputationEnvironment.For(spells.System));
            Assert.NotNull(service.GainModifier);
            Assert.Contains(typeof(ArcaneCore.Game.Spells.ReputationSpellHandlers), spells.System.Modules);
            Assert.NotNull(service.StopAttackFaction);
            Assert.False(service.SendForcedReactions); // the wire width is unconfirmed: off by default
        }
    }
}

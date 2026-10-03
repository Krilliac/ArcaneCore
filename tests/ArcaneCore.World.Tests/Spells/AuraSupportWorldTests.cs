using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// The aura support matrix against a fully composed world (every discovered feature attached): a type is
/// <see cref="AuraSupportLevel.Handler"/> exactly when the spell system has a handler for it. A registration that no row
/// mentions, or a row that claims a handler nothing registered, fails here: the check that would otherwise stop checking.
/// </summary>
public sealed class AuraSupportWorldTests
{
    [Fact]
    public async Task EveryRowAgreesWithTheLiveHandlerRegistrations()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("MATRIX", "Matrix");

        var disagreements = new List<string>();
        int registered = 0;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Matrix")!;
            SpellFeature spells = ((WorldSession)player.Session).Services.GetRequiredService<SpellFeature>();
            foreach (AuraSupportEntry row in AuraSupport.Entries)
            {
                bool live = spells.System.HasAuraHandler(row.Type);
                registered += live ? 1 : 0;
                if (live != (row.Level == AuraSupportLevel.Handler))
                {
                    disagreements.Add($"{row.Type}: row says {row.Level}, live handler = {live}");
                }
            }
        });

        Assert.Empty(disagreements);
        Assert.True(registered >= 50, $"only {registered} handlers registered: the host did not compose the features, so this check proved nothing");
    }
}

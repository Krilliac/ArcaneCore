using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>The ghost form feature in the real daemon: registered, and harmless when the spell data has no ghost spell.</summary>
public sealed class GhostFormWorldTests
{
    [Fact]
    public async Task TheFeature_RegistersTheGhostForm_AndAReleasedSpiritIsStillAGhostWithoutTheSpellData()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GHOSTF1", "Ghostformer");
        Assert.NotNull(await host.OnWorldAsync(() => DeathSeams.Find(host.World)?.GhostForm));

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostformer")!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
        });

        await host.WaitForWorldAsync(
            () => (host.World.FindOnlinePlayer("Ghostformer")!.Flags & PlayerFlags.Ghost) != 0, "the spirit to be a ghost");
    }
}

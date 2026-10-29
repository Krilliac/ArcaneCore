using ArcaneCore.Game.Entities;
using ArcaneCore.World.Tests.PacketHeuristics;
using Xunit;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>
/// The opcode handler fuzz's player invariant checker must fail on corrupted state. The corruption is written straight into the entity's
/// fields: <c>Relocate</c> refuses non-finite coordinates, so a handler going through it cannot demonstrate the position branch.
/// </summary>
public sealed class HandlerFuzzInvariantTests
{
    [Fact]
    public async Task CheckPlayer_ReportsCorruptedPosition_AndHealth_ButNotAHealthyPlayer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, ArcaneCore.World.Net.WorldSession session) = await PacketHeuristicsTestKit.EnterClientAsync(host, "INVARIANT", "Invariant");
        await using WorldTestClient _ = client;
        Player player = session.Player!;

        List<HandlerFuzzBreak> breaks = await host.OnWorldAsync(() =>
        {
            var found = new List<HandlerFuzzBreak>();
            HandlerFuzzInventory before = HandlerFuzzInventory.Take(player);
            HandlerFuzzKit.CheckPlayer(player, before, mayChange: false, mapChecks: true, found);
            Assert.Empty(found); // a healthy player passes

            player.X = float.NaN;
            HandlerFuzzKit.CheckPlayer(player, before, false, true, found);
            player.X = 1f;
            player.Y = 20_000f;
            HandlerFuzzKit.CheckPlayer(player, before, false, true, found);
            player.Y = 1f;
            player.Health = player.MaxHealth + 5;
            HandlerFuzzKit.CheckPlayer(player, before, false, true, found);
            player.Health = player.MaxHealth;
            player.Money += 1;
            HandlerFuzzKit.CheckPlayer(player, before, false, true, found);
            return found;
        });

        string[] names = [.. breaks.Select(b => b.Name)];
        Assert.Contains("position-not-finite", names);
        Assert.Contains("position-out-of-range", names);
        Assert.Contains("health-above-max", names);
        Assert.Contains("money-changed-outside-set", names);
    }
}

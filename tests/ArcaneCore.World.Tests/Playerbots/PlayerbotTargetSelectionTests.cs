using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotTargetSelectionTests
{
    [Fact]
    public async Task IdleSelectionSkipsServiceNpcsEvenWithUnknownFactionFallback()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature? vendor = null, enemy = null;
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                vendor = new Creature(3, new CreatureTemplate { Entry = 1213, Name = "service NPC", CreatureType = 7,
                    NpcFlags = 0x4004, MinLevelHealth = 20, MaxLevelHealth = 20 }, null, CreatureContent.Empty, new Random(1));
                enemy = Creature(6, 7, 4);
                vendor.SetPosition(player.X + 1, player.Y, player.Z, 0);
                enemy.SetPosition(player.X + 8, player.Y, player.Z, 0);
                player.Map!.AddObject(vendor); player.Map.AddObject(enemy);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(vendor!.Guid)
                && session.Player.VisibleObjects.Contains(enemy!.Guid), "service and combat candidates");
            await host.World.InvokeAsync(() =>
            {
                Assert.Same(enemy, PlayerbotBrain.FindTarget(session.Player!));
                Assert.Same(vendor, PlayerbotBrain.FindTarget(session.Player!, 1213)); // Explicit objective keeps normal combat checks.
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task QuestTargetSelection_DoesNotChaseNearbyCritters_AndMissingTargetAllowsTravel()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Creature? rabbit = null, kobold = null;
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                rabbit = Creature(1213, 8, 1);
                kobold = Creature(6, 7, 2);
                rabbit.Relocate(player.X + 1, player.Y, player.Z, 0, host.World.NowMs);
                kobold.Relocate(player.X + 8, player.Y, player.Z, 0, host.World.NowMs);
                player.Map!.AddObject(rabbit); player.Map.AddObject(kobold);
                return true;
            });
            await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(rabbit!.Guid)
                && session.Player.VisibleObjects.Contains(kobold!.Guid), "bot candidate visibility");
            await host.World.InvokeAsync(() =>
            {
                var player = session.Player!;
                Assert.Same(kobold, PlayerbotBrain.FindTarget(player, 6));
                Assert.Same(kobold, PlayerbotBrain.FindTarget(player));
                Assert.Null(PlayerbotBrain.FindTarget(player, 99999));
                Assert.Same(rabbit, PlayerbotBrain.FindTarget(player, 1213)); // Explicit critter quests remain possible.
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private static Creature Creature(uint entry, uint type, uint low)
        => new(low, new CreatureTemplate { Entry = entry, Name = "candidate", CreatureType = type,
            MinLevelHealth = 20, MaxLevelHealth = 20 }, null, new CreatureContent([], [], [], [], []), new Random(1));
}

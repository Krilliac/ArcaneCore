using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>vmangos DELAYED_CAST_HONORLESS_TARGET (Player.cpp:1923-1927, 2083, 2183-2184): which teleports schedule Honorless Target.</summary>
public sealed class TeleportHonorlessTargetTests
{
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
        ],
        [], [], [], []);

    private static (WorldRuntime World, TeleportService Teleports, Player Player, List<bool> Due) Rig()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        WorldMaps.Of(world).Load(Content);
        var teleports = new TeleportService(world, _ => { }, _ => { });
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(player);
        world.RunTick(50);
        List<bool> due = [];
        teleports.HonorlessTargetDue += (_, far) => due.Add(far);
        return (world, teleports, player, due);
    }

    [Fact]
    public void NearTeleport_LeavingCombat_SchedulesIt_OnTheAck()
    {
        (WorldRuntime world, TeleportService teleports, Player player, List<bool> due) = Rig();
        using (world)
        {
            Assert.True(teleports.TeleportTo(player, 0, 500, 0, 90, 0f));
            Assert.Empty(due);
            Assert.True(teleports.HandleTeleportAck(player, player.Guid.Value));
            Assert.Equal([false], due);
        }
    }

    [Fact]
    public void NearSpellTeleport_KeepingCombat_DoesNotScheduleIt()
    {
        (WorldRuntime world, TeleportService teleports, Player player, List<bool> due) = Rig();
        using (world)
        {
            Assert.True(teleports.TeleportTo(player, 0, 500, 0, 90, 0f, TeleportOptions.NotLeaveCombat));
            Assert.True(teleports.HandleTeleportAck(player, player.Guid.Value));
            Assert.Empty(due);
        }
    }

    [Fact]
    public void FarTeleport_SchedulesIt_OnArrival()
    {
        (WorldRuntime world, TeleportService teleports, Player player, List<bool> due) = Rig();
        using (world)
        {
            Assert.True(teleports.TeleportTo(player, 1, 1000, 2000, 30, 0f));
            world.RunTick(50);
            Assert.True(teleports.HandleWorldportAck(player));
            world.RunTick(50);
            Assert.Equal(1u, player.MapId);
            Assert.Equal([true], due);
        }
    }
}

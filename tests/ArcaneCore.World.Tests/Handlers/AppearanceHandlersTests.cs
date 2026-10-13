using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>The helm and cloak toggles and the mount special animation (vmangos CharacterHandler.cpp:753-761, MovementHandler.cpp:974-979).</summary>
public sealed class AppearanceHandlersTests
{
    [Fact]
    public async Task ToggleHelmAndCloak_FlipTheirPlayerFlags()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("HELM", "Helmet");
        Player player = await host.PlayerAsync("Helmet");

        await client.SendAsync(WorldOpcode.CmsgToggleHelm, []);
        await host.WaitForWorldAsync(() => (player.Flags & PlayerFlags.HideHelm) != 0, "the helm is hidden");
        await client.SendAsync(WorldOpcode.CmsgToggleCloak, []);
        await host.WaitForWorldAsync(() => (player.Flags & PlayerFlags.HideCloak) != 0, "the cloak is hidden");
        await client.SendAsync(WorldOpcode.CmsgToggleHelm, []);
        await host.WaitForWorldAsync(() => (player.Flags & PlayerFlags.HideHelm) == 0, "the helm shows again");
        Assert.NotEqual(default, player.Flags & PlayerFlags.HideCloak);
    }

    [Fact]
    public async Task MountSpecialAnim_ReachesAnObserver_WithTheRidersGuid()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient rider = await host.EnterWorldAsync("RIDER", "Rider");
        await using WorldTestClient watcher = await host.EnterWorldAsync("WATCHR", "Watcher");
        Player player = await host.PlayerAsync("Rider");

        await rider.SendAsync(WorldOpcode.CmsgMountspecialAnim, []);
        byte[] anim = await watcher.ReadUntilAsync(WorldOpcode.SmsgMountspecialAnim);
        Assert.Equal(player.Guid.Value, BitConverter.ToUInt64(anim, 0));
    }

    [Fact]
    public async Task CinematicAndGrowthNotices_AreAcceptedWithoutEffect()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("CINE", "Cinema");
        Player player = await host.PlayerAsync("Cinema");
        PlayerFlags before = player.Flags;

        await client.SendAsync(WorldOpcode.CmsgNextCinematicCamera, []);
        await client.SendAsync(WorldOpcode.CmsgCompleteCinematic, []);
        await client.SendAsync(WorldOpcode.CmsgCancelGrowthAura, []);
        await client.SendAsync(WorldOpcode.CmsgToggleCloak, []);
        await host.WaitForWorldAsync(() => (player.Flags & PlayerFlags.HideCloak) != 0, "the session still runs after the notices");
        Assert.Equal(before | PlayerFlags.HideCloak, player.Flags);
    }
}

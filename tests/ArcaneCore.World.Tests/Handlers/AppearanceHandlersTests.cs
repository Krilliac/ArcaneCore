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
    public async Task HelmAndCloakToggles_SurviveARelog()
    {
        // vmangos 0e3ff01 Player::SaveToDB stores the hide bits in characters.character_flags (Player.cpp:16339-16340) and
        // Player::LoadFromDB sets them back on PLAYER_FLAGS (Player.cpp:14678-14681). Disconnect and relog as DeathDurabilityWorldTests.
        const string account = "HELMRELOG";
        const string name = "Helmrelog";
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync(account);
        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(account, key);
            await client.CreateCharacterAsync(name);
            await client.LoginAsync(1);
            Player player = await host.PlayerAsync(name);
            await client.SendAsync(WorldOpcode.CmsgToggleHelm, []);
            await client.SendAsync(WorldOpcode.CmsgToggleCloak, []);
            await host.WaitForWorldAsync(() => (player.Flags & (PlayerFlags.HideHelm | PlayerFlags.HideCloak)) == (PlayerFlags.HideHelm | PlayerFlags.HideCloak),
                "the helm and cloak are hidden");
        }

        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "player disconnected");
        await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "hidden flags saved");

        await using (WorldTestClient relog = await host.ConnectAsync())
        {
            await relog.AuthenticateAsync(account, key);
            await relog.LoginAsync(1);
            Assert.Equal(PlayerFlags.HideHelm | PlayerFlags.HideCloak,
                await host.PlayerStateAsync(name, p => p.Flags & (PlayerFlags.HideHelm | PlayerFlags.HideCloak)));
            await relog.SendAsync(WorldOpcode.CmsgToggleHelm, []);
            Player player = await host.PlayerAsync(name);
            await host.WaitForWorldAsync(() => (player.Flags & PlayerFlags.HideHelm) == 0, "the helm shows again");
        }

        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "player disconnected again");
        await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "shown helm saved");

        await using WorldTestClient third = await host.ConnectAsync();
        await third.AuthenticateAsync(account, key);
        await third.LoginAsync(1);
        Assert.Equal(PlayerFlags.HideCloak, await host.PlayerStateAsync(name, p => p.Flags & (PlayerFlags.HideHelm | PlayerFlags.HideCloak)));
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

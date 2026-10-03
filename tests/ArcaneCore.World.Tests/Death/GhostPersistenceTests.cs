using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>
/// A spirit survives a logout and a relog with its body: the ghost is stored with a corpse row,
/// the login puts the body back at the stored place, and a stored ghost without a body (or a
/// stored dead character that never released) comes back at half health, as in vmangos
/// (WorldSession.cpp:694-701, Player::LoadCorpse Player.cpp:15427-15440).
/// </summary>
public sealed class GhostPersistenceTests
{
    private static async Task<(WorldTestClient Client, byte[] Key, CharacterRecord Record)> CreateAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        CharacterRecord record = (await host.Characters.GetByIdAsync(1))!;
        return (client, key, record);
    }

    private static CharacterLife GhostLife(CharacterRecord at, long ghostTimeAgo, bool withBody = true)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new CharacterLife(1, [0, 0, 0, 0, 0], 0, now + 300, true,
            withBody ? new CorpseSnapshot(at.MapId, at.X, at.Y, at.Z, at.Orientation, now - ghostTimeAgo, (byte)CorpseType.ResurrectablePve) : null);
    }

    [Fact]
    public async Task LogoutWhileDead_RelogsAsAGhostAtTheBody()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient first, byte[] key, _) = await CreateAsync(host, "GHOST1", "Ghostone");
        await first.LoginAsync(1);
        (float x, float y, float z) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostone")!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player); // dead, the spirit not yet released
            return (player.X, player.Y, player.Z);
        });

        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ghostone") is null, "the session to leave the world");
        // The logout save is queued after the player leaves the world and written by the save queue's own task, so wait for it
        // (reading the store right after the removal raced the queue: about 40 percent failures under load at wave-2 integration).
        await host.WaitForWorldAsync(() => host.Characters.Life(1) is not null, "the logout save to reach the store");
        CharacterLife stored = host.Characters.Life(1)!;
        Assert.True(stored.IsGhost);
        Assert.Equal(1u, stored.Health);
        Assert.Equal((x, y, z), (stored.Corpse!.X, stored.Corpse.Y, stored.Corpse.Z));

        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("GHOST1", key);
        await again.LoginAsync(1);

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ghostone")?.Combat.Corpse is not null, "the body to be put back");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Ghostone")!;
            Assert.NotEqual(0u, (uint)(player.Flags & PlayerFlags.Ghost));
            Assert.Equal(DeathState.Dead, player.Combat.DeathState);
            Assert.False(player.IsAlive);
            Assert.Equal(1u, player.Health);
            Corpse corpse = Assert.Single(player.Map!.Combat.Corpses);
            Assert.Equal((x, y, z), (corpse.X, corpse.Y, corpse.Z));
            Assert.Equal(player.Guid.Value, corpse.Owner.Value);
        });
    }

    [Fact]
    public async Task AStoredGhost_ReclaimsItsBodyOnceTheDelayHasPassed()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _, CharacterRecord record) = await CreateAsync(host, "GHOST2", "Ghosttwo");
        await using (client)
        {
            host.Characters.SetLife(1, GhostLife(record, ghostTimeAgo: 100)); // the 30 s delay is long over
            await client.LoginAsync(1);
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Ghosttwo")?.Combat.Corpse is not null, "the body to be put back");

            Assert.True(await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ghosttwo")!.Map!.Combat.TryReclaimCorpse(host.World.FindOnlinePlayer("Ghosttwo")!)));

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Ghosttwo")!;
                Assert.True(player.IsAlive);
                Assert.Equal(player.MaxHealth / 2, player.Health);
                Assert.Empty(player.Map!.Combat.Corpses);
            });
        }
    }

    [Fact]
    public async Task AStoredGhostWithoutABody_ComesBackAtHalfHealth()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _, CharacterRecord record) = await CreateAsync(host, "GHOST3", "Ghostthree");
        await using (client)
        {
            host.Characters.SetLife(1, GhostLife(record, 5, withBody: false));
            await client.LoginAsync(1);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Ghostthree")!;
                Assert.True(player.IsAlive);
                Assert.Equal(0u, (uint)(player.Flags & PlayerFlags.Ghost));
                Assert.Equal(player.MaxHealth / 2, player.Health);
                Assert.Empty(player.Map!.Combat.Corpses);
            });
        }
    }

    [Fact]
    public async Task AStoredDeadCharacterThatNeverReleased_ComesBackAtHalfHealth()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        (WorldTestClient client, _, _) = await CreateAsync(host, "GHOST4", "Ghostfour");
        await using (client)
        {
            host.Characters.SetLife(1, new CharacterLife(0, [0, 0, 0, 0, 0], 0, 0, false, null));
            await client.LoginAsync(1);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Ghostfour")!;
                Assert.True(player.IsAlive);
                Assert.Equal(player.MaxHealth / 2, player.Health);
            });
        }
    }
}

using System.Buffers.Binary;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Instances;

/// <summary>
/// vmangos Player::LoadFromDB relocates a character stored inside a dungeon it has no instance
/// for (to the entrance, else the bind point) before WorldSession::HandlePlayerLogin sends
/// SMSG_LOGIN_VERIFY_WORLD, so that packet carries the final map and position the player is
/// really added at.
/// </summary>
public sealed class LoginVerifyWorldRelocationTests
{
    private const uint Deadmines = 36;

    [Fact]
    public async Task LoginInsideADungeonWithoutAnInstance_VerifyWorldCarriesTheRelocatedPosition()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("LVWREL");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("LVWREL", key);
        await client.CreateCharacterAsync("Lvwrel");
        Account account = (await host.Accounts.FindByUsernameAsync("LVWREL"))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();

        // Stored inside the Deadmines with no instance bind: the login must send it back out.
        record.MapId = Deadmines;
        record.X = -16.4f;
        record.Y = -383.07f;
        record.Z = 61.78f;
        record.Orientation = 1.86f;

        await client.LoginAsync((ulong)record.Id);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Lvwrel") is not null, "Lvwrel enters the world");

        // The test map data has no area trigger leading out of map 36, so this is the bind-point branch.
        (uint map, float x, float y, float z, float o) actual = await host.PlayerStateAsync("Lvwrel", p => (p.MapId, p.X, p.Y, p.Z, p.Orientation));
        Assert.NotEqual(Deadmines, actual.map);
        Assert.NotEqual(record.X, actual.x);

        byte[] verify = client.LastLoginPackets.Single(p => p.Opcode == WorldOpcode.SmsgLoginVerifyWorld).Payload;
        Assert.Equal(actual.map, BinaryPrimitives.ReadUInt32LittleEndian(verify.AsSpan(0, 4)));
        Assert.Equal(actual.x, BinaryPrimitives.ReadSingleLittleEndian(verify.AsSpan(4, 4)));
        Assert.Equal(actual.y, BinaryPrimitives.ReadSingleLittleEndian(verify.AsSpan(8, 4)));
        Assert.Equal(actual.z, BinaryPrimitives.ReadSingleLittleEndian(verify.AsSpan(12, 4)));
        Assert.Equal(actual.o, BinaryPrimitives.ReadSingleLittleEndian(verify.AsSpan(16, 4)));
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// M4: two clients in the same map see each other and each other's movement. Both sessions
/// share one <see cref="WorldState"/>, account store and character store, as in the real
/// daemon.
/// </summary>
public sealed class VisibilityTests
{
    [Fact]
    public async Task TwoPlayers_SeeEachOtherEnterAndMove()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var worldData = new InMemoryWorldDataStore();
        var world = new WorldState();

        byte[] keyA = await AddAccount(accounts, "PLAYERA");
        byte[] keyB = await AddAccount(accounts, "PLAYERB");

        // --- Player A logs in (alone) ---
        await using var clientA = await WorldTestClient.StartAsync(accounts, characters, worldData, world);
        await clientA.AuthenticateAsync("PLAYERA", keyA);
        await CreateAndLoginAsync(clientA, "Aaa", guid: 1);

        // --- Player B logs in; A and B should now see each other ---
        await using var clientB = await WorldTestClient.StartAsync(accounts, characters, worldData, world);
        await clientB.AuthenticateAsync("PLAYERB", keyB);
        await CreateAndLoginAsync(clientB, "Bbb", guid: 2);

        // B's enter pushes a create-update of B to A.
        (WorldOpcode op, _) = await clientA.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);

        // ...and a create-update of A to B.
        (op, _) = await clientB.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);

        // --- A moves: B receives the relayed movement with A's packed GUID ---
        await clientA.SendAsync(WorldOpcode.MsgMoveHeartbeat, BuildMovement(-8945f, -130f, 83.5f, 0.5f));
        (op, byte[] payload) = await clientB.ReadAsync();
        Assert.Equal(WorldOpcode.MsgMoveHeartbeat, op);
        Assert.Equal(0x01, payload[0]); // packed-guid mask for low guid 1
        Assert.Equal(0x01, payload[1]); // guid byte
    }

    [Fact]
    public async Task PlayerLeaving_DestroysObjectForOthers()
    {
        var accounts = new InMemoryAccountStore();
        var characters = new InMemoryCharacterStore();
        var worldData = new InMemoryWorldDataStore();
        var world = new WorldState();

        byte[] keyA = await AddAccount(accounts, "PLAYERA");
        byte[] keyB = await AddAccount(accounts, "PLAYERB");

        await using var clientA = await WorldTestClient.StartAsync(accounts, characters, worldData, world);
        await clientA.AuthenticateAsync("PLAYERA", keyA);
        await CreateAndLoginAsync(clientA, "Aaa", guid: 1);

        var clientB = await WorldTestClient.StartAsync(accounts, characters, worldData, world);
        await clientB.AuthenticateAsync("PLAYERB", keyB);
        await CreateAndLoginAsync(clientB, "Bbb", guid: 2);

        await clientA.ReadAsync(); // B's create-update to A
        await clientB.ReadAsync(); // A's create-update to B

        // B disconnects → A should be told to destroy B's object.
        await clientB.DisposeAsync();

        (WorldOpcode op, byte[] payload) = await clientA.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgDestroyObject, op);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt64LittleEndian(payload)); // B's guid
    }

    private static async Task<byte[]> AddAccount(InMemoryAccountStore accounts, string name)
    {
        byte[] key = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength);
        await accounts.CreateAsync(new Account
        {
            Username = name, Salt = new byte[32], Verifier = new byte[32], SessionKey = key,
        });
        return key;
    }

    private static async Task CreateAndLoginAsync(WorldTestClient client, string name, ulong guid)
    {
        var create = new PacketWriter(32);
        create.WriteCString(name);
        create.WriteByte(1); // race human
        create.WriteByte(1); // class warrior
        for (int i = 0; i < 6; i++)
        {
            create.WriteByte(0); // gender + appearance + outfit
        }

        await client.SendAsync(WorldOpcode.CmsgCharCreate, create.AsMemory().ToArray());
        Assert.Equal((byte)CharResult.CharCreateSuccess, (await client.ReadAsync()).Payload[0]);

        var login = new PacketWriter(8);
        login.WriteUInt64(guid);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.AsMemory().ToArray());

        // Consume the five login-sequence packets (verify world, tutorial, time, spells, self update).
        for (int i = 0; i < 5; i++)
        {
            await client.ReadAsync();
        }
    }

    private static byte[] BuildMovement(float x, float y, float z, float o)
    {
        var writer = new PacketWriter(24);
        writer.WriteUInt32(0);  // move flags
        writer.WriteUInt32(50); // timestamp
        writer.WriteSingle(x);
        writer.WriteSingle(y);
        writer.WriteSingle(z);
        writer.WriteSingle(o);
        return writer.AsMemory().ToArray();
    }
}

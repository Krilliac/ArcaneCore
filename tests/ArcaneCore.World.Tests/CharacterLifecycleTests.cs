using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Drives the M3 character lifecycle against a real <see cref="ArcaneCore.World.Net.WorldSession"/>
/// over loopback: create → enumerate → enter world (object update) → delete.
/// </summary>
public sealed class CharacterLifecycleTests
{
    private const string Account = "PLAYER1";
    private const string CharName = "Tester";

    [Fact]
    public async Task FullLifecycle_CreateEnumerateLoginDelete()
    {
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account
        {
            Username = Account,
            Salt = new byte[32],
            Verifier = new byte[32],
            SessionKey = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength),
        });
        byte[] sessionKey = (await accounts.FindByUsernameAsync(Account))!.SessionKey!;

        var characters = new InMemoryCharacterStore();
        var worldData = new InMemoryWorldDataStore();

        await using var client = await WorldTestClient.StartAsync(accounts, characters, worldData);
        await client.AuthenticateAsync(Account, sessionKey);

        // --- create (human warrior) ---
        await client.SendAsync(WorldOpcode.CmsgCharCreate, BuildCreate(CharName, race: 1, cls: 1, gender: 0));
        (WorldOpcode op, byte[] payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharCreate, op);
        Assert.Equal((byte)CharResult.CharCreateSuccess, payload[0]);

        // --- enumerate ---
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        (op, payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharEnum, op);
        Assert.Equal(1, payload[0]); // one character
        Assert.Equal(CharName, ReadEnumName(payload));

        // --- login (guid 1) → world entry sequence ---
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, BuildGuid(1));
        Assert.Equal(WorldOpcode.SmsgLoginVerifyWorld, (await client.ReadAsync()).Opcode);
        Assert.Equal(WorldOpcode.SmsgTutorialFlags, (await client.ReadAsync()).Opcode);
        Assert.Equal(WorldOpcode.SmsgLoginSetTimeSpeed, (await client.ReadAsync()).Opcode);
        Assert.Equal(WorldOpcode.SmsgInitialSpells, (await client.ReadAsync()).Opcode);

        (op, payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);
        AssertSelfCreateBlock(payload);

        // --- delete ---
        await client.SendAsync(WorldOpcode.CmsgCharDelete, BuildGuid(1));
        (op, payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharDelete, op);
        Assert.Equal((byte)CharResult.CharDeleteSuccess, payload[0]);

        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        (_, payload) = await client.ReadAsync();
        Assert.Equal(0, payload[0]); // no characters left
    }

    [Fact]
    public async Task CreateWithDuplicateName_IsRejected()
    {
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account
        {
            Username = Account, Salt = new byte[32], Verifier = new byte[32],
            SessionKey = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength),
        });
        byte[] sessionKey = (await accounts.FindByUsernameAsync(Account))!.SessionKey!;

        await using var client = await WorldTestClient.StartAsync(accounts, new InMemoryCharacterStore(), new InMemoryWorldDataStore());
        await client.AuthenticateAsync(Account, sessionKey);

        await client.SendAsync(WorldOpcode.CmsgCharCreate, BuildCreate("Dup", 1, 1, 0));
        Assert.Equal((byte)CharResult.CharCreateSuccess, (await client.ReadAsync()).Payload[0]);

        await client.SendAsync(WorldOpcode.CmsgCharCreate, BuildCreate("Dup", 1, 1, 0));
        Assert.Equal((byte)CharResult.CharCreateNameInUse, (await client.ReadAsync()).Payload[0]);
    }

    [Fact]
    public async Task CreateWithInvalidRaceClass_IsRejected()
    {
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account
        {
            Username = Account, Salt = new byte[32], Verifier = new byte[32],
            SessionKey = RandomNumberGenerator.GetBytes(WowSrp6.SessionKeyLength),
        });
        byte[] sessionKey = (await accounts.FindByUsernameAsync(Account))!.SessionKey!;

        await using var client = await WorldTestClient.StartAsync(accounts, new InMemoryCharacterStore(), new InMemoryWorldDataStore());
        await client.AuthenticateAsync(Account, sessionKey);

        // The in-memory world data only allows human warrior; orc mage is invalid.
        await client.SendAsync(WorldOpcode.CmsgCharCreate, BuildCreate("Badcombo", race: 2, cls: 8, gender: 0));
        Assert.Equal((byte)CharResult.CharCreateFailed, (await client.ReadAsync()).Payload[0]);
    }

    private static byte[] BuildCreate(string name, byte race, byte cls, byte gender)
    {
        var writer = new PacketWriter(32);
        writer.WriteCString(name);
        writer.WriteByte(race);
        writer.WriteByte(cls);
        writer.WriteByte(gender);
        writer.WriteByte(0); // skin
        writer.WriteByte(0); // face
        writer.WriteByte(0); // hair style
        writer.WriteByte(0); // hair color
        writer.WriteByte(0); // facial hair
        writer.WriteByte(0); // outfit id
        return writer.AsMemory().ToArray();
    }

    private static byte[] BuildGuid(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.AsMemory().ToArray();
    }

    private static string ReadEnumName(byte[] enumPayload)
    {
        // count(1) + guid(8), then the null-terminated name.
        int start = 1 + 8;
        int end = Array.IndexOf(enumPayload, (byte)0, start);
        return Encoding.ASCII.GetString(enumPayload, start, end - start);
    }

    private static void AssertSelfCreateBlock(byte[] payload)
    {
        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        Assert.Equal(1u, blockCount);
        Assert.Equal(0, payload[4]);                 // has-transport
        Assert.Equal(3, payload[5]);                 // UPDATETYPE_CREATE_OBJECT2
        // packed guid for low-guid 1 = mask 0x01 + byte 0x01, then the type id.
        Assert.Equal(0x01, payload[6]);
        Assert.Equal(0x01, payload[7]);
        Assert.Equal(TypeId.Player, payload[8]);     // TYPEID_PLAYER
    }
}

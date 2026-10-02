using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The character lifecycle against a real world daemon over loopback: create → enumerate →
/// enter world (self create) → delete.
/// </summary>
public sealed class CharacterLifecycleTests
{
    private const string Account = "PLAYER1";
    private const string CharName = "Tester";

    [Fact]
    public async Task FullLifecycle_CreateEnumerateLoginDelete()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync(Account);

        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.CreateCharacterAsync(CharName);

            // --- enumerate ---
            await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
            (WorldOpcode op, byte[] payload) = await client.ReadAsync();
            Assert.Equal(WorldOpcode.SmsgCharEnum, op);
            Assert.Equal(1, payload[0]); // one character
            Assert.Equal(CharName, ReadEnumName(payload));

            // --- login (guid 1): the login sequence, then the self create ---
            byte[] self = await client.LoginAsync(1);
            AssertSelfCreateBlock(self, guid: 1);
        }

        // Disconnecting leaves the world (saved), so the character can be deleted next session.
        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "player removed after disconnect");

        await using (WorldTestClient client = await host.ConnectAsync())
        {
            await client.AuthenticateAsync(Account, key);
            await client.SendAsync(WorldOpcode.CmsgCharDelete, BuildGuid(1));
            (WorldOpcode op, byte[] payload) = await client.ReadAsync();
            Assert.Equal(WorldOpcode.SmsgCharDelete, op);
            Assert.Equal((byte)CharResult.CharDeleteSuccess, payload[0]);

            await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
            (_, payload) = await client.ReadAsync();
            Assert.Equal(0, payload[0]); // no characters left
        }
    }

    [Fact]
    public async Task CreateWithDuplicateName_IsRejected()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync(Account);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);

        await client.CreateCharacterAsync("Dup");

        await client.SendAsync(WorldOpcode.CmsgCharCreate, BuildCreate("Dup", 1, 1, 0));
        Assert.Equal((byte)CharResult.CharCreateNameInUse, (await client.ReadAsync()).Payload[0]);
    }

    [Fact]
    public async Task CreateWithInvalidRaceClass_IsRejected()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync(Account);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);

        // The in-memory world data only allows human warrior; orc mage is invalid.
        await client.SendAsync(WorldOpcode.CmsgCharCreate, BuildCreate("Badcombo", race: 2, cls: 8, gender: 0));
        Assert.Equal((byte)CharResult.CharCreateFailed, (await client.ReadAsync()).Payload[0]);
    }

    [Fact]
    public async Task LoginForSomeoneElsesCharacter_FailsWithNoCharacter()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient owner = await host.EnterWorldAsync("OWNER", "Owned");

        byte[] key = await host.AddAccountAsync("THIEF");
        await using WorldTestClient thief = await host.ConnectAsync();
        await thief.AuthenticateAsync("THIEF", key);

        await thief.SendAsync(WorldOpcode.CmsgPlayerLogin, BuildGuid(1));
        (WorldOpcode op, byte[] payload) = await thief.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgCharacterLoginFailed, op);
        Assert.Equal((byte)CharResult.CharLoginNoCharacter, payload[0]);
    }

    private static byte[] BuildCreate(string name, byte race, byte cls, byte gender)
    {
        var writer = new PacketWriter(32);
        writer.WriteCString(name);
        writer.WriteByte(race);
        writer.WriteByte(cls);
        writer.WriteByte(gender);
        for (int i = 0; i < 6; i++)
        {
            writer.WriteByte(0);
        }

        return writer.ToArray();
    }

    private static byte[] BuildGuid(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    private static string ReadEnumName(byte[] enumPayload)
    {
        // count(1) + guid(8), then the null-terminated name.
        int start = 1 + 8;
        int end = Array.IndexOf(enumPayload, (byte)0, start);
        return Encoding.UTF8.GetString(enumPayload, start, end - start);
    }

    internal static void AssertSelfCreateBlock(byte[] body, byte guid)
    {
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(body)); // one block
        Assert.Equal(0, body[4]);                                       // has transport
        Assert.Equal((byte)ObjectUpdateType.CreateObject, body[5]);     // vmangos SendInitSelf: UPDATETYPE_CREATE_OBJECT
        Assert.Equal(0x01, body[6]);                                    // packed guid mask
        Assert.Equal(guid, body[7]);
        Assert.Equal(TypeId.Player, body[8]);
        Assert.Equal((byte)(ObjectUpdateFlags.Self | ObjectUpdateFlags.All | ObjectUpdateFlags.Living | ObjectUpdateFlags.HasPosition), body[9]);
    }
}

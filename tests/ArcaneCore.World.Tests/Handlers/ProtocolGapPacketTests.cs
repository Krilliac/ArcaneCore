using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// The server packets the lane sends with a reference-stated layout (<see cref="ProtocolGapPackets"/>): the login flow's SMSG_EXPECTED_SPAM_RECORDS
/// (option World:ExpectedSpamRecords, default empty) and the <c>.wiresample</c> diagnostic for SMSG_CHAT_RESTRICTED, SMSG_PLAY_TIME_WARNING,
/// SMSG_SPLINE_MOVE_START_SWIM, SMSG_SPLINE_MOVE_STOP_SWIM and SMSG_COMPRESSED_MOVES. Bytes asserted on the wire through a loopback client.
/// Written without a build or test run (lane rule); unproven until the integration gate.
/// </summary>
public sealed class ProtocolGapPacketTests
{
    [Fact]
    public void ExpectedSpamRecords_IsACountThenNulTerminatedStrings_AndStopsBeforeTheFrameBound()
    {
        Assert.Equal([2, 0, 0, 0, (byte)'a', 0, (byte)'b', (byte)'c', 0], ProtocolGapPackets.ExpectedSpamRecords(["a", "bc"]));
        Assert.Equal([0, 0, 0, 0], ProtocolGapPackets.ExpectedSpamRecords([]));
        string big = new('x', 20000);
        byte[] body = ProtocolGapPackets.ExpectedSpamRecords([big, big, big, big]);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.True(body.Length <= 4 + ProtocolGapPackets.MaxSpamRecordBytes);
    }

    [Fact]
    public void TheFixedBuilders_MatchTheReferenceLayouts()
    {
        Assert.Empty(ProtocolGapPackets.ChatRestricted());
        Assert.Equal([0x00, 0x10, 0, 0, 0x58, 0x02, 0, 0], ProtocolGapPackets.PlayTimeWarning(0x1000, 600));
        Assert.Equal([0x01, 0x02], ProtocolGapPackets.SplineMoveSwim(2));
    }

    [Fact]
    public void CompressedRunSpeedMove_HoldsTheSizePrefixAndAZlibStreamOfOneEntry()
    {
        byte[] body = ProtocolGapPackets.CompressedRunSpeedMove(2, 7f);
        byte[] inflated = WorldTestClient.Inflate(body);
        // entry: u8 size (opcode 2 + packed guid 2 + f32 4 = 8), u16 opcode SMSG_SPLINE_SET_RUN_SPEED 0x02FE, packed guid, f32
        Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(body), (uint)inflated.Length);
        Assert.Equal([8, 0xFE, 0x02, 0x01, 0x02, 0x00, 0x00, 0xE0, 0x40], inflated);
    }

    [Fact]
    public async Task WithRecordsConfigured_LoginSendsThemBeforeTheMotd_AndWithoutThemNothing()
    {
        await using (var host = WorldTestHost.Start(configure: o => o.ExpectedSpamRecords = "alpha@beta"))
        await using (WorldTestClient client = await ConnectAndLoginAsync(host, "Z30SPAM", "Spamone"))
        {
            int spam = client.LastLoginPackets.FindIndex(p => p.Opcode == WorldOpcode.SmsgExpectedSpamRecords);
            int motd = client.LastLoginPackets.FindIndex(p => p.Opcode == WorldOpcode.SmsgMessagechat);
            Assert.True(spam >= 0, "SMSG_EXPECTED_SPAM_RECORDS was sent");
            Assert.True(motd < 0 || spam < motd, "before the MOTD lines");
            Assert.Equal(ProtocolGapPackets.ExpectedSpamRecords(["alpha", "beta"]), client.LoginPacket(WorldOpcode.SmsgExpectedSpamRecords));
        }

        await using (var plain = WorldTestHost.Start())
        await using (WorldTestClient client = await plain.EnterWorldAsync("Z30NOSPAM", "Nospamone"))
        {
            Assert.DoesNotContain(client.LastLoginPackets, p => p.Opcode == WorldOpcode.SmsgExpectedSpamRecords);
        }
    }

    [Theory]
    [InlineData("chat-restricted", WorldOpcode.SmsgChatRestricted)]
    [InlineData("play-time-warning", WorldOpcode.SmsgPlayTimeWarning)]
    [InlineData("expected-spam-records", WorldOpcode.SmsgExpectedSpamRecords)]
    [InlineData("spline-swim-start", WorldOpcode.SmsgSplineMoveStartSwim)]
    [InlineData("spline-swim-stop", WorldOpcode.SmsgSplineMoveStopSwim)]
    [InlineData("compressed-moves", WorldOpcode.SmsgCompressedMoves)]
    public async Task WireSample_SendsTheNamedPacket_ToAnAdministratorOnly(string name, WorldOpcode expected)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("Z30ADM" + (ushort)expected, "Adm" + Letters((ushort)expected), AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("Z30PLR" + (ushort)expected, "Plr" + Letters((ushort)expected));
        await admin.CollectAsync();
        await player.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".wiresample " + name);
        byte[] body = await admin.ReadUntilAsync(expected);
        Player self = await host.PlayerAsync("Adm" + Letters((ushort)expected));
        Assert.Equal(Expected(name, self.Guid.Value), body);

        await player.SendChatAsync(ChatType.Say, Language.Common, ".wiresample " + name);
        await player.AssertNoPacketsAfterAsync(TimeSpan.FromMilliseconds(300), expected);
    }

    private static byte[] Expected(string name, ulong guid) => name switch
    {
        "chat-restricted" => ProtocolGapPackets.ChatRestricted(),
        "play-time-warning" => ProtocolGapPackets.PlayTimeWarning(0x1000, 600),
        "expected-spam-records" => ProtocolGapPackets.ExpectedSpamRecords(["wiresample"]),
        "spline-swim-start" or "spline-swim-stop" => ProtocolGapPackets.SplineMoveSwim(guid),
        _ => ProtocolGapPackets.CompressedRunSpeedMove(guid, 7f), // zlib at a fixed level is deterministic for the same input
    };

    private static string Letters(int n) => string.Concat(n.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => (char)('a' + (c - '0'))));

    /// <summary>Connect, create a character and log in by hand: <see cref="WorldTestClient.LoginAsync"/> asserts the stock login order, which a spam-records packet changes on purpose.</summary>
    private static async Task<WorldTestClient> ConnectAndLoginAsync(WorldTestHost host, string account, string character)
    {
        byte[] key = await host.AddAccountAsync(account);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(character);
        Account stored = (await host.Accounts.FindByUsernameAsync(account))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(stored.Id)).Single();
        var login = new PacketWriter(8);
        login.WriteUInt64((ulong)record.Id);
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, login.ToArray());
        client.LastLoginPackets.Clear();
        while (true)
        {
            (WorldOpcode opcode, byte[] payload) = await client.ReadAsync();
            client.LastLoginPackets.Add((opcode, payload));
            if (opcode == WorldOpcode.SmsgInitWorldStates)
            {
                return client;
            }
        }
    }
}

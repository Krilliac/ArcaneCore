using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Time;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.DebugTools;
using ArcaneCore.World.Packets;
using Xunit;

namespace ArcaneCore.World.Tests.Handlers;

/// <summary>
/// The server packets the lane sends with a reference-stated layout (<see cref="ProtocolGapPackets"/>): the login flow's SMSG_EXPECTED_SPAM_RECORDS
/// (option World:ExpectedSpamRecords, default empty) and the <c>.wiresample</c> diagnostic for the 27 server packets no gameplay flow sends. Layouts are the 1.12.1 client's own
/// readers (docs/areas/protocol-decisions.md). Bytes asserted on the wire through a loopback client.
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

    [Fact]
    public void TheClientLayoutBuilders_WriteExactlyTheFieldsTheClientReads()
    {
        // LOOT_ITEM_NOTIFY (client 0x5EC040): u64, u8, u8, u32, cstring.
        Assert.Equal([0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 7, 8, 0x24, 0x1B, 0, 0, (byte)'h', (byte)'i', 0],
            ProtocolGapPackets.LootItemNotify(0x1122334455667788UL, 7, 8, 6948, "hi"));
        // SPELLSTEALLOG (0x5E8C00): packed, packed, u32 count, u32 spell ids.
        Assert.Equal([0x01, 0x02, 0x01, 0x03, 2, 0, 0, 0, 0xB3, 0x05, 0, 0, 0x01, 0, 0, 0],
            ProtocolGapPackets.SpellStealLog(2, 3, [1459, 1]));
        // RWHOIS (0x5ADDE0): u32 count, cstring, u32 inner count, u32 flag, cstrings.
        Assert.Equal([1, 0, 0, 0, (byte)'n', 0, 2, 0, 0, 0, 4, 0, 0, 0, (byte)'a', 0, (byte)'b', 0],
            ProtocolGapPackets.RWhois([new ProtocolGapPackets.RWhoisEntry("n", 4, ["a", "b"])]));
        Assert.Equal([0, 0, 0, 0], ProtocolGapPackets.RWhois([]));
        Assert.Equal([0], ProtocolGapPackets.GroupCancel());
        Assert.Equal([0x78, 0x56, 0x34, 0x12], ProtocolGapPackets.GameTimeSet(0x12345678));
        Assert.Equal([0x00, 0x00, 0x80, 0x3F], ProtocolGapPackets.GameSpeedSet(1f));
        // ITEM_QUERY_MULTIPLE_RESPONSE (0x55BDB0): u8 count, u32 entry with bit 31 for "not found".
        Assert.Equal([2, 0x24, 0x1B, 0, 0x80, 0x01, 0, 0, 0x80], ProtocolGapPackets.ItemQueryMultipleNotFound([6948, 1]));
        Assert.Equal([9, 0, 0, 0, 0, 0, 0, 0, 1], ProtocolGapPackets.NpcWontTalk(9));
        Assert.Equal([(byte)'p', 0], ProtocolGapPackets.ChatPlayerAmbiguous("p"));
        Assert.Empty(ProtocolGapPackets.Empty());

        // MINIGAME_SETUP (0x4C4C70 + 0x7285E0): u64, u8 type, u8 state, u64, u64, 64 bytes, u8 only for state 2. MINIGAME_STATE: 9 + 1 bytes.
        byte[] setup = ProtocolGapPackets.MinigameSetup(5, 1, 1, 6, 7, [0xAA], 0xEE);
        Assert.Equal(8 + 1 + 1 + 8 + 8 + 64, setup.Length);
        Assert.Equal([5, 0, 0, 0, 0, 0, 0, 0, 1, 1, 6, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0, 0, 0, 0, 0, 0, 0xAA], setup[..27]);
        Assert.All(setup[27..], b => Assert.Equal(0, b));
        byte[] setupTwo = ProtocolGapPackets.MinigameSetup(5, 1, 2, 6, 7, [], 0xEE);
        Assert.Equal(8 + 1 + 1 + 8 + 8 + 64 + 1, setupTwo.Length);
        Assert.Equal(0xEE, setupTwo[^1]);
        Assert.Equal([1, 2, 0, 0, 0, 0, 0, 0, 0, 9], ProtocolGapPackets.MinigameState([1, 2], 9));
    }

    public static TheoryData<string> SampleNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in WireSampleCommand.SampleNames)
        {
            data.Add(name);
        }

        return data;
    }

    [Fact]
    public void TheDiagnosticOffersEveryOneOfTheTwentySevenSamples()
    {
        Assert.Equal(27, WireSampleCommand.SampleNames.Count);
        Assert.Equal(WireSampleCommand.SampleNames.Count, Opcodes.Count);
        Assert.Equal(Opcodes.Keys.Order(StringComparer.Ordinal), WireSampleCommand.SampleNames);
    }

    private static readonly Dictionary<string, WorldOpcode> Opcodes = new()
    {
        ["chat-restricted"] = WorldOpcode.SmsgChatRestricted,
        ["play-time-warning"] = WorldOpcode.SmsgPlayTimeWarning,
        ["expected-spam-records"] = WorldOpcode.SmsgExpectedSpamRecords,
        ["spline-swim-start"] = WorldOpcode.SmsgSplineMoveStartSwim,
        ["spline-swim-stop"] = WorldOpcode.SmsgSplineMoveStopSwim,
        ["compressed-moves"] = WorldOpcode.SmsgCompressedMoves,
        ["loot-item-notify"] = WorldOpcode.SmsgLootItemNotify,
        ["standstate-change-failure"] = WorldOpcode.SmsgStandstateChangeFailure,
        ["player-bind-error"] = WorldOpcode.SmsgPlayerbinderror,
        ["clear-far-sight-immediate"] = WorldOpcode.SmsgClearFarSightImmediate,
        ["spell-steal-log"] = WorldOpcode.SmsgSpellsteallog,
        ["rwhois"] = WorldOpcode.SmsgRwhois,
        ["group-cancel"] = WorldOpcode.SmsgGroupCancel,
        ["game-time-set"] = WorldOpcode.SmsgGametimeSet,
        ["game-speed-set"] = WorldOpcode.SmsgGamespeedSet,
        ["item-query-multiple"] = WorldOpcode.SmsgItemQueryMultipleResponse,
        ["invalid-promotion-code"] = WorldOpcode.SmsgInvalidPromotionCode,
        ["minigame-setup"] = WorldOpcode.SmsgMinigameSetup,
        ["minigame-state"] = WorldOpcode.SmsgMinigameState,
        ["npc-wont-talk"] = WorldOpcode.SmsgNpcWontTalk,
        ["ghostee-gone"] = WorldOpcode.SmsgGhosteeGone,
        ["chat-player-ambiguous"] = WorldOpcode.SmsgChatPlayerAmbiguous,
        ["script-message"] = WorldOpcode.SmsgScriptMessage,
        ["gametimebias-set"] = WorldOpcode.SmsgGametimebiasSet,
        ["character-profile"] = WorldOpcode.SmsgCharacterProfile,
        ["character-profile-realm-connected"] = WorldOpcode.SmsgCharacterProfileRealmConnected,
        ["minigame-move-failed"] = WorldOpcode.SmsgMinigameMoveFailed,
    };

    [Theory]
    [MemberData(nameof(SampleNames))]
    public async Task WireSample_SendsTheNamedPacket_ToAnAdministratorOnly(string name)
    {
        WorldOpcode expected = Opcodes[name];
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("Z30ADM" + (ushort)expected, "Adm" + Letters((ushort)expected), AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("Z30PLR" + (ushort)expected, "Plr" + Letters((ushort)expected));
        await admin.CollectAsync();
        await player.CollectAsync();

        Player self = await host.PlayerAsync("Adm" + Letters((ushort)expected));
        uint packedBefore = GameTimePacker.Pack(WorldStateHooks.For(host.World).LocalNow());
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".wiresample " + name);
        byte[] body = await admin.ReadUntilAsync(expected);
        uint packedAfter = GameTimePacker.Pack(WorldStateHooks.For(host.World).LocalNow());
        if (name == "game-time-set")
        {
            Assert.Equal(4, body.Length);
            uint sent = BinaryPrimitives.ReadUInt32LittleEndian(body);
            Assert.True(sent == packedBefore || sent == packedAfter, "the packed game time of the moment of the command");
        }
        else
        {
            Assert.Equal(Expected(name, self.Guid.Value), body);
        }

        await player.SendChatAsync(ChatType.Say, Language.Common, ".wiresample " + name);
        await player.AssertNoPacketsAfterAsync(TimeSpan.FromMilliseconds(300), expected);
    }

    private static byte[] Packed(ulong guid)
    {
        var writer = new PacketWriter(9);
        writer.WritePackedGuid(guid);
        return writer.ToArray();
    }

    private static byte[] Le64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Expected(string name, ulong guid) => name switch
    {
        "chat-restricted" or "standstate-change-failure" or "player-bind-error" or "clear-far-sight-immediate" or "invalid-promotion-code"
            or "ghostee-gone" or "script-message" or "gametimebias-set" or "character-profile" or "character-profile-realm-connected" or "minigame-move-failed" => [],
        "play-time-warning" => [0x00, 0x10, 0, 0, 0x58, 0x02, 0, 0],
        "expected-spam-records" => Cat([1, 0, 0, 0], "wiresample"u8.ToArray(), [0]),
        "spline-swim-start" or "spline-swim-stop" => Packed(guid),
        "compressed-moves" => ProtocolGapPackets.CompressedRunSpeedMove(guid, 7f), // zlib at a fixed level is deterministic for the same input
        "loot-item-notify" => Cat(Le64(guid), [1, 1, 0x24, 0x1B, 0, 0], "wiresample"u8.ToArray(), [0]),
        "spell-steal-log" => Cat(Packed(guid), Packed(guid), [1, 0, 0, 0, 0xB3, 0x05, 0, 0]),
        "rwhois" => Cat([1, 0, 0, 0], "wiresample"u8.ToArray(), [0, 1, 0, 0, 0, 0, 0, 0, 0], "line"u8.ToArray(), [0]),
        "group-cancel" => [0],
        "game-speed-set" => BitConverter.GetBytes(GameTimePacker.GameSpeedMinutesPerSecond),
        "item-query-multiple" => [1, 0x24, 0x1B, 0, 0x80],
        "minigame-setup" => Cat(Le64(guid), [1, 1], Le64(guid), Le64(0), new byte[64]),
        "minigame-state" => new byte[10],
        "npc-wont-talk" => Cat(Le64(guid), [1]),
        "chat-player-ambiguous" => Cat("wiresample"u8.ToArray(), [0]),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no expectation"),
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

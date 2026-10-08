using System.Buffers.Binary;
using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>
/// GM tickets end to end: the player packets (CMSG_GMTICKET_*, the 1.12 layouts of vmangos Server/Packets/GmTicket.cpp and
/// wow_messages gamemaster/*.wowm, which agree) and the
/// staff <c>.ticket</c> commands, with the store, the retained-write path and the character-deletion hook.
/// </summary>
public sealed class TicketTests
{
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        private long _advanced;

        internal void Advance(long seconds) => Interlocked.Add(ref _advanced, seconds);

        public override DateTimeOffset GetUtcNow() => _start.AddSeconds(Interlocked.Read(ref _advanced));
    }

    /// <summary>A host on a settable clock with <c>World:GmCommands:TicketMutationsPerMinute</c> configured.</summary>
    private static WorldTestHost StartLimited(Clock clock, int perMinute) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:GmCommands:TicketMutationsPerMinute"] = perMinute.ToString(CultureInfo.InvariantCulture),
        }).Build());
    });

    internal static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    internal static byte[] CreatePayload(string text, byte category = 1)
    {
        var writer = new PacketWriter(64);
        writer.WriteByte(category);
        writer.WriteUInt32(0);        // map (the server stores its own)
        writer.WriteSingle(1f);
        writer.WriteSingle(2f);
        writer.WriteSingle(3f);
        writer.WriteCString(text);
        writer.WriteCString(string.Empty);
        return writer.ToArray();
    }

    /// <summary>CMSG_GMTICKET_UPDATETEXT for 1.12: u8 ticket type, then the text (vmangos GmTicketUpdateText, wow_messages "versions = 1").</summary>
    internal static byte[] TextPayload(string text, byte type = 1)
    {
        var writer = new PacketWriter(32);
        writer.WriteByte(type);
        writer.WriteCString(text);
        return writer.ToArray();
    }

    internal static uint U32(byte[] payload) => BinaryPrimitives.ReadUInt32LittleEndian(payload);

    internal static async Task<string> ReplyAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    internal static async Task<string[]> LinesAsync(WorldTestClient client, string command)
    {
        await client.CollectAsync();
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }

    internal static InMemoryGmAuditStore StoreOf(WorldTestHost host) => host.WorldServices.GetRequiredService<InMemoryGmAuditStore>();

    internal static GmAuditFeature AuditOf(WorldTestHost host) => host.WorldServices.GetRequiredService<GmAuditFeature>();

    /// <summary>A player files a ticket with <paramref name="text"/> and the response code is read.</summary>
    internal static async Task<uint> CreateAsync(WorldTestClient client, string text)
    {
        await client.SendAsync(WorldOpcode.CmsgGmticketCreate, CreatePayload(text));
        return U32(await client.ReadUntilAsync(WorldOpcode.SmsgGmticketCreate));
    }

    // ---- the player's packets ----

    [Fact]
    public async Task Create_StoresTheTicket_AnswersCreated_TellsStaff_AndPersists()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await host.PlaceAsync("Plain", -8800f, -100f, 90f);
        await gm.CollectAsync();
        await mod.CollectAsync();
        await player.CollectAsync();

        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(player, "I am stuck in the wall"));

        Assert.Equal($"New ticket from {Link("Plain")} (ID 1)", (await gm.ReadChatAsync()).Text);
        Assert.DoesNotContain(await mod.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);   // tickets are GameMaster work
        GmTicketRecord ticket = AuditOf(host).OpenTicketOf(3)!;
        Assert.Equal((1, "I am stuck in the wall", (byte)1, -8800f, -100f, 90f), (ticket.Id, ticket.Text, ticket.Category, ticket.X, ticket.Y, ticket.Z));   // the server's own position
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1) is not null, "the ticket to reach storage");
        Assert.Equal(GmTicketStatus.Open, StoreOf(host).Ticket(1)!.Status);
    }

    [Fact]
    public async Task Create_SecondTicketOfTheSameCharacter_IsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(player, "first"));
        Assert.Equal(GmTicketHandlers.ResponseCreateError, await CreateAsync(player, "second"));   // vmangos: the response stays CREATE_ERROR

        Assert.Equal("first", Assert.Single(AuditOf(host).OpenTickets()).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u0007\u0007")]
    public async Task Create_WithNoUsableText_IsAnError_AndStoresNothing(string text)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        Assert.Equal(GmTicketHandlers.ResponseCreateError, await CreateAsync(player, text));
        Assert.Empty(AuditOf(host).OpenTickets());
    }

    [Fact]
    public async Task Create_ATruncatedPacket_IsAnError_NotAFault()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        await player.SendAsync(WorldOpcode.CmsgGmticketCreate, [1, 0, 0]);

        Assert.Equal(GmTicketHandlers.ResponseCreateError, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketCreate)));
        Assert.Empty(AuditOf(host).OpenTickets());
    }

    [Fact]
    public async Task Create_TheTextIsCappedAndStrippedOfControlCharacters()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(player, "\u0007  help\nme " + new string('x', 3000)));

        string stored = AuditOf(host).OpenTickets()[0].Text;
        Assert.Equal(GmAuditLimits.MaxTextLength, stored.Length);
        Assert.StartsWith("helpme x", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTicket_ReportsNoTicket_ThenTheOpenTicket()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        await player.SendAsync(WorldOpcode.CmsgGmticketGetticket, []);
        Assert.Equal(0x0Au, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketGetticket)));

        await player.SendAsync(WorldOpcode.CmsgGmticketCreate, CreatePayload("need a GM", category: 4));
        await player.ReadUntilAsync(WorldOpcode.SmsgGmticketCreate);
        await player.SendAsync(WorldOpcode.CmsgGmticketGetticket, []);
        await player.ReadUntilAsync(WorldOpcode.SmsgQueryTimeResponse);   // vmangos HandleGMTicketGetTicketOpcode answers the time first
        var reader = new PacketReader(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketGetticket));
        // vmangos GmTicketGetTicket::AppendBodyTo / wow_messages smsg_gmticket_getticket (1.12): status, text, the ticket's own
        // type, three ages in days (since its last change, of the oldest open ticket, since the queue last changed),
        // escalation status, read by a GM.
        Assert.Equal((GmTicketHandlers.StatusHasTicket, "need a GM"), (reader.ReadUInt32(), reader.ReadCString()));
        Assert.Equal(4, reader.ReadByte());
        Assert.InRange(reader.ReadSingle(), 0f, 0.01f);
        Assert.InRange(reader.ReadSingle(), 0f, 0.01f);
        Assert.InRange(reader.ReadSingle(), 0f, 0.01f);
        Assert.Equal((0, 0), (reader.ReadByte(), reader.ReadByte()));
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public async Task UpdateText_ReplacesTheText_TellsStaff_AndFailsWithoutATicket()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await gm.CollectAsync();
        await player.CollectAsync();

        await player.SendAsync(WorldOpcode.CmsgGmticketUpdatetext, TextPayload("nothing to update"));
        Assert.Equal(GmTicketHandlers.ResponseUpdateError, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketUpdatetext)));

        await CreateAsync(player, "old text");
        await gm.CollectAsync();
        await player.SendAsync(WorldOpcode.CmsgGmticketUpdatetext, TextPayload("new text", type: 7));   // the leading byte is the type (7 = quest NPC)

        Assert.Equal(GmTicketHandlers.ResponseUpdated, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketUpdatetext)));
        Assert.Equal($"Player {Link("Plain")} has updated his ticket (ID 1).", (await gm.ReadChatAsync()).Text);
        Assert.Equal(("new text", (byte)7), (AuditOf(host).OpenTickets()[0].Text, AuditOf(host).OpenTickets()[0].Category));   // vmangos SetTicketType
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1)?.Text == "new text", "the update to reach storage");

        await player.SendAsync(WorldOpcode.CmsgGmticketUpdatetext, TextPayload("\u0007"));           // nothing left after cleaning
        Assert.Equal(GmTicketHandlers.ResponseUpdateError, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketUpdatetext)));
        Assert.Equal("new text", AuditOf(host).OpenTickets()[0].Text);
    }

    [Fact]
    public async Task DeleteTicket_WithdrawsTheTicket_AndRemovesTheRow()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();
        await CreateAsync(player, "never mind");
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1) is not null, "the ticket row");

        await player.SendAsync(WorldOpcode.CmsgGmticketDeleteticket, []);

        Assert.Equal(GmTicketHandlers.ResponseDeleted, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketDeleteticket)));
        Assert.Equal(0x0Au, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketGetticket)));
        Assert.Empty(AuditOf(host).OpenTickets());
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1) is null, "the row to go");

        await player.SendAsync(WorldOpcode.CmsgGmticketDeleteticket, []);   // again, with no ticket: vmangos answers nothing
        Assert.DoesNotContain(await player.CollectAsync(), p => p.Opcode is WorldOpcode.SmsgGmticketDeleteticket or WorldOpcode.SmsgGmticketGetticket);
    }

    [Fact]
    public async Task Create_AnUnknownTicketType_IsIgnored_AndASuccessSendsOnlyTheCreateAnswer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        // vmangos HandleGMTicketCreateOpcode: "if (packet.ticketType >= GMTICKET_MAX) return;" (GMTICKET_MAX = 11).
        await player.SendAsync(WorldOpcode.CmsgGmticketCreate, CreatePayload("odd type", category: 11));
        Assert.DoesNotContain(await player.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgGmticketCreate);
        Assert.Empty(AuditOf(host).OpenTickets());

        // A good one is answered with SMSG_GMTICKET_CREATE alone (no time response, unlike the get-ticket request).
        await player.SendAsync(WorldOpcode.CmsgGmticketCreate, CreatePayload("I fell through the world", category: 10));
        List<(WorldOpcode Opcode, byte[] Payload)> answer = await player.CollectAsync();
        Assert.Equal(GmTicketHandlers.ResponseCreated, U32(Assert.Single(answer, p => p.Opcode == WorldOpcode.SmsgGmticketCreate).Payload));
        Assert.DoesNotContain(answer, p => p.Opcode == WorldOpcode.SmsgQueryTimeResponse);
        Assert.Equal(10, AuditOf(host).OpenTickets()[0].Category);
    }

    [Fact]
    public async Task Mutations_AreRateLimitedPerAccount_RefusedUnreadBeyondTheLimit_AndStaffHearOnlyOfChanges()
    {
        var clock = new Clock();
        await using WorldTestHost host = StartLimited(clock, perMinute: 3);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await gm.CollectAsync();
        await player.CollectAsync();
        Assert.Equal(3, AuditOf(host).TicketMutationsPerMinute);

        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(player, "one"));                 // 1
        Assert.Equal($"New ticket from {Link("Plain")} (ID 1)", (await gm.ReadChatAsync()).Text);
        await player.SendAsync(WorldOpcode.CmsgGmticketUpdatetext, TextPayload("two"));                 // 2
        Assert.Equal(GmTicketHandlers.ResponseUpdated, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketUpdatetext)));
        Assert.Equal($"Player {Link("Plain")} has updated his ticket (ID 1).", (await gm.ReadChatAsync()).Text);
        long stamped = AuditOf(host).OpenTickets()[0].UpdatedAt;

        // The same text again is answered "updated" but changes nothing, so staff are not told again.
        clock.Advance(5);
        await player.SendAsync(WorldOpcode.CmsgGmticketUpdatetext, TextPayload("two"));                 // 3
        Assert.Equal(GmTicketHandlers.ResponseUpdated, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketUpdatetext)));
        Assert.DoesNotContain(await gm.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);
        Assert.Equal(stamped, AuditOf(host).OpenTickets()[0].UpdatedAt);

        // The fourth packet inside the minute is refused before its text is read: the error code, a line to the player, nothing to staff.
        await player.SendAsync(WorldOpcode.CmsgGmticketUpdatetext, TextPayload("three"));               // 4: over
        Assert.Equal(GmTicketHandlers.ResponseUpdateError, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketUpdatetext)));
        Assert.Equal(GmAuditStrings.TicketTooFast, (await player.ReadChatAsync()).Text);
        Assert.Equal("two", AuditOf(host).OpenTickets()[0].Text);
        Assert.DoesNotContain(await gm.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        // A refused delete deletes nothing and does not say it did: the client gets the ticket's real state.
        await player.SendAsync(WorldOpcode.CmsgGmticketDeleteticket, []);
        List<(WorldOpcode Opcode, byte[] Payload)> answer = await player.CollectFromAsync(WorldOpcode.SmsgGmticketGetticket);
        Assert.DoesNotContain(answer, p => p.Opcode == WorldOpcode.SmsgGmticketDeleteticket);
        Assert.Equal(GmTicketHandlers.StatusHasTicket, U32(Assert.Single(answer, p => p.Opcode == WorldOpcode.SmsgGmticketGetticket).Payload));
        Assert.Contains(answer, p => p.Opcode == WorldOpcode.SmsgMessagechat && ChatMessage.Parse(p.Payload).Text == GmAuditStrings.TicketTooFast);
        Assert.Single(AuditOf(host).OpenTickets());

        // Another account is not held back by this one's flood.
        await using WorldTestClient other = await host.EnterWorldAsync("OTHER", "Otherone");
        await other.CollectAsync();
        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(other, "mine"));

        // The minute passes: the account may act again.
        clock.Advance(60);
        await player.SendAsync(WorldOpcode.CmsgGmticketDeleteticket, []);
        Assert.Equal(GmTicketHandlers.ResponseDeleted, U32(await player.ReadUntilAsync(WorldOpcode.SmsgGmticketDeleteticket)));
        Assert.Equal("mine", Assert.Single(AuditOf(host).OpenTickets()).Text);
    }

    [Fact]
    public async Task RateLimit_FailsClosed_ZeroRefusesEveryMutation_ANegativeValueIsTheDefault()
    {
        var clock = new Clock();
        await using (WorldTestHost host = StartLimited(clock, perMinute: 0))
        {
            await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
            await player.CollectAsync();
            Assert.Equal(0, AuditOf(host).TicketMutationsPerMinute);

            Assert.Equal(GmTicketHandlers.ResponseCreateError, await CreateAsync(player, "nobody may file"));
            Assert.Equal(GmAuditStrings.TicketTooFast, (await player.ReadChatAsync()).Text);
            Assert.Empty(AuditOf(host).OpenTickets());
        }

        await using (WorldTestHost host = StartLimited(clock, perMinute: -5))
        {
            Assert.Equal(new GmOptions().TicketMutationsPerMinute, AuditOf(host).TicketMutationsPerMinute);
            Assert.True(AuditOf(host).TicketMutationsPerMinute > 0);
        }
    }

    // ---- staff commands ----

    [Fact]
    public void Levels_ListShowRespondCloseNeedGameMaster_DeleteNeedsAdministrator()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "ticket", "ticket list", "ticket onlinelist", "ticket show", "ticket respond", "ticket close" })
        {
            ChatCommand command = table.Find(path)!;
            Assert.True(table.IsAvailable(command, AccountSecurity.GameMaster), path);
            Assert.False(table.IsAvailable(command, AccountSecurity.Moderator), path);
        }

        ChatCommand delete = table.Find("ticket delete")!;
        Assert.False(table.IsAvailable(delete, AccountSecurity.GameMaster));
        Assert.True(table.IsAvailable(delete, AccountSecurity.Administrator));
    }

    [Fact]
    public async Task Commands_AreRefusedBelowTheirLevel()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await CreateAsync(player, "help");
        await mod.CollectAsync();
        await gm.CollectAsync();

        foreach (string command in new[] { ".ticket", ".ticket list", ".ticket show 1", ".ticket respond 1 hi", ".ticket close 1" })
        {
            Assert.Equal("This command is not available to you.", await ReplyAsync(mod, command));
        }

        Assert.Equal("This command is not available to you.", await ReplyAsync(gm, ".ticket delete 1"));
        Assert.Equal("Open tickets: 1", await ReplyAsync(gm, ".ticket"));   // nothing was deleted
        Assert.Single(AuditOf(host).OpenTickets());
    }
}

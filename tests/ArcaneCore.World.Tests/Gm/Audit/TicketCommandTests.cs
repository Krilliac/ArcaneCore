using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Audit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Gm.Audit.TicketTests;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>The staff <c>.ticket</c> commands, the startup load of tickets, and the failure and character-deletion paths.</summary>
public sealed class TicketCommandTests
{
    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        private long _advanced;

        internal long UnixNow => GetUtcNow().ToUnixTimeSeconds();

        internal void Advance(long seconds) => Interlocked.Add(ref _advanced, seconds);

        public override DateTimeOffset GetUtcNow() => _start.AddSeconds(Interlocked.Read(ref _advanced));
    }

    private static WorldTestHost Start(Clock clock, Action<IServiceCollection>? more = null) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<TimeProvider>(clock);
        more?.Invoke(services);
    });

    /// <summary>A character that exists but never logs in (offline), with its id.</summary>
    private static async Task<int> OfflineCharacterAsync(WorldTestHost host, string account, string name)
    {
        byte[] key = await host.AddAccountAsync(account);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
        return host.Directory.FindByName(name)!.Id;
    }

    [Fact]
    public async Task List_WithNoTickets_SaysSo()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Assert.Equal("Open tickets: 0", await ReplyAsync(gm, ".ticket"));
        Assert.Equal("There are no open tickets.", await ReplyAsync(gm, ".ticket list"));
        Assert.Equal("There are no open tickets.", await ReplyAsync(gm, ".ticket onlinelist"));
    }

    [Fact]
    public async Task List_AndOnlineList_ShowTheOwnersState_AndTheAge()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        int sleeperId = await OfflineCharacterAsync(host, "SLEEPER", "Sleepyhead");
        await one.CollectAsync();
        await CreateAsync(one, "first problem");
        AuditOf(host).CreateTicket(sleeperId, "second problem", 1, 0, 0, 0, 0);
        clock.Advance(90);
        await gm.CollectAsync();

        Assert.Equal(
            ["Open tickets: 2", $"ID 1 from {Link("Playerone")} (online), changed 1 Minute 30 Seconds ago", $"ID 2 from {Link("Sleepyhead")} (offline), changed 1 Minute 30 Seconds ago"],
            await LinesAsync(gm, ".ticket list"));
        Assert.Equal(
            ["Open tickets: 1", $"ID 1 from {Link("Playerone")} (online), changed 1 Minute 30 Seconds ago"],
            await LinesAsync(gm, ".ticket onlinelist"));
        Assert.Equal("Open tickets: 2", await ReplyAsync(gm, ".ticket"));
    }

    [Fact]
    public async Task List_IsCapped_AndSaysHowManyWereLeftOut()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        GmAuditFeature audit = AuditOf(host);
        for (int i = 1; i <= TicketCommands.MaxListed + 5; i++)
        {
            audit.CreateTicket(1000 + i, "t", 1, 0, 0, 0, 0);
        }

        string[] lines = await LinesAsync(gm, ".ticket list");

        Assert.Equal(1 + TicketCommands.MaxListed + 1, lines.Length);
        Assert.Equal($"Open tickets: {TicketCommands.MaxListed + 5}", lines[0]);
        Assert.Equal("... and 5 more (the list is capped).", lines[^1]);
    }

    [Fact]
    public async Task Show_PrintsTheTextAndTheAnswer_AndRefusesBadIds()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        await one.CollectAsync();
        await CreateAsync(one, "first problem");
        await gm.CollectAsync();
        string first = $"Ticket 1 of {Link("Playerone")} (Last updated: 2026-10-03 12:00:00 UTC): first problem";

        Assert.Equal(first, await ReplyAsync(gm, ".ticket show 1"));
        AuditOf(host).RespondToTicket(1, "try /unstuck");
        Assert.Equal([first, "Response: try /unstuck"], await LinesAsync(gm, ".ticket show 1"));

        Assert.Equal("Ticket 99 doesn't exist", await ReplyAsync(gm, ".ticket show 99"));
        foreach (string bad in new[] { ".ticket show", ".ticket show abc", ".ticket show 0", ".ticket show -1" })
        {
            Assert.StartsWith("Syntax: .ticket show", (await LinesAsync(gm, bad))[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Respond_TellsAnOnlineOwner_KeepsTheTicketOpen_AndSavesTheAnswer()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        await one.CollectAsync();
        await CreateAsync(one, "first problem");
        await gm.CollectAsync();

        Assert.Equal($"Response sent to {Link("Playerone")}.", await ReplyAsync(gm, ".ticket respond 1 please relog"));

        Assert.Equal("<GM>Gmaaa answers your ticket: please relog", (await one.ReadChatAsync()).Text);
        GmTicketRecord ticket = AuditOf(host).OpenTicket(1)!;
        Assert.Equal(("please relog", GmTicketStatus.Open), (ticket.Response, ticket.Status));
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1)?.Response == "please relog", "the answer to reach storage");
        Assert.StartsWith("Syntax: .ticket respond", (await LinesAsync(gm, ".ticket respond 1"))[0], StringComparison.Ordinal);
        Assert.Equal("Ticket 5 doesn't exist", (await LinesAsync(gm, ".ticket respond 5 hello"))[0]);
    }

    [Fact]
    public async Task Respond_ToAnOfflineOwner_SavesTheAnswer_AndSaysItWasNotDelivered()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        int sleeperId = await OfflineCharacterAsync(host, "SLEEPER", "Sleepyhead");
        AuditOf(host).CreateTicket(sleeperId, "help", 1, 0, 0, 0, 0);
        await gm.CollectAsync();

        Assert.Equal($"Response saved. {Link("Sleepyhead")} is offline and was not told.", await ReplyAsync(gm, ".ticket respond 1 done"));
        Assert.Equal("done", AuditOf(host).OpenTicket(1)!.Response);
    }

    [Fact]
    public async Task Close_EndsTheTicket_TellsTheOwner_ClearsTheirWindow_AndKeepsTheRowAsHistory()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        await one.CollectAsync();
        await CreateAsync(one, "first problem");
        await gm.CollectAsync();
        clock.Advance(30);

        Assert.Equal($"Ticket 1 from {Link("Playerone")} has been closed by <GM>Gmaaa", await ReplyAsync(gm, ".ticket close 1 fixed it, enjoy"));

        Assert.Equal("Your ticket has been closed by <GM>Gmaaa.", (await one.ReadChatAsync()).Text);
        Assert.Equal("<GM>Gmaaa answers your ticket: fixed it, enjoy", (await one.ReadChatAsync()).Text);
        Assert.Equal(0x0Au, U32(await one.ReadUntilAsync(WorldOpcode.SmsgGmticketGetticket)));
        Assert.Empty(AuditOf(host).OpenTickets());
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1)?.Status == GmTicketStatus.Closed, "the closed row");
        GmTicketRecord row = StoreOf(host).Ticket(1)!;
        Assert.Equal(("Gmaaa", clock.UnixNow, "fixed it, enjoy", "first problem"), (row.ClosedBy, row.ClosedAt, row.Response, row.Text));

        Assert.Equal("Ticket 1 doesn't exist", await ReplyAsync(gm, ".ticket show 1"));            // history is not an open ticket
        Assert.Equal("Ticket 1 doesn't exist", await ReplyAsync(gm, ".ticket close 1"));
        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(one, "a new problem"));     // the character may file again
        Assert.Equal(2, AuditOf(host).OpenTickets()[0].Id);                                           // and ids are never reused
    }

    [Fact]
    public async Task Close_WithoutAnAnswer_OnlyTellsTheOwnerItIsClosed()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        await one.CollectAsync();
        await CreateAsync(one, "first problem");
        await gm.CollectAsync();
        await one.CollectAsync();

        Assert.StartsWith("Ticket 1 from", await ReplyAsync(gm, ".ticket close 1"), StringComparison.Ordinal);

        string[] told = [.. (await one.CollectAsync()).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text)];
        Assert.Equal(["Your ticket has been closed by <GM>Gmaaa."], told);
    }

    [Fact]
    public async Task Delete_RemovesTheTicketAndItsRow_AndTellsTheOwner()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient admin = await host.EnterWorldAsync("ADM", "Admiral", AccountSecurity.Administrator);
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        await one.CollectAsync();
        await CreateAsync(one, "first problem");
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1) is not null, "the ticket row");
        await admin.CollectAsync();

        Assert.Equal("Ticket deleted.", await ReplyAsync(admin, ".ticket delete 1"));

        Assert.Equal("Your ticket has been deleted by staff.", (await one.ReadChatAsync()).Text);
        Assert.Equal(0x0Au, U32(await one.ReadUntilAsync(WorldOpcode.SmsgGmticketGetticket)));
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1) is null, "the row to go");
        Assert.Equal("Ticket 1 doesn't exist", await ReplyAsync(admin, ".ticket delete 1"));
        Assert.StartsWith("Syntax: .ticket delete", (await LinesAsync(admin, ".ticket delete"))[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .ticket delete", (await LinesAsync(admin, ".ticket delete 2 extra"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoredTickets_AreLoadedAtStartup_AndNewIdsSkipEveryIdEverStored()
    {
        var clock = new Clock();
        var store = new InMemoryGmAuditStore();
        store.Seed(new GmTicketRecord(3, 1, "stored and open", 1, 0, 0, 0, 0, clock.UnixNow - 500, clock.UnixNow - 400, GmTicketStatus.Open, string.Empty, string.Empty, 0));
        store.Seed(new GmTicketRecord(9, 78, "stored and closed", 1, 0, 0, 0, 0, clock.UnixNow - 900, clock.UnixNow - 800, GmTicketStatus.Closed, "done", "Old", clock.UnixNow - 800));
        await using WorldTestHost host = Start(clock, services => services.AddSingleton<IGmAuditStore>(store));
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");   // character id 1 owns the stored ticket
        await using WorldTestClient two = await host.EnterWorldAsync("TWO", "Playertwo");
        await one.CollectAsync();
        await two.CollectAsync();

        await one.SendAsync(WorldOpcode.CmsgGmticketGetticket, []);
        var reader = new PacketReader(await one.ReadUntilAsync(WorldOpcode.SmsgGmticketGetticket));
        Assert.Equal((GmTicketHandlers.StatusHasTicket, "stored and open"), (reader.ReadUInt32(), reader.ReadCString()));
        Assert.Equal(GmTicketHandlers.ResponseAlreadyExists, await CreateAsync(one, "another"));

        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(two, "mine"));
        Assert.Equal(10, AuditOf(host).OpenTicketOf(2)!.Id);
        Assert.Equal(["stored and open", "mine"], AuditOf(host).OpenTickets().Select(t => t.Text));   // the closed one is not loaded
    }

    // ---- storage failure and character deletion ----

    [Fact]
    public async Task Create_StorageFailing_StillFilesTheTicket_AndRetainsTheWrite()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        await one.CollectAsync();
        await gm.CollectAsync();
        StoreOf(host).FailNext(3);

        Assert.Equal(GmTicketHandlers.ResponseCreated, await CreateAsync(one, "first problem"));
        await AuditOf(host).Writes.FlushAsync();
        await gm.CollectAsync();

        Assert.Equal("Open tickets: 1", await ReplyAsync(gm, ".ticket"));                        // staff see it at once
        Assert.Equal(["ticket:1"], AuditOf(host).Writes.RetainedKeys);
        Assert.Null(StoreOf(host).Ticket(1));

        await AuditOf(host).Writes.RetryRetainedAsync();
        Assert.Equal("first problem", StoreOf(host).Ticket(1)!.Text);
        Assert.Empty(AuditOf(host).Writes.RetainedKeys);
    }

    [Fact]
    public async Task DeletingTheCharacter_DropsItsOpenTicket_AndItsRow()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient one = await host.EnterWorldAsync("ONE", "Playerone");
        await one.CollectAsync();
        await CreateAsync(one, "first problem");
        await WorldTestHost.WaitForAsync(() => StoreOf(host).Ticket(1) is not null, "the ticket row");
        CharacterRecord character = (await host.Characters.GetByAccountAsync((await host.Accounts.FindByUsernameAsync("ONE"))!.Id)).Single();

        await AuditOf(host).OnCharacterDeletedAsync(null!, character);

        Assert.Empty(AuditOf(host).OpenTickets());
        Assert.Null(StoreOf(host).Ticket(1));
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Protocol;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>.pinfo, .mute, .unmute, .gmannounce, .gmnotify and .gm ingame/list against the real world host (docs/integration/gm-audit-lane.md).</summary>
public sealed class AuditCommandTests
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

    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    private static async Task<string> ReplyAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    /// <summary>Run a command and read every chat line it produced.</summary>
    private static async Task<string[]> LinesAsync(WorldTestClient client, string command)
    {
        await client.CollectAsync();
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return await client.CollectChatLinesAsync();
    }

    private static string NotificationOf(byte[] payload) => new PacketReader(payload).ReadCString();

    private static async Task<int> AccountIdAsync(WorldTestHost host, string account) => (await host.Accounts.FindByUsernameAsync(account))!.Id;

    // ---- levels and registration ----

    [Fact]
    public void Levels_AreArcaneCoresOwn_AndTheRootsDoNotCollideWithRetailNames()
    {
        CommandTable table = ChatCommands.CreateTable();
        (string Path, AccountSecurity Lowest)[] expected =
        [
            ("pinfo", AccountSecurity.GameMaster), ("mute", AccountSecurity.Moderator), ("unmute", AccountSecurity.Moderator),
            ("gmannounce", AccountSecurity.Moderator), ("gmnotify", AccountSecurity.Moderator),
            ("gm ingame", AccountSecurity.Moderator), ("gm list", AccountSecurity.Administrator),
        ];
        foreach ((string path, AccountSecurity lowest) in expected)
        {
            ChatCommand command = table.Find(path)!;
            Assert.True(table.IsAvailable(command, lowest), path);
            if (lowest > AccountSecurity.Player)
            {
                Assert.False(table.IsAvailable(command, lowest - 1), path);
            }
        }

        // The native root is not a retail command name, so no future retail table can claim it.
        Assert.Equal(-1, ArcaneCore.World.Gm.Core.RetailCommandOrder.IndexOf("arcane"));
        Assert.NotNull(table.Find("arcane bancheck"));
        Assert.NotNull(table.Find("arcane mutes"));
        Assert.NotNull(table.Find("arcane gmlog"));
        Assert.NotNull(table.Find("arcane queues"));
    }

    [Fact]
    public async Task APlayer_CannotRunAnyOfThem_AndTheyStayOutOfTheirCommandList()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        foreach (string command in new[] { ".pinfo", ".mute 5", ".unmute", ".gmannounce hi", ".gmnotify hi", ".gm list", ".gm ingame", ".arcane mutes", ".arcane gmlog", ".arcane queues", ".arcane bancheck ip 1.2.3.4" })
        {
            Assert.Equal("This command is not available to you.", await ReplyAsync(player, command));
            clock.Advance(1); // vmangos counts command lines toward the one-second chat flood window.
        }

        // Those eleven commands trip the chat flood mute, which would answer .commands with a "You must wait"
        // notification and no list at all; let the mute run out so the list is really read.
        clock.Advance(60);
        await player.SendChatAsync(ChatType.Say, Language.Common, ".commands");
        string[] listed = await player.CollectChatLinesAsync();
        Assert.Contains(listed, line => line.Contains("commands", StringComparison.Ordinal));
        Assert.DoesNotContain(
            listed,
            line => line.Contains("gm", StringComparison.Ordinal) || line.Contains("arcane", StringComparison.Ordinal) || line.Contains("ticket", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlayerCommands_CountTowardFlood_AndAChatCommandCannotBypassTheMute()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await player.CollectAsync();

        for (int i = 0; i < 11; i++)
        {
            Assert.Equal("This command is not available to you.", await ReplyAsync(player, ".pinfo"));
        }

        await player.SendChatAsync(ChatType.Say, Language.Common, ".pinfo");
        Assert.Equal("You must wait 10 Seconds. before speaking again.",
            NotificationOf(await player.ReadUntilAsync(WorldOpcode.SmsgNotification)));

        // vmangos parses whisper commands before its receiver-specific mute gate.
        Assert.Equal("This command is not available to you.",
            await WhisperedReplyAsync(player, ".pinfo", "Nobody"));

        clock.Advance(10);
        Assert.Equal("This command is not available to you.", await ReplyAsync(player, ".pinfo"));
    }

    // ---- .mute / .unmute ----

    [Fact]
    public async Task Mute_SilencesTheAccount_TellsBothSides_AndPersists()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await mod.CollectAsync();
        await target.CollectAsync();
        await listener.CollectAsync();

        Assert.Equal($"You have disabled {Link("Targetone")}'s chat for 30 Minutes.", await ReplyAsync(mod, ".mute targetone 30m spamming the trade channel"));
        Assert.Equal("Your chat has been disabled for 30 Minutes. By: Moddy, Reason: spamming the trade channel.", (await target.ReadChatAsync()).Text);

        await target.SendChatAsync(ChatType.Say, Language.Common, "can anyone hear me");
        Assert.StartsWith("You must wait 30 Minutes", NotificationOf(await target.ReadUntilAsync(WorldOpcode.SmsgNotification)), StringComparison.Ordinal);
        Assert.DoesNotContain(await listener.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        InMemoryGmAuditStore store = host.WorldServices.GetRequiredService<InMemoryGmAuditStore>();
        int account = await AccountIdAsync(host, "TARGET");
        await WorldTestHost.WaitForAsync(() => store.Mute(account) is not null, "the mute to reach storage");
        AccountMuteRecord row = store.Mute(account)!;
        Assert.Equal((clock.UnixNow + 1800, "Moddy", (byte)AccountSecurity.Moderator, "spamming the trade channel"), (row.MutedUntil, row.MutedBy, row.MutedBySecurity, row.Reason));

        clock.Advance(1800);   // the mute is over: they speak again
        await target.SendChatAsync(ChatType.Say, Language.Common, "free again");
        Assert.Equal("free again", (await listener.ReadChatAsync()).Text);
    }

    [Theory]
    [InlineData("45", "45 Minutes")]          // a bare number is minutes
    [InlineData("2h", "2 Hours")]
    [InlineData("1d12h", "1 Day 12 Hours")]
    public async Task Mute_DurationGrammar(string duration, string shown)
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await mod.CollectAsync();

        Assert.Equal($"You have disabled {Link("Targetone")}'s chat for {shown}.", await ReplyAsync(mod, $".mute targetone {duration}"));
        Assert.Contains("Reason: No reason given.", (await target.ReadChatAsync()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mute_WithoutAName_UsesTheSelectedPlayer()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await mod.CollectAsync();
        ObjectGuid guid = (await host.PlayerAsync("Targetone")).Guid;
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Moddy")!.Selection = guid);

        Assert.Equal($"You have disabled {Link("Targetone")}'s chat for 10 Minutes.", await ReplyAsync(mod, ".mute 10"));
        Assert.NotNull(host.WorldServices.GetRequiredService<GmAuditFeature>().MuteOf(await AccountIdAsync(host, "TARGET")));
    }

    [Theory]
    [InlineData(".mute")]                         // nothing at all
    [InlineData(".mute targetone")]               // no duration
    [InlineData(".mute targetone soon")]          // not a duration
    [InlineData(".mute targetone 0")]             // zero
    [InlineData(".mute targetone 366d")]          // beyond a year
    [InlineData(".mute targetone 1h30")]          // a trailing number without unit
    public async Task Mute_BadArguments_ShowTheSyntax_AndMuteNobody(string command)
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await mod.CollectAsync();

        Assert.StartsWith("Syntax: .mute", await ReplyAsync(mod, command), StringComparison.Ordinal);
        Assert.Null(host.WorldServices.GetRequiredService<GmAuditFeature>().MuteOf(await AccountIdAsync(host, "TARGET")));
    }

    [Fact]
    public async Task Mute_UnknownOrOfflinePlayer_SaysPlayerNotFound()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await mod.CollectAsync();

        Assert.Equal("Player not found!", await ReplyAsync(mod, ".mute nobodyhere 10m"));
    }

    [Fact]
    public async Task Mute_RefusesAStaffTargetOfEqualOrHigherSecurity_ButNotAStrictlyLowerOne()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient mod2 = await host.EnterWorldAsync("MODTWO", "Modtwo", AccountSecurity.Moderator);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await mod.CollectAsync();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();

        Assert.Equal("You have low security level for this.", await ReplyAsync(mod, ".mute modtwo 10m"));   // strong check: equal is refused too
        Assert.Equal("You have low security level for this.", await ReplyAsync(mod, ".mute gmaaa 10m"));
        Assert.Null(audit.MuteOf(await AccountIdAsync(host, "MODTWO")));
        Assert.Null(audit.MuteOf(await AccountIdAsync(host, "GMA")));

        await gm.CollectAsync();
        Assert.StartsWith("You have disabled", await ReplyAsync(gm, ".mute modtwo 10m"));                  // GameMaster over Moderator is fine
        Assert.NotNull(audit.MuteOf(await AccountIdAsync(host, "MODTWO")));
    }

    [Fact]
    public async Task Mute_ALowerStaffMemberCannotReplaceOrLiftAMuteSetByHigherStaff()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADM", "Admiral", AccountSecurity.Administrator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await mod.CollectAsync();
        await admin.CollectAsync();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        int account = await AccountIdAsync(host, "TARGET");

        Assert.StartsWith("You have disabled", await ReplyAsync(admin, ".mute targetone 1d harassment"));
        Assert.Equal(GmAuditStrings.MuteSetByHigher, await ReplyAsync(mod, ".mute targetone 1m"));     // would shorten it
        Assert.Equal("You have low security level for this.", await ReplyAsync(mod, ".unmute targetone"));
        Assert.Equal(86400, audit.MuteOf(account)!.MutedUntil - audit.NowUnixSeconds);

        Assert.Equal($"You have enabled {Link("Targetone")}'s chat.", await ReplyAsync(admin, ".unmute targetone"));
        Assert.Null(audit.MuteOf(account));
    }

    /// <summary>
    /// A command sent as a WHISPER: the mute gate of the chat handler does not apply to whispers, and the command table is
    /// reached before the whisper is delivered, so this is how a muted staff member can still type commands.
    /// </summary>
    private static async Task<string> WhisperedReplyAsync(WorldTestClient client, string command, string to)
    {
        await client.SendChatAsync(ChatType.Whisper, Language.Common, command, target: to);
        return (await client.ReadChatAsync()).Text;
    }

    [Fact]
    public async Task Mute_AMutedStaffMember_CannotLiftOrShortenTheirOwnMuteSetByHigherStaff()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADM", "Admiral", AccountSecurity.Administrator);
        await mod.CollectAsync();
        await admin.CollectAsync();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        int account = await AccountIdAsync(host, "MOD");

        Assert.StartsWith("You have disabled", await ReplyAsync(admin, ".mute moddy 1d abusing players"));
        Assert.StartsWith("Your chat has been disabled for 1 Day", (await mod.ReadChatAsync()).Text, StringComparison.Ordinal);

        // Saying anything, a command line included, is stopped by the mute ...
        await mod.SendChatAsync(ChatType.Say, Language.Common, ".unmute");
        Assert.StartsWith("You must wait 1 Day", NotificationOf(await mod.ReadUntilAsync(WorldOpcode.SmsgNotification)), StringComparison.Ordinal);
        Assert.NotNull(audit.MuteOf(account));

        // ... but a whispered command line runs, with the invoker as the target: the mute's author still outranks them.
        Assert.Equal("You have low security level for this.", await WhisperedReplyAsync(mod, ".unmute", "Admiral"));
        Assert.Equal(GmAuditStrings.MuteSetByHigher, await WhisperedReplyAsync(mod, ".mute 1s", "Admiral"));
        Assert.Equal(GmAuditStrings.MuteSetByHigher, await WhisperedReplyAsync(mod, ".mute moddy 1s", "Admiral"));
        Assert.Equal(86400, audit.MuteOf(account)!.MutedUntil - audit.NowUnixSeconds);
        Assert.DoesNotContain(await admin.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);   // the whispers never arrived as whispers
    }

    [Fact]
    public async Task Unmute_ClearsTheFloodMuteToo_AndSaysAlreadyEnabledOnlyWhenItIs()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener");
        await gm.CollectAsync();
        await target.CollectAsync();
        await listener.CollectAsync();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        int account = await AccountIdAsync(host, "TARGET");

        // The chat lane's own anti-flood mute, not an account mute: GmAuditFeature knows nothing of it.
        await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ChatFeature>().MuteUntil(host.World.FindOnlinePlayer("Targetone")!, clock.UnixNow + 600));
        await target.SendChatAsync(ChatType.Say, Language.Common, "anyone?");
        Assert.StartsWith("You must wait 10 Minutes", NotificationOf(await target.ReadUntilAsync(WorldOpcode.SmsgNotification)), StringComparison.Ordinal);
        Assert.Null(audit.MuteOf(account));
        Assert.Equal("GM mode: off Chat: muted for 10 Minutes (anti-flood) Ticket: none", (await LinesAsync(gm, ".pinfo targetone"))[3]);

        Assert.Equal($"You have enabled {Link("Targetone")}'s chat.", await ReplyAsync(gm, ".unmute targetone"));
        Assert.Equal("Your chat has been enabled.", (await target.ReadChatAsync()).Text);

        await target.SendChatAsync(ChatType.Say, Language.Common, "free again");
        Assert.Equal("free again", (await listener.ReadChatAsync()).Text);
        Assert.Equal("GM mode: off Chat: enabled Ticket: none", (await LinesAsync(gm, ".pinfo targetone"))[3]);
        Assert.Equal("Player's chat is already enabled.", await ReplyAsync(gm, ".unmute targetone"));
    }

    [Fact]
    public async Task Unmute_LiftsTheMute_TellsBothSides_AndDeletesTheRow()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await mod.CollectAsync();
        InMemoryGmAuditStore store = host.WorldServices.GetRequiredService<InMemoryGmAuditStore>();
        int account = await AccountIdAsync(host, "TARGET");

        Assert.Equal("Player's chat is already enabled.", await ReplyAsync(mod, ".unmute targetone"));
        Assert.StartsWith("You have disabled", await ReplyAsync(mod, ".mute targetone 10m"));
        await target.CollectAsync();
        await WorldTestHost.WaitForAsync(() => store.Mute(account) is not null, "the mute row");

        Assert.Equal($"You have enabled {Link("Targetone")}'s chat.", await ReplyAsync(mod, ".unmute targetone"));
        Assert.Equal("Your chat has been enabled.", (await target.ReadChatAsync()).Text);
        await WorldTestHost.WaitForAsync(() => store.Mute(account) is null, "the mute row to go");
        Assert.Equal("Player's chat is already enabled.", await ReplyAsync(mod, ".unmute targetone"));
    }

    [Fact]
    public async Task Unmute_WorksOnAnOfflineCharacter_ByTheAccountOfTheName()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient admin = await host.EnterWorldAsync("ADM", "Admiral", AccountSecurity.Administrator);
        byte[] key = await host.AddAccountAsync("SLEEPER");
        await using WorldTestClient sleeper = await host.ConnectAsync();
        await sleeper.AuthenticateAsync("SLEEPER", key);
        await sleeper.CreateCharacterAsync("Sleepyhead");   // created, never logged in: offline
        await admin.CollectAsync();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        int account = await AccountIdAsync(host, "SLEEPER");
        audit.Mute(account, 3600, "Someone", AccountSecurity.Moderator, "left muted");

        Assert.Equal($"You have enabled {Link("Sleepyhead")}'s chat.", await ReplyAsync(admin, ".unmute sleepyhead"));
        Assert.Null(audit.MuteOf(account));
        Assert.Equal("Player not found!", await ReplyAsync(admin, ".unmute nosuchperson"));
        Assert.Equal("Player's chat is already enabled.", await ReplyAsync(admin, ".unmute sleepyhead"));
    }

    [Fact]
    public async Task AMute_IsLoadedAtStartup_SoARestartDoesNotForgetIt()
    {
        var clock = new Clock();
        var store = new InMemoryGmAuditStore();
        store.Seed(new AccountMuteRecord(1, clock.UnixNow + 600, clock.UnixNow - 10, "Earlier", (byte)AccountSecurity.GameMaster, "from before the restart"));
        store.Seed(new AccountMuteRecord(2, clock.UnixNow - 5, clock.UnixNow - 100, "Earlier", (byte)AccountSecurity.GameMaster, "already over"));
        await using WorldTestHost host = Start(clock, services => services.AddSingleton<IGmAuditStore>(store));
        await using WorldTestClient muted = await host.EnterWorldAsync("MUTED", "Mutedone");   // account id 1
        await using WorldTestClient listener = await host.EnterWorldAsync("LISTENER", "Listener"); // account id 2: its mute ended
        await muted.CollectAsync();
        await listener.CollectAsync();

        await muted.SendChatAsync(ChatType.Say, Language.Common, "hello?");
        Assert.StartsWith("You must wait 10 Minutes", NotificationOf(await muted.ReadUntilAsync(WorldOpcode.SmsgNotification)), StringComparison.Ordinal);

        await listener.SendChatAsync(ChatType.Say, Language.Common, "I am not muted");
        Assert.Equal("I am not muted", (await listener.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task AnExpiredMute_IsForgottenAndItsRowDeleted_WithoutAnyoneAskingAboutIt()
    {
        var clock = new Clock();
        await using WorldTestHost host = Start(clock);
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await mod.CollectAsync();
        InMemoryGmAuditStore store = host.WorldServices.GetRequiredService<InMemoryGmAuditStore>();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        int account = await AccountIdAsync(host, "TARGET");

        Assert.StartsWith("You have disabled", await ReplyAsync(mod, ".mute targetone 30m spam"));
        await WorldTestHost.WaitForAsync(() => store.Mute(account) is not null, "the mute row");
        Assert.Equal(1, audit.RememberedMutes);

        // The mute runs out. Nobody speaks, nobody runs .pinfo or .arcane mutes: the world tick alone ends it.
        clock.Advance(1800);
        await WorldTestHost.WaitForAsync(() => store.Mute(account) is null, "the expired mute row to be deleted");
        Assert.Equal(0, audit.RememberedMutes);
    }

    [Fact]
    public async Task ExpiredMuteRows_LeftByAnEarlierRun_AreDeletedAtStartup_AndActiveOnesKept()
    {
        var clock = new Clock();
        var store = new InMemoryGmAuditStore();
        store.Seed(new AccountMuteRecord(71, clock.UnixNow + 600, clock.UnixNow - 10, "Earlier", (byte)AccountSecurity.GameMaster, "still running"));
        store.Seed(new AccountMuteRecord(72, clock.UnixNow - 5, clock.UnixNow - 100, "Earlier", (byte)AccountSecurity.GameMaster, "already over"));
        await using WorldTestHost host = Start(clock, services => services.AddSingleton<IGmAuditStore>(store));

        await WorldTestHost.WaitForAsync(() => store.Mute(72) is null, "the expired row to be deleted");
        Assert.NotNull(store.Mute(71));
        Assert.Equal(1, host.WorldServices.GetRequiredService<GmAuditFeature>().RememberedMutes);
    }

    [Fact]
    public async Task Mute_StorageFailing_StillMutes_AndTheWriteIsRetainedNotLost()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await mod.CollectAsync();
        InMemoryGmAuditStore store = host.WorldServices.GetRequiredService<InMemoryGmAuditStore>();
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        int account = await AccountIdAsync(host, "TARGET");
        store.FailNext(3);

        Assert.StartsWith("You have disabled", await ReplyAsync(mod, ".mute targetone 10m"));
        await audit.Writes.FlushAsync();

        Assert.NotNull(audit.MuteOf(account));                  // staff and chat see the mute at once
        Assert.Equal([$"mute:{account}"], audit.Writes.RetainedKeys);
        Assert.Null(store.Mute(account));

        await audit.Writes.RetryRetainedAsync();
        Assert.NotNull(store.Mute(account));
        Assert.Empty(audit.Writes.RetainedKeys);
    }

    // ---- .pinfo ----

    [Fact]
    public async Task Pinfo_ShowsAnOnlinePlayer_WithItsMuteAndTicketState()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await gm.CollectAsync();
        int account = await AccountIdAsync(host, "TARGET");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Targetone")!.Money = 123456);

        string[] lines = await LinesAsync(gm, ".pinfo targetone");

        Assert.Equal(4, lines.Length);
        Assert.StartsWith($"Player {Link("Targetone")} (online, guid: 2) Account id: {account} Security: Player", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Level: 1 Played time: ", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("Money: 12g 34s 56c", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Map: ", lines[2], StringComparison.Ordinal);
        Assert.Equal("GM mode: off Chat: enabled Ticket: none", lines[3]);

        host.WorldServices.GetRequiredService<GmAuditFeature>().Mute(account, 7200, "Gmaaa", AccountSecurity.GameMaster, "ads");
        host.WorldServices.GetRequiredService<GmAuditFeature>().CreateTicket(2, "stuck", 1, 0, 0, 0, 0);
        lines = await LinesAsync(gm, ".pinfo targetone");
        Assert.Equal("GM mode: off Chat: muted for 2 Hours by Gmaaa (ads) Ticket: #1", lines[3]);
    }

    [Fact]
    public async Task Pinfo_WithoutAnArgument_DescribesTheSelection_ElseTheInvoker()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient target = await host.EnterWorldAsync("TARGET", "Targetone");
        await gm.CollectAsync();

        Assert.StartsWith($"Player {Link("Gmaaa")} (online, guid: 1)", (await LinesAsync(gm, ".pinfo"))[0], StringComparison.Ordinal);
        ObjectGuid guid = (await host.PlayerAsync("Targetone")).Guid;
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Gmaaa")!.Selection = guid);
        Assert.StartsWith($"Player {Link("Targetone")} (online, guid: 2)", (await LinesAsync(gm, ".pinfo"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pinfo_OfAnOfflineCharacter_UsesTheDirectory_AndDoesNotInventAnAccountSecurity()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        byte[] key = await host.AddAccountAsync("SLEEPER");
        await using WorldTestClient sleeper = await host.ConnectAsync();
        await sleeper.AuthenticateAsync("SLEEPER", key);
        await sleeper.CreateCharacterAsync("Sleepyhead");
        await gm.CollectAsync();
        int account = await AccountIdAsync(host, "SLEEPER");

        string[] lines = await LinesAsync(gm, ".pinfo sleepyhead");

        Assert.Equal($"Player {Link("Sleepyhead")} (offline, guid: 2) Account id: {account}", lines[0]);
        Assert.StartsWith("Level: 1 Zone: ", lines[1], StringComparison.Ordinal);
        Assert.Equal("Chat: enabled Ticket: none", lines[2]);
        Assert.Equal("Player not found!", await ReplyAsync(gm, ".pinfo nosuchperson"));
    }

    // ---- .gmannounce / .gmnotify ----

    [Fact]
    public async Task GmAnnounce_ReachesStaffOnly()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADM", "Admiral", AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await mod.CollectAsync();
        await admin.CollectAsync();
        await player.CollectAsync();

        await mod.SendChatAsync(ChatType.Say, Language.Common, ".gmannounce server restart at noon");

        Assert.Equal("|cff00ccff[GM] Moddy:|r server restart at noon", (await mod.ReadChatAsync()).Text);
        Assert.Equal("|cff00ccff[GM] Moddy:|r server restart at noon", (await admin.ReadChatAsync()).Text);
        Assert.DoesNotContain(await player.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);
        Assert.StartsWith("Syntax: .gmannounce", await ReplyAsync(mod, ".gmannounce"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GmNotify_ReachesStaffOnly_AsAScreenNotification()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADM", "Admiral", AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await mod.CollectAsync();
        await admin.CollectAsync();
        await player.CollectAsync();

        await mod.SendChatAsync(ChatType.Say, Language.Common, ".gmnotify queue is long");

        Assert.Equal("GM notify: queue is long", NotificationOf(await admin.ReadUntilAsync(WorldOpcode.SmsgNotification)));
        Assert.DoesNotContain(await player.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgNotification);
        Assert.StartsWith("Syntax: .gmnotify", await ReplyAsync(mod, ".gmnotify"), StringComparison.Ordinal);
    }

    // ---- .gm ingame / .gm list ----

    [Fact]
    public async Task GmIngame_ListsPlayersInGmMode_WithTheirWhisperState_ForModerators()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain", AccountSecurity.Moderator);
        await gm.CollectAsync();
        await player.CollectAsync();

        Assert.Equal(["There are no GMs currently logged in on this server."], await LinesAsync(player, ".gm ingame"));   // staff with GM mode off are not listed

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Gmaaa")!.SetGameMaster(true));
        await gm.CollectAsync();
        string[] lines = await LinesAsync(player, ".gm ingame");
        Assert.Equal("There are the following active GMs on this server:", lines[0]);
        Assert.Equal($"{Link("Gmaaa")} - does not accept whispers", lines[1]);   // the default for staff accounts (Chat:GmWhisperingTo)
    }

    [Fact]
    public async Task GmList_ShowsEveryStaffMemberOnline_ForAdministratorsOnly()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient mod = await host.EnterWorldAsync("MOD", "Moddy", AccountSecurity.Moderator);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("ADM", "Admiral", AccountSecurity.Administrator);
        await using WorldTestClient player = await host.EnterWorldAsync("PLAIN", "Plain");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Gmaaa")!.SetGameMaster(true));
        await mod.CollectAsync();
        await gm.CollectAsync();
        await admin.CollectAsync();

        Assert.Equal("This command is not available to you.", await ReplyAsync(gm, ".gm list"));
        string[] lines = await LinesAsync(admin, ".gm list");

        Assert.Equal(
            ["Staff online:", $"{Link("Admiral")} - Administrator", $"{Link("Gmaaa")} - GameMaster (GM mode on)", $"{Link("Moddy")} - Moderator"],
            lines);
    }

    [Fact]
    public async Task ExistingGmSubcommands_StillWork_AfterTheExtension()
    {
        await using WorldTestHost host = Start(new Clock());
        await using WorldTestClient gm = await host.EnterWorldAsync("GMA", "Gmaaa", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm on");
        await WorldTestHost.WaitForAsync(() => host.World.FindOnlinePlayer("Gmaaa")?.IsGameMaster == true, "GM mode on");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm chat on");
        await WorldTestHost.WaitForAsync(() => host.World.FindOnlinePlayer("Gmaaa")?.GmChat == true, "GM chat on");
    }
}

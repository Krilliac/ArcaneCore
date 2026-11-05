using ArcaneCore.Game;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Cross-worker chat through the real handlers (S2c): two <see cref="WorldTestHost"/> worlds ("worker-a", "worker-b") share one presence directory and social router, each with its
/// own <see cref="ClusterChatRouting"/> registered the way the cluster hosting does. A chat packet sent by a client on worker A must arrive as SMSG bytes on a client of worker B
/// (and the whisper's answer back on A). The pump is driven by the test (it is a background timer in production). Presence is published by the test, as the publisher would.
/// </summary>
public sealed class CrossWorkerChatGameplayTests : IAsyncLifetime
{
    private const string GatewayId = "gateway";
    private const string WorkerA = "worker-a";
    private const string WorkerB = "worker-b";

    private readonly InMemoryRealmPresenceDirectory _directory = new();
    private readonly InMemoryClusterSocialRouter _router;
    private readonly ClusterChatRouting _routingA;
    private readonly ClusterChatRouting _routingB;
    private readonly WorldTestHost _hostA;
    private readonly WorldTestHost _hostB;
    private readonly List<WorldTestClient> _clients = [];
    private long _version;

    public CrossWorkerChatGameplayTests()
    {
        _router = new InMemoryClusterSocialRouter(_directory);
        _routingA = new ClusterChatRouting(_router, _directory, WorkerA, GatewayId, static _ => 1);
        _routingB = new ClusterChatRouting(_router, _directory, WorkerB, GatewayId, static _ => 1);
        _hostA = WorldTestHost.Start(configureServices: services => services.AddSingleton(_routingA));
        _hostB = WorldTestHost.Start(configureServices: services => services.AddSingleton(_routingB));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        foreach (WorldTestClient client in _clients)
        {
            await client.DisposeAsync();
        }

        await _hostA.DisposeAsync();
        await _hostB.DisposeAsync();
        await _routingA.DisposeAsync();
        await _routingB.DisposeAsync();
    }

    private async Task<WorldTestClient> EnterAsync(WorldTestHost host, string account, string name, AccountSecurity security = AccountSecurity.Player, byte race = 1)
    {
        WorldTestClient client = await host.EnterWorldAsync(account, name, security, race);
        _clients.Add(client);
        return client;
    }

    /// <summary>Burns character guids on worker B so its characters do not share a guid with worker A's (each in-memory store counts from 1).</summary>
    private async Task SkipGuidsOnBAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            await EnterAsync(_hostB, "FILLER" + (char)('A' + i), "Filler" + (char)('a' + i));
        }
    }

    private async Task<ulong> PublishAsync(WorldTestHost host, string worker, string name, int guildId = 0, byte security = 0, bool gmInvisible = false)
    {
        ulong guid = await host.PlayerStateAsync(name, p => p.Guid.Value);
        byte race = await host.PlayerStateAsync(name, p => (byte)p.Race);
        int accountId = await host.PlayerStateAsync(name, p => p.AccountId); // the router checks the sender's presence against the session's real account id
        var entry = new RealmPresenceEntry(
            guid, name, accountId, worker, GatewayId, 0, 12, 0, 10, 1, race, 0, guildId, 0, ++_version, 1, DateTimeOffset.UtcNow, Security: security, GmInvisible: gmInvisible);
        Assert.Equal(PresenceMutationResult.Applied, _directory.TryUpsert(entry, out _));
        return guid;
    }

    private static async Task<int> PumpAsync(ClusterChatRouting routing)
    {
        int handed = 0;
        await WorldTestHost.WaitForAsync(() =>
        {
            handed += routing.PumpOnce();
            return handed > 0;
        }, "a chat delivery for the worker");
        return handed;
    }

    private static async Task<ChatMessage> ReadChatOfAsync(WorldTestClient client, ChatType type)
    {
        while (true)
        {
            ChatMessage message = await client.ReadChatAsync();
            if (message.Type == type)
            {
                return message;
            }
        }
    }

    private static async Task<byte[]> ReadNotifyAsync(WorldTestClient client, ChatNotify type)
    {
        while (true)
        {
            byte[] notify = await client.ReadUntilAsync(WorldOpcode.SmsgChannelNotify);
            if (notify[0] == (byte)type)
            {
                return notify;
            }
        }
    }

    private static byte[] CString(string value)
    {
        var writer = new PacketWriter(value.Length + 1);
        writer.WriteCString(value);
        return writer.ToArray();
    }

    private static async Task<string> CommandAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await ReadChatOfAsync(client, ChatType.System);
        return reply.Text;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Whisper_ReachesTheOtherWorker_AndTheAwayOrBusyReplyComesBack(bool busy)
    {
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        await SkipGuidsOnBAsync(1);
        WorldTestClient bob = await EnterAsync(_hostB, "BOB", "Bob");
        ulong aliceGuid = await PublishAsync(_hostA, WorkerA, "Alice");
        ulong bobGuid = await PublishAsync(_hostB, WorkerB, "Bob");
        Assert.NotEqual(aliceGuid, bobGuid);
        await _hostB.OnWorldAsync(() =>
        {
            Player player = _hostB.World.FindOnlinePlayer("Bob")!;
            player.Flags |= busy ? PlayerFlags.Dnd : PlayerFlags.Afk;
            if (busy)
            {
                player.DndMessage = "working";
            }
            else
            {
                player.AfkMessage = "brb";
            }
        });
        await alice.CollectAsync();
        await bob.CollectAsync();

        await alice.SendChatAsync(ChatType.Whisper, Language.Common, "hello over there", target: "Bob");
        await PumpAsync(_routingB);
        ChatMessage heard = await ReadChatOfAsync(bob, ChatType.Whisper);
        Assert.Equal((aliceGuid, "hello over there"), (heard.Sender, heard.Text));

        await PumpAsync(_routingA);
        ChatMessage inform = await ReadChatOfAsync(alice, ChatType.WhisperInform);
        Assert.Equal((bobGuid, "hello over there"), (inform.Sender, inform.Text));
        ChatMessage auto = await ReadChatOfAsync(alice, busy ? ChatType.Dnd : ChatType.Afk);
        Assert.Equal((bobGuid, busy ? "working" : "brb"), (auto.Sender, auto.Text));
    }

    [Fact]
    public async Task Whisper_ToAnUnknownName_AnswersPlayerNotFound()
    {
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        await PublishAsync(_hostA, WorkerA, "Alice");
        await alice.CollectAsync();

        await alice.SendChatAsync(ChatType.Whisper, Language.Common, "anyone?", target: "Nobody");

        Assert.Equal("Nobody", new PacketReader(await alice.ReadUntilAsync(WorldOpcode.SmsgChatPlayerNotFound)).ReadCString());
    }

    [Fact]
    public async Task Whisper_ToAStaffMemberOnAnotherWorker_ReadsAsOffline_AndToAnotherFactionIsRefused()
    {
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        await SkipGuidsOnBAsync(1);
        await EnterAsync(_hostB, "STAFF", "Staff", AccountSecurity.GameMaster);
        await EnterAsync(_hostB, "ORC", "Gruk", race: 2);
        await PublishAsync(_hostA, WorkerA, "Alice");
        await PublishAsync(_hostB, WorkerB, "Staff");
        await PublishAsync(_hostB, WorkerB, "Gruk");
        await alice.CollectAsync();

        // Staff do not accept whispers by default: the same notice as for an offline name.
        await alice.SendChatAsync(ChatType.Whisper, Language.Common, "hello?", target: "Staff");
        await PumpAsync(_routingB);
        await PumpAsync(_routingA);
        Assert.Equal("Staff", new PacketReader(await alice.ReadUntilAsync(WorldOpcode.SmsgChatPlayerNotFound)).ReadCString());

        await alice.SendChatAsync(ChatType.Whisper, Language.Common, "for the alliance", target: "Gruk");
        await PumpAsync(_routingB);
        await PumpAsync(_routingA);
        await alice.ReadUntilAsync(WorldOpcode.SmsgChatWrongFaction);
    }

    private static async Task<List<string>> WhoAsync(WorldTestClient client)
    {
        var who = new PacketWriter(64);
        who.WriteUInt32(0);
        who.WriteUInt32(100);
        who.WriteCString(string.Empty);
        who.WriteCString(string.Empty);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32(0xFFFFFFFF);
        who.WriteUInt32(0);
        who.WriteUInt32(0);

        await client.SendAsync(WorldOpcode.CmsgWho, who.ToArray());
        var reader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgWho));
        uint listed = reader.ReadUInt32();
        reader.ReadUInt32();
        var names = new List<string>();
        for (int i = 0; i < listed; i++)
        {
            names.Add(reader.ReadCString());
            reader.ReadCString();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    [Fact]
    public async Task Who_ForAPlainPlayer_ListsOtherWorkersPlayers_AndHidesAnInvisibleGm()
    {
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        await SkipGuidsOnBAsync(1);
        await EnterAsync(_hostB, "BOB", "Bob");
        await EnterAsync(_hostB, "SEER", "Seer", AccountSecurity.GameMaster);
        await EnterAsync(_hostB, "KEEPER", "Keeper", AccountSecurity.GameMaster);
        await PublishAsync(_hostA, WorkerA, "Alice");
        await PublishAsync(_hostB, WorkerB, "Bob");
        await PublishAsync(_hostB, WorkerB, "Seer", security: (byte)AccountSecurity.GameMaster);
        await PublishAsync(_hostB, WorkerB, "Keeper", security: (byte)AccountSecurity.GameMaster, gmInvisible: true);
        await alice.CollectAsync();

        // No staff gate: a plain player sees Bob and the visible GM of the other worker, not the GM who is hidden.
        Assert.Equal(["Alice", "Bob", "Seer"], await WhoAsync(alice));
    }

    [Fact]
    public void RemoteWhoVisible_AppliesGmLevelInWhoList_Faction_AndGmVisibility()
    {
        var options = new WorldRuntimeOptions { GmLevelInWhoList = AccountSecurity.Moderator };
        RealmPresenceEntry Entry(byte race, byte security, bool hidden) => new(
            9, "Other", 1009, WorkerB, GatewayId, 0, 12, 0, 10, 1, race, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow, Security: security, GmInvisible: hidden);

        Assert.True(ChatHandlers.RemoteWhoVisible(Entry(1, 0, false), AccountSecurity.Player, Team.Alliance, options));
        Assert.False(ChatHandlers.RemoteWhoVisible(Entry(2, 0, false), AccountSecurity.Player, Team.Alliance, options), "other faction");
        options.AllowTwoSideWhoList = true;
        Assert.True(ChatHandlers.RemoteWhoVisible(Entry(2, 0, false), AccountSecurity.Player, Team.Alliance, options));

        Assert.True(ChatHandlers.RemoteWhoVisible(Entry(1, 1, false), AccountSecurity.Player, Team.Alliance, options), "staff up to GmLevelInWhoList");
        Assert.False(ChatHandlers.RemoteWhoVisible(Entry(1, 2, false), AccountSecurity.Player, Team.Alliance, options), "staff above GmLevelInWhoList");
        Assert.True(ChatHandlers.RemoteWhoVisible(Entry(1, 2, false), AccountSecurity.Moderator, Team.Alliance, options));

        Assert.False(ChatHandlers.RemoteWhoVisible(Entry(1, 1, true), AccountSecurity.Player, Team.Alliance, options), "hidden GM");
        Assert.True(ChatHandlers.RemoteWhoVisible(Entry(1, 2, true), AccountSecurity.GameMaster, Team.Alliance, options), "an asker of at least its security still sees it");
        Assert.False(ChatHandlers.RemoteWhoVisible(Entry(1, 2, true), AccountSecurity.Moderator, Team.Alliance, options));
    }

    [Fact]
    public async Task GuildChat_ReachesAGuildMemberOnAnotherWorker()
    {
        WorldTestClient gmA = await EnterAsync(_hostA, "GMA", "Keepera", AccountSecurity.GameMaster);
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        await SkipGuidsOnBAsync(2);
        WorldTestClient gmB = await EnterAsync(_hostB, "GMB", "Keeperb", AccountSecurity.GameMaster);
        WorldTestClient bob = await EnterAsync(_hostB, "BOB", "Bob");
        await _hostA.WorldServices.GetRequiredService<SocialFeature>().GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));
        await _hostB.WorldServices.GetRequiredService<SocialFeature>().GuildsLoaded.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Guild Arcane created.", await CommandAsync(gmA, ".guild create Alice \"Arcane\""));
        Assert.Equal("Guild Arcane created.", await CommandAsync(gmB, ".guild create Bob \"Arcane\""));
        int guildId = await _hostA.PlayerStateAsync("Alice", p => ((WorldSession)p.Session).Services.GetRequiredService<SocialFeature>().Context.Guilds.GetGuildOf(p)!.Id);
        ulong aliceGuid = await PublishAsync(_hostA, WorkerA, "Alice", guildId);
        await PublishAsync(_hostB, WorkerB, "Bob", guildId);
        await alice.CollectAsync();
        await bob.CollectAsync();

        await alice.SendChatAsync(ChatType.Guild, Language.Common, "welcome to arcane");
        await PumpAsync(_routingB);

        ChatMessage heard = await ReadChatOfAsync(bob, ChatType.Guild);
        Assert.Equal((aliceGuid, "welcome to arcane"), (heard.Sender, heard.Text));
    }

    [Fact]
    public async Task ChannelJoin_FromAClient_ReachesTheOtherWorkersMembers_AndLeavingStopsIt()
    {
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        await SkipGuidsOnBAsync(1);
        WorldTestClient bob = await EnterAsync(_hostB, "BOB", "Bob");
        ulong aliceGuid = await PublishAsync(_hostA, WorkerA, "Alice");
        await PublishAsync(_hostB, WorkerB, "Bob");
        await alice.CollectAsync();
        await bob.CollectAsync();

        await alice.SendAsync(WorldOpcode.CmsgJoinChannel, [.. CString("Club"), .. CString(string.Empty)]);
        await ReadNotifyAsync(alice, ChatNotify.YouJoined);
        await bob.SendAsync(WorldOpcode.CmsgJoinChannel, [.. CString("club"), .. CString(string.Empty)]);
        await ReadNotifyAsync(bob, ChatNotify.YouJoined);
        // The join handlers run on the world thread; after these barriers the subscriptions are queued, and each sender task runs them in order before any later line.
        await _hostA.OnWorldAsync(() => { });
        await _hostB.OnWorldAsync(() => { });
        await _routingA.FlushAsync();
        await _routingB.FlushAsync();

        await alice.SendChatAsync(ChatType.Channel, Language.Common, "wts a thing", "Club");
        await PumpAsync(_routingB);
        byte[] line = await bob.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        Assert.Equal(ChannelPackets.BuildChannelMessage("Club", Language.Common, new ObjectGuid(aliceGuid), "wts a thing", ChatTag.None), line);

        await bob.SendAsync(WorldOpcode.CmsgLeaveChannel, CString("Club"));
        await ReadNotifyAsync(bob, ChatNotify.YouLeft);
        await _hostB.OnWorldAsync(() => { });
        await _routingB.FlushAsync();
        await alice.SendChatAsync(ChatType.Channel, Language.Common, "anyone left", "Club");
        await _hostA.OnWorldAsync(() => { });
        await _routingA.FlushAsync();
        await _routingB.FlushAsync();
        Assert.Equal(0, _routingB.PumpOnce());
    }

    private async Task<bool> IsOnLocalChannelAsync(WorldTestHost host, string name, string channel)
        => await host.PlayerStateAsync(name, p => host.WorldServices.GetRequiredService<SocialFeature>().Context.Channels.Find(p.Team, channel)?.IsOn(p.Guid) ?? false);

    private async Task JoinClubAsync(WorldTestHost host, ClusterChatRouting routing, WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgJoinChannel, [.. CString("Club"), .. CString(string.Empty)]);
        await ReadNotifyAsync(client, ChatNotify.YouJoined);
        await host.OnWorldAsync(() => { });
        await routing.FlushAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChannelKickOrBan_ByAMemberWithoutTheRightAtTheHolder_StillUnsubscribesTheTargetThere(bool ban)
    {
        // Carol (worker B) owns Club at the holder; Alice owns it only on worker A's local object. Alice's kick or ban of Bob removes him locally but the holder refuses
        // the action (Alice holds no right there), so only the handler's LeaveChannel can take Bob's router subscription away.
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        WorldTestClient bob = await EnterAsync(_hostA, "BOB", "Bob");
        await SkipGuidsOnBAsync(2);
        WorldTestClient carol = await EnterAsync(_hostB, "CAROL", "Carol");
        await PublishAsync(_hostA, WorkerA, "Alice");
        await PublishAsync(_hostA, WorkerA, "Bob");
        await PublishAsync(_hostB, WorkerB, "Carol");
        await JoinClubAsync(_hostB, _routingB, carol);
        await JoinClubAsync(_hostA, _routingA, alice);
        await JoinClubAsync(_hostA, _routingA, bob);

        await alice.SendAsync(ban ? WorldOpcode.CmsgChannelBan : WorldOpcode.CmsgChannelKick, [.. CString("Club"), .. CString("Bob")]);
        // The client's packet reaches the world thread asynchronously, so a bare world-thread barrier can run before the handler is even queued. The kick/ban notice to
        // the actor is sent by the handler itself; once it is read the handler has started, and the barrier below then waits for it to finish.
        await ReadNotifyAsync(alice, ban ? ChatNotify.PlayerBanned : ChatNotify.PlayerKicked);
        await _hostA.OnWorldAsync(() => { });
        await _routingA.FlushAsync();
        Assert.False(await IsOnLocalChannelAsync(_hostA, "Bob", "Club"));

        await carol.SendChatAsync(ChatType.Channel, Language.Common, "anyone?", "Club");
        await _hostB.OnWorldAsync(() => { });
        await _routingB.FlushAsync();

        Assert.Equal(1, await PumpAsync(_routingA)); // Alice only; Bob's router subscription is gone (the line reaches the holder asynchronously, so wait for it)
    }

    [Fact]
    public async Task ChannelJoin_OfACharacterBannedAtTheHolder_IsRefusedLocally_ColdAndWarm()
    {
        WorldTestClient dave = await EnterAsync(_hostA, "DAVE", "Dave");
        await SkipGuidsOnBAsync(1);
        WorldTestClient carol = await EnterAsync(_hostB, "CAROL", "Carol");
        ulong daveGuid = await PublishAsync(_hostA, WorkerA, "Dave");
        ulong carolGuid = await PublishAsync(_hostB, WorkerB, "Carol");
        await JoinClubAsync(_hostB, _routingB, carol);
        Assert.True(_router.ModerateChannel("Club", ChannelModerationAction.Ban, carolGuid, daveGuid));
        await dave.CollectAsync();

        // Cold: worker A has not read the holder's state, so the join is undone once the holder's refusal arrives.
        await dave.SendAsync(WorldOpcode.CmsgJoinChannel, [.. CString("Club"), .. CString(string.Empty)]);
        await ReadNotifyAsync(dave, ChatNotify.Banned);
        await _hostA.OnWorldAsync(() => { });
        Assert.False(await IsOnLocalChannelAsync(_hostA, "Dave", "Club"));

        // Warm: the state is cached now, so the join is refused before any local join.
        await dave.SendAsync(WorldOpcode.CmsgJoinChannel, [.. CString("Club"), .. CString(string.Empty)]);
        await ReadNotifyAsync(dave, ChatNotify.Banned);
        await _hostA.OnWorldAsync(() => { });
        Assert.False(await IsOnLocalChannelAsync(_hostA, "Dave", "Club"));
        Assert.True(_routingA.IsBannedAtHolder("Club", daveGuid));
    }

    [Fact]
    public async Task ChannelSay_OfAHolderMutedSpeaker_IsNotHeardLocallyOrSentOn()
    {
        WorldTestClient alice = await EnterAsync(_hostA, "ALICE", "Alice");
        WorldTestClient bob = await EnterAsync(_hostA, "BOB", "Bob");
        await SkipGuidsOnBAsync(2);
        WorldTestClient carol = await EnterAsync(_hostB, "CAROL", "Carol");
        ulong aliceGuid = await PublishAsync(_hostA, WorkerA, "Alice");
        ulong bobGuid = await PublishAsync(_hostA, WorkerA, "Bob");
        ulong carolGuid = await PublishAsync(_hostB, WorkerB, "Carol");
        await JoinClubAsync(_hostB, _routingB, carol);
        await JoinClubAsync(_hostA, _routingA, alice);
        await JoinClubAsync(_hostA, _routingA, bob);
        Assert.True(_router.ModerateChannel("Club", ChannelModerationAction.Mute, carolGuid, aliceGuid));
        await alice.CollectAsync();
        await bob.CollectAsync();

        // Worker A learns the holder's state when it handles a delivery for the channel.
        await carol.SendChatAsync(ChatType.Channel, Language.Common, "hello", "Club");
        await _hostB.OnWorldAsync(() => { });
        await _routingB.FlushAsync();
        await PumpAsync(_routingA);
        await bob.ReadUntilAsync(WorldOpcode.SmsgMessagechat);
        await alice.ReadUntilAsync(WorldOpcode.SmsgMessagechat); // Carol's "hello": read it now, because waiting for the Muted notify below would skip past it
        Assert.True(_routingA.IsSpeakerBarred("Club", aliceGuid));

        await alice.SendChatAsync(ChatType.Channel, Language.Common, "let me talk", "Club");
        await ReadNotifyAsync(alice, ChatNotify.Muted);
        await _hostA.OnWorldAsync(() => { });
        await _routingA.FlushAsync();
        Assert.Equal(0, _routingB.PumpOnce());
        await bob.SendChatAsync(ChatType.Channel, Language.Common, "ping", "Club");
        byte[] next = await alice.ReadUntilAsync(WorldOpcode.SmsgMessagechat); // Bob's line comes next: Alice's own refused line was never sent
        Assert.Equal(ChannelPackets.BuildChannelMessage("Club", Language.Common, new ObjectGuid(bobGuid), "ping", ChatTag.None), next);
    }
}

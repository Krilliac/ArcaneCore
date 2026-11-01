using ArcaneCore.Game;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Social;
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
public sealed class CrossWorkerChatGameplayTests : IAsyncDisposable
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

    public async ValueTask DisposeAsync()
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

    private async Task<ulong> PublishAsync(WorldTestHost host, string worker, string name, int guildId = 0)
    {
        ulong guid = await host.PlayerStateAsync(name, p => p.Guid.Value);
        byte race = await host.PlayerStateAsync(name, p => (byte)p.Race);
        var entry = new RealmPresenceEntry(
            guid, name, (int)guid + 1000, worker, GatewayId, 0, 12, 0, 10, 1, race, 0, guildId, 0, ++_version, 1, DateTimeOffset.UtcNow);
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
}

using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Group operations across workers (S3a) with two in-process workers: each has its own world, social context and <see cref="ClusterGroupRouting"/>, and both reach one
/// <see cref="PresenceHolderService"/> (its <see cref="GroupAuthority"/> is the table the Social lease holder would run). The tests drive the pumps by hand, so no timers are involved.
/// </summary>
public sealed class CrossWorkerGroupTests : IAsyncLifetime
{
    private const int AliceId = 51;
    private const int BobId = 52;
    private const int CarolId = 53;
    private const string GatewayId = "gateway";

    private sealed class Worker(string id, PresenceHolderService holder) : IAsyncDisposable
    {
        public string Id { get; } = id;

        public WorldRuntime World { get; } = new(
            new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);

        public SocialContext Context { get; private set; } = null!;

        public ClusterGroupRouting Routing { get; } = new(holder, id);

        public void Init(ICharacterLookup lookup)
        {
            Context = new SocialContext(World, lookup, new NullSocialPersistence());
            Context.Groups.Authority = Routing;
            Routing.Deliver = ev => World.Post(() => Context.Groups.ApplyAuthorityEvent(ev));
            World.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await Routing.DisposeAsync();
            World.Dispose();
        }
    }

    private readonly InMemoryRealmPresenceDirectory _directory = new();
    private readonly PresenceHolderService _holder;
    private readonly Worker _a;
    private readonly Worker _b;
    private long _version;

    public CrossWorkerGroupTests()
    {
        _holder = new PresenceHolderService(_directory, new InMemoryClusterSocialRouter(_directory));
        _a = new Worker("worker-a", _holder);
        _b = new Worker("worker-b", _holder);
    }

    public Task InitializeAsync()
    {
        var lookup = new FixedLookup(
            new CharacterInfo(AliceId, 151, "Alice", Race.Human, Class.Warrior),
            new CharacterInfo(BobId, 152, "Bob", Race.Human, Class.Warrior),
            new CharacterInfo(CarolId, 153, "Carol", Race.Human, Class.Warrior));
        _a.Init(lookup);
        _b.Init(lookup);
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    private static Player CreatePlayer(int id, string name, RecordingSession session)
    {
        var record = new CharacterRecord
        {
            Id = id, AccountId = 100 + id, Name = name,
            Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 10, ZoneId = 12,
        };
        return new Player(record, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
    }

    private async Task<(Player Player, RecordingSession Session)> Login(Worker worker, int id, string name)
    {
        var session = new RecordingSession();
        Player player = CreatePlayer(id, name, session);
        await worker.World.InvokeAsync(() =>
        {
            worker.World.AddPlayer(player);
            return true;
        });

        var entry = new RealmPresenceEntry(
            (ulong)id, name, 100 + id, worker.Id, GatewayId, 0, 12, 0, 10, (byte)Class.Warrior, (byte)Race.Human, 0, 0, 0, ++_version, 1, DateTimeOffset.UtcNow);
        Assert.Equal(PresenceMutationResult.Applied, _directory.TryUpsert(entry, out _));
        return (player, session);
    }

    /// <summary>Sends every queued command, then drains and applies the events of both workers; repeated so an answer that triggers a second command (an ignored invite) settles too.</summary>
    private async Task Settle()
    {
        for (int round = 0; round < 3; round++)
        {
            await _a.Routing.FlushAsync();
            await _b.Routing.FlushAsync();
            _a.Routing.PumpOnce();
            _b.Routing.PumpOnce();
            await _a.World.InvokeAsync(() => true);
            await _b.World.InvokeAsync(() => true);
        }
    }

    private static Task On(Worker worker, Action action) => worker.World.InvokeAsync(() =>
    {
        action();
        return true;
    });

    [Fact]
    public async Task InviteAcrossWorkers_AcceptShowsTheGroupListToBoth()
    {
        (Player alice, RecordingSession aliceSession) = await Login(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await Login(_b, BobId, "Bob");

        await On(_a, () => _a.Context.Groups.Invite(alice, "Bob"));
        await Settle();

        Assert.Single(bobSession.Of(WorldOpcode.SmsgGroupInvite));
        byte[] inviteResult = Assert.Single(aliceSession.Of(WorldOpcode.SmsgPartyCommandResult));
        Assert.Equal((uint)PartyResult.Ok, BitConverter.ToUInt32(inviteResult, inviteResult.Length - 4));
        Assert.Null(_a.Context.Groups.GetGroup(alice.Guid));

        await On(_b, () => _b.Context.Groups.Accept(bob));
        await Settle();

        Assert.NotEmpty(aliceSession.Of(WorldOpcode.SmsgGroupList));
        byte[] bobList = bobSession.Of(WorldOpcode.SmsgGroupList).Last();
        Assert.Equal((byte)GroupType.Normal, bobList[0]);
        Assert.Equal(1u, BitConverter.ToUInt32(bobList, 2)); // one other member, Alice

        Group? onA = _a.Context.Groups.GetGroup(alice.Guid);
        Group? onB = _b.Context.Groups.GetGroup(bob.Guid);
        Assert.NotNull(onA);
        Assert.NotNull(onB);
        Assert.Equal(onA!.Id, onB!.Id);
        Assert.Equal(2, onA.MemberCount);
        Assert.Equal(2, onB.MemberCount);
        Assert.Equal(alice.Guid, onA.LeaderGuid);
        Assert.Equal(alice.Guid, onB.LeaderGuid);
        Assert.True(_a.Context.Groups.AreInSameGroup(alice.Guid, bob.Guid));
        Assert.True(_b.Context.Groups.AreInSameGroup(bob.Guid, alice.Guid));
        Assert.Single(_holder.Groups.Groups);
    }

    [Fact]
    public async Task LeaderChange_PropagatesToBothWorkers()
    {
        (Player alice, RecordingSession aliceSession) = await Login(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await Login(_b, BobId, "Bob");
        await On(_a, () => _a.Context.Groups.Invite(alice, "Bob"));
        await Settle();
        await On(_b, () => _b.Context.Groups.Accept(bob));
        await Settle();
        aliceSession.Clear();
        bobSession.Clear();

        await On(_a, () => _a.Context.Groups.SetLeader(alice, bob.Guid));
        await Settle();

        Assert.Equal(bob.Guid, _a.Context.Groups.GetGroup(alice.Guid)!.LeaderGuid);
        Assert.Equal(bob.Guid, _b.Context.Groups.GetGroup(bob.Guid)!.LeaderGuid);
        Assert.Equal("Bob", _a.Context.Groups.GetGroup(alice.Guid)!.LeaderName);
        Assert.Single(aliceSession.Of(WorldOpcode.SmsgGroupSetLeader));
        Assert.Single(bobSession.Of(WorldOpcode.SmsgGroupSetLeader));
        Assert.NotEmpty(bobSession.Of(WorldOpcode.SmsgGroupList));
        Assert.Equal(bob.Guid, _holder.Groups.GetGroup(alice.Guid)!.LeaderGuid);

        // Alice is no longer the leader: a leader-only action of hers is refused at the holder.
        await On(_a, () => _a.Context.Groups.SetLeader(alice, alice.Guid));
        await On(_a, () => _a.Context.Groups.ConvertToRaid(alice));
        await Settle();
        Assert.False(_b.Context.Groups.GetGroup(bob.Guid)!.IsRaid);
    }

    [Fact]
    public async Task Kick_RemovesAMemberOnAnotherWorker_AndTheGroupGoesOn()
    {
        (Player alice, RecordingSession aliceSession) = await Login(_a, AliceId, "Alice");
        (Player bob, _) = await Login(_b, BobId, "Bob");
        (Player carol, RecordingSession carolSession) = await Login(_b, CarolId, "Carol");
        await On(_a, () => _a.Context.Groups.Invite(alice, "Bob"));
        await Settle();
        await On(_b, () => _b.Context.Groups.Accept(bob));
        await Settle();
        await On(_a, () => _a.Context.Groups.Invite(alice, "Carol"));
        await Settle();
        await On(_b, () => _b.Context.Groups.Accept(carol));
        await Settle();
        Assert.Equal(3, _a.Context.Groups.GetGroup(alice.Guid)!.MemberCount);
        Assert.Equal(3, _b.Context.Groups.GetGroup(carol.Guid)!.MemberCount);

        // The leader (worker A) kicks Carol (worker B).
        carolSession.Clear();
        aliceSession.Clear();
        await On(_a, () => _a.Context.Groups.UninviteByGuid(alice, carol.Guid));
        await Settle();

        Assert.Single(carolSession.Of(WorldOpcode.SmsgGroupUninvite));
        Assert.Null(_b.Context.Groups.GetGroup(carol.Guid));
        Assert.Null(_holder.Groups.GetGroup(carol.Guid));
        Group onA = _a.Context.Groups.GetGroup(alice.Guid)!;
        Group onB = _b.Context.Groups.GetGroup(bob.Guid)!;
        Assert.Equal(2, onA.MemberCount);
        Assert.Equal(2, onB.MemberCount);
        Assert.False(onB.IsMember(carol.Guid));
        Assert.NotEmpty(aliceSession.Of(WorldOpcode.SmsgGroupList));

        // A member without the right cannot kick: Bob (not leader, not assistant) is refused with NOT_LEADER.
        await On(_b, () => _b.Context.Groups.UninviteByGuid(bob, alice.Guid));
        await Settle();
        Assert.NotNull(_a.Context.Groups.GetGroup(alice.Guid));
        Assert.Equal(2, _holder.Groups.GetGroup(alice.Guid)!.MemberCount);
    }

    [Fact]
    public async Task Leave_OfTheLastTwoMembers_EndsTheGroupOnBothWorkers()
    {
        (Player alice, RecordingSession aliceSession) = await Login(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await Login(_b, BobId, "Bob");
        await On(_a, () => _a.Context.Groups.Invite(alice, "Bob"));
        await Settle();
        await On(_b, () => _b.Context.Groups.Accept(bob));
        await Settle();
        aliceSession.Clear();
        bobSession.Clear();

        await On(_b, () => _b.Context.Groups.Leave(bob));
        await Settle();

        Assert.Null(_a.Context.Groups.GetGroup(alice.Guid));
        Assert.Null(_b.Context.Groups.GetGroup(bob.Guid));
        Assert.Empty(_holder.Groups.Groups);
        Assert.NotEmpty(aliceSession.Of(WorldOpcode.SmsgGroupList));
        Assert.NotEmpty(bobSession.Of(WorldOpcode.SmsgGroupList));
    }

    [Fact]
    public async Task Decline_AcrossWorkers_TellsTheInviter()
    {
        (Player alice, RecordingSession aliceSession) = await Login(_a, AliceId, "Alice");
        (Player bob, _) = await Login(_b, BobId, "Bob");
        await On(_a, () => _a.Context.Groups.Invite(alice, "Bob"));
        await Settle();
        aliceSession.Clear();

        await On(_b, () => _b.Context.Groups.Decline(bob));
        await Settle();

        Assert.Single(aliceSession.Of(WorldOpcode.SmsgGroupDecline));
        Assert.Empty(_holder.Groups.Groups);
        await On(_b, () => _b.Context.Groups.Accept(bob)); // the invite is gone
        await Settle();
        Assert.Null(_b.Context.Groups.GetGroup(bob.Guid));
    }

    [Fact]
    public async Task Invite_WithoutAGroupAuthority_StaysLocal()
    {
        // A manager with no link behaves as before (single process): the invite is decided on the spot.
        var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        using (world)
        {
            var lookup = new FixedLookup(
                new CharacterInfo(AliceId, 151, "Alice", Race.Human, Class.Warrior),
                new CharacterInfo(BobId, 152, "Bob", Race.Human, Class.Warrior));
            var context = new SocialContext(world, lookup, new NullSocialPersistence());
            var aliceSession = new RecordingSession();
            var bobSession = new RecordingSession();
            Player alice = CreatePlayer(AliceId, "Alice", aliceSession);
            Player bob = CreatePlayer(BobId, "Bob", bobSession);
            world.Start();
            await world.InvokeAsync(() =>
            {
                world.AddPlayer(alice);
                world.AddPlayer(bob);
                context.Groups.Invite(alice, "Bob");
                return true;
            });

            Assert.Null(context.Groups.Authority);
            Assert.Single(bobSession.Of(WorldOpcode.SmsgGroupInvite));
        }
    }

    [Fact]
    public void WireCodec_RoundTripsCommandsAndEvents()
    {
        var command = new GroupCommand(GroupCommandKind.SetLootMethod, 7, "Alice", 1, true, 9, "Bob", "Carol", 2, 3, 11, [1, 2, 3]);
        using var commandStream = new MemoryStream();
        using (var w = new BinaryWriter(commandStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            GroupWireCodec.WriteCommand(w, command);
        }

        commandStream.Position = 0;
        using var commandReader = new BinaryReader(commandStream, System.Text.Encoding.UTF8);
        GroupCommand decoded = GroupWireCodec.ReadCommand(commandReader);
        Assert.Equal(command with { Payload = null }, decoded with { Payload = null });
        Assert.Equal(command.Payload, decoded.Payload);

        var replica = new GroupReplica(4, true, 7, "Alice", 2, 9, 3, [0, 5, 0, 0, 0, 0, 0, 0], [new GroupReplicaMember(7, "Alice", 0, false), new GroupReplicaMember(9, "Bob", 1, true)]);
        GroupEvent[] events =
        [
            new GroupEvent(9, replica, 0, false, 7, [new GroupPacket(WorldOpcode.SmsgGroupList, [1, 2]), new GroupPacket(WorldOpcode.SmsgGroupInvite, [])]),
            new GroupEvent(7, null, 4, true, 0, []),
        ];
        using var eventStream = new MemoryStream();
        using (var w = new BinaryWriter(eventStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            GroupWireCodec.WriteEvents(w, events);
        }

        eventStream.Position = 0;
        using var eventReader = new BinaryReader(eventStream, System.Text.Encoding.UTF8);
        IReadOnlyList<GroupEvent> back = GroupWireCodec.ReadEvents(eventReader);
        Assert.Equal(2, back.Count);
        Assert.Equal(9UL, back[0].RecipientGuid);
        Assert.Equal(7UL, back[0].InviterGuid);
        Assert.Equal(replica.Members, back[0].Replica!.Members);
        Assert.Equal(replica.TargetIcons, back[0].Replica!.TargetIcons);
        Assert.Equal(2, back[0].Packets.Count);
        Assert.Equal(new byte[] { 1, 2 }, back[0].Packets[0].Payload);
        Assert.Null(back[1].Replica);
        Assert.Equal(4u, back[1].DepartedGroupId);
        Assert.True(back[1].Disbanded);
    }

    private sealed class RecordingSession : IPlayerSession
    {
        private readonly object _gate = new();
        private readonly List<(WorldOpcode Opcode, byte[] Payload)> _sent = [];

        public int AccountId => 0;
        public AccountSecurity Security => AccountSecurity.Player;

        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload)
        {
            lock (_gate)
            {
                _sent.Add((opcode, payload.ToArray()));
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _sent.Clear();
            }
        }

        public List<byte[]> Of(WorldOpcode opcode)
        {
            lock (_gate)
            {
                return [.. _sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];
            }
        }

        public void ProcessWorldPackets(Player player)
        {
        }

        public void Kick()
        {
        }

        public void OnLoggedOut()
        {
        }
    }

    private sealed class FixedLookup(params CharacterInfo[] characters) : ICharacterLookup
    {
        public CharacterInfo? Find(uint characterId) => characters.FirstOrDefault(c => c.Id == characterId);

        public CharacterInfo? FindByName(string name) => characters.FirstOrDefault(c => c.Name == name);
    }

    private sealed class NullSocialPersistence : ISocialPersistence
    {
        public void SetSocial(int characterId, int otherId, SocialFlags flags)
        {
        }

        public void SaveGuild(GuildData guild)
        {
        }

        public void DeleteGuild(int guildId)
        {
        }
    }
}

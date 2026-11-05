using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Peer;
using ArcaneCore.World.Cluster.Social;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// The production guild link (S3b2): <see cref="CrossWorkerGuildTests"/>' scenarios with the in-process hub replaced by <see cref="PeerGuildHolderLink"/>. Worker "node-a" holds the
/// Social lease and runs the holder's <see cref="GuildHolderService"/> behind a real <see cref="PeerLinkHost"/>; worker "node-b" is a real <see cref="PeerLinkClient"/> whose
/// <see cref="PeerPresenceClient"/> carries every forwarded request and every drain over the peer link. Each worker is a real <see cref="WorldRuntime"/> with its own
/// <see cref="SocialContext"/>; players are driven through the same public manager calls the opcode handlers make.
/// </summary>
public sealed class PeerGuildHolderLinkTests : IAsyncLifetime
{
    private const string Secret = "peer-secret-0123456789";
    private const int AliceId = 51;
    private const int BobId = 52;
    private const int CarolId = 53;
    private const int DaveId = 54;
    private const string GuildName = "Wolves";
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private readonly InMemoryRealmAuthorityLeaseStore _store = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly InMemoryRealmPresenceDirectory _directory = new();
    private readonly PresenceHolderService _holder;
    private readonly PeerLinkHost _host;
    private readonly PeerLinkClient _client;
    private readonly Worker _a;
    private readonly Worker _b;

    public PeerGuildHolderLinkTests()
    {
        _holder = new PresenceHolderService(_directory, new InMemoryClusterSocialRouter(_directory));
        var incarnation = Guid.NewGuid();
        _store.TryAcquire(RealmAuthorityScopes.Social, "node-a", incarnation, Hour, DateTimeOffset.UtcNow);
        _host = new PeerLinkHost(FastOptions(), Secret, Manifest("node-a", incarnation), new StoreLeaseSource(_store, "node-a"), NullLogger.Instance)
        {
            PayloadHandler = _holder.Handle,
        };
        _host.Start();
        _disposables.Add(_host);

        PeerLinkOptions options = FastOptions();
        options.Nodes["node-a"] = $"127.0.0.1:{_host.LocalEndPoint.Port}";
        var locator = new PeerHolderLocator(_store, options);
        _client = new PeerLinkClient(options, Secret, Manifest("node-b", Guid.NewGuid()), new StoreLeaseSource(_store, "node-b"), locator.Locate, NullLogger.Instance);
        _client.Start();
        _disposables.Add(_client);

        CharacterInfo[] characters =
        [
            new((uint)AliceId, 151, "Alice", Race.Human, Class.Warrior),
            new((uint)BobId, 152, "Bob", Race.Human, Class.Warrior),
            new((uint)CarolId, 153, "Carol", Race.Human, Class.Warrior),
            new((uint)DaveId, 154, "Dave", Race.Human, Class.Warrior),
        ];
        _a = new Worker("node-a", characters, _holder, _holder.Guilds, holds: true);
        _b = new Worker("node-b", characters, new PeerPresenceClient(_client), new GuildHolderService(new InMemoryRealmPresenceDirectory()), holds: false);
        _disposables.Add(_a.Link);
        _disposables.Add(_b.Link);
    }

    public async Task InitializeAsync()
    {
        DateTime end = DateTime.UtcNow.AddSeconds(15);
        while (!_client.IsConnected)
        {
            Assert.True(DateTime.UtcNow < end, "worker admitted by the holder");
            await Task.Delay(5);
        }

        foreach (Worker worker in new[] { _a, _b })
        {
            worker.World.Start();
            await worker.World.InvokeAsync(() =>
            {
                worker.Context.Guilds.Load([]);
                worker.Context.Petitions.Load([]);
                worker.Context.Guilds.Link = worker.Link;
                worker.Link.Bind(worker.World, worker.Context.Guilds, worker.Context.Petitions);
                return true;
            });
        }

        // The first drain registers worker B at the holder deterministically; from then on the pump keeps it current.
        _b.Link.PumpOnce();
        _b.Link.Start(TimeSpan.FromMilliseconds(20));
    }

    public async Task DisposeAsync()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }

        _a.World.Dispose();
        _b.World.Dispose();
    }

    private sealed class StoreLeaseSource(IRealmAuthorityLeaseStore store, string nodeId) : IPeerLeaseSource
    {
        public string NodeId => nodeId;

        public IReadOnlyList<PeerLeaseToken> HeldLeases()
        {
            var held = new List<PeerLeaseToken>();
            if (store.GetCurrent(RealmAuthorityScopes.Social, DateTimeOffset.UtcNow) is { } lease && lease.OwnerNodeId == nodeId)
            {
                held.Add(new PeerLeaseToken(RealmAuthorityScopes.Social, lease.FencingToken));
            }

            return held;
        }
    }

    private static ClusterNodeManifest Manifest(string node, Guid incarnation)
        => GatewayLinkManifest.Create(ClusterRole.Worker, node, incarnation, 1, "rev", 1, 1);

    private static PeerLinkOptions FastOptions() => new()
    {
        Port = 0,
        ReconnectInitialMs = 20,
        ReconnectMaxMs = 100,
        HolderPollMs = 30,
        HandshakeTimeoutMs = 5000,
        RequestTimeoutMs = 3000,
    };

    private async Task<(Player Player, RecordingSession Session)> JoinAsync(Worker worker, int id, string name)
    {
        var session = new RecordingSession(100 + id);
        var record = new CharacterRecord
        {
            Id = id, AccountId = 100 + id, Name = name,
            Race = (byte)Race.Human, Class = (byte)Class.Warrior,
            Gender = (byte)Gender.Male, Level = 10, ZoneId = 12,
        };
        var player = new Player(record, new PlayerAppearance(49, 1, PowerType.Rage, 60, 0, 60, 1000, 0, 400), session);
        await worker.World.InvokeAsync(() =>
        {
            worker.World.AddPlayer(player);
            return true;
        });

        // What the presence publisher does in production: the realm directory at the holder learns where the character is online.
        PresenceUpsertReply reply = _holder.Register(new RealmPresenceEntry(
            (ulong)id, name, 100 + id, worker.Id, "gateway", 0, 12, 0, 10, (byte)Class.Warrior, (byte)Race.Human, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow));
        Assert.Equal(PresenceMutationResult.Applied, reply.Result);
        return (player, session);
    }

    private static async Task OnWorldAsync(Worker worker, Action action)
    {
        await worker.World.InvokeAsync(() =>
        {
            action();
            return true;
        });
    }

    private static Task<T> OnWorldAsync<T>(Worker worker, Func<T> action) => worker.World.InvokeAsync(action);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Budget;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"timed out waiting for {what}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitOnWorldAsync(Worker worker, Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Budget;
        while (!await worker.World.InvokeAsync(condition))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"timed out waiting for {what}");
            }

            await Task.Delay(10);
        }
    }

    private static bool Received(RecordingSession session, WorldOpcode opcode, byte[] expected)
        => session.Of(opcode).Any(payload => payload.AsSpan().SequenceEqual(expected));

    private async Task<int> FoundAsync()
    {
        (GuildAdminResult result, int id) = await OnWorldAsync(_a, () =>
        {
            GuildAdminResult created = _a.Context.Guilds.Create((uint)AliceId, GuildName, out Guild? guild);
            return (created, guild?.Id ?? 0);
        });
        Assert.Equal(GuildAdminResult.Ok, result);
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.Get(id) is not null, "the guild replica on worker B");
        return id;
    }

    [Fact]
    public async Task InviteFromTheHolder_ToAPlayerOnAnotherWorker_IsAnsweredByAcceptThere()
    {
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        int guildId = await FoundAsync();

        // Alice (holder) invites Bob (worker B): the holder finds him in the realm directory and the invitation travels to his worker through the drain.
        await OnWorldAsync(_a, () => _a.Context.Guilds.Invite(alice, "Bob"));
        await WaitUntilAsync(() => bobSession.Of(WorldOpcode.SmsgGuildInvite).Count == 1, "the guild invitation on worker B");
        Assert.Equal(GuildPackets.BuildInvite("Alice", GuildName), Assert.Single(bobSession.Of(WorldOpcode.SmsgGuildInvite)));

        // Bob accepts on worker B: the request crosses the peer link, the holder adds him and tells every member on both workers.
        await OnWorldAsync(_b, () => _b.Context.Guilds.Accept(bob));
        byte[] joined = GuildPackets.BuildEvent(GuildEvent.Joined, bob.Guid, "Bob");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, joined), "GE_JOINED on worker B");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgGuildEvent, joined), "GE_JOINED on the holder");

        Guild held = await OnWorldAsync(_a, () => _a.Context.Guilds.GetGuildOf((uint)BobId)!);
        Assert.Equal(guildId, held.Id);
        Assert.Equal(held.LowestRank, held.Find((uint)BobId)!.Rank);
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(bob)?.Id == guildId, "Bob's membership on worker B");
    }

    [Fact]
    public async Task OfficerOnAnotherWorker_InvitesAndPromotes_ThroughTheHolder()
    {
        (Player alice, _) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        (Player carol, RecordingSession carolSession) = await JoinAsync(_b, CarolId, "Carol");
        (Player dave, RecordingSession daveSession) = await JoinAsync(_a, DaveId, "Dave");
        int guildId = await FoundAsync();
        await OnWorldAsync(_a, () =>
        {
            Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminInvite((uint)BobId, GuildName));
            Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminInvite((uint)CarolId, GuildName));
            Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminSetRank((uint)BobId, Guild.OfficerRank));
        });
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(carol)?.Find((uint)BobId)?.Rank == Guild.OfficerRank, "the roster replica on worker B");
        Assert.Equal(guildId, await OnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(bob)!.Id));

        // Bob, an officer on worker B, invites Dave who is on the holder's worker.
        await OnWorldAsync(_b, () => _b.Context.Guilds.Invite(bob, "Dave"));
        await WaitUntilAsync(() => daveSession.Of(WorldOpcode.SmsgGuildInvite).Count == 1, "the invitation on the holder's worker");
        Assert.Equal(GuildPackets.BuildInvite("Bob", GuildName), Assert.Single(daveSession.Of(WorldOpcode.SmsgGuildInvite)));

        // Bob promotes Carol (both on worker B): the rank changes at the holder and comes back as an event and a replica.
        byte lowest = await OnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(carol)!.LowestRank);
        await OnWorldAsync(_b, () => _b.Context.Guilds.Promote(bob, "Carol"));
        byte[] promoted = GuildPackets.BuildEvent(GuildEvent.Promotion, ObjectGuid.Empty, "Bob", "Carol", await OnWorldAsync(_a, () => _a.Context.Guilds.Get(guildId)!.RankName((byte)(lowest - 1))));
        await WaitUntilAsync(() => Received(carolSession, WorldOpcode.SmsgGuildEvent, promoted), "the promotion event on worker B");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, promoted), "the promotion event for the promoter");
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.Get(guildId)!.Find((uint)CarolId)!.Rank == lowest - 1, "Carol's new rank on worker B");

        // Without the right the holder refuses, and the refusal is answered on the requester's own worker.
        carolSession.Clear();
        await OnWorldAsync(_b, () => _b.Context.Guilds.Promote(carol, "Bob"));
        await WaitUntilAsync(() => carolSession.Of(WorldOpcode.SmsgGuildCommandResult).Count == 1, "the permission refusal on worker B");
        Assert.Equal(GuildPackets.BuildCommandResult(GuildCommand.Invite, string.Empty, GuildCommandError.Permissions), Assert.Single(carolSession.Of(WorldOpcode.SmsgGuildCommandResult)));
        Assert.NotNull(alice);
        Assert.NotNull(dave);
    }

    [Fact]
    public async Task MotdChange_ReachesMembersOnBothWorkers_FromEitherWorker()
    {
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        int guildId = await FoundAsync();
        await OnWorldAsync(_a, () =>
        {
            Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminInvite((uint)BobId, GuildName));
            Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminSetRank((uint)BobId, Guild.OfficerRank));
        });
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(bob)?.Find((uint)BobId)?.Rank == Guild.OfficerRank, "Bob's membership on worker B");

        await OnWorldAsync(_a, () => _a.Context.Guilds.SetMotd(alice, "Hunt at dawn"));
        byte[] first = GuildPackets.BuildEvent(GuildEvent.Motd, ObjectGuid.Empty, "Hunt at dawn");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgGuildEvent, first), "the MOTD on the holder");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, first), "the MOTD on worker B");
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.Get(guildId)!.Motd == "Hunt at dawn", "the MOTD in worker B's replica");

        await OnWorldAsync(_b, () => _b.Context.Guilds.SetMotd(bob, "Hunt at dusk"));
        byte[] second = GuildPackets.BuildEvent(GuildEvent.Motd, ObjectGuid.Empty, "Hunt at dusk");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgGuildEvent, second), "the forwarded MOTD on the holder");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, second), "the forwarded MOTD on worker B");
        Assert.Equal("Hunt at dusk", await OnWorldAsync(_a, () => _a.Context.Guilds.Get(guildId)!.Motd));
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.Get(guildId)!.Motd == "Hunt at dusk", "the forwarded MOTD in worker B's replica");
    }

    [Fact]
    public async Task SignedOnEvent_AndRoster_ReachTheHolderAndTheMemberOnAnotherWorker()
    {
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        await FoundAsync();
        await OnWorldAsync(_a, () => Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminInvite((uint)BobId, GuildName)));
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(bob) is not null, "Bob's membership on worker B");

        await OnWorldAsync(_b, () => _b.Context.Guilds.OnLoggedIn(bob));
        byte[] signedOn = GuildPackets.BuildEvent(GuildEvent.SignedOn, bob.Guid, "Bob");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgGuildEvent, signedOn), "GE_SIGNED_ON on the holder");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, signedOn), "GE_SIGNED_ON on worker B");

        // The roster is built by the holder from both workers' online members (the realm directory tells it Bob is online) and sent to the requester on worker B.
        await OnWorldAsync(_b, () => _b.Context.Guilds.Roster(bob));
        await WaitUntilAsync(() => bobSession.Of(WorldOpcode.SmsgGuildRoster).Count == 1, "the roster on worker B");
        byte[] expected = await OnWorldAsync(_a, () =>
        {
            Guild guild = _a.Context.Guilds.GetGuildOf((uint)AliceId)!;
            GuildRosterEntry Online(GuildMember member, string name) => new(member.Guid, GuildRosterFlags.Online, name, member.Rank, 10, (byte)Class.Warrior, 12, 0, member.PublicNote, member.OfficerNote);
            return GuildPackets.BuildRoster(guild, [Online(guild.Find((uint)AliceId)!, "Alice"), Online(guild.Find((uint)BobId)!, "Bob")]);
        });
        Assert.Equal(expected, Assert.Single(bobSession.Of(WorldOpcode.SmsgGuildRoster)));
        Assert.NotNull(alice);
    }

    [Fact]
    public async Task Petition_IsSignedFromAnotherWorker_AndTheOwnerAndSignerAreAnswered()
    {
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        ObjectGuid charter = ObjectGuid.Item(500);
        var stored = new PetitionData(1, AliceId, 500, GuildName, []);
        foreach (Worker worker in new[] { _a, _b })
        {
            await OnWorldAsync(worker, () => worker.Context.Petitions.Load([stored]));
        }

        await OnWorldAsync(_b, () => _b.Context.Petitions.Sign(bob, charter));
        byte[] ok = PetitionPackets.BuildSignResults(charter, bob.Guid, PetitionResult.Ok);
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgPetitionSignResults, ok), "the sign result on worker B");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgPetitionSignResults, ok), "the sign result for the owner on the holder");
        PetitionSignature held = await OnWorldAsync(_a, () => Assert.Single(_a.Context.Petitions.Get(1)!.Signatures));
        Assert.Equal((uint)BobId, held.PlayerId);
        await WaitOnWorldAsync(_b, () => _b.Context.Petitions.Get(1)!.Signatures.Count == 1, "the signature in worker B's replica");

        bobSession.Clear();
        aliceSession.Clear();
        await OnWorldAsync(_b, () => _b.Context.Petitions.Sign(bob, charter));
        byte[] again = PetitionPackets.BuildSignResults(charter, bob.Guid, PetitionResult.AlreadySigned);
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgPetitionSignResults, again), "the duplicate refusal on worker B");
        await WaitUntilAsync(() => aliceSession.Of(WorldOpcode.SmsgGuildCommandResult).Count == 1, "the duplicate notice for the owner");
        Assert.Single(await OnWorldAsync(_a, () => _a.Context.Petitions.Get(1)!.Signatures));
        Assert.NotNull(alice);
    }

    private static readonly ItemTemplateStore CharterTemplates = new(
        [new ItemTemplate { Entry = PetitionConstants.CharterEntry, Class = 12, Name = "Guild Charter", DisplayId = 16161, Quality = 1, Flags = 0x2000, MaxCount = 1, Bonding = 1 }], []);

    /// <summary>A real charter item in the player's bags bound to petition 1 (what a purchase leaves), made on the player's world thread. Returns its guid.</summary>
    private static Task<ObjectGuid> GiveCharterAsync(Worker worker, Player player)
        => OnWorldAsync(worker, () =>
        {
            player.Inventory.Templates = CharterTemplates;
            player.Inventory.GuidAllocator = new ItemGuidAllocator(player.Guid.Low * 1000);
            player.Inventory.Load([]);
            var destination = new List<ItemPosCount>();
            Assert.Equal(InventoryResult.Ok, player.Inventory.CanStoreNewItem(PetitionConstants.CharterEntry, 1, destination, out _));
            Item charter = player.Inventory.StoreNewItem(destination, CharterTemplates.Find(PetitionConstants.CharterEntry)!, 1);
            charter.SetUInt32(UpdateFields.ItemFieldEnchantment + (PetitionConstants.EnchantmentSlot * 3), 1);
            return charter.Guid;
        });

    private async Task LoadPetitionAsync(ObjectGuid charter, int ownerId, params PetitionSignatureData[] signatures)
    {
        var stored = new PetitionData(1, ownerId, (int)charter.Low, GuildName, signatures);
        foreach (Worker worker in new[] { _a, _b })
        {
            await OnWorldAsync(worker, () => worker.Context.Petitions.Load([stored]));
        }
    }

    [Fact]
    public async Task PetitionOffer_FromAnotherWorker_GoesThroughTheHandler_ReachesTheTargetOnTheHolder_AndOnlyWithACharterInTheBags()
    {
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        (_, RecordingSession daveSession) = await JoinAsync(_a, DaveId, "Dave");
        ObjectGuid charter = await GiveCharterAsync(_b, bob);
        await LoadPetitionAsync(charter, BobId, new PetitionSignatureData(CarolId, 153));

        // Bob offers a charter guid that is not in his bags: his worker marks it unheld and the holder sends nothing.
        await OnWorldAsync(_b, () => _b.Context.Petitions.Offer(bob, ObjectGuid.Item(999_999), ObjectGuid.Player(DaveId)));
        await _b.Link.FlushAsync();

        // The real charter: Offer sees Dave is not on this worker, forwards, and the holder sends the list to Dave's session there.
        await OnWorldAsync(_b, () => _b.Context.Petitions.Offer(bob, charter, ObjectGuid.Player(DaveId)));
        await WaitUntilAsync(() => daveSession.Of(WorldOpcode.SmsgPetitionShowSignatures).Count >= 1, "the signature list on the holder's worker");
        await _b.Link.FlushAsync();
        byte[] expected = await OnWorldAsync(_a, () => PetitionPackets.BuildShowSignatures(charter, bob.Guid, 1, _a.Context.Petitions.Get(1)!.Signatures));
        Assert.Equal(expected, Assert.Single(daveSession.Of(WorldOpcode.SmsgPetitionShowSignatures)));
        Assert.Empty(bobSession.Of(WorldOpcode.SmsgPetitionShowSignatures));
    }

    [Fact]
    public async Task PetitionOffer_FromTheHolder_ToATargetOnAnotherWorker_ArrivesThroughTheDrain()
    {
        (Player alice, _) = await JoinAsync(_a, AliceId, "Alice");
        (_, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        ObjectGuid charter = await GiveCharterAsync(_a, alice);
        await LoadPetitionAsync(charter, AliceId, new PetitionSignatureData(CarolId, 153));

        await OnWorldAsync(_a, () => _a.Context.Petitions.Offer(alice, charter, ObjectGuid.Player(BobId)));
        await WaitUntilAsync(() => bobSession.Of(WorldOpcode.SmsgPetitionShowSignatures).Count == 1, "the signature list on worker B");
        byte[] expected = await OnWorldAsync(_a, () => PetitionPackets.BuildShowSignatures(charter, alice.Guid, 1, _a.Context.Petitions.Get(1)!.Signatures));
        Assert.Equal(expected, Assert.Single(bobSession.Of(WorldOpcode.SmsgPetitionShowSignatures)));
    }

    [Fact]
    public async Task PetitionOffer_ToAGuildedTargetOnAnotherWorker_RefusesTheOffererByHisOwnName()
    {
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        ObjectGuid charter = await GiveCharterAsync(_b, bob);
        await LoadPetitionAsync(charter, BobId);
        await FoundAsync();

        // Alice leads a guild on the holder; Bob offers her a charter from worker B: the refusal names the offerer and is answered on his worker.
        await OnWorldAsync(_b, () => _b.Context.Petitions.Offer(bob, charter, alice.Guid));
        byte[] refusal = GuildPackets.BuildCommandResult(GuildCommand.Invite, "Bob", GuildCommandError.AlreadyInGuildS);
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildCommandResult, refusal), "the refusal on the offerer's worker");
        Assert.Empty(aliceSession.Of(WorldOpcode.SmsgPetitionShowSignatures));
    }

    [Fact]
    public async Task ForwardedRequestWithACallback_IsAnsweredOnTheCallersWorldThread()
    {
        (_, _) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, _) = await JoinAsync(_b, BobId, "Bob");
        await FoundAsync();

        bool? answer = null;
        int thread = 0;
        Assert.True(_b.Link.Forward(new GuildRequest(GuildRequestKind.Info, bob.Guid), result =>
        {
            thread = Environment.CurrentManagedThreadId;
            answer = result;
        }));
        await WaitUntilAsync(() => answer is not null, "the answer of the holder");
        Assert.True(answer);
        Assert.Equal(await OnWorldAsync(_b, () => Environment.CurrentManagedThreadId), thread);

        // The holder's own link never forwards: it is the holder.
        Assert.False(_a.Link.Forward(new GuildRequest(GuildRequestKind.Info, bob.Guid), null));
        Assert.True(_a.Link.IsHolder);
        Assert.False(_b.Link.IsHolder);
    }

    [Fact]
    public async Task FindOnline_OnTheHolder_ReadsTheRealmDirectory_AndAnotherWorkerResolvesNothing()
    {
        (Player bob, _) = await JoinAsync(_b, BobId, "Bob");
        await JoinAsync(_a, AliceId, "Alice");

        GuildRemoteCharacter found = _a.Link.FindOnline(bob.Guid)!;
        Assert.Equal(("Bob", Team.Alliance, (byte)10, (byte)Class.Warrior, 12u), (found.Name, found.Team, found.Level, found.Class, found.ZoneId));
        Assert.Equal(bob.Guid, _a.Link.FindOnline("Bob")!.Guid);

        // A character the directory places on the holder's own worker is that worker's own session, never a remote one.
        Assert.Null(_a.Link.FindOnline("Alice"));
        Assert.Null(_a.Link.FindOnline("Nobody"));

        // Only the holder runs the rules that look characters up; the directory read would block on the peer link elsewhere.
        Assert.Null(_b.Link.FindOnline(bob.Guid));
        Assert.False(_a.Link.TargetIgnores(bob.Guid, ObjectGuid.Player(AliceId)));
    }

    private sealed class Worker
    {
        public Worker(string id, CharacterInfo[] characters, IGuildHolderTransport transport, GuildHolderService local, bool holds)
        {
            Id = id;
            World = new WorldRuntime(
                new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
            Context = new SocialContext(World, new FixedLookup(characters), new NullSocialPersistence());
            Link = new PeerGuildHolderLink(transport, local, id, () => holds);
        }

        public string Id { get; }

        public WorldRuntime World { get; }

        public SocialContext Context { get; }

        public PeerGuildHolderLink Link { get; }
    }

    private sealed class RecordingSession(int accountId) : IPlayerSession
    {
        private readonly object _gate = new();
        private readonly List<(WorldOpcode Opcode, byte[] Payload)> _sent = [];

        public int AccountId => accountId;

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

/// <summary>The holder's outboxes without any world: routing by the realm directory, coalescing, the bounded batch and the caps.</summary>
public sealed class GuildHolderServiceTests
{
    private static RealmPresenceEntry Entry(ulong guid, string worker)
        => new(guid, "C" + guid, (int)guid + 100, worker, "g", 0, 12, 0, 10, 1, (byte)Race.Human, 0, 0, 0, 1, 1, DateTimeOffset.UtcNow);

    private static (GuildHolderService Service, InMemoryRealmPresenceDirectory Directory) Create()
    {
        var directory = new InMemoryRealmPresenceDirectory();
        return (new GuildHolderService(directory), directory);
    }

    [Fact]
    public void Deliver_GoesToTheWorkerTheDirectoryPlacesTheCharacterOn_AndIsDroppedWhenOnlineNowhere()
    {
        (GuildHolderService service, InMemoryRealmPresenceDirectory directory) = Create();
        directory.TryUpsert(Entry(7, "wb"), out _);

        service.Deliver(ObjectGuid.Player(7), WorldOpcode.SmsgGuildInvite, [1]);
        service.Deliver(ObjectGuid.Player(8), WorldOpcode.SmsgGuildInvite, [2]);

        GuildLinkEvent only = Assert.Single(service.Drain("wb"));
        Assert.Equal((GuildLinkEventKind.Deliver, 7UL), (only.Kind, only.Character));
        Assert.Empty(service.Drain("wc"));
    }

    [Fact]
    public void Replicas_ReachEveryKnownWorker_AndTheNewestStateOfOneGuildReplacesTheQueuedOne()
    {
        (GuildHolderService service, _) = Create();
        service.Drain("wb");
        service.Drain("wc");

        service.ReplicateGuild(4, new GuildData(4, "One", 1, "a", "", 0, 0, 0, 0, 0, 0, [], []));
        service.ReplicateGuild(5, null);
        service.ReplicateGuild(4, new GuildData(4, "One", 1, "b", "", 0, 0, 0, 0, 0, 0, [], []));
        service.ReplicatePetition(1, null);

        Assert.Equal(3, service.QueuedFor("wb"));
        IReadOnlyList<GuildLinkEvent> events = service.Drain("wb");
        Assert.Equal(new[] { 5, 4 }, events.Where(e => e.Kind == GuildLinkEventKind.Guild).Select(e => e.Id));
        Assert.Equal("b", events.Single(e => e.Kind == GuildLinkEventKind.Guild && e.Id == 4).Guild!.Motd);
        Assert.Equal(3, service.QueuedFor("wc"));
    }

    [Fact]
    public void Drain_TakesBoundedBatches_AndEveryEventArrivesInOrder()
    {
        (GuildHolderService service, InMemoryRealmPresenceDirectory directory) = Create();
        directory.TryUpsert(Entry(7, "wb"), out _);
        const int count = 40;
        for (int i = 0; i < count; i++)
        {
            service.Deliver(ObjectGuid.Player(7), WorldOpcode.SmsgGuildEvent, new byte[4000]);
        }

        var all = new List<GuildLinkEvent>();
        int batches = 0;
        while (service.Drain("wb") is { Count: > 0 } batch)
        {
            batches++;
            Assert.True(batch.Sum(GuildWireCodec.EncodedSize) <= GuildHolderService.MaxBatchBytes);
            Assert.True(batch.Count <= GuildHolderService.MaxBatchEvents);
            all.AddRange(batch);
        }

        Assert.Equal(count, all.Count);
        Assert.True(batches >= 3);
        Assert.Equal(0, service.QueuedFor("wb"));
    }

    [Fact]
    public void AReplicaTooLargeForOneFrame_IsDroppedAndCounted_WithoutBlockingTheQueue()
    {
        (GuildHolderService service, _) = Create();
        service.Drain("wb");
        var huge = new GuildData(4, "Big", 1, new string('x', GuildHolderService.MaxBatchBytes + 10), "", 0, 0, 0, 0, 0, 0, [], []);
        service.ReplicateGuild(4, huge);
        service.ReplicateGuild(5, null);

        GuildLinkEvent only = Assert.Single(service.Drain("wb"));
        Assert.Equal(5, only.Id);
        Assert.Equal(1, service.Oversized);
    }

    [Fact]
    public void AQueueOverTheCap_DropsItsOldestEvents()
    {
        (GuildHolderService service, InMemoryRealmPresenceDirectory directory) = Create();
        directory.TryUpsert(Entry(7, "wb"), out _);
        for (int i = 0; i < GuildHolderService.MaxQueuedEventsPerWorker + 10; i++)
        {
            service.Deliver(ObjectGuid.Player(7), WorldOpcode.SmsgGuildEvent, [(byte)i]);
        }

        Assert.Equal(GuildHolderService.MaxQueuedEventsPerWorker, service.QueuedFor("wb"));
        Assert.Equal(10, service.Dropped);
    }

    [Fact]
    public void Submit_BeforeTheManagersAreBound_IsRefused()
    {
        (GuildHolderService service, _) = Create();
        Assert.Throws<InvalidOperationException>(() => service.Submit("wb", 1, new GuildRequest(GuildRequestKind.Info, ObjectGuid.Player(1))));
    }

    [Fact]
    public void FindCharacter_ReadsTheDirectory_AndSkipsTheHoldersOwnWorker()
    {
        (GuildHolderService service, InMemoryRealmPresenceDirectory directory) = Create();
        directory.TryUpsert(Entry(7, "wb"), out _);

        GuildRemoteCharacter found = service.FindCharacter(ObjectGuid.Player(7))!;
        Assert.Equal(("C7", Team.Alliance), (found.Name, found.Team));
        Assert.Equal(ObjectGuid.Player(7), service.FindCharacter("C7")!.Guid);
        Assert.Null(service.FindCharacter(ObjectGuid.Player(9)));
    }
}

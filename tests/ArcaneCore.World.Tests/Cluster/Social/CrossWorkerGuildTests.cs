using System.Collections.Concurrent;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Social;

/// <summary>
/// Guilds and charters across workers (S3b): worker "A" is the Social holder, worker "B" forwards. Each worker is a real <see cref="WorldRuntime"/> with its own
/// <see cref="SocialContext"/>; the in-process <see cref="Hub"/> plays the cluster hosting's part of <see cref="IGuildHolderLink"/> (it hands a forwarded request to the
/// holder's world thread, delivers packets to the world thread of the worker a character is online on, and publishes the holder's saved state to the other worker's
/// replica). Players are driven through the same public <see cref="GuildManager"/> and <see cref="PetitionManager"/> calls the opcode handlers make.
/// </summary>
public sealed class CrossWorkerGuildTests : IDisposable
{
    private const int AliceId = 51;
    private const int BobId = 52;
    private const int CarolId = 53;
    private const int DaveId = 54;
    private const string GuildName = "Wolves";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    private readonly Hub _hub = new();
    private readonly Worker _a;
    private readonly Worker _b;

    public CrossWorkerGuildTests()
    {
        CharacterInfo[] characters =
        [
            new((uint)AliceId, 151, "Alice", Race.Human, Class.Warrior),
            new((uint)BobId, 152, "Bob", Race.Human, Class.Warrior),
            new((uint)CarolId, 153, "Carol", Race.Human, Class.Warrior),
            new((uint)DaveId, 154, "Dave", Race.Human, Class.Warrior),
        ];
        _a = new Worker(_hub, characters);
        _b = new Worker(_hub, characters);
        _hub.Holder = _a;
        _hub.Workers.Add(_a);
        _hub.Workers.Add(_b);
    }

    public void Dispose()
    {
        _a.World.Dispose();
        _b.World.Dispose();
    }

    private async Task StartAsync()
    {
        foreach (Worker worker in _hub.Workers)
        {
            worker.World.Start();
            await worker.World.InvokeAsync(() =>
            {
                worker.Context.Guilds.Load([]);
                worker.Context.Petitions.Load([]);
                return true;
            });
        }
    }

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
        _hub.Online[player.Guid] = (worker, new GuildRemoteCharacter(player.Guid, name, Team.Alliance, 10, (byte)Class.Warrior, 12, false, false));
        return (player, session);
    }

    private static Task<T> OnWorldAsync<T>(Worker worker, Func<T> action) => worker.World.InvokeAsync(action);

    private static async Task OnWorldAsync(Worker worker, Action action)
    {
        await worker.World.InvokeAsync(() =>
        {
            action();
            return true;
        });
    }

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

    /// <summary>Founds the guild on the holder with Alice as leader and waits for worker B's replica to have it.</summary>
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
        await StartAsync();
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        int guildId = await FoundAsync();

        // Alice (holder) invites Bob (worker B): the invitation reaches Bob's session on his own worker.
        await OnWorldAsync(_a, () => _a.Context.Guilds.Invite(alice, "Bob"));
        await WaitUntilAsync(() => bobSession.Of(WorldOpcode.SmsgGuildInvite).Count == 1, "the guild invitation on worker B");
        Assert.Equal(GuildPackets.BuildInvite("Alice", GuildName), Assert.Single(bobSession.Of(WorldOpcode.SmsgGuildInvite)));

        // Bob accepts on worker B: the request goes to the holder, who adds him and tells every member, on both workers.
        await OnWorldAsync(_b, () => _b.Context.Guilds.Accept(bob));
        byte[] joined = GuildPackets.BuildEvent(GuildEvent.Joined, bob.Guid, "Bob");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, joined), "GE_JOINED on worker B");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgGuildEvent, joined), "GE_JOINED on the holder");

        // The holder has him as a member at the lowest rank, and worker B's replica followed.
        Guild held = await OnWorldAsync(_a, () => _a.Context.Guilds.GetGuildOf((uint)BobId)!);
        Assert.Equal(guildId, held.Id);
        Assert.Equal(held.LowestRank, held.Find((uint)BobId)!.Rank);
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(bob)?.Id == guildId, "Bob's membership on worker B");
    }

    [Fact]
    public async Task OfficerOnAnotherWorker_InvitesAndPromotes_ThroughTheHolder()
    {
        await StartAsync();
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

        // Bob, an officer on worker B, invites Dave who is on the holder's worker: the holder checks the rights and Dave gets the invitation.
        await OnWorldAsync(_b, () => _b.Context.Guilds.Invite(bob, "Dave"));
        await WaitUntilAsync(() => daveSession.Of(WorldOpcode.SmsgGuildInvite).Count == 1, "the invitation on the holder's worker");
        Assert.Equal(GuildPackets.BuildInvite("Bob", GuildName), Assert.Single(daveSession.Of(WorldOpcode.SmsgGuildInvite)));

        // Bob promotes Carol (both on worker B): the rank changes at the holder, the promotion event reaches both of them and worker B's replica.
        byte lowest = await OnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(carol)!.LowestRank);
        await OnWorldAsync(_b, () => _b.Context.Guilds.Promote(bob, "Carol"));
        byte[] promoted = GuildPackets.BuildEvent(GuildEvent.Promotion, ObjectGuid.Empty, "Bob", "Carol", await OnWorldAsync(_a, () => _a.Context.Guilds.Get(guildId)!.RankName((byte)(lowest - 1))));
        await WaitUntilAsync(() => Received(carolSession, WorldOpcode.SmsgGuildEvent, promoted), "the promotion event on worker B");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, promoted), "the promotion event for the promoter");
        Assert.Equal((byte)(lowest - 1), await OnWorldAsync(_a, () => _a.Context.Guilds.Get(guildId)!.Find((uint)CarolId)!.Rank));
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.Get(guildId)!.Find((uint)CarolId)!.Rank == lowest - 1, "Carol's new rank on worker B");

        // Without the right the holder refuses: Carol (a veteran now) may not promote, and the refusal is answered on her own worker.
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
        await StartAsync();
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        int guildId = await FoundAsync();
        await OnWorldAsync(_a, () =>
        {
            Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminInvite((uint)BobId, GuildName));
            Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminSetRank((uint)BobId, Guild.OfficerRank));
        });
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(bob)?.Find((uint)BobId)?.Rank == Guild.OfficerRank, "Bob's membership on worker B");

        // The leader sets it on the holder.
        await OnWorldAsync(_a, () => _a.Context.Guilds.SetMotd(alice, "Hunt at dawn"));
        byte[] first = GuildPackets.BuildEvent(GuildEvent.Motd, ObjectGuid.Empty, "Hunt at dawn");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgGuildEvent, first), "the MOTD on the holder");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, first), "the MOTD on worker B");
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.Get(guildId)!.Motd == "Hunt at dawn", "the MOTD in worker B's replica");

        // An officer sets it from worker B: forwarded, applied once at the holder, and again seen on both workers.
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
        await StartAsync();
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        await FoundAsync();
        await OnWorldAsync(_a, () => Assert.Equal(GuildAdminResult.Ok, _a.Context.Guilds.AdminInvite((uint)BobId, GuildName)));
        await WaitOnWorldAsync(_b, () => _b.Context.Guilds.GetGuildOf(bob) is not null, "Bob's membership on worker B");

        // Bob enters the world on worker B: the holder tells the guild, so Alice (holder) and Bob himself (worker B) see GE_SIGNED_ON.
        await OnWorldAsync(_b, () => _b.Context.Guilds.OnLoggedIn(bob));
        byte[] signedOn = GuildPackets.BuildEvent(GuildEvent.SignedOn, bob.Guid, "Bob");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgGuildEvent, signedOn), "GE_SIGNED_ON on the holder");
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgGuildEvent, signedOn), "GE_SIGNED_ON on worker B");

        // The roster is built by the holder from both workers' online members and sent to the requester on worker B.
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
        await StartAsync();
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_b, BobId, "Bob");
        ObjectGuid charter = ObjectGuid.Item(500);
        var stored = new PetitionData(1, AliceId, 500, GuildName, []);
        foreach (Worker worker in _hub.Workers)
        {
            await OnWorldAsync(worker, () => worker.Context.Petitions.Load([stored]));
        }

        // Bob signs on worker B: the holder checks him against the authoritative guild state, stores the signature and answers him and the owner.
        await OnWorldAsync(_b, () => _b.Context.Petitions.Sign(bob, charter));
        byte[] ok = PetitionPackets.BuildSignResults(charter, bob.Guid, PetitionResult.Ok);
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgPetitionSignResults, ok), "the sign result on worker B");
        await WaitUntilAsync(() => Received(aliceSession, WorldOpcode.SmsgPetitionSignResults, ok), "the sign result for the owner on the holder");
        PetitionSignature held = await OnWorldAsync(_a, () => Assert.Single(_a.Context.Petitions.Get(1)!.Signatures));
        Assert.Equal((uint)BobId, held.PlayerId);
        await WaitOnWorldAsync(_b, () => _b.Context.Petitions.Get(1)!.Signatures.Count == 1, "the signature in worker B's replica");

        // A second signature by the same account is refused at the holder, who tells the signer and (by vmangos' odd code) the owner.
        bobSession.Clear();
        aliceSession.Clear();
        await OnWorldAsync(_b, () => _b.Context.Petitions.Sign(bob, charter));
        byte[] again = PetitionPackets.BuildSignResults(charter, bob.Guid, PetitionResult.AlreadySigned);
        await WaitUntilAsync(() => Received(bobSession, WorldOpcode.SmsgPetitionSignResults, again), "the duplicate refusal on worker B");
        await WaitUntilAsync(() => aliceSession.Of(WorldOpcode.SmsgGuildCommandResult).Count == 1, "the duplicate notice for the owner");
        Assert.Single(await OnWorldAsync(_a, () => _a.Context.Petitions.Get(1)!.Signatures));
        Assert.NotNull(alice);
    }

    [Fact]
    public async Task OneWorker_WithoutALink_BehavesAsBefore()
    {
        await StartAsync();
        _a.Context.Guilds.Link = null;
        _b.Context.Guilds.Link = null;
        (Player alice, RecordingSession aliceSession) = await JoinAsync(_a, AliceId, "Alice");
        (Player bob, RecordingSession bobSession) = await JoinAsync(_a, BobId, "Bob");
        (GuildAdminResult result, _) = await OnWorldAsync(_a, () => (_a.Context.Guilds.Create((uint)AliceId, GuildName, out Guild? guild), guild?.Id ?? 0));
        Assert.Equal(GuildAdminResult.Ok, result);

        await OnWorldAsync(_a, () => _a.Context.Guilds.Invite(alice, "Bob"));
        await OnWorldAsync(_a, () => _a.Context.Guilds.Accept(bob));
        byte[] joined = GuildPackets.BuildEvent(GuildEvent.Joined, bob.Guid, "Bob");
        Assert.True(Received(aliceSession, WorldOpcode.SmsgGuildEvent, joined));
        Assert.True(Received(bobSession, WorldOpcode.SmsgGuildEvent, joined));
        Assert.True(await OnWorldAsync(_a, () => _a.Context.Guilds.IsAuthority));
    }

    /// <summary>The in-process cluster: who is the holder, which worker each character is online on, and the other workers.</summary>
    private sealed class Hub
    {
        public Worker Holder { get; set; } = null!;

        public List<Worker> Workers { get; } = [];

        public ConcurrentDictionary<ObjectGuid, (Worker Worker, GuildRemoteCharacter Info)> Online { get; } = new();
    }

    /// <summary>One worker: its world and social context, and its end of the link (what the cluster hosting provides in production).</summary>
    private sealed class Worker : IGuildHolderLink
    {
        private readonly Hub _hub;

        public Worker(Hub hub, CharacterInfo[] characters)
        {
            _hub = hub;
            World = new WorldRuntime(
                new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
                new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
            Context = new SocialContext(World, new FixedLookup(characters), new NullSocialPersistence());
            Context.Guilds.Link = this;
        }

        public WorldRuntime World { get; }

        public SocialContext Context { get; }

        public bool IsHolder => ReferenceEquals(_hub.Holder, this);

        public bool Forward(GuildRequest request, Action<bool>? completed)
        {
            Worker holder = _hub.Holder;
            holder.World.Post(() =>
            {
                bool result = holder.Context.Guilds.Serve(request);
                if (completed is not null)
                {
                    World.Post(() => completed(result));
                }
            });
            return true;
        }

        public void Deliver(ObjectGuid character, WorldOpcode opcode, byte[] payload)
        {
            if (!_hub.Online.TryGetValue(character, out var entry) || ReferenceEquals(entry.Worker, this))
            {
                return;
            }

            Worker target = entry.Worker;
            target.World.Post(() => target.World.FindOnlinePlayer(character)?.Session.Send(opcode, payload));
        }

        public void ReplicateGuild(int guildId, GuildData? data)
        {
            foreach (Worker other in _hub.Workers.Where(w => !ReferenceEquals(w, this)))
            {
                other.World.Post(() => other.Context.Guilds.ApplyReplica(guildId, data));
            }
        }

        public void ReplicatePetition(int petitionId, PetitionData? data)
        {
            foreach (Worker other in _hub.Workers.Where(w => !ReferenceEquals(w, this)))
            {
                other.World.Post(() => other.Context.Petitions.ApplyReplica(petitionId, data));
            }
        }

        public GuildRemoteCharacter? FindOnline(ObjectGuid character)
            => _hub.Online.TryGetValue(character, out var entry) && !ReferenceEquals(entry.Worker, this) ? entry.Info : null;

        public GuildRemoteCharacter? FindOnline(string name)
            => _hub.Online.Values.FirstOrDefault(e => !ReferenceEquals(e.Worker, this) && e.Info.Name == name).Info;

        public bool TargetIgnores(ObjectGuid target, ObjectGuid ignorer) => false;
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

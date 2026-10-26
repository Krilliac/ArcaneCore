using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>What a status packet needs to know about the battleground it describes (vmangos <c>BuildBattleGroundStatusPacket</c>, BattleGroundMgr.cpp:1049-1052).</summary>
public readonly record struct BattlegroundStatusSubject(uint MapId, byte Bracket, uint ClientInstanceId)
{
    /// <summary>The template: bracket id -1, which the wire shows as 255, and no client instance (BattleGround.cpp:202).</summary>
    public static BattlegroundStatusSubject OfTemplate(BattlegroundTemplate template) => new(template.MapId, byte.MaxValue, 0);

    public static BattlegroundStatusSubject Of(Battleground bg) => new(bg.MapId, (byte)bg.Bracket, bg.ClientInstanceId);
}

/// <summary>
/// The world side of the battleground manager: players, packets, instances and teleports. The daemon implements it; tests record.
/// World thread.
/// </summary>
public interface IBattlegroundManagerHost
{
    /// <summary>Whether the character is online (vmangos <c>ObjectAccessor::FindPlayerNotInWorld</c> finds it).</summary>
    bool IsOnline(ObjectGuid player);

    /// <summary>The effects channel of one match (broadcasts to its players, spawns its objects).</summary>
    IBattlegroundHost CreateMatchHost(BattlegroundType type, uint instanceId);

    /// <summary>A new map instance id for a match (the shared instance id generator, vmangos <c>MapManager::CreateBgMap</c>).</summary>
    uint AllocateInstanceId();

    /// <summary>SMSG_BATTLEFIELD_STATUS for the player's queue slot.</summary>
    void SendStatus(ObjectGuid player, uint queueSlot, BattlegroundStatusSubject subject, BattlegroundStatus status, uint time1, uint time2);

    /// <summary>SMSG_GROUP_JOINED_BATTLEGROUND: a map id, <see cref="BattlegroundPackets.GroupJoinDeserters"/> or <see cref="BattlegroundPackets.GroupJoinFailed"/>.</summary>
    void SendGroupJoined(ObjectGuid player, uint result);

    /// <summary>The group-join error message (vmangos <c>SendBattleGroundJoinError</c>).</summary>
    void SendJoinError(ObjectGuid player, BattlegroundJoinError error);

    /// <summary>"Group queue limit is set to N. You have been queued solo." (vmangos BattleGroundMgr.cpp:185).</summary>
    void GroupQueueLimitNotice(ObjectGuid player, uint limit);

    /// <summary>Remember where the member is to return to (vmangos <c>SetBattleGroundEntryPoint</c>, from the leader's position or the portal).</summary>
    void StoreEntryPoint(ObjectGuid member, ObjectGuid leader, bool queuedAtPortal);

    /// <summary>Resurrect the player and end a taxi flight before the port (vmangos BattleGroundHandler.cpp:453-464).</summary>
    void PrepareToPortIn(ObjectGuid player);

    /// <summary>Take AFK off and teleport the player to its team's start location in the match (vmangos <c>SendToBattleGround</c>, BattleGroundMgr.cpp:1452-1473).</summary>
    void SendToBattleground(ObjectGuid player, Battleground battleground, Team team);

    /// <summary>A match was created: create its map instance (vmangos <c>MapManager::CreateBgMap</c>).</summary>
    void BattlegroundCreated(Battleground battleground);

    /// <summary>A match was deleted: unload its map (vmangos <c>~BattleGround</c>, BattleGround.cpp:276-282).</summary>
    void BattlegroundDeleted(Battleground battleground);
}

/// <summary>Creates the match object of a type; null when the type is not supported (vmangos <c>CreateNewBattleGround</c> returns null for a type it cannot build).</summary>
public delegate Battleground? BattlegroundFactory(BattlegroundTemplate template, int bracket, uint instanceId, uint clientInstanceId, BattlegroundOptions options, BattlegroundPorts ports);

/// <summary>The match types this build can run: Alterac Valley, Warsong Gulch and Arathi Basin (vmangos <c>CreateNewBattleGround</c>, BattleGroundMgr.cpp:1250-1262).</summary>
public static class BattlegroundFactories
{
    public static Battleground? Default(BattlegroundTemplate template, int bracket, uint instanceId, uint clientInstanceId, BattlegroundOptions options, BattlegroundPorts ports)
        => template.Type switch
        {
            BattlegroundType.AlteracValley => new AlteracValley(template, bracket, instanceId, clientInstanceId, options, ports),
            BattlegroundType.WarsongGulch => new WarsongGulch(template, bracket, instanceId, clientInstanceId, options, ports),
            BattlegroundType.ArathiBasin => new ArathiBasin(template, bracket, instanceId, clientInstanceId, options, ports),
            _ => null,
        };
}

/// <summary>A player's queue slots and the match it is bound to (vmangos <c>m_bgBattleGroundQueueID[3]</c> and <c>m_bgData</c>).</summary>
public sealed class BattlegroundPlayerState
{
    private readonly BattlegroundQueueType[] _queue = new BattlegroundQueueType[BattlegroundOptions.MaxQueuesPerPlayer];
    private readonly uint[] _invitedToInstance = new uint[BattlegroundOptions.MaxQueuesPerPlayer];

    /// <summary>The match the player is in, 0 for none (vmangos <c>GetBattleGroundId</c>).</summary>
    public uint InstanceId { get; internal set; }

    public BattlegroundType Type { get; internal set; }

    /// <summary>The side the player fights on in the match (vmangos <c>GetBGTeam</c>).</summary>
    public Team? Team { get; internal set; }

    public bool InBattleground => InstanceId != 0;

    /// <summary>The slot of a queue type, or -1 (vmangos <c>GetBattleGroundQueueIndex</c>).</summary>
    public int SlotOf(BattlegroundQueueType type)
    {
        for (int i = 0; i < _queue.Length; i++)
        {
            if (_queue[i] == type)
            {
                return i;
            }
        }

        return -1;
    }

    public BattlegroundQueueType QueueTypeInSlot(int slot)
        => Invariant.Check((uint)slot < (uint)_queue.Length, $"battleground queue slot {slot} is out of range")
            ? _queue[slot]
            : BattlegroundQueueType.None;

    public bool InAnyQueue => _queue.Any(q => q != BattlegroundQueueType.None);

    /// <summary>The instance the player was invited to for the queue type, 0 for none (vmangos <c>IsInvitedForBattleGroundQueueType</c>).</summary>
    public uint InvitedInstance(BattlegroundQueueType type)
    {
        int slot = SlotOf(type);
        return slot < 0 ? 0 : _invitedToInstance[slot];
    }

    internal bool HasFreeSlot(uint queuesCount)
    {
        for (int i = 0; i < queuesCount && i < _queue.Length; i++)
        {
            if (_queue[i] == BattlegroundQueueType.None)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos <c>AddBattleGroundQueueId</c>: the first free slot.</summary>
    internal int AddSlot(BattlegroundQueueType type)
    {
        for (int i = 0; i < _queue.Length; i++)
        {
            if (_queue[i] == BattlegroundQueueType.None)
            {
                _queue[i] = type;
                _invitedToInstance[i] = 0;
                return i;
            }
        }

        return -1;
    }

    internal bool RemoveSlot(BattlegroundQueueType type)
    {
        int slot = SlotOf(type);
        if (slot < 0)
        {
            return false;
        }

        _queue[slot] = BattlegroundQueueType.None;
        _invitedToInstance[slot] = 0;
        return true;
    }

    internal void SetInvite(BattlegroundQueueType type, uint instanceId)
    {
        int slot = SlotOf(type);
        if (slot >= 0)
        {
            _invitedToInstance[slot] = instanceId;
        }
    }
}

/// <summary>A CMSG_BATTLEMASTER_JOIN / CMSG_BATTLEFIELD_JOIN after the handler validated the battlemaster or the portal.</summary>
public sealed record BattlegroundJoinRequest(
    BattlegroundMember Requester,
    BattlegroundType Type,
    uint DesiredInstanceId,
    bool AsGroup,
    bool QueuedAtPortal,
    IReadOnlyList<BattlegroundMember> Group);

/// <summary>A queue candidate with the facts about it the manager cannot know itself (deserter, level, side).</summary>
public sealed record BattlegroundMember(ObjectGuid Guid, Team Team, uint Level, bool InWorld, bool Deserter);

/// <summary>The outcome of a join request.</summary>
public enum BattlegroundJoinOutcome
{
    /// <summary>Queued.</summary>
    Queued,

    /// <summary>Nothing to do (ignored like vmangos: invalid type, already in a battleground, no template, below the minimum level, already queued, no free slot).</summary>
    Ignored,

    /// <summary>A group tried to queue for Alterac Valley (vmangos flags it as an attempt to cheat).</summary>
    GroupForAlterac,

    /// <summary>A deserter, answered with the deserter result.</summary>
    Deserter,

    /// <summary>The group could not join; <see cref="BattlegroundJoinResult.GroupError"/> says why.</summary>
    GroupError,
}

public sealed record BattlegroundJoinResult(BattlegroundJoinOutcome Outcome, BattlegroundJoinError GroupError, IReadOnlyList<ObjectGuid> Queued);

/// <summary>CMSG_BATTLEFIELD_PORT after the handler read it (vmangos <c>HandleBattleFieldPortOpcode</c>).</summary>
public sealed record BattlegroundPortRequest(ObjectGuid Player, uint MapId, byte Action, uint Level, bool Deserter);

public enum BattlegroundPortOutcome
{
    /// <summary>Not a valid request (no such type, not queued, no invitation, a cheat): nothing changed.</summary>
    Ignored,

    /// <summary>The player left the queue (a request to enter that was downgraded by a rule also ends here).</summary>
    LeftQueue,

    /// <summary>The player was sent to the match; the world-port acknowledgement adds it to the match.</summary>
    Entering,
}

/// <summary>One slot of the status poll answer (vmangos <c>HandleBattlefieldStatusOpcode</c>).</summary>
public sealed record BattlegroundStatusReport(uint QueueSlot, BattlegroundStatusSubject Subject, BattlegroundStatus Status, uint Time1, uint Time2);

/// <summary>
/// The battleground manager (vmangos <c>BattleGroundMgr</c> and the queue handlers): the templates, the three queues, the running matches,
/// the free-slot lists, the client-visible instance ids, the scheduled queue updates and the invitation timers. Time moves only through
/// <see cref="Update"/>. World thread.
/// <para>
/// Not delivered: the premade-queue announcer, randomized
/// queue order and the debug "testing" mode of vmangos (docs/areas/battlegrounds.md).
/// </para>
/// </summary>
public sealed partial class BattlegroundManager : IBattlegroundLifecycle, Death.IBattlegroundPresence
{
    private readonly Dictionary<BattlegroundType, BattlegroundTemplate> _templates = [];
    private readonly BattlegroundQueue[] _queues;
    private readonly Dictionary<BattlegroundType, SortedDictionary<uint, Battleground>> _running = [];
    private readonly Dictionary<BattlegroundType, List<Battleground>> _freeSlots = [];
    private readonly Dictionary<(BattlegroundType Type, int Bracket), SortedSet<uint>> _clientIds = [];
    private readonly Dictionary<ObjectGuid, BattlegroundPlayerState> _states = [];
    private readonly List<(BattlegroundQueueType Queue, BattlegroundType Type, int Bracket)> _scheduled = [];
    private readonly List<InviteEvent> _events = [];
    private readonly BattlegroundFactory _factory;
    private readonly BattlegroundPorts _basePorts;
    private long _eventSequence;

    private sealed record InviteEvent(long Sequence, uint DueMs, bool Remove, ObjectGuid Player, uint InstanceId, BattlegroundType Type, uint RemoveTimeMs);

    public BattlegroundManager(BattlegroundOptions options, IBattlegroundManagerHost host, BattlegroundPorts? basePorts = null, BattlegroundFactory? factory = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _basePorts = basePorts ?? new BattlegroundPorts();
        _factory = factory ?? BattlegroundFactories.Default;
        _queues = new BattlegroundQueue[4];
        for (int i = 1; i < _queues.Length; i++)
        {
            _queues[i] = new BattlegroundQueue(this);
        }
    }

    public BattlegroundOptions Options { get; }

    internal IBattlegroundManagerHost Host { get; }

    /// <summary>The manager clock in milliseconds: the sum of the <see cref="Update"/> diffs.</summary>
    public uint NowMs { get; private set; }

    // ------------------------------------------------------------------ templates and types

    /// <summary>The map of a type (vmangos <c>GetBattleGrounMapIdByTypeId</c>, SharedDefines.h:1671-1680).</summary>
    public static uint MapOfType(BattlegroundType type) => type switch
    {
        BattlegroundType.AlteracValley => 30,
        BattlegroundType.WarsongGulch => 489,
        BattlegroundType.ArathiBasin => 529,
        _ => 0,
    };

    /// <summary>The type of a map (vmangos <c>GetBattleGroundTypeIdByMapId</c>, SharedDefines.h:1660-1669).</summary>
    public static BattlegroundType TypeOfMap(uint mapId) => mapId switch
    {
        30 => BattlegroundType.AlteracValley,
        489 => BattlegroundType.WarsongGulch,
        529 => BattlegroundType.ArathiBasin,
        _ => BattlegroundType.None,
    };

    public void RegisterTemplate(BattlegroundTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.Type == BattlegroundType.None || template.MapId != MapOfType(template.Type))
        {
            throw new ArgumentException($"battleground template {template.Type} is not on its map ({template.MapId})", nameof(template));
        }

        if (!Invariant.Check(template.IsFinite(),
            $"battleground template {template.Type} has non-finite start coordinates"))
        {
            return;
        }

        _templates[template.Type] = template;
    }

    public BattlegroundTemplate? TemplateOf(BattlegroundType type) => _templates.GetValueOrDefault(type);

    // ------------------------------------------------------------------ matches

    /// <summary>A running match by instance id and type (vmangos <c>GetBattleGround</c>).</summary>
    public Battleground? GetBattleground(uint instanceId, BattlegroundType type)
        => _running.TryGetValue(type, out SortedDictionary<uint, Battleground>? set) && set.TryGetValue(instanceId, out Battleground? bg) ? bg : null;

    /// <summary>A running match by the instance id the client knows (vmangos <c>GetBattleGroundThroughClientInstance</c>).</summary>
    public Battleground? GetBattlegroundThroughClientInstance(uint clientInstanceId, BattlegroundType type)
        => _templates.ContainsKey(type) && _running.TryGetValue(type, out SortedDictionary<uint, Battleground>? set)
            ? set.Values.FirstOrDefault(bg => bg.ClientInstanceId == clientInstanceId)
            : null;

    public IEnumerable<Battleground> RunningBattlegrounds => _running.Values.SelectMany(s => s.Values);

    /// <summary>The queued players of a type's queue by team, every bracket (vmangos .bg status, MiscCommands.cpp:1777-1797).</summary>
    public (int Alliance, int Horde) QueuedTeamCounts(BattlegroundType type)
        => TemplateOf(type) is { } template ? QueueOf(template.QueueType).TeamCounts() : (0, 0);

    /// <summary>The instance ids the client lists for a type and bracket, in order (vmangos <c>m_clientBattleGroundIds</c>).</summary>
    public IReadOnlyList<uint> ClientInstanceIds(BattlegroundType type, int bracket)
        => bracket is >= 0 and <= BattlegroundConstants.LastBracket && _clientIds.TryGetValue((type, bracket), out SortedSet<uint>? ids) ? [.. ids] : [];

    internal IEnumerable<Battleground> FreeSlotBattlegrounds(BattlegroundType type)
        => _freeSlots.TryGetValue(type, out List<Battleground>? list) ? list.ToArray() : [];

    /// <summary>The lowest unused client-visible instance id from 1 (vmangos <c>CreateClientVisibleInstanceId</c>, BattleGroundMgr.cpp:1217-1236).</summary>
    private uint CreateClientVisibleInstanceId(BattlegroundType type, int bracket)
    {
        if (!_clientIds.TryGetValue((type, bracket), out SortedSet<uint>? ids))
        {
            ids = [];
            _clientIds[(type, bracket)] = ids;
        }

        uint last = 1;
        foreach (uint id in ids)
        {
            if (last == id)
            {
                last++;
            }
            else
            {
                break;
            }
        }

        ids.Add(last);
        return last;
    }

    /// <summary>Create a match of a type for a bracket (vmangos <c>CreateNewBattleGround</c>, BattleGroundMgr.cpp:1239-1279); null without a template or a supported type.</summary>
    internal Battleground? CreateNewBattleground(BattlegroundType type, int bracket)
    {
        if (!Invariant.Check(bracket is >= 0 and <= BattlegroundConstants.LastBracket, $"battleground bracket {bracket} is out of range")
            || !_templates.TryGetValue(type, out BattlegroundTemplate? template))
        {
            return null;
        }

        uint instanceId = Host.AllocateInstanceId();
        uint clientId = CreateClientVisibleInstanceId(type, bracket);
        BattlegroundPorts ports = new()
        {
            Host = Host.CreateMatchHost(type, instanceId),
            Spells = _basePorts.Spells,
            Honor = _basePorts.Honor,
            Ranks = _basePorts.Ranks,
            Reputation = _basePorts.Reputation,
            Calendar = _basePorts.Calendar,
            Random = _basePorts.Random,
            Lifecycle = this,
            Scripts = _basePorts.Scripts,
        };

        Battleground? bg = _factory(template, bracket, instanceId, clientId, Options, ports);
        if (bg is null)
        {
            _clientIds[(type, bracket)].Remove(clientId);
            return null;
        }

        Host.BattlegroundCreated(bg);
        return bg;
    }

    /// <summary>The match starts taking players and is registered (vmangos <c>BattleGround::StartBattleGround</c> → <c>AddBattleGround</c>).</summary>
    internal void StartBattleground(Battleground bg)
    {
        bg.StartBattleground();
        if (!_running.TryGetValue(bg.Type, out SortedDictionary<uint, Battleground>? set))
        {
            set = [];
            _running[bg.Type] = set;
        }

        set[bg.InstanceId] = bg;
    }

    private void DeleteBattleground(Battleground bg)
    {
        bg.RemoveFromFreeSlotQueue();
        if (_running.TryGetValue(bg.Type, out SortedDictionary<uint, Battleground>? set))
        {
            set.Remove(bg.InstanceId);
        }

        if (_clientIds.TryGetValue((bg.Type, bg.Bracket), out SortedSet<uint>? ids))
        {
            ids.Remove(bg.ClientInstanceId);
        }

        Host.BattlegroundDeleted(bg);
    }

    // ------------------------------------------------------------------ IBattlegroundLifecycle

    /// <inheritdoc />
    public void AddToFreeSlotQueue(Battleground battleground)
    {
        if (!_freeSlots.TryGetValue(battleground.Type, out List<Battleground>? list))
        {
            list = [];
            _freeSlots[battleground.Type] = list;
        }

        if (!list.Contains(battleground))
        {
            list.Insert(0, battleground);   // push_front
        }
    }

    /// <inheritdoc />
    public void RemoveFromFreeSlotQueue(Battleground battleground)
    {
        if (_freeSlots.TryGetValue(battleground.Type, out List<Battleground>? list))
        {
            list.RemoveAll(b => b.InstanceId == battleground.InstanceId);
        }
    }

    /// <inheritdoc />
    public void ScheduleQueueUpdate(Battleground battleground)
        => ScheduleQueueUpdate(battleground.Type, battleground.Bracket);

    /// <summary>Run the queue of a type and bracket at the next <see cref="Update"/> (vmangos <c>ScheduleQueueUpdate</c>, once per key).</summary>
    public void ScheduleQueueUpdate(BattlegroundType type, int bracket)
    {
        var queueType = (BattlegroundQueueType)(byte)type;
        if (!Invariant.Check(bracket is >= 0 and <= BattlegroundConstants.LastBracket && (int)queueType >= 1 && (int)queueType < _queues.Length,
            $"invalid scheduled queue update ({type}, bracket {bracket})"))
        {
            return;
        }

        var key = (queueType, type, bracket);
        if (!_scheduled.Contains(key))
        {
            _scheduled.Add(key);
        }
    }

    // ------------------------------------------------------------------ players

    /// <summary>The state of a player (queue slots, bound match); created on first use.</summary>
    public BattlegroundPlayerState StateOf(ObjectGuid player)
    {
        Invariant.Check(!player.IsEmpty, "battleground player state queried for an empty ObjectGuid");
        if (!_states.TryGetValue(player, out BattlegroundPlayerState? state))
        {
            state = new BattlegroundPlayerState();
            _states[player] = state;
        }

        return state;
    }

    internal BattlegroundQueue QueueOf(BattlegroundQueueType type)
    {
        int index = (int)type;
        if (!Invariant.Check(index >= 1 && index < _queues.Length, $"battleground queue index {index} ({type}) is out of range"))
        {
            return _queues[1];
        }

        return _queues[index];
    }

    /// <summary>
    /// The status of the match the player is bound to, or null when it is in none (vmangos <c>Player::GetBattleGround</c>; the death
    /// rules refuse a corpse reclaim while it is not <see cref="BattlegroundStatus.InProgress"/>). Creates no player state.
    /// </summary>
    public BattlegroundStatus? MatchStatusOf(ObjectGuid player)
        => _states.TryGetValue(player, out BattlegroundPlayerState? state) && state.InBattleground
            ? GetBattleground(state.InstanceId, state.Type)?.Status
            : null;

    /// <summary>The queued group of a player, or null.</summary>
    public QueuedGroup? QueuedGroupOf(ObjectGuid player, BattlegroundType type)
    {
        int q = (int)(BattlegroundQueueType)(byte)type;
        return q >= 1 && q < _queues.Length ? _queues[q]?.GroupOf(player) : null;
    }

    /// <summary>The average wait the queue reports for the player's group.</summary>
    public uint AverageWaitOf(ObjectGuid player, BattlegroundType type, uint level)
    {
        BattlegroundTemplate? template = TemplateOf(type);
        QueuedGroup? group = QueuedGroupOf(player, type);
        if (template is null || group is null)
        {
            return 0;
        }

        int bracket = BattlegroundConstants.BracketOfLevel(level, template.MinLevel);
        return bracket < 0 ? 0 : QueueOf(template.QueueType).AverageQueueWaitTime(group, bracket);
    }

    // ------------------------------------------------------------------ joining

    /// <summary>
    /// A player (or its group) asks to join a queue (vmangos <c>WorldSession::RequestBgJoinQueue</c>, BattleGroundHandler.cpp:88-264, after the
    /// battlemaster or portal check). Sends the status and group-joined packets through the host.
    /// </summary>
    public BattlegroundJoinResult JoinQueue(BattlegroundJoinRequest request)
    {
        BattlegroundJoinResult Ignored() => new(BattlegroundJoinOutcome.Ignored, BattlegroundJoinError.Ok, []);

        if (!Invariant.CheckNotNull(request, nameof(request)) || !Invariant.Check(!request.Requester.Guid.IsEmpty, "battleground join request has an empty requester guid"))
        {
            return Ignored();
        }

        BattlegroundType type = request.Type;
        if (type == BattlegroundType.None)
        {
            return Ignored();
        }

        if (type == BattlegroundType.AlteracValley && request.AsGroup)
        {
            return new BattlegroundJoinResult(BattlegroundJoinOutcome.GroupForAlterac, BattlegroundJoinError.Ok, []);
        }

        BattlegroundMember self = request.Requester;
        BattlegroundPlayerState selfState = StateOf(self.Guid);

        // Ignore if the player is already in a battleground.
        // A binding to a match that no longer exists (deleted while the player was loading in) is none, as vmangos's outdated cleanup.
        if (selfState.InBattleground && !ClearStaleBinding(self.Guid))
        {
            return Ignored();
        }

        // The instance the client asked for, or the template.
        Battleground? requested = request.DesiredInstanceId != 0 ? GetBattlegroundThroughClientInstance(request.DesiredInstanceId, type) : null;
        if (!_templates.TryGetValue(type, out BattlegroundTemplate? template))
        {
            return Ignored();
        }

        int bracket = BattlegroundConstants.BracketOfLevel(self.Level, template.MinLevel);
        if (bracket < 0)
        {
            return Ignored();
        }

        BattlegroundQueueType queueType = template.QueueType;
        BattlegroundQueue queue = QueueOf(queueType);
        BattlegroundStatusSubject subject = requested is not null ? BattlegroundStatusSubject.Of(requested) : BattlegroundStatusSubject.OfTemplate(template);
        uint maxPerTeam = requested?.MaxPlayersPerTeam ?? template.MaxPlayersPerTeam;
        List<ObjectGuid> queued = [];

        if (!request.AsGroup)
        {
            // Deserter debuff.
            if (self.Deserter)
            {
                Host.SendGroupJoined(self.Guid, BattlegroundPackets.GroupJoinDeserters);
                return new BattlegroundJoinResult(BattlegroundJoinOutcome.Deserter, BattlegroundJoinError.Ok, []);
            }

            // Already in this queue, or no free queue slot.
            if (selfState.SlotOf(queueType) >= 0 || !selfState.HasFreeSlot(Options.EffectiveQueuesCount))
            {
                return Ignored();
            }

            QueuedGroup group = queue.AddGroup(self.Guid, self.Team, [self.Guid], 1, type, bracket, isPremade: false, request.DesiredInstanceId, NowMs, isGroup: false);
            uint avg = queue.AverageQueueWaitTime(group, bracket);
            int slot = selfState.AddSlot(queueType);
            Host.StoreEntryPoint(self.Guid, self.Guid, request.QueuedAtPortal);
            Host.SendStatus(self.Guid, (uint)Math.Max(slot, 0), subject, BattlegroundStatus.WaitQueue, avg, 0);
            queued.Add(self.Guid);
        }
        else
        {
            if (request.Group.Count == 0)
            {
                return Ignored();
            }

            BattlegroundJoinError error = CheckGroupCanJoin(request.Group, self, template, queueType, maxPerTeam, out List<ObjectGuid> excluded);
            bool isPremade = Options.PremadeGroupWaitForMatchMs != 0 && request.Group.Count >= Options.PremadeQueueMinGroupSize;

            if (error == BattlegroundJoinError.GroupDeserter)
            {
                Host.SendGroupJoined(self.Guid, BattlegroundPackets.GroupJoinDeserters);
                Host.SendJoinError(self.Guid, error);
                return new BattlegroundJoinResult(BattlegroundJoinOutcome.GroupError, error, []);
            }

            if (error != BattlegroundJoinError.Ok)
            {
                Host.SendJoinError(self.Guid, error);
                return new BattlegroundJoinResult(BattlegroundJoinOutcome.GroupError, error, []);
            }

            List<ObjectGuid> members = [.. request.Group.Where(m => !excluded.Contains(m.Guid)).Select(m => m.Guid)];
            QueuedGroup group = queue.AddGroup(self.Guid, self.Team, members, request.Group.Count, type, bracket, isPremade, request.DesiredInstanceId, NowMs, isGroup: true);
            uint avg = queue.AverageQueueWaitTime(group, bracket);
            foreach (BattlegroundMember member in request.Group)
            {
                if (excluded.Contains(member.Guid))
                {
                    Host.SendGroupJoined(member.Guid, BattlegroundPackets.GroupJoinFailed);
                    continue;
                }

                BattlegroundPlayerState state = StateOf(member.Guid);
                int slot = state.AddSlot(queueType);
                Host.StoreEntryPoint(member.Guid, self.Guid, request.QueuedAtPortal);
                Host.SendStatus(member.Guid, (uint)Math.Max(slot, 0), subject, BattlegroundStatus.WaitQueue, avg, 0);
                Host.SendGroupJoined(member.Guid, template.MapId);
                queued.Add(member.Guid);
            }
        }

        ScheduleQueueUpdate(type, bracket);
        return new BattlegroundJoinResult(BattlegroundJoinOutcome.Queued, BattlegroundJoinError.Ok, queued);
    }

    /// <summary>The rules a group must meet to queue together (vmangos <c>Group::CanJoinBattleGroundQueue</c>, Group.cpp:2077-2122).</summary>
    private BattlegroundJoinError CheckGroupCanJoin(IReadOnlyList<BattlegroundMember> group, BattlegroundMember leader, BattlegroundTemplate template, BattlegroundQueueType queueType, uint maxPlayers, out List<ObjectGuid> excluded)
    {
        excluded = [];
        if (group.Count > maxPlayers)
        {
            return BattlegroundJoinError.GroupTooMany;
        }

        int leaderBracket = BattlegroundConstants.BracketOfLevel(leader.Level, template.MinLevel);
        foreach (BattlegroundMember member in group)
        {
            if (!Invariant.Check(!member.Guid.IsEmpty, "battleground group member has an empty guid"))
            {
                return BattlegroundJoinError.OfflineMember;
            }

            // An offline member lets nobody join.
            if (!member.InWorld)
            {
                return BattlegroundJoinError.OfflineMember;
            }

            // No cross-faction group.
            if (member.Team != leader.Team)
            {
                return BattlegroundJoinError.MixedFaction;
            }

            // Not in the same level bracket: left out, the rest queue.
            if (BattlegroundConstants.BracketOfLevel(member.Level, template.MinLevel) != leaderBracket && !excluded.Contains(member.Guid))
            {
                excluded.Add(member.Guid);
            }

            BattlegroundPlayerState state = StateOf(member.Guid);
            if (state.SlotOf(queueType) >= 0)
            {
                return BattlegroundJoinError.GroupMemberAlreadyInQueue;
            }

            if (member.Deserter)
            {
                return BattlegroundJoinError.GroupDeserter;
            }

            if (!state.HasFreeSlot(Options.EffectiveQueuesCount))
            {
                return BattlegroundJoinError.AllQueuesUsed;
            }

            if (!Options.TagInBattlegrounds && state.InBattleground && !ClearStaleBinding(member.Guid))
            {
                return BattlegroundJoinError.OfflineMember;
            }
        }

        return BattlegroundJoinError.Ok;
    }

    // ------------------------------------------------------------------ invitations

    /// <summary>A player was invited to a match: remember it, start the timers and send the invitation (BattleGroundMgr.cpp:428-443).</summary>
    internal void OnInvited(ObjectGuid player, Battleground bg, QueuedGroup group)
    {
        if (!Invariant.CheckNotNull(bg, nameof(bg)) || !Invariant.CheckNotNull(group, nameof(group)) || !Invariant.Check(!player.IsEmpty, $"battleground invitation for {player} has missing state"))
        {
            return;
        }

        BattlegroundPlayerState state = StateOf(player);
        BattlegroundQueueType queueType = bg.Template.QueueType;
        state.SetInvite(queueType, group.InvitedToInstanceId);

        AddEvent(NowMs + BattlegroundConstants.InvitationRemindTimeMs, remove: false, player, group.InvitedToInstanceId, bg.Type, group.RemoveInviteTimeMs);
        AddEvent(NowMs + BattlegroundConstants.InviteAcceptWaitTimeMs, remove: true, player, group.InvitedToInstanceId, bg.Type, group.RemoveInviteTimeMs);

        int slot = state.SlotOf(queueType);
        Host.SendStatus(player, (uint)Math.Max(slot, 0), BattlegroundStatusSubject.Of(bg), BattlegroundStatus.WaitJoin, BattlegroundConstants.InviteAcceptWaitTimeMs, 0);
    }

    private void AddEvent(uint dueMs, bool remove, ObjectGuid player, uint instanceId, BattlegroundType type, uint removeTimeMs)
        => _events.Add(new InviteEvent(_eventSequence++, dueMs, remove, player, instanceId, type, removeTimeMs));

    private void RunInviteEvents()
    {
        while (true)
        {
            InviteEvent? due = _events.Where(e => e.DueMs <= NowMs).OrderBy(e => e.DueMs).ThenBy(e => e.Sequence).FirstOrDefault();
            if (due is null)
            {
                return;
            }

            _events.Remove(due);
            if (due.Remove)
            {
                RunRemoveEvent(due);
            }
            else
            {
                RunRemindEvent(due);
            }
        }
    }

    /// <summary>"You can still enter": a status with the time left, sent a minute after the invitation (vmangos <c>BgQueueInviteEvent</c>, BattleGroundMgr.cpp:897-922).</summary>
    private void RunRemindEvent(InviteEvent e)
    {
        if (!Host.IsOnline(e.Player) || GetBattleground(e.InstanceId, e.Type) is not { } bg)
        {
            return;
        }

        BattlegroundQueueType queueType = bg.Template.QueueType;
        int slot = StateOf(e.Player).SlotOf(queueType);
        if (slot >= 0 && QueueOf(queueType).IsPlayerInvited(e.Player, e.InstanceId, e.RemoveTimeMs))
        {
            Host.SendStatus(e.Player, (uint)slot, BattlegroundStatusSubject.Of(bg), BattlegroundStatus.WaitJoin, BattlegroundConstants.InviteAcceptWaitTimeMs - BattlegroundConstants.InvitationRemindTimeMs, 0);
        }
    }

    /// <summary>
    /// The invitation lapsed: remove the player from the queue if it is still invited to this very match with this deadline, the only case where
    /// it did not answer (vmangos <c>BGQueueRemoveEvent</c>, BattleGroundMgr.cpp:938-970).
    /// </summary>
    private void RunRemoveEvent(InviteEvent e)
    {
        if (!Host.IsOnline(e.Player))
        {
            return;
        }

        BattlegroundQueueType queueType = (BattlegroundQueueType)(byte)e.Type;
        BattlegroundPlayerState state = StateOf(e.Player);
        int slot = state.SlotOf(queueType);
        if (slot < 0 || !QueueOf(queueType).IsPlayerInvited(e.Player, e.InstanceId, e.RemoveTimeMs))
        {
            return;
        }

        Battleground? bg = GetBattleground(e.InstanceId, e.Type);
        state.RemoveSlot(queueType);
        QueueOf(queueType).RemovePlayer(e.Player, true);
        if (bg is not null && bg.Status != BattlegroundStatus.WaitLeave)
        {
            ScheduleQueueUpdate(e.Type, bg.Bracket);
        }

        if (_templates.TryGetValue(e.Type, out BattlegroundTemplate? template))
        {
            Host.SendStatus(e.Player, (uint)slot, BattlegroundStatusSubject.OfTemplate(template), BattlegroundStatus.None, 0, 0);
        }
    }

    /// <summary>Free a player's queue slot and tell it (vmangos <c>RemoveBattleGroundQueueId</c> + the empty status).</summary>
    internal void ReleaseQueueSlot(ObjectGuid player, BattlegroundType type, Battleground? bg)
    {
        BattlegroundQueueType queueType = (BattlegroundQueueType)(byte)type;
        BattlegroundPlayerState state = StateOf(player);
        int slot = state.SlotOf(queueType);
        if (slot < 0)
        {
            return;
        }

        state.RemoveSlot(queueType);
        if (bg is not null)
        {
            Host.SendStatus(player, (uint)slot, BattlegroundStatusSubject.Of(bg), BattlegroundStatus.None, 0, 0);
        }
        else if (_templates.TryGetValue(type, out BattlegroundTemplate? template))
        {
            Host.SendStatus(player, (uint)slot, BattlegroundStatusSubject.OfTemplate(template), BattlegroundStatus.None, 0, 0);
        }
    }

    // ------------------------------------------------------------------ the port

    /// <summary>
    /// The player answered an invitation, or asked to leave a queue (vmangos <c>HandleBattleFieldPortOpcode</c>, BattleGroundHandler.cpp:361-507).
    /// </summary>
    public BattlegroundPortOutcome HandlePort(BattlegroundPortRequest request)
    {
        if (!Invariant.CheckNotNull(request, nameof(request)) || !Invariant.Check(!request.Player.IsEmpty, "battleground port request has an empty player guid"))
        {
            return BattlegroundPortOutcome.Ignored;
        }

        BattlegroundType type = TypeOfMap(request.MapId);
        if (type == BattlegroundType.None || !_templates.TryGetValue(type, out BattlegroundTemplate? template))
        {
            return BattlegroundPortOutcome.Ignored;
        }

        BattlegroundPlayerState state = StateOf(request.Player);
        if (!state.InAnyQueue)
        {
            return BattlegroundPortOutcome.Ignored;
        }

        BattlegroundQueueType queueType = template.QueueType;
        BattlegroundQueue queue = QueueOf(queueType);
        QueuedGroup? group = queue.GroupOf(request.Player);
        if (group is null)
        {
            return BattlegroundPortOutcome.Ignored;
        }

        byte action = request.Action;

        // If action == 1, the instance is required.
        if (group.InvitedToInstanceId == 0 && action == 1)
        {
            return BattlegroundPortOutcome.Ignored;
        }

        Battleground? bg = GetBattleground(group.InvitedToInstanceId, type);
        if (bg is null && action != 0)
        {
            return BattlegroundPortOutcome.Ignored;
        }

        // Some checks if the player is not cheating.
        if (action == 1)
        {
            // A deserter who tries to enter is just removed from the queue, with the message.
            if (request.Deserter)
            {
                Host.SendGroupJoined(request.Player, BattlegroundPackets.GroupJoinDeserters);
                action = 0;
            }

            // A player who levelled past the maximum while waiting does not get in.
            if (bg is not null && request.Level > bg.MaxLevel)
            {
                action = 0;
            }

            // Do not enter a battleground that is already over.
            if (bg is not null && bg.Status == BattlegroundStatus.WaitLeave)
            {
                action = 0;
            }
        }

        int queueSlot = state.SlotOf(queueType);
        if (!Invariant.Check(queueSlot >= 0, $"player {request.Player} in group for {queueType} has no queue slot"))
        {
            return BattlegroundPortOutcome.Ignored;
        }

        if (action == 1)
        {
            if (bg is null || state.InvitedInstance(queueType) == 0)
            {
                // Not invited in this queue type: a cheat.
                return BattlegroundPortOutcome.Ignored;
            }

            Host.PrepareToPortIn(request.Player);
            Host.SendStatus(request.Player, (uint)queueSlot, BattlegroundStatusSubject.Of(bg), BattlegroundStatus.InProgress, 0, bg.StartTimeMs);

            // Remove the queue status; this also leaves another battleground without the deserter debuff.
            queue.RemovePlayer(request.Player, false);
            if (state.InBattleground && GetBattleground(state.InstanceId, state.Type) is { } current)
            {
                current.RemovePlayerAtLeave(request.Player, teleportToEntryPoint: false, sendStatus: true);
            }

            state.InstanceId = bg.InstanceId;
            state.Type = type;
            state.Team = group.Team;
            Host.SendToBattleground(request.Player, bg, group.Team);
            return BattlegroundPortOutcome.Entering;
        }

        if (action != 0)
        {
            // vmangos logs "unknown action" and does nothing.
            return BattlegroundPortOutcome.Ignored;
        }

        // Leave the queue.
        state.RemoveSlot(queueType);
        queue.RemovePlayer(request.Player, true);
        int bracket = BattlegroundConstants.BracketOfLevel(request.Level, template.MinLevel);
        if (bracket >= 0)
        {
            ScheduleQueueUpdate(type, bracket);
        }

        Host.SendStatus(request.Player, (uint)queueSlot, bg is not null ? BattlegroundStatusSubject.Of(bg) : BattlegroundStatusSubject.OfTemplate(template), BattlegroundStatus.None, 0, 0);
        return BattlegroundPortOutcome.LeftQueue;
    }

    /// <summary>
    /// The world-port acknowledgement arrived: the player is in the match's map, add it (vmangos <c>HandleMoveWorldportAckOpcode</c> →
    /// <c>BattleGround::AddPlayer</c>). False when the player is bound to no running match, or was not invited to it: vmangos adds a player
    /// only <c>if (_player->IsInvitedForBattleGroundInstance(...))</c>, so a player bound by a GM command (<see cref="BindPlayer"/>) is on the map
    /// but not a participant (no team count, no scoreboard row, no invitation to give back when it leaves).
    /// </summary>
    public bool EnterBattleground(ObjectGuid player)
    {
        if (!Invariant.Check(!player.IsEmpty, "EnterBattleground called for an empty player guid"))
        {
            return false;
        }

        BattlegroundPlayerState state = StateOf(player);
        if (!state.InBattleground || state.Team is not { } team || GetBattleground(state.InstanceId, state.Type) is not { } bg)
        {
            return false;
        }

        // The invitation of the queue slot outlives the acceptance (the slot stays until the player leaves), so a queue entrant passes.
        if (state.InvitedInstance(bg.Template.QueueType) != bg.InstanceId)
        {
            return false;
        }

        return bg.AddPlayer(player, team);
    }

    /// <summary>
    /// Bind a player to a running match without a queue invitation (vmangos <c>Player::SetBattleGroundId(id, type)</c>, which the GM commands
    /// <c>.goname</c> and <c>.namego</c> call, TeleportCommands.cpp:1165, 1254). The world-port acknowledgement then adds the player to the
    /// match with <paramref name="team"/> (<see cref="EnterBattleground"/>); <see cref="ClearBinding"/> undoes it when the player leaves.
    /// </summary>
    public void BindPlayer(ObjectGuid player, Battleground battleground, Team team)
    {
        if (!Invariant.Check(!player.IsEmpty, "BindPlayer called for an empty player guid")
            || !Invariant.CheckNotNull(battleground, nameof(battleground)))
        {
            return;
        }

        BattlegroundPlayerState state = StateOf(player);
        state.InstanceId = battleground.InstanceId;
        state.Type = battleground.Type;
        state.Team = team;
    }

    /// <summary>
    /// Undo <see cref="BindPlayer"/> for a player that never entered the match (the teleport was refused): the binding fields go and the
    /// queue slots stay, unlike <see cref="ClearBinding"/>, which also frees the slot of the match's type.
    /// </summary>
    public void UnbindPlayer(ObjectGuid player)
    {
        if (!Invariant.Check(!player.IsEmpty, "UnbindPlayer called for an empty player guid"))
        {
            return;
        }

        BattlegroundPlayerState state = StateOf(player);
        state.InstanceId = 0;
        state.Type = BattlegroundType.None;
        state.Team = null;
    }

    /// <summary>
    /// A player left its match (vmangos <c>BattleGround::RemovePlayerAtLeave</c>, BattleGround.cpp:945-960 and 989-991): drop the queue slot of the
    /// match's type (only when it holds the invitation to this very match, so the queue slot of a player that a GM command bound to a match
    /// it was never invited to is kept) and the binding. Called from the match host's <see cref="IBattlegroundHost.ClearPlayerBinding"/>.
    /// </summary>
    public void ClearBinding(ObjectGuid player)
    {
        if (!Invariant.Check(!player.IsEmpty, "ClearBinding called for an empty player guid"))
        {
            return;
        }

        BattlegroundPlayerState state = StateOf(player);
        if (state.InBattleground)
        {
            var queueType = (BattlegroundQueueType)(byte)state.Type;
            if (state.InvitedInstance(queueType) == state.InstanceId)
            {
                state.RemoveSlot(queueType);
            }
        }

        state.InstanceId = 0;
        state.Type = BattlegroundType.None;
        state.Team = null;
    }

    /// <summary>
    /// A binding whose match is gone (the match was deleted while the player was loading in, or the player was bound by a GM command to a
    /// match that then ended) is none (vmangos "cleanup setting if outdated"): clear it. True when a stale binding was cleared; false when
    /// the player has no binding or its match is running.
    /// </summary>
    public bool ClearStaleBinding(ObjectGuid player)
    {
        if (!Invariant.Check(!player.IsEmpty, "ClearStaleBinding called for an empty player guid"))
        {
            return false;
        }

        BattlegroundPlayerState state = StateOf(player);
        if (!state.InBattleground || GetBattleground(state.InstanceId, state.Type) is not null)
        {
            return false;
        }

        ClearBinding(player);
        return true;
    }

    // ------------------------------------------------------------------ logout and login

    /// <summary>The player logged out: its queue slots are dropped, its place in the queue stays for a minute (vmangos <c>PlayerLoggedOut</c>, BattleGroundMgr.cpp:1759-1769).</summary>
    public void PlayerLoggedOut(ObjectGuid player)
    {
        if (!Invariant.Check(!player.IsEmpty, "PlayerLoggedOut called for an empty player guid"))
        {
            return;
        }

        BattlegroundPlayerState state = StateOf(player);
        for (int slot = 0; slot < BattlegroundOptions.MaxQueuesPerPlayer; slot++)
        {
            BattlegroundQueueType queueType = state.QueueTypeInSlot(slot);
            if (queueType != BattlegroundQueueType.None)
            {
                state.RemoveSlot(queueType);
                QueueOf(queueType).PlayerLoggedOut(player, NowMs);
            }
        }
    }

    /// <summary>A player logged in: if it is still in a queue, give it a slot and show the status again (vmangos <c>PlayerLoggedIn</c>, BattleGroundMgr.cpp:1731-1757).</summary>
    public void PlayerLoggedIn(ObjectGuid player, uint level)
    {
        if (!Invariant.Check(!player.IsEmpty, "PlayerLoggedIn called for an empty player guid"))
        {
            return;
        }

        BattlegroundPlayerState state = StateOf(player);
        for (int q = 1; q <= BattlegroundOptions.MaxQueuesPerPlayer; q++)
        {
            var queueType = (BattlegroundQueueType)q;
            BattlegroundQueue queue = _queues[q];
            if (!queue.PlayerLoggedIn(player) || queue.GroupOf(player) is not { } group)
            {
                continue;
            }

            var type = (BattlegroundType)q;
            BattlegroundTemplate? template = TemplateOf(type);
            int bracket = template is null ? -1 : BattlegroundConstants.BracketOfLevel(level, template.MinLevel);
            uint avg = bracket < 0 ? 0 : queue.AverageQueueWaitTime(group, bracket);
            int slot = state.AddSlot(queueType);
            if (template is not null && slot >= 0)
            {
                Host.SendStatus(player, (uint)slot, BattlegroundStatusSubject.OfTemplate(template), BattlegroundStatus.WaitQueue, avg, NowMs - group.JoinTimeMs);
            }

            if (group.InvitedToInstanceId != 0 && slot >= 0)
            {
                state.SetInvite(queueType, group.InvitedToInstanceId);
                uint offset = NowMs > group.RemoveInviteTimeMs ? 1 : group.RemoveInviteTimeMs - NowMs;
                AddEvent(NowMs + offset, remove: true, player, group.InvitedToInstanceId, type, group.RemoveInviteTimeMs);
            }
        }
    }

    // ------------------------------------------------------------------ the status poll

    /// <summary>The answer to CMSG_BATTLEFIELD_STATUS (vmangos <c>HandleBattlefieldStatusOpcode</c>, BattleGroundHandler.cpp:527-576): one status per occupied queue slot.</summary>
    public IReadOnlyList<BattlegroundStatusReport> StatusReports(ObjectGuid player, uint level)
    {
        List<BattlegroundStatusReport> reports = [];
        if (!Invariant.Check(!player.IsEmpty, "StatusReports called for an empty player guid"))
        {
            return reports;
        }

        BattlegroundPlayerState state = StateOf(player);
        for (int slot = 0; slot < BattlegroundOptions.MaxQueuesPerPlayer; slot++)
        {
            BattlegroundQueueType queueType = state.QueueTypeInSlot(slot);
            if (queueType == BattlegroundQueueType.None)
            {
                continue;
            }

            var type = (BattlegroundType)(byte)queueType;
            if (state.InBattleground && type == state.Type && GetBattleground(state.InstanceId, state.Type) is { } current)
            {
                // The player is inside: the end time and the start time.
                reports.Add(new BattlegroundStatusReport((uint)slot, BattlegroundStatusSubject.Of(current), BattlegroundStatus.InProgress, (uint)current.EndTimeMs, current.StartTimeMs));
                continue;
            }

            QueuedGroup? group = QueueOf(queueType).GroupOf(player);
            if (group is null)
            {
                continue;
            }

            if (group.InvitedToInstanceId != 0)
            {
                if (GetBattleground(group.InvitedToInstanceId, type) is not { } invitedTo)
                {
                    continue;
                }

                uint remaining = group.RemoveInviteTimeMs > NowMs ? group.RemoveInviteTimeMs - NowMs : 0;
                reports.Add(new BattlegroundStatusReport((uint)slot, BattlegroundStatusSubject.Of(invitedTo), BattlegroundStatus.WaitJoin, remaining, 0));
            }
            else if (TemplateOf(type) is { } template)
            {
                uint avg = AverageWaitOf(player, type, level);
                reports.Add(new BattlegroundStatusReport((uint)slot, BattlegroundStatusSubject.OfTemplate(template), BattlegroundStatus.WaitQueue, avg, NowMs - group.JoinTimeMs));
            }
        }

        return reports;
    }

    /// <summary>Send the status poll answer.</summary>
    public void SendStatusReports(ObjectGuid player, uint level)
    {
        foreach (BattlegroundStatusReport report in StatusReports(player, level))
        {
            Host.SendStatus(player, report.QueueSlot, report.Subject, report.Status, report.Time1, report.Time2);
        }
    }

    // ------------------------------------------------------------------ the update

    /// <summary>Advance the clock and everything on it (vmangos <c>BattleGroundMgr::Update</c> plus the map updates of the matches and the invitation events).</summary>
    public void Update(uint diffMs)
    {
        NowMs += diffMs;

        RunInviteEvents();

        foreach (Battleground bg in RunningBattlegrounds.ToArray())
        {
            if (!bg.Update(diffMs))
            {
                DeleteBattleground(bg);
            }
        }

        // The scheduled queues, copied first: running them may schedule more for the next round.
        var scheduled = _scheduled.ToArray();
        _scheduled.Clear();
        foreach ((BattlegroundQueueType queue, BattlegroundType type, int bracket) in scheduled)
        {
            QueueOf(queue).Update(type, bracket);
        }
    }
}

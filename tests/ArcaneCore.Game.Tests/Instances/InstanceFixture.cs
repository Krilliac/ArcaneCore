using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Social;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Records instance persistence calls.</summary>
internal sealed class RecordingInstancePersistence : IInstancePersistence
{
    public List<string> Calls { get; } = [];

    public HashSet<uint> Instances { get; } = [];

    public HashSet<(uint Character, uint Instance, bool Permanent)> Binds { get; } = [];

    public Dictionary<uint, long> ResetTimes { get; } = [];

    public Dictionary<uint, (uint Map, uint Instance)> Last { get; } = [];

    public void InstanceSaved(InstanceSave save)
    {
        Calls.Add($"save {save.InstanceId}");
        Instances.Add(save.InstanceId);
    }

    public void InstanceDeleted(uint instanceId)
    {
        Calls.Add($"delete {instanceId}");
        Instances.Remove(instanceId);
        Binds.RemoveWhere(b => b.Instance == instanceId);
        GroupBinds.RemoveWhere(b => b.Instance == instanceId);
    }

    public void PlayerBound(uint characterId, uint instanceId, bool permanent)
    {
        Calls.Add($"bind {characterId} {instanceId} {permanent}");
        Binds.RemoveWhere(b => b.Character == characterId && b.Instance == instanceId);
        Binds.Add((characterId, instanceId, permanent));
    }

    public void PlayerUnbound(uint characterId, uint instanceId)
    {
        Calls.Add($"unbind {characterId} {instanceId}");
        Binds.RemoveWhere(b => b.Character == characterId && b.Instance == instanceId);
    }

    public void RaidResetTimeChanged(uint mapId, long resetTime) => ResetTimes[mapId] = resetTime;

    public void PlayerEnteredInstance(uint characterId, uint mapId, uint instanceId) => Last[characterId] = (mapId, instanceId);

    public void InstanceDataSaved(InstanceSave save) => Calls.Add($"data {save.InstanceId} {save.Data}");

    /// <summary>Stored group binds, keyed by the leader's character id (vmangos <c>group_instance</c>).</summary>
    public HashSet<(uint Leader, uint Instance, bool Permanent)> GroupBinds { get; } = [];

    public void GroupBound(uint leaderCharacterId, uint instanceId, bool permanent)
    {
        Calls.Add($"group bind {leaderCharacterId} {instanceId} {permanent}");
        GroupBinds.RemoveWhere(b => b.Leader == leaderCharacterId && b.Instance == instanceId);
        GroupBinds.Add((leaderCharacterId, instanceId, permanent));
    }

    public void GroupUnbound(uint leaderCharacterId, uint instanceId)
    {
        Calls.Add($"group unbind {leaderCharacterId} {instanceId}");
        GroupBinds.RemoveWhere(b => b.Leader == leaderCharacterId && b.Instance == instanceId);
    }
}

/// <summary>
/// A world with maps, groups, teleports and the instance manager wired as the world daemon
/// does, with a settable clock. Map 36 is a 5-man dungeon (player limit 3 here to test the
/// cap), 409 a raid with a 7-day reset; trigger 78 on map 0 leads into 36, trigger 1000 on 36 out.
/// </summary>
internal sealed class InstanceFixture : IDisposable
{
    public const uint Dungeon = 36;
    public const uint Raid = 409;

    /// <summary>2026-10-01 00:00:00 UTC.</summary>
    public const long Start = 1_790_812_800;

    public static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(Dungeon, 0, MapType.Instance, 0, 3, 0, 0, -11208f, 1672f, "Deadmines", ""),
            new MapTemplate(Raid, 0, MapType.Raid, 0, 40, 7, 0, -7510f, -1036f, "Molten Core", ""),
        ],
        [],
        [
            new AreaTriggerTemplate(78, 0, -11208f, 1672f, 24f, 5f, 0, 0, 0, 0, "Deadmines entrance"),
            new AreaTriggerTemplate(1000, Dungeon, -16f, -383f, 61f, 5f, 0, 0, 0, 0, "Deadmines exit"),
        ],
        [
            new AreaTriggerTeleport(78, "Deadmines", "", 10, Dungeon, -16.4f, -383.07f, 61.78f, 1.86f),
            new AreaTriggerTeleport(1000, "Deadmines exit", "", 0, 0, -11209.6f, 1666.54f, 24.69f, 1.42f),
        ],
        []);

    private readonly Dictionary<ObjectGuid, FakeSession> _sessions = [];

    public InstanceFixture(InstanceOptions? options = null, bool load = true)
    {
        World = TestWorld.CreateRuntime();
        WorldMaps.Of(World).Load(Content);
        Social = new SocialContext(World, Characters, new FakePersistence());
        Teleports = new TeleportService(World, _ => { }, _ => { });
        World.PlayerLoggingOut += Teleports.Forget;
        Manager = new InstanceManager(World, options ?? new InstanceOptions(), Persistence, () => Now);
        Manager.GroupOf = Social.Groups.GetGroup;
        Manager.TeleportToHomebind = Teleports.TeleportToHomebind;
        Manager.SystemMessage = (player, text) => SystemMessages.Add((player, text));
        Social.Groups.MemberAdded += Manager.OnGroupMemberAdded;
        Social.Groups.MemberRemoved += Manager.OnGroupMemberRemoved;
        Social.Groups.Disbanding += Manager.OnGroupDisbanding;
        Social.Groups.LeaderChanged += Manager.OnGroupLeaderChanged;
        Manager.Install();
        if (load)
        {
            Manager.Load(InstanceStoreSnapshot.Empty);
        }
    }

    public long Now { get; set; } = Start;

    public WorldRuntime World { get; }

    public FakeCharacters Characters { get; } = new();

    public SocialContext Social { get; }

    public TeleportService Teleports { get; }

    public InstanceManager Manager { get; }

    public RecordingInstancePersistence Persistence { get; } = new();

    public List<(Player Player, string Text)> SystemMessages { get; } = [];

    /// <summary>An online level-60 player P{guid} on map 0 near the Deadmines, bound (hearthstone) at Stormwind.</summary>
    public Player AddPlayer(uint guid, bool gm = false)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, -11208f + guid, 1672f, session);
        player.Level = 60;
        player.Home = new HomeBind(0, 1519, -8833f, 628f, 94f);
        if (gm)
        {
            player.Flags |= PlayerFlags.Gm;
        }

        World.AddPlayer(player);
        Characters.Add(new CharacterInfo(guid, (int)guid, player.Name, Race.Human, player.Class));
        Social.Friends.Load(player, []);
        _sessions[player.Guid] = session;
        return player;
    }

    public FakeSession Session(Player player) => _sessions[player.Guid];

    public List<byte[]> Sent(Player player, WorldOpcode opcode)
        => [.. Session(player).Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    public void ClearAll()
    {
        foreach (FakeSession session in _sessions.Values)
        {
            session.Clear();
        }
    }

    public Group Party(Player leader, params Player[] members)
    {
        foreach (Player member in members)
        {
            Social.Groups.Invite(leader, member.Name);
            Social.Groups.Accept(member);
        }

        return Social.Groups.GetGroup(leader.Guid)!;
    }

    public Group RaidGroup(Player leader, params Player[] members)
    {
        Group group = Party(leader, members);
        Social.Groups.ConvertToRaid(leader);
        Assert.True(group.IsRaid);
        return group;
    }

    /// <summary>Run a whole far teleport (schedule → SMSG_NEW_WORLD → worldport ack → arrival); returns whether it started.</summary>
    public bool Teleport(Player player, uint mapId, float x, float y, float z)
    {
        if (!Teleports.TeleportTo(player, mapId, x, y, z, 0))
        {
            return false;
        }

        Tick();
        if (Teleports.StageOf(player) == TeleportStage.Far)
        {
            Teleports.HandleWorldportAck(player);
            Tick();
        }
        else if (Teleports.StageOf(player) == TeleportStage.Near)
        {
            Teleports.HandleTeleportAck(player, player.Guid.Value);
        }

        return true;
    }

    public bool EnterDungeon(Player player) => Teleport(player, Dungeon, -16.4f, -383.07f, 61.78f);

    public bool EnterRaid(Player player) => Teleport(player, Raid, 1096f, -467f, -104f);

    public bool LeaveToContinent(Player player) => Teleport(player, 0, -11209.6f, 1666.54f, 24.69f);

    public void Tick(uint diffMs = 50) => World.RunTick(diffMs);

    public void Dispose() => World.Dispose();
}

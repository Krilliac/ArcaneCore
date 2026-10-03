using System.Buffers.Binary;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// Per-instance maps, binds, resets and the instance rules of 1.12 (vmangos
/// MapPersistentStateManager, DungeonMap, Player/Group instance code), driven through real
/// far teleports and group operations.
/// </summary>
public sealed class InstanceManagerTests
{
    private const long Day = 86400;

    /// <summary>The first global reset of the raid after <see cref="InstanceFixture.Start"/>: today + 7 days + 04:00.</summary>
    private const long FirstRaidReset = Start + (7 * Day) + (4 * 3600);

    private static uint[] U32s(byte[] payload)
    {
        var values = new uint[payload.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(i * 4));
        }

        return values;
    }

    [Fact]
    public void TwoGroups_EnteringTheSameDungeon_GetDifferentInstanceMaps()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3), d = f.AddPlayer(4);
        Group first = f.Party(a, b);
        Group second = f.Party(c, d);

        Assert.True(f.EnterDungeon(a));
        Assert.True(f.EnterDungeon(c));
        Assert.True(f.EnterDungeon(b));
        Assert.True(f.EnterDungeon(d));

        Map one = a.Map!, two = c.Map!;
        Assert.Equal(Dungeon, one.MapId);
        Assert.Equal(Dungeon, two.MapId);
        Assert.NotEqual(one.InstanceId, two.InstanceId);
        Assert.NotSame(one, two);
        Assert.Same(one, b.Map);
        Assert.Same(two, d.Map);
        Assert.True(one.InstanceId > 100); // vmangos RESERVED_INSTANCES_LAST
        Assert.Same(one, f.World.FindMap(Dungeon, one.InstanceId));
        Assert.Null(f.World.FindMap(Dungeon)); // nobody uses the shared copy of a dungeon
        Assert.Equal(one.InstanceId, f.Manager.GetGroupBind(first, Dungeon)!.Value.Save.InstanceId);
        Assert.Equal(two.InstanceId, f.Manager.GetGroupBind(second, Dungeon)!.Value.Save.InstanceId);
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Dungeon)); // a group bind, no personal one
        Assert.Equal(2, one.PlayerCount);

        // Each instance map has its own per-map systems and grids.
        Assert.NotNull(one.FindUpdater<MapCombat>());
        Assert.NotSame(one.FindUpdater<MapCombat>(), two.FindUpdater<MapCombat>());
        Assert.Equal((Dungeon, one.InstanceId), f.Persistence.Last[1]);
    }

    [Fact]
    public void SoloPlayers_GetTheirOwnInstance_AndReturnToIt()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);

        f.EnterDungeon(a);
        f.EnterDungeon(b);
        uint first = a.Map!.InstanceId;
        Assert.NotEqual(first, b.Map!.InstanceId);
        Assert.Equal(first, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save.InstanceId);
        Assert.Contains((1u, first, false), f.Persistence.Binds);

        f.LeaveToContinent(a);
        Assert.Equal(0u, a.Map!.MapId);
        f.EnterDungeon(a);
        Assert.Equal(first, a.Map!.InstanceId);
    }

    [Fact]
    public void EmptyInstance_UnloadsAfterTheDelay_AndKeepsItsSave()
    {
        using var f = new InstanceFixture(new InstanceOptions { UnloadDelayMs = 1000 });
        Player a = f.AddPlayer(1);
        var unloaded = new List<Map>();
        f.World.MapUnloading += unloaded.Add;

        f.EnterDungeon(a);
        Map map = a.Map!;
        uint id = map.InstanceId;
        f.Tick(5000); // occupied: no unload
        Assert.Same(map, f.World.FindMap(Dungeon, id));

        f.LeaveToContinent(a);
        Assert.Equal(0, map.PlayerCount);
        Assert.Equal(950u, f.Manager.StateOf(map)!.UnloadTimerMs);
        f.Tick(600);
        Assert.Same(map, f.World.FindMap(Dungeon, id));

        f.Tick(600);
        Assert.Null(f.World.FindMap(Dungeon, id));
        Assert.Equal([map], unloaded);
        Assert.True(map.IsUnloaded);
        Assert.Null(f.Manager.StateOf(map));
        Assert.NotNull(f.Manager.FindSave(id)); // the bind keeps the save
        Assert.Throws<InvalidOperationException>(() => map.AddPlayer(a));

        // Re-entering recreates the same instance in a fresh map.
        f.EnterDungeon(a);
        Assert.Equal(id, a.Map!.InstanceId);
        Assert.NotSame(map, a.Map);
    }

    [Fact]
    public void ReEntering_BeforeTheUnload_CancelsIt()
    {
        using var f = new InstanceFixture(new InstanceOptions { UnloadDelayMs = 1000 });
        Player a = f.AddPlayer(1);
        f.EnterDungeon(a);
        Map map = a.Map!;
        f.LeaveToContinent(a);
        f.Tick(500);
        f.EnterDungeon(a);
        Assert.Same(map, a.Map);
        Assert.Equal(0u, f.Manager.StateOf(map)!.UnloadTimerMs);
        f.Tick(5000);
        Assert.Same(map, f.World.FindMap(Dungeon, map.InstanceId));
    }

    [Fact]
    public void WorldRuntime_OnlyUnloadsEmptyInstanceMaps()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        f.EnterDungeon(a);
        Map continent = f.World.GetMap(0);

        f.World.UnloadMap(a.Map!); // occupied
        f.World.UnloadMap(continent); // instance 0 never unloads
        f.Tick();

        Assert.Same(a.Map, f.World.FindMap(Dungeon, a.Map!.InstanceId));
        Assert.Same(continent, f.World.FindMap(0));
        Assert.False(continent.IsUnloaded);
    }

    [Fact]
    public void SoloReset_ResetsTheInstance_ButNotWhileInside()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        f.EnterDungeon(a);
        uint id = a.Map!.InstanceId;

        f.ClearAll();
        f.Manager.HandleResetInstances(a); // inside: skipped
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgInstanceReset));
        Assert.NotNull(f.Manager.FindSave(id));

        f.LeaveToContinent(a);
        f.ClearAll();
        f.Manager.HandleResetInstances(a);
        Assert.Equal([Dungeon], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceReset))));
        Assert.Null(f.Manager.FindSave(id));
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Dungeon));
        Assert.Contains($"delete {id}", f.Persistence.Calls);

        f.Tick(); // the reset map unloads at the next update
        Assert.Null(f.World.FindMap(Dungeon, id));

        f.EnterDungeon(a);
        Assert.NotEqual(id, a.Map!.InstanceId);
    }

    [Fact]
    public void GroupReset_OnlyByTheLeader_FailsWhileOccupiedOrMembersOffline()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3);
        Group group = f.Party(a, b, c);
        f.EnterDungeon(a);
        f.EnterDungeon(b);
        uint id = a.Map!.InstanceId;
        f.LeaveToContinent(b);
        f.ClearAll();

        f.Manager.HandleResetInstances(b); // not the leader: ignored
        Assert.Empty(f.Session(b).Sent);

        f.Manager.HandleResetInstances(a); // a is inside
        Assert.Equal([(uint)InstanceResetFailedReason.General, Dungeon], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceResetFailed))));
        Assert.Contains(f.SystemMessages, m => ReferenceEquals(m.Player, a));
        Assert.NotNull(f.Manager.FindSave(id));

        f.LeaveToContinent(a);
        f.World.RemovePlayer(c); // c goes offline (still a member)
        f.ClearAll();
        f.Manager.HandleResetInstances(a);
        Assert.Equal([(uint)InstanceResetFailedReason.Offline, Dungeon], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceResetFailed))));
        Assert.NotNull(f.Manager.FindSave(id));

        f.Social.Groups.UninviteByName(a, c.Name);
        f.ClearAll();
        f.Manager.HandleResetInstances(a);
        Assert.Equal([Dungeon], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceReset))));
        Assert.Null(f.Manager.FindSave(id));
        Assert.Null(f.Manager.GetGroupBind(group, Dungeon));

        f.EnterDungeon(b);
        Assert.NotEqual(id, b.Map!.InstanceId);
    }

    [Fact]
    public void Raid_NeedsARaidGroup_UnlessGameMaster()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), gm = f.AddPlayer(3, gm: true);
        f.Party(a, b); // a party is not enough

        Assert.False(f.EnterRaid(a));
        Assert.Equal([0u, (uint)RaidGroupError.Required], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgRaidGroupOnly))));
        Assert.Equal(0u, a.MapId);
        Assert.False(f.Teleports.IsBeingTeleported(a));

        Assert.True(f.EnterRaid(gm));
        Assert.Equal(Raid, gm.Map!.MapId);
    }

    [Fact]
    public void Raid_WelcomesOnEntry_PermanentBindOnBossKill_AndListsTheLockout()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        Group raid = f.RaidGroup(a, b);
        f.ClearAll();

        Assert.True(f.EnterRaid(a));
        Map map = a.Map!;
        uint[] welcome = U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgRaidInstanceMessage)));
        Assert.Equal([(uint)RaidInstanceMessageType.Welcome, Raid, (uint)(FirstRaidReset - Start)], welcome);
        Assert.Equal(FirstRaidReset, f.Manager.FindSave(map.InstanceId)!.ResetTime);

        f.Manager.PermBindAllPlayers(map, a); // boss with CREATURE_FLAG_EXTRA_INSTANCE_BIND died
        Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceSaveCreated));
        Assert.True(f.Manager.GetPlayerBind(a.Guid, Raid)!.Value.Permanent);
        Assert.True(f.Manager.GetGroupBind(raid, Raid)!.Value.Permanent); // the leader was inside
        Assert.Null(f.Manager.GetPlayerBind(b.Guid, Raid)); // outside: not bound
        Assert.False(f.Manager.FindSave(map.InstanceId)!.CanReset);
        Assert.Contains((1u, map.InstanceId, true), f.Persistence.Binds);

        f.ClearAll();
        f.Manager.SendRaidInfo(a);
        uint[] info = U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgRaidInstanceInfo)));
        Assert.Equal([1u, Raid, (uint)(FirstRaidReset - Start), map.InstanceId], info);

        f.LeaveToContinent(a);
        f.ClearAll();
        f.Manager.HandleResetInstances(a); // raids never reset on request
        Assert.Empty(f.Sent(a, WorldOpcode.SmsgInstanceReset));
        Assert.NotNull(f.Manager.FindSave(map.InstanceId));

        // b enters through the permanent group bind and is locked out too.
        Assert.True(f.EnterRaid(b));
        Assert.Same(map, b.Map);
        Assert.True(f.Manager.GetPlayerBind(b.Guid, Raid)!.Value.Permanent);
        Assert.Single(f.Sent(b, WorldOpcode.SmsgInstanceSaveCreated));
    }

    [Fact]
    public void GlobalRaidReset_WarnsThenUnbindsEveryone_AndSendsPlayersHome()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.RaidGroup(a, b);
        f.EnterRaid(a);
        Map map = a.Map!;
        uint id = map.InstanceId;
        f.Manager.PermBindAllPlayers(map, a);
        Assert.Equal(FirstRaidReset, f.Persistence.ResetTimes[Raid]);
        f.ClearAll();

        f.Now = FirstRaidReset - 3600;
        f.Manager.UpdateSchedule();
        Assert.Equal([(uint)RaidInstanceMessageType.WarningHours, Raid, 3600u], U32s(Assert.Single(f.Sent(a, WorldOpcode.SmsgRaidInstanceMessage))));
        f.Manager.UpdateSchedule(); // each warning once
        Assert.Single(f.Sent(a, WorldOpcode.SmsgRaidInstanceMessage));

        f.Now = FirstRaidReset - 300;
        f.Manager.UpdateSchedule(); // the 15-minute warning was missed; the 5-minute one goes out
        Assert.Equal([(uint)RaidInstanceMessageType.WarningMinutesSoon, Raid, 300u], U32s(f.Sent(a, WorldOpcode.SmsgRaidInstanceMessage)[1]));

        f.Now = FirstRaidReset - 60;
        f.Manager.UpdateSchedule();
        Assert.Null(f.Manager.FindSave(id));
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Raid));
        Assert.Equal(FirstRaidReset + (7 * Day), f.Manager.GetRaidResetTime(Raid));
        Assert.Equal(FirstRaidReset + (7 * Day), f.Persistence.ResetTimes[Raid]);
        Assert.True(f.Teleports.IsBeingTeleportedFar(a)); // to the bind point
        Assert.False(f.Manager.IsInstanceValid(a));

        f.Tick();
        f.Teleports.HandleWorldportAck(a);
        f.Tick();
        Assert.Equal((0u, -8833f, 628f), (a.Map!.MapId, a.X, a.Y));
        f.Tick(); // empty and pending reset: unloads at once
        Assert.Null(f.World.FindMap(Raid, id));
        Assert.True(f.Manager.IsInstanceValid(a));
    }

    [Fact]
    public void NormalDungeon_ResetsTwoHoursAfterCreation_OnlyWhileEmpty()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        f.EnterDungeon(a);
        uint id = a.Map!.InstanceId;
        Assert.Equal(Start + 7200, f.Manager.FindSave(id)!.ResetTime);

        f.Now = Start + (3 * 3600);
        f.Manager.UpdateSchedule();
        f.Tick();
        Assert.NotNull(f.Manager.FindSave(id)); // occupied

        f.LeaveToContinent(a);
        f.Manager.UpdateSchedule();
        f.Tick();
        Assert.Null(f.Manager.FindSave(id));
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Dungeon));
        Assert.Null(f.World.FindMap(Dungeon, id));
    }

    [Fact]
    public void PlayerCap_RefusesExtraPlayers_ExceptGameMasters_AndEjectsLateArrivals()
    {
        using var f = new InstanceFixture(); // dungeon player limit 3
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3), d = f.AddPlayer(4), gm = f.AddPlayer(5, gm: true);
        f.Party(a, b, c, d, gm);
        f.EnterDungeon(a);
        f.EnterDungeon(b);
        Map map = a.Map!;

        // c and d both pass the check at the trigger; d arrives after c filled the instance.
        Assert.True(f.Teleports.TeleportTo(c, Dungeon, -16.4f, -383.07f, 61.78f, 0));
        Assert.True(f.Teleports.TeleportTo(d, Dungeon, -16.4f, -383.07f, 61.78f, 0));
        f.Tick();
        f.Teleports.HandleWorldportAck(c);
        f.Teleports.HandleWorldportAck(d);
        f.ClearAll();
        f.Tick();
        Assert.Same(map, c.Map);
        Assert.Equal(0u, d.Map!.MapId); // returned to where the teleport started
        Assert.Equal([(byte)1], Assert.Single(f.Sent(d, WorldOpcode.SmsgTransferAborted))); // TRANSFER_ABORT_MAX_PLAYERS
        Assert.Equal(3, map.PlayerCount);

        // Now the trigger itself refuses.
        f.ClearAll();
        Assert.False(f.EnterDungeon(d));
        Assert.Single(f.Sent(d, WorldOpcode.SmsgTransferAborted));

        // Game masters do not count and are not capped.
        Assert.True(f.EnterDungeon(gm));
        Assert.Same(map, gm.Map);
    }

    [Fact]
    public void LeavingTheGroupInside_StartsTheHomebindTimer_RejoiningCancelsIt()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3);
        f.Party(a, b, c);
        f.EnterDungeon(a);
        f.EnterDungeon(c);
        Map map = c.Map!;
        f.ClearAll();

        f.Social.Groups.Leave(c);
        Assert.False(f.Manager.IsInstanceValid(c));
        f.Tick();
        Assert.Equal([60000u, (uint)RaidGroupError.Required], U32s(Assert.Single(f.Sent(c, WorldOpcode.SmsgRaidGroupOnly))));

        f.Tick(30000);
        f.Social.Groups.Invite(a, c.Name);
        f.Social.Groups.Accept(c);
        Assert.True(f.Manager.IsInstanceValid(c));
        f.Tick();
        Assert.Equal([0u, (uint)RaidGroupError.Required], U32s(f.Sent(c, WorldOpcode.SmsgRaidGroupOnly)[1]));
        f.Tick(60000);
        Assert.Same(map, c.Map); // stays

        // Leaving again and waiting out the timer sends c to its bind point.
        f.Social.Groups.Leave(c);
        f.Tick();
        f.Tick(60000);
        Assert.True(f.Teleports.IsBeingTeleportedFar(c));
        f.Tick();
        f.Teleports.HandleWorldportAck(c);
        f.Tick();
        Assert.Equal((0u, -8833f), (c.Map!.MapId, c.X));
        Assert.True(f.Manager.IsInstanceValid(c));
        Assert.Same(map, a.Map);
    }

    [Fact]
    public void Disband_LeavesTheRemainingPlayerInside_WithTheInstanceAsASoloBind()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        Group group = f.Party(a, b);
        f.EnterDungeon(a);
        f.EnterDungeon(b);
        uint id = a.Map!.InstanceId;

        f.Social.Groups.Leave(b); // two members: the group disbands, b initiated it
        Assert.Null(f.Social.Groups.GetGroup(a.Guid));
        Assert.Equal(id, f.Manager.GetPlayerBind(a.Guid, Dungeon)!.Value.Save.InstanceId);
        Assert.True(f.Manager.IsInstanceValid(a));
        Assert.False(f.Manager.IsInstanceValid(b));
        Assert.Empty(f.Manager.GetGroupBinds(group));
        Assert.NotNull(f.Manager.FindSave(id));
    }

    [Fact]
    public void CreatingAGroup_TurnsTheLeadersInstanceIntoTheGroups()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        f.EnterDungeon(a);
        uint id = a.Map!.InstanceId;
        f.LeaveToContinent(a);

        Group group = f.Party(a, b);
        Assert.Equal(id, f.Manager.GetGroupBind(group, Dungeon)!.Value.Save.InstanceId);
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Dungeon));

        f.EnterDungeon(b);
        Assert.Equal(id, b.Map!.InstanceId);
    }

    [Fact]
    public void JoiningAGroup_ResetsTheJoinersOwnInstance_UnlessInsideIt()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2), c = f.AddPlayer(3), d = f.AddPlayer(4);
        f.Party(a, b);
        f.EnterDungeon(c);
        uint solo = c.Map!.InstanceId;
        f.LeaveToContinent(c);
        f.EnterDungeon(d);
        uint inside = d.Map!.InstanceId;

        f.Social.Groups.Invite(a, c.Name);
        f.Social.Groups.Accept(c);
        Assert.Null(f.Manager.FindSave(solo));
        Assert.Null(f.Manager.GetPlayerBind(c.Guid, Dungeon));

        f.Social.Groups.Invite(a, d.Name);
        f.Social.Groups.Accept(d);
        Assert.NotNull(f.Manager.FindSave(inside)); // d is inside its own instance
        Assert.Same(f.World.FindMap(Dungeon, inside), d.Map);
    }

    [Fact]
    public void Login_InTheBoundInstance_ReentersIt_ElseAtTheEntrance()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1);
        f.EnterDungeon(a);
        uint id = a.Map!.InstanceId;
        f.World.RemovePlayer(a);

        Player again = Relog(f, 1, Dungeon);
        Assert.Equal(id, again.Map!.InstanceId);
        Assert.Equal(Dungeon, again.MapId);

        f.World.RemovePlayer(again);
        Assert.True(f.Manager.UnbindPlayer(again.Guid, Dungeon));
        Player third = Relog(f, 1, Dungeon);
        Assert.Equal((0u, -11209.6f), (third.Map!.MapId, third.X)); // the exit trigger's target

        f.World.RemovePlayer(third);
        Player raider = Relog(f, 1, Raid); // no bind, no exit trigger: the bind point
        Assert.Equal((0u, -8833f), (raider.Map!.MapId, raider.X));
    }

    [Fact]
    public void Load_RestoresBinds_DropsExpiredUnboundAndUnknownInstances()
    {
        using var f = new InstanceFixture(load: false);
        var snapshot = new InstanceStoreSnapshot(
            [
                new InstanceRecord(150, Dungeon, Start + 3600),
                new InstanceRecord(151, Raid, Start - 10), // expired raid lockout
                new InstanceRecord(152, Dungeon, Start + 3600), // nobody bound
                new InstanceRecord(153, 999, Start + 3600), // unknown map
                new InstanceRecord(154, Raid, Start + Day),
            ],
            [
                new CharacterInstanceBindRecord(1, 150, false),
                new CharacterInstanceBindRecord(1, 151, true),
                new CharacterInstanceBindRecord(2, 154, true),
                new CharacterInstanceBindRecord(3, 777, false), // instance row missing
            ],
            [new InstanceResetRecord(Raid, Start - 100)], // passed while the server was down
            [new CharacterLastInstanceRecord(1, Dungeon, 150)]);

        f.Manager.Load(snapshot);

        Assert.Equal([150u, 154u], f.Manager.Saves.Select(s => s.InstanceId).Order());
        Assert.False(f.Manager.FindSave(154)!.CanReset);
        Assert.Equal(150u, f.Manager.GetPlayerBind(ObjectGuid.Player(1), Dungeon)!.Value.Save.InstanceId);
        Assert.Null(f.Manager.GetPlayerBind(ObjectGuid.Player(1), Raid));
        Assert.Contains("delete 151", f.Persistence.Calls);
        Assert.Contains("delete 152", f.Persistence.Calls);
        Assert.Contains("delete 153", f.Persistence.Calls);
        Assert.Contains("unbind 3 777", f.Persistence.Calls);
        Assert.Equal(Start - 100 + (7 * Day), f.Manager.GetRaidResetTime(Raid)); // weekly phase kept
        Assert.Equal(Start - 100 + (7 * Day), f.Persistence.ResetTimes[Raid]);

        // Logging in where it left off, and new instances never reuse a stored id.
        Player a = Relog(f, 1, Dungeon);
        Assert.Equal(150u, a.Map!.InstanceId);
        Player b = f.AddPlayer(4);
        f.EnterDungeon(b);
        Assert.True(b.Map!.InstanceId > 154);
    }

    [Fact]
    public void KillingAnInstanceBindCreature_BindsEveryoneInsidePermanently()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(1), b = f.AddPlayer(2);
        Group raid = f.RaidGroup(a, b);
        f.EnterRaid(a);
        f.EnterRaid(b);
        Map map = a.Map!;
        MapCombat combat = map.Combat;

        combat.Kill(a, NewCreature(1, extraFlags: 0)); // an ordinary creature
        Assert.False(f.Manager.GetGroupBind(raid, Raid)!.Value.Permanent);
        Assert.Null(f.Manager.GetPlayerBind(a.Guid, Raid));

        combat.Kill(b, NewCreature(2, extraFlags: InstanceManager.CreatureFlagExtraInstanceBind)); // the boss
        Assert.True(f.Manager.GetPlayerBind(a.Guid, Raid)!.Value.Permanent);
        Assert.True(f.Manager.GetPlayerBind(b.Guid, Raid)!.Value.Permanent);
        Assert.True(f.Manager.GetGroupBind(raid, Raid)!.Value.Permanent); // the leader is inside
        Assert.Single(f.Sent(a, WorldOpcode.SmsgInstanceSaveCreated));
        Assert.Single(f.Sent(b, WorldOpcode.SmsgInstanceSaveCreated));
    }

    private static Creature NewCreature(uint counter, uint extraFlags)
    {
        var template = new CreatureTemplate { Entry = 11502, Name = "Ragnaros", Faction = 14, ExtraFlags = extraFlags, MinLevel = 63, MaxLevel = 63 };
        return new Creature(counter, template, null, CreatureContent.Empty, new Random(1));
    }

    private static Player Relog(InstanceFixture f, uint guid, uint mapId)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, -16.4f, -383.07f, session, mapId: mapId);
        player.Level = 60;
        player.Home = new Kernel.Characters.HomeBind(0, 1519, -8833f, 628f, 94f);
        f.World.AddPlayer(player);
        return player;
    }
}

using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using Xunit;

namespace ArcaneCore.Game.Tests.Battlegrounds;

/// <summary>Records every effect a battleground asks the world for; the clock is the test's own <c>Update(diff)</c> calls.</summary>
internal sealed class RecordingHost : IBattlegroundHost
{
    public readonly List<(uint Text, BattlegroundChatKind Kind, ObjectGuid Source)> Announcements = [];
    public readonly List<uint> Sounds = [];
    public readonly List<(uint Field, uint Value)> WorldStates = [];
    public readonly List<(Team Team, ObjectGuid Who)> Joined = [];
    public readonly List<(Team Team, ObjectGuid Who)> Left = [];
    public readonly List<(ObjectGuid Player, BattlegroundStatus Status, uint Time1, uint Time2)> Statuses = [];
    public readonly List<(ObjectGuid Player, bool Won, PvpLogSnapshot Log, uint AutoLeave, uint StartTime)> EndPackets = [];
    public readonly List<(ObjectGuid Player, PvpLogSnapshot Log)> PvpLogs = [];
    public readonly List<ObjectGuid> ResurrectOrStop = [];
    public readonly List<ObjectGuid> ResurrectDead = [];
    public readonly List<ObjectGuid> Blocked = [];
    public readonly List<ObjectGuid> Skinnable = [];
    public readonly List<ObjectGuid> StoppedPets = [];
    public readonly List<(ObjectGuid Player, Team Team)> ReturnToStart = [];
    public readonly List<(byte E1, byte E2, bool Spawn, bool Forced)> Events = [];
    public readonly List<ObjectGuid> DeletedObjects = [];
    public readonly List<ObjectGuid> Leaves = [];
    public readonly List<ObjectGuid> Cleared = [];
    public readonly List<ObjectGuid> TeleportedToEntry = [];
    public readonly List<(ObjectGuid Player, uint Trigger)> Unhandled = [];
    public readonly List<(uint Amount, bool Minutes)> PrematureWarnings = [];
    public readonly HashSet<(ObjectGuid Player, uint Trigger)> InTriggers = [];
    public readonly HashSet<ObjectGuid> NearVictim = [];
    public readonly List<(byte E1, byte E2, bool Spawn, bool Forced, uint Delay)> DelayedEvents = [];
    public readonly List<(uint Text, BattlegroundChatKind Kind, ObjectGuid Source, uint Arg1, uint Arg2)> Formatted = [];
    public readonly List<(uint Text, uint Arg1, uint Arg2)> Yells = [];
    public readonly List<(ObjectGuid Player, uint Entry)> Credits = [];
    public readonly List<uint> QuestsCompleted = [];
    public readonly List<(int Index, uint Entry, float X, float Y, float Z, float O)> AddedObjects = [];
    public readonly List<(int Index, uint Seconds)> ObjectSpawns = [];
    public readonly List<(byte E1, byte E2, BattlegroundSpawnMode Mode)> SpawnModes = [];
    public readonly List<(ObjectGuid Creature, string Text, bool Yell, ObjectGuid Player)> Says = [];
    public readonly List<(byte E1, uint Text)> EventYells = [];
    public readonly List<(byte E1, byte E2)> RemovedEventObjects = [];
    public readonly Dictionary<ObjectGuid, uint> Charms = [];
    public int OpenDoorsCalls;
    public int DespawnDoorsCalls;

    public void SetSpawnEventMode(byte event1, byte event2, BattlegroundSpawnMode mode) => SpawnModes.Add((event1, event2, mode));

    public void CreatureSay(ObjectGuid creature, string text, bool yell, ObjectGuid player) => Says.Add((creature, text, yell, player));

    public void EventCreatureYell(byte event1, uint textId) => EventYells.Add((event1, textId));

    public void RemoveEventGameObjects(byte event1, byte event2) => RemovedEventObjects.Add((event1, event2));

    public uint CharmedEntryOf(ObjectGuid player) => Charms.GetValueOrDefault(player);

    public void Announce(uint textId, BattlegroundChatKind kind, ObjectGuid source) => Announcements.Add((textId, kind, source));

    public void AnnouncePrematureFinish(uint amount, bool inMinutes) => PrematureWarnings.Add((amount, inMinutes));

    public void PlaySoundToAll(uint soundId) => Sounds.Add(soundId);

    public void UpdateWorldState(uint field, uint value) => WorldStates.Add((field, value));

    public void PlayerJoinedTeam(Team team, ObjectGuid joiner) => Joined.Add((team, joiner));

    public void PlayerLeftTeam(Team team, ObjectGuid leaver) => Left.Add((team, leaver));

    public void SendStatus(ObjectGuid player, BattlegroundStatus status, uint time1, uint time2) => Statuses.Add((player, status, time1, time2));

    public void SendEndOfMatch(ObjectGuid player, bool won, PvpLogSnapshot finalScore, uint autoLeaveMs, uint startTimeMs) => EndPackets.Add((player, won, finalScore, autoLeaveMs, startTimeMs));

    public void SendPvpLog(ObjectGuid player, PvpLogSnapshot log) => PvpLogs.Add((player, log));

    public void ResurrectOrStopCombat(ObjectGuid player) => ResurrectOrStop.Add(player);

    public void ResurrectIfDead(ObjectGuid player) => ResurrectDead.Add(player);

    public void StopCombatWithPets(ObjectGuid player) => StoppedPets.Add(player);

    public void BlockMovement(ObjectGuid player) => Blocked.Add(player);

    public void MarkSkinnable(ObjectGuid player) => Skinnable.Add(player);

    public bool IsAtGroupRewardDistance(ObjectGuid player, ObjectGuid victim) => NearVictim.Contains(player);

    public void ReturnToStartIfFar(ObjectGuid player, Team team) => ReturnToStart.Add((player, team));

    public void OpenDoors() => OpenDoorsCalls++;

    public void DespawnDoors() => DespawnDoorsCalls++;

    public void EventStateChanged(byte event1, byte event2, bool spawn, bool forcedDespawn) => Events.Add((event1, event2, spawn, forcedDespawn));

    public void DeleteGameObject(ObjectGuid gameObject) => DeletedObjects.Add(gameObject);

    public bool IsInAreaTrigger(ObjectGuid player, uint areaTriggerId) => InTriggers.Contains((player, areaTriggerId));

    public void LeaveBattleground(ObjectGuid player) => Leaves.Add(player);

    public void ClearPlayerBinding(ObjectGuid player) => Cleared.Add(player);

    public void TeleportToEntryPoint(ObjectGuid player) => TeleportedToEntry.Add(player);

    public void UnhandledAreaTrigger(ObjectGuid player, uint areaTriggerId) => Unhandled.Add((player, areaTriggerId));

    public void EventStateChanged(byte event1, byte event2, bool spawn, bool forcedDespawn, uint respawnDelaySeconds)
    {
        Events.Add((event1, event2, spawn, forcedDespawn));
        DelayedEvents.Add((event1, event2, spawn, forcedDespawn, respawnDelaySeconds));
    }

    public void AnnounceFormatted(uint textId, BattlegroundChatKind kind, ObjectGuid source, uint arg1, uint arg2) => Formatted.Add((textId, kind, source, arg1, arg2));

    public void HeraldYell(uint textId, uint arg1, uint arg2) => Yells.Add((textId, arg1, arg2));

    public void KilledMonsterCredit(ObjectGuid player, uint creatureEntry) => Credits.Add((player, creatureEntry));

    public void CompleteQuestForAll(uint questId) => QuestsCompleted.Add(questId);

    public void AddBattlegroundObject(int index, uint entry, float x, float y, float z, float orientation) => AddedObjects.Add((index, entry, x, y, z, orientation));

    public void SpawnBattlegroundObject(int index, uint respawnSeconds) => ObjectSpawns.Add((index, respawnSeconds));
}

/// <summary>One fake for the spell, honor, rank, reputation, calendar and lifecycle ports.</summary>
internal sealed class RecordingPorts : IBattlegroundSpellPort, IBattlegroundHonorSink, IHonorRankSource, IBattlegroundReputationSink, IBattlegroundCalendar, IBattlegroundLifecycle
{
    public readonly List<(ObjectGuid Player, uint Spell)> Casts = [];
    public readonly List<(ObjectGuid Player, uint Spell)> Removed = [];
    public readonly HashSet<(ObjectGuid Player, uint Spell)> Auras = [];
    public readonly List<ObjectGuid> SpiritOfRedemptionEnded = [];
    public readonly Dictionary<ObjectGuid, uint> Honor = [];
    public readonly List<(ObjectGuid Player, uint Faction, int Amount)> Reputation = [];
    public readonly Dictionary<ObjectGuid, uint> Ranks = [];
    public bool HonorAccepted = true;
    public bool Weekend;
    public int FreeSlotAdds;
    public int FreeSlotRemoves;
    public int QueueUpdates;

    public void CastOnSelf(ObjectGuid player, uint spellId)
    {
        Casts.Add((player, spellId));
        Auras.Add((player, spellId));
    }

    public void RemoveAura(ObjectGuid player, uint spellId)
    {
        Removed.Add((player, spellId));
        Auras.Remove((player, spellId));
    }

    public bool HasAura(ObjectGuid player, uint spellId) => Auras.Contains((player, spellId));

    public void RemoveSpiritOfRedemption(ObjectGuid player) => SpiritOfRedemptionEnded.Add(player);

    public bool TryAddBonusHonor(ObjectGuid player, uint honor)
    {
        if (!HonorAccepted)
        {
            return false;
        }

        Honor[player] = Honor.GetValueOrDefault(player) + honor;
        return true;
    }

    public uint? RankOf(ObjectGuid player) => Ranks.TryGetValue(player, out uint rank) ? rank : null;

    public void Reward(ObjectGuid player, uint factionId, int baseAmount) => Reputation.Add((player, factionId, baseAmount));

    public bool IsBattlegroundWeekend(BattlegroundType type) => Weekend;

    public void AddToFreeSlotQueue(Battleground battleground) => FreeSlotAdds++;

    public void RemoveFromFreeSlotQueue(Battleground battleground) => FreeSlotRemoves++;

    public void ScheduleQueueUpdate(Battleground battleground) => QueueUpdates++;

    public BattlegroundPorts ToPorts(RecordingHost host, Random? random = null) => new()
    {
        Host = host,
        Spells = this,
        Honor = this,
        Ranks = this,
        Reputation = this,
        Calendar = this,
        Lifecycle = this,
        Random = random ?? Random.Shared,
    };
}

/// <summary>Shared builders for the battleground tests.</summary>
internal static class BgTestData
{
    public static readonly ObjectGuid[] Alliance = [.. Enumerable.Range(1, 12).Select(i => ObjectGuid.Player((uint)i))];
    public static readonly ObjectGuid[] Horde = [.. Enumerable.Range(101, 12).Select(i => ObjectGuid.Player((uint)i))];

    /// <summary>The Warsong Gulch row (classic-db battleground_template id 2: 5 to 10 players per team, levels 10 to 60) with the vmangos mark spells.</summary>
    public static BattlegroundTemplate WsgTemplate(uint minPerTeam = 2, uint maxPerTeam = 10) => new()
    {
        Type = BattlegroundType.WarsongGulch,
        MapId = 489,
        Name = "Warsong Gulch",
        MinPlayersPerTeam = minPerTeam,
        MaxPlayersPerTeam = maxPerTeam,
        MinLevel = 10,
        MaxLevel = 60,
        AllianceWinSpell = 24951,
        AllianceLoseSpell = 24950,
        HordeWinSpell = 24951,
        HordeLoseSpell = 24950,
    };

    public static (WarsongGulch Bg, RecordingHost Host, RecordingPorts Ports) NewWsg(int bracket = 5, BattlegroundOptions? options = null, uint minPerTeam = 2, bool weekend = false)
    {
        var host = new RecordingHost();
        var ports = new RecordingPorts { Weekend = weekend };
        var bg = new WarsongGulch(WsgTemplate(minPerTeam), bracket, instanceId: 101, clientInstanceId: 1, options ?? new BattlegroundOptions(), ports.ToPorts(host));
        return (bg, host, ports);
    }

    /// <summary>Add <paramref name="perTeam"/> players to each side and run the start sequence until the match is in progress.</summary>
    public static void StartMatch(WarsongGulch bg, int perTeam = 2)
    {
        bg.StartBattleground();
        for (int i = 0; i < perTeam; i++)
        {
            bg.IncreaseInvitedCount(Team.Alliance);
            bg.IncreaseInvitedCount(Team.Horde);
            Assert.True(bg.AddPlayer(Alliance[i], Team.Alliance));
            Assert.True(bg.AddPlayer(Horde[i], Team.Horde));
        }

        for (int i = 0; i < 400 && bg.Status != BattlegroundStatus.InProgress; i++)
        {
            Assert.True(bg.Update(1000));
        }

        Assert.Equal(BattlegroundStatus.InProgress, bg.Status);
    }

    public static void Tick(Battleground bg, uint totalMs, uint step = 1)
    {
        for (uint done = 0; done < totalMs; done += step)
        {
            bg.Update(Math.Min(step, totalMs - done));
        }
    }
}

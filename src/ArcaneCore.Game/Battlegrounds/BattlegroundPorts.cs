using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// What a battleground needs the rest of the world to do for it. Everything the vmangos <c>BattleGround</c> classes do to a
/// <c>Player*</c>, a <c>GameObject*</c> or a packet goes through this interface, so the rules stay pure and testable with an
/// injected clock; the daemon implements it with the real sessions and maps. World thread.
/// </summary>
public interface IBattlegroundHost
{
    /// <summary>
    /// A text for every player of the match (vmangos <c>SendMessageToAll(entry, chatKind, source)</c>; the text id is a
    /// <c>mangos_string</c>/broadcast text, see <see cref="BattlegroundTexts"/>).
    /// </summary>
    void Announce(uint textId, BattlegroundChatKind kind, ObjectGuid source);

    /// <summary>The premature-finish warning (vmangos LANG_BATTLEGROUND_PREMATURE_FINISH_WARNING / _SECS, BattleGround.cpp:344,350).</summary>
    void AnnouncePrematureFinish(uint amount, bool inMinutes);

    /// <summary>SMSG_PLAY_SOUND to every player of the match (vmangos <c>PlaySoundToAll</c>).</summary>
    void PlaySoundToAll(uint soundId);

    /// <summary>SMSG_UPDATE_WORLD_STATE to every player of the match (vmangos <c>UpdateWorldState</c>; a "-1" value is sent as 0xFFFFFFFF).</summary>
    void UpdateWorldState(uint field, uint value);

    /// <summary>SMSG_BATTLEGROUND_PLAYER_JOINED to <paramref name="team"/> except the joiner (vmangos BattleGround::AddPlayer, BattleGround.cpp:1058).</summary>
    void PlayerJoinedTeam(Team team, ObjectGuid joiner);

    /// <summary>SMSG_BATTLEGROUND_PLAYER_LEFT to <paramref name="team"/> except the leaver (vmangos RemovePlayerAtLeave, BattleGround.cpp:981).</summary>
    void PlayerLeftTeam(Team team, ObjectGuid leaver);

    /// <summary>SMSG_BATTLEFIELD_STATUS for the player's queue slot of this battleground type.</summary>
    void SendStatus(ObjectGuid player, BattlegroundStatus status, uint time1, uint time2);

    /// <summary>
    /// The end-of-match packets for one player (vmangos EndBattleGround, BattleGround.cpp:716-725): SMSG_BATTLEFIELD_WIN or
    /// _LOSE, the final MSG_PVP_LOG_DATA and the IN_PROGRESS status with the auto-leave time.
    /// </summary>
    void SendEndOfMatch(ObjectGuid player, bool won, PvpLogSnapshot finalScore, uint autoLeaveMs, uint startTimeMs);

    /// <summary>MSG_PVP_LOG_DATA (a late joiner into an ended match, BattleGround.cpp:1818).</summary>
    void SendPvpLog(ObjectGuid player, PvpLogSnapshot log);

    /// <summary>Resurrect the player at full health when dead (vmangos <c>ResurrectPlayer(1.0f)</c> + <c>SpawnCorpseBones</c>), otherwise stop combat and clear threat.</summary>
    void ResurrectOrStopCombat(ObjectGuid player);

    /// <summary>Resurrect the player at full health when dead and nothing else (RemovePlayerAtLeave, BattleGround.cpp:937-941).</summary>
    void ResurrectIfDead(ObjectGuid player);

    /// <summary>Stop combat with the player's pets (vmangos <c>CombatStopWithPets(true)</c>).</summary>
    void StopCombatWithPets(ObjectGuid player);

    /// <summary>Disable the client's movement control (vmangos <c>BlockMovement</c>, <c>SetClientControl(player, 0)</c>).</summary>
    void BlockMovement(ObjectGuid player);

    /// <summary>Make the player's corpse lootable by insignia (vmangos sets <c>UNIT_FLAG_SKINNABLE</c>, BattleGround.cpp:1788).</summary>
    void MarkSkinnable(ObjectGuid player);

    /// <summary>Whether <paramref name="player"/> is within group reward distance of the victim (vmangos <c>IsAtGroupRewardDistance</c>).</summary>
    bool IsAtGroupRewardDistance(ObjectGuid player, ObjectGuid victim);

    /// <summary>Repop the player at a graveyard when further than 100 yards from the team's start position (vmangos ReturnPlayersToHomeGY, BattleGround.cpp:1449-1467; a GM is skipped).</summary>
    void ReturnToStartIfFar(ObjectGuid player, Team team);

    /// <summary>Open the door game objects of event <see cref="BattlegroundConstants.EventDoor"/> (vmangos <c>OpenDoorEvent</c>).</summary>
    void OpenDoors();

    /// <summary>Remove the door game objects (vmangos <c>StartingEventDespawnDoors</c>).</summary>
    void DespawnDoors();

    /// <summary>An event became active or inactive: spawn or despawn the creatures and game objects that carry it (vmangos <c>SpawnEvent</c>).</summary>
    void EventStateChanged(byte event1, byte event2, bool spawn, bool forcedDespawn);

    /// <summary>Delete the dropped-flag game object (vmangos RespawnFlagAfterDrop, BattleGroundWS.cpp:173-175).</summary>
    void DeleteGameObject(ObjectGuid gameObject);

    /// <summary>Whether the player stands inside the area trigger (vmangos <c>IsPointInAreaTriggerZone</c> with a 2 yard radius, BattleGroundWS.cpp:188).</summary>
    bool IsInAreaTrigger(ObjectGuid player, uint areaTriggerId);

    /// <summary>The player left through an exit trigger: run the full leave sequence (vmangos <c>Player::LeaveBattleground()</c>).</summary>
    void LeaveBattleground(ObjectGuid player);

    /// <summary>Drop the player's battleground binding (vmangos <c>SetBattleGroundId(0)</c>, <c>SetBGTeam(TEAM_NONE)</c>) and its queue slot of this type.</summary>
    void ClearPlayerBinding(ObjectGuid player);

    /// <summary>Teleport the player to its battleground entry point (vmangos <c>TeleportToBGEntryPoint</c>).</summary>
    void TeleportToEntryPoint(ObjectGuid player);

    /// <summary>"Warning: Unhandled AreaTrigger in Battleground" (vmangos BattleGroundWS.cpp:578).</summary>
    void UnhandledAreaTrigger(ObjectGuid player, uint areaTriggerId);
}

/// <summary>Spells and auras a battleground casts (the flag auras, marks, the deserter debuff); mapped to the aura engine.</summary>
public interface IBattlegroundSpellPort
{
    /// <summary>Cast a spell on the player as a triggered self-cast (vmangos <c>CastSpell(player, spell, true)</c>).</summary>
    void CastOnSelf(ObjectGuid player, uint spellId);

    /// <summary>Remove the auras of a spell (vmangos <c>RemoveAurasDueToSpell</c>).</summary>
    void RemoveAura(ObjectGuid player, uint spellId);

    /// <summary>Whether the player has an aura of the spell.</summary>
    bool HasAura(ObjectGuid player, uint spellId);

    /// <summary>End a Spirit of Redemption form so the player can be resurrected (vmangos BattleGround.cpp:688-689, <c>RemoveSpellsCausingAura(SPELL_AURA_MOD_SHAPESHIFT)</c>).</summary>
    void RemoveSpiritOfRedemption(ObjectGuid player);
}

/// <summary>Bonus honor (vmangos <c>HonorMgr::Add(value, BONUS)</c>); mapped to the honor lane.</summary>
public interface IBattlegroundHonorSink
{
    /// <summary>Add bonus honor; true when it was counted (the scoreboard only shows honor that was added, BattleGround.cpp:1275-1276).</summary>
    bool TryAddBonusHonor(ObjectGuid player, uint honor);
}

/// <summary>Honor rank shown on the scoreboard (vmangos <c>GetHonorMgr().GetRank().rank</c>, BattleGroundMgr.cpp:1085).</summary>
public interface IHonorRankSource
{
    /// <summary>The rank value of an online player, or null when the player is not online (the log then shows <see cref="BattlegroundConstants.DefaultPvpLogRank"/>).</summary>
    uint? RankOf(ObjectGuid player);
}

/// <summary>Reputation for a battleground objective (vmangos <c>RewardReputationToTeam</c>, BattleGround.cpp:586-613); mapped to the reputation lane.</summary>
public interface IBattlegroundReputationSink
{
    /// <summary>
    /// Give the player <paramref name="baseAmount"/> reputation with the faction; the sink applies the spell-source gain
    /// rules (<c>CalculateReputationGain(REPUTATION_SOURCE_SPELL, ...)</c>) and ignores an unknown faction.
    /// </summary>
    void Reward(ObjectGuid player, uint factionId, int baseAmount);
}

/// <summary>Whether a battleground holiday weekend is on (vmangos <c>BattleGroundMgr::IsBgWeekend</c>); mapped to the game-events lane.</summary>
public interface IBattlegroundCalendar
{
    /// <summary>True during the weekend of the type.</summary>
    bool IsBattlegroundWeekend(BattlegroundType type);
}

/// <summary>Bookkeeping the battleground manager does when a battleground changes (free slots, queue updates).</summary>
public interface IBattlegroundLifecycle
{
    /// <summary>The battleground has free slots again: put it in the free-slot list (vmangos <c>AddToBGFreeSlotQueue</c>).</summary>
    void AddToFreeSlotQueue(Battleground battleground);

    /// <summary>The battleground is full or over: take it out of the free-slot list (vmangos <c>RemoveFromBGFreeSlotQueue</c>).</summary>
    void RemoveFromFreeSlotQueue(Battleground battleground);

    /// <summary>Run the queue of the battleground's type and bracket soon (vmangos <c>ScheduleQueueUpdate</c>).</summary>
    void ScheduleQueueUpdate(Battleground battleground);
}

/// <summary>
/// The ports a battleground uses. Every member defaults to an inert object that does nothing and answers "no", so a missing
/// lane (spells, honor, reputation, calendar) leaves the framework running with that effect absent rather than faked.
/// </summary>
public sealed class BattlegroundPorts
{
    public IBattlegroundHost Host { get; init; } = InertBattlegroundHost.Instance;

    public IBattlegroundSpellPort Spells { get; init; } = InertBattlegroundPorts.Instance;

    public IBattlegroundHonorSink Honor { get; init; } = InertBattlegroundPorts.Instance;

    public IHonorRankSource Ranks { get; init; } = InertBattlegroundPorts.Instance;

    public IBattlegroundReputationSink Reputation { get; init; } = InertBattlegroundPorts.Instance;

    public IBattlegroundCalendar Calendar { get; init; } = InertBattlegroundPorts.Instance;

    public IBattlegroundLifecycle Lifecycle { get; init; } = InertBattlegroundPorts.Instance;
}

/// <summary>The inert implementations behind <see cref="BattlegroundPorts"/>' defaults.</summary>
public sealed class InertBattlegroundPorts : IBattlegroundSpellPort, IBattlegroundHonorSink, IHonorRankSource, IBattlegroundReputationSink, IBattlegroundCalendar, IBattlegroundLifecycle
{
    public static InertBattlegroundPorts Instance { get; } = new();

    public void CastOnSelf(ObjectGuid player, uint spellId)
    {
    }

    public void RemoveAura(ObjectGuid player, uint spellId)
    {
    }

    public bool HasAura(ObjectGuid player, uint spellId) => false;

    public void RemoveSpiritOfRedemption(ObjectGuid player)
    {
    }

    public bool TryAddBonusHonor(ObjectGuid player, uint honor) => false;

    public uint? RankOf(ObjectGuid player) => null;

    public void Reward(ObjectGuid player, uint factionId, int baseAmount)
    {
    }

    public bool IsBattlegroundWeekend(BattlegroundType type) => false;

    public void AddToFreeSlotQueue(Battleground battleground)
    {
    }

    public void RemoveFromFreeSlotQueue(Battleground battleground)
    {
    }

    public void ScheduleQueueUpdate(Battleground battleground)
    {
    }
}

/// <summary>A host that does nothing; used when a battleground runs without a world around it.</summary>
public sealed class InertBattlegroundHost : IBattlegroundHost
{
    public static InertBattlegroundHost Instance { get; } = new();

    public void Announce(uint textId, BattlegroundChatKind kind, ObjectGuid source)
    {
    }

    public void AnnouncePrematureFinish(uint amount, bool inMinutes)
    {
    }

    public void PlaySoundToAll(uint soundId)
    {
    }

    public void UpdateWorldState(uint field, uint value)
    {
    }

    public void PlayerJoinedTeam(Team team, ObjectGuid joiner)
    {
    }

    public void PlayerLeftTeam(Team team, ObjectGuid leaver)
    {
    }

    public void SendStatus(ObjectGuid player, BattlegroundStatus status, uint time1, uint time2)
    {
    }

    public void SendEndOfMatch(ObjectGuid player, bool won, PvpLogSnapshot finalScore, uint autoLeaveMs, uint startTimeMs)
    {
    }

    public void SendPvpLog(ObjectGuid player, PvpLogSnapshot log)
    {
    }

    public void ResurrectOrStopCombat(ObjectGuid player)
    {
    }

    public void ResurrectIfDead(ObjectGuid player)
    {
    }

    public void StopCombatWithPets(ObjectGuid player)
    {
    }

    public void BlockMovement(ObjectGuid player)
    {
    }

    public void MarkSkinnable(ObjectGuid player)
    {
    }

    public bool IsAtGroupRewardDistance(ObjectGuid player, ObjectGuid victim) => false;

    public void ReturnToStartIfFar(ObjectGuid player, Team team)
    {
    }

    public void OpenDoors()
    {
    }

    public void DespawnDoors()
    {
    }

    public void EventStateChanged(byte event1, byte event2, bool spawn, bool forcedDespawn)
    {
    }

    public void DeleteGameObject(ObjectGuid gameObject)
    {
    }

    public bool IsInAreaTrigger(ObjectGuid player, uint areaTriggerId) => false;

    public void LeaveBattleground(ObjectGuid player)
    {
    }

    public void ClearPlayerBinding(ObjectGuid player)
    {
    }

    public void TeleportToEntryPoint(ObjectGuid player)
    {
    }

    public void UnhandledAreaTrigger(ObjectGuid player, uint areaTriggerId)
    {
    }
}

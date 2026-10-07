using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>The state of one team's flag (vmangos <c>BG_WS_FlagState</c>, BattleGroundWS.h:62-68; also the wire-adjacent value the scoreboard logic keys on).</summary>
public enum WsgFlagState : byte
{
    /// <summary>In the base, can be taken by the enemy.</summary>
    OnBase = 0,

    /// <summary>Captured; respawns in the base after <see cref="WarsongGulch.FlagRespawnTimeMs"/>.</summary>
    WaitRespawn = 1,

    /// <summary>Carried by an enemy player.</summary>
    OnPlayer = 2,

    /// <summary>Dropped; returns by itself after <see cref="WarsongGulch.FlagDropTimeMs"/> unless picked up.</summary>
    OnGround = 3,
}

/// <summary>
/// The flag game object a player clicked: its entry, the first event number of its battleground spawn row (vmangos
/// <c>GetGameObjectEventIndex(guid).event1</c>, 255 for a spawn that is not in <c>gameobject_battleground</c> such as a dropped flag)
/// and whether the player is within 10 yards of it (vmangos <c>IsWithinDistInMap(go, 10)</c>).
/// </summary>
public readonly record struct WsgFlagObject(uint Entry, byte Event1, bool WithinTenYards);

/// <summary>
/// Warsong Gulch (vmangos BattleGroundWS.cpp/.h): two flags, a score limit of three captures, a 23 second flag respawn and a
/// 10 second return of a dropped flag. The match ends with the third capture (or the common premature-finish rule).
/// <para>
/// Not delivered here: the visible flag auras and the dropped-flag game object come from the spell and game-object layers through
/// <see cref="IBattlegroundSpellPort"/> and <see cref="IBattlegroundHost"/>; with the inert ports the flag is tracked and scored but
/// nothing is shown (docs/areas/battlegrounds.md).
/// </para>
/// </summary>
public sealed class WarsongGulch : Battleground
{
    /// <summary>Captures that win the match (vmangos <c>BG_WS_MAX_TEAM_SCORE</c>).</summary>
    public const int MaxTeamScore = 3;

    /// <summary>The captured flag respawns once its timer is below zero after this many ms (vmangos <c>BG_WS_FLAG_RESPAWN_TIME</c>, 23 s).</summary>
    public const uint FlagRespawnTimeMs = 23_000;

    /// <summary>A dropped flag returns once its timer is below zero after this many ms (vmangos <c>BG_WS_FLAG_DROP_TIME</c>, 10 s).</summary>
    public const uint FlagDropTimeMs = 10_000;

    // BG_WS_Sound (BattleGroundWS.h:31-40)
    public const uint SoundFlagCapturedAlliance = 8173;
    public const uint SoundFlagCapturedHorde = 8213;
    public const uint SoundFlagReturned = 8192;
    public const uint SoundHordeFlagPickedUp = 8212;
    public const uint SoundAllianceFlagPickedUp = 8174;
    public const uint SoundFlagsRespawned = 8232;

    // BG_WS_SpellId (BattleGroundWS.h:42-48)
    public const uint SpellWarsongFlag = 23333;
    public const uint SpellWarsongFlagDropped = 23334;
    public const uint SpellSilverwingFlag = 23335;
    public const uint SpellSilverwingFlagDropped = 23336;

    // BG_WS_WorldStates (BattleGroundWS.h:50-60)
    public const uint WorldStateFlagTakenAlliance = 1545;
    public const uint WorldStateFlagTakenHorde = 1546;
    public const uint WorldStateFlagCapturesAlliance = 1581;
    public const uint WorldStateFlagCapturesHorde = 1582;
    public const uint WorldStateFlagCapturesMax = 1601;
    public const uint WorldStateFlagStateHorde = 2338;
    public const uint WorldStateFlagStateAlliance = 2339;

    // BG_WS_Graveyards (BattleGroundWS.h:70-76): WorldSafeLocs ids
    public const uint GraveyardFlagRoomAlliance = 769;
    public const uint GraveyardFlagRoomHorde = 770;
    public const uint GraveyardMainAlliance = 771;
    public const uint GraveyardMainHorde = 772;

    // BG_WS_GameObjects (BattleGroundWS.h:79-85)
    public const uint HordeFlagBaseEntry = 179831;
    public const uint HordeFlagGroundEntry = 179786;
    public const uint AllianceFlagBaseEntry = 179830;
    public const uint AllianceFlagGroundEntry = 179785;

    // BG_WS_Events (BattleGroundWS.h:102-108)
    public const byte EventFlagAlliance = 0;
    public const byte EventFlagHorde = 1;
    public const byte EventSpiritGuides = 2;

    // BattleGroundWS.h:87-91
    public const uint AreaTriggerAllianceFlagSpawn = 3646;
    public const uint AreaTriggerHordeFlagSpawn = 3647;

    private static readonly uint[] s_flagCapturedHonor = [48, 82, 136, 226, 378, 396];
    private static readonly uint[] s_winMatchHonor = [24, 41, 68, 113, 189, 198];
    private static readonly uint[] s_winMatchHonorHoliday = [48, 82, 136, 226, 378, 396];
    private static readonly uint[] s_winMatchHonorBonusCompleteHoliday = [72, 123, 204, 339, 567, 594];

    /// <summary>Honor for a flag capture by bracket (vmangos <c>BG_WSG_FlagCapturedHonor</c>, BattleGroundWS.h:111).</summary>
    public static uint FlagCapturedHonor(int bracket) => s_flagCapturedHonor[bracket];

    /// <summary>Honor for winning by bracket (vmangos <c>BG_WSG_WinMatchHonor</c>, BattleGroundWS.h:112).</summary>
    public static uint WinMatchHonor(int bracket) => s_winMatchHonor[bracket];

    /// <summary>Extra honor for winning on a battleground weekend (vmangos <c>BG_WSG_WinMatchHonorHolidays</c>, BattleGroundWS.h:113).</summary>
    public static uint WinMatchHonorHoliday(int bracket) => s_winMatchHonorHoliday[bracket];

    /// <summary>Honor both sides get for completing a match on a battleground weekend (vmangos <c>BG_WSG_WinMatchHonorBonusCompleteHolidays</c>, BattleGroundWS.h:114).</summary>
    public static uint WinMatchHonorBonusCompleteHoliday(int bracket) => s_winMatchHonorBonusCompleteHoliday[bracket];

    private readonly ObjectGuid[] _flagKeepers = new ObjectGuid[2];
    private readonly ObjectGuid[] _droppedFlagGuid = new ObjectGuid[2];
    private readonly WsgFlagState[] _flagState = new WsgFlagState[2];
    private readonly int[] _flagsTimer = new int[2];
    private readonly int[] _flagsDropTimer = new int[2];
    private readonly int[] _teamScores = new int[2];
    private readonly int _reputationCapture;

    public WarsongGulch(BattlegroundTemplate template, int bracket, uint instanceId, uint clientInstanceId, BattlegroundOptions options, BattlegroundPorts ports)
        : base(template, bracket, instanceId, clientInstanceId, options, ports)
    {
        if (template.Type != BattlegroundType.WarsongGulch)
        {
            throw new ArgumentException("not a Warsong Gulch template", nameof(template));
        }

        StartMessageIds[1] = BattlegroundTexts.WsStartOneMinute;
        StartMessageIds[2] = BattlegroundTexts.WsStartHalfMinute;
        StartMessageIds[3] = BattlegroundTexts.WsHasBegun;

        // Reset() (BattleGroundWS.cpp:589-618): spirit guides and flags are not spawned at the beginning, the ghost gates are.
        SetActiveEvent(EventSpiritGuides, BattlegroundConstants.EventNone);
        SetActiveEvent(EventFlagAlliance, BattlegroundConstants.EventNone);
        SetActiveEvent(EventFlagHorde, BattlegroundConstants.EventNone);
        SetActiveEvent(BattlegroundConstants.EventGhostGate, 0);

        // Client patch 1.10 and later (the accurate-PvP 20/30 values of vmangos belong to patches before 1.10, BattleGroundWS.cpp:612-614).
        _reputationCapture = Ports.Calendar.IsBattlegroundWeekend(Type) ? 45 : 35;
    }

    // ------------------------------------------------------------------ queries

    private static int Idx(Team team) => BattlegroundConstants.TeamIndex(team);

    /// <summary>The state of <paramref name="flagTeam"/>'s flag (vmangos <c>GetFlagState</c>).</summary>
    public WsgFlagState FlagState(Team flagTeam) => _flagState[Idx(flagTeam)];

    /// <summary>The carrier of <paramref name="flagTeam"/>'s flag, empty when nobody carries it (vmangos <c>Get{Alliance,Horde}FlagPickerGuid</c>).</summary>
    public ObjectGuid FlagPicker(Team flagTeam) => _flagKeepers[Idx(flagTeam)];

    /// <summary>Captures of a team (vmangos <c>GetTeamScore</c>).</summary>
    public int TeamScore(Team team) => _teamScores[Idx(team)];

    /// <summary>The dropped-flag game object of <paramref name="flagTeam"/>'s flag (vmangos <c>GetDroppedFlagGuid</c>).</summary>
    public ObjectGuid DroppedFlagGuid(Team flagTeam) => _droppedFlagGuid[Idx(flagTeam)];

    /// <summary>Record the game object the dropped-flag spell created (vmangos <c>SetDroppedFlagGuid</c>, called from the spell effect).</summary>
    public void SetDroppedFlagGuid(ObjectGuid gameObject, Team flagTeam) => _droppedFlagGuid[Idx(flagTeam)] = gameObject;

    private bool IsPickedUp(Team flagTeam) => !_flagKeepers[Idx(flagTeam)].IsEmpty;

    // ------------------------------------------------------------------ base overrides

    protected override BattlegroundScore CreateScore() => new WsgScore();

    protected override uint WinnerText(Team winner) => winner == Team.Horde ? BattlegroundTexts.WsHordeWins : BattlegroundTexts.WsAllianceWins;

    protected override void StartingEventOpenDoors()
    {
        // BattleGroundWS.cpp:102-112: open the doors, spawn spirit guides and flags, remove the ghost gates.
        if (IsActiveEvent(BattlegroundConstants.EventDoor, 0))
        {
            Host.OpenDoors();
        }

        SpawnEvent(EventSpiritGuides, 0, spawn: true, forcedDespawn: true);
        SpawnEvent(EventFlagAlliance, 0, spawn: true, forcedDespawn: true);
        SpawnEvent(EventFlagHorde, 0, spawn: true, forcedDespawn: true);
        SpawnEvent(BattlegroundConstants.EventGhostGate, 0, spawn: false, forcedDespawn: true);
    }

    /// <inheritdoc />
    public override bool Update(uint diffMs)
    {
        if (Status == BattlegroundStatus.InProgress)
        {
            TickFlag(Team.Alliance, diffMs);
            TickFlag(Team.Horde, diffMs);
        }

        // Last: it reports an empty battleground that must be deleted (BattleGroundWS.cpp:94-95).
        return base.Update(diffMs);
    }

    private void TickFlag(Team flagTeam, uint diffMs)
    {
        int i = Idx(flagTeam);
        if (_flagState[i] == WsgFlagState.WaitRespawn)
        {
            _flagsTimer[i] -= (int)diffMs;
            if (_flagsTimer[i] < 0)
            {
                _flagsTimer[i] = 0;
                RespawnFlag(flagTeam, captured: true);
            }
        }

        if (_flagState[i] == WsgFlagState.OnGround)
        {
            _flagsDropTimer[i] -= (int)diffMs;
            if (_flagsDropTimer[i] < 0)
            {
                _flagsDropTimer[i] = 0;
                RespawnFlagAfterDrop(flagTeam);
            }
        }
    }

    // ------------------------------------------------------------------ flag events

    private void RespawnFlag(Team flagTeam, bool captured)
    {
        if (flagTeam == Team.Alliance)
        {
            _flagState[Idx(Team.Alliance)] = WsgFlagState.OnBase;
            SpawnEvent(EventFlagAlliance, 0, spawn: true, forcedDespawn: true);
        }
        else
        {
            _flagState[Idx(Team.Horde)] = WsgFlagState.OnBase;
            SpawnEvent(EventFlagHorde, 0, spawn: true, forcedDespawn: true);
        }

        if (captured)
        {
            // When map updates are allowed for battlegrounds this code will be useless (vmangos comment, BattleGroundWS.cpp:140).
            SpawnEvent(EventFlagAlliance, 0, spawn: true, forcedDespawn: true);
            SpawnEvent(EventFlagHorde, 0, spawn: true, forcedDespawn: true);
            Host.Announce(BattlegroundTexts.WsFlagsPlaced, BattlegroundChatKind.Neutral, ObjectGuid.Empty);
            Host.PlaySoundToAll(SoundFlagsRespawned);
        }
    }

    private void RespawnFlagAfterDrop(Team flagTeam)
    {
        if (Status != BattlegroundStatus.InProgress)
        {
            return;
        }

        RespawnFlag(flagTeam, captured: false);
        Host.UpdateWorldState(flagTeam == Team.Horde ? WorldStateFlagTakenHorde : WorldStateFlagTakenAlliance, 0);
        Host.Announce(
            flagTeam == Team.Alliance ? BattlegroundTexts.WsAllianceFlagRespawned : BattlegroundTexts.WsHordeFlagRespawned,
            BattlegroundChatKind.Neutral,
            ObjectGuid.Empty);
        Host.PlaySoundToAll(SoundFlagsRespawned);

        ObjectGuid dropped = _droppedFlagGuid[Idx(flagTeam)];
        if (!dropped.IsEmpty)
        {
            Host.DeleteGameObject(dropped);
        }

        _droppedFlagGuid[Idx(flagTeam)] = ObjectGuid.Empty;
        ForceFlagAreaTrigger(flagTeam);
    }

    /// <summary>
    /// If the enemy carrier already stands in the base trigger when the flag is back, capture now (vmangos <c>ForceFlagAreaTrigger</c>,
    /// BattleGroundWS.cpp:183-191): the client only sends an area trigger when a player enters it.
    /// </summary>
    private void ForceFlagAreaTrigger(Team flagTeam)
    {
        ObjectGuid carrier = flagTeam == Team.Alliance ? _flagKeepers[Idx(Team.Horde)] : _flagKeepers[Idx(Team.Alliance)];
        uint trigger = flagTeam == Team.Alliance ? AreaTriggerAllianceFlagSpawn : AreaTriggerHordeFlagSpawn;
        if (carrier.IsEmpty || !Host.IsInAreaTrigger(carrier, trigger))
        {
            return;
        }

        HandleAreaTrigger(carrier, trigger);
    }

    private void UpdateFlagState(Team team, uint value) => Host.UpdateWorldState(team == Team.Alliance ? WorldStateFlagStateAlliance : WorldStateFlagStateHorde, value);

    private void UpdateTeamScore(Team team) => Host.UpdateWorldState(team == Team.Alliance ? WorldStateFlagCapturesAlliance : WorldStateFlagCapturesHorde, (uint)TeamScore(team));

    /// <summary>A player captured the enemy flag at its own base (vmangos <c>EventPlayerCapturedFlag</c>, BattleGroundWS.cpp:193-267).</summary>
    public void OnPlayerCapturedFlag(ObjectGuid source)
    {
        if (PlayerTeam(source) is { } team)
        {
            CaptureFlag(source, team);
        }
    }

    private void CaptureFlag(ObjectGuid source, Team team)
    {
        if (Status != BattlegroundStatus.InProgress)
        {
            return;
        }

        // Only the carrier of the enemy flag scores, and only while its own flag is home: the conditions of the base trigger, which is
        // the only caller of vmangos EventPlayerCapturedFlag (BattleGroundWS.cpp:539-548). The public entry point must not skip them.
        if (_flagKeepers[Idx(BattlegroundConstants.OtherTeam(team))] != source || _flagState[Idx(team)] != WsgFlagState.OnBase)
        {
            return;
        }

        Team? winner = null;
        if (team == Team.Alliance)
        {
            if (!IsPickedUp(Team.Horde))
            {
                return;
            }

            _flagKeepers[Idx(Team.Horde)] = ObjectGuid.Empty;     // before the aura removal, to prevent a drop and a capture at once
            _flagState[Idx(Team.Horde)] = WsgFlagState.WaitRespawn;
            Ports.Spells.RemoveAura(source, SpellWarsongFlag);
            if (TeamScore(Team.Alliance) < MaxTeamScore)
            {
                _teamScores[Idx(Team.Alliance)]++;
            }

            Host.PlaySoundToAll(SoundFlagCapturedAlliance);
            RewardReputationToTeam(890, _reputationCapture, Team.Alliance);
            Host.UpdateWorldState(WorldStateFlagTakenHorde, 0);
        }
        else
        {
            if (!IsPickedUp(Team.Alliance))
            {
                return;
            }

            _flagKeepers[Idx(Team.Alliance)] = ObjectGuid.Empty;
            _flagState[Idx(Team.Alliance)] = WsgFlagState.WaitRespawn;
            Ports.Spells.RemoveAura(source, SpellSilverwingFlag);
            if (TeamScore(Team.Horde) < MaxTeamScore)
            {
                _teamScores[Idx(Team.Horde)]++;
            }

            Host.PlaySoundToAll(SoundFlagCapturedHorde);
            RewardReputationToTeam(889, _reputationCapture, Team.Horde);
            Host.UpdateWorldState(WorldStateFlagTakenAlliance, 0);
        }

        // The capture honor follows the bracket of the battleground.
        RewardHonorToTeam(FlagCapturedHonor(Bracket), team);

        // Despawn the flags.
        SpawnEvent(EventFlagAlliance, 0, spawn: false, forcedDespawn: true);
        SpawnEvent(EventFlagHorde, 0, spawn: false, forcedDespawn: true);

        Host.Announce(
            team == Team.Alliance ? BattlegroundTexts.WsCapturedHordeFlag : BattlegroundTexts.WsCapturedAllianceFlag,
            team == Team.Alliance ? BattlegroundChatKind.Alliance : BattlegroundChatKind.Horde,
            source);

        UpdateFlagState(team, 1);
        UpdateTeamScore(team);
        UpdatePlayerScore(source, BattlegroundScoreType.FlagCaptures, 1);

        if (TeamScore(Team.Alliance) == MaxTeamScore)
        {
            winner = Team.Alliance;
        }

        if (TeamScore(Team.Horde) == MaxTeamScore)
        {
            winner = Team.Horde;
        }

        if (winner is { } w)
        {
            UpdateFlagState(Team.Alliance, 1);
            UpdateFlagState(Team.Horde, 1);
            EndBattleground(w);
        }
        else
        {
            // The captured flag is the other team's: its timer is indexed by that team.
            _flagsTimer[Idx(BattlegroundConstants.OtherTeam(team))] = (int)FlagRespawnTimeMs;
        }
    }

    /// <summary>A carrier lost the flag: death, summon, aura removal or leaving (vmangos <c>EventPlayerDroppedFlag</c>, BattleGroundWS.cpp:269-352).</summary>
    public void OnPlayerDroppedFlag(ObjectGuid source)
    {
        if (PlayerTeam(source) is { } team)
        {
            DropFlag(source, team);
        }
    }

    private void DropFlag(ObjectGuid source, Team team)
    {
        // The flag the player can carry is the enemy's.
        Team flagTeam = BattlegroundConstants.OtherTeam(team);
        uint carriedAura = flagTeam == Team.Horde ? SpellWarsongFlag : SpellSilverwingFlag;
        uint droppedSpell = flagTeam == Team.Horde ? SpellWarsongFlagDropped : SpellSilverwingFlagDropped;

        if (Status != BattlegroundStatus.InProgress)
        {
            // Not running: do not cast things at the dropper (that would spawn the "dropped" flag) and send nothing, just take off the aura.
            if (!IsPickedUp(flagTeam))
            {
                return;
            }

            if (_flagKeepers[Idx(flagTeam)] == source)
            {
                _flagKeepers[Idx(flagTeam)] = ObjectGuid.Empty;
                Ports.Spells.RemoveAura(source, carriedAura);

                // vmangos leaves the state at ON_PLAYER with no carrier; a late joiner's initial world states would then show a carried
                // flag. Nothing is sent and nothing respawns (the match is over), the state just stops claiming a carrier.
                _flagState[Idx(flagTeam)] = WsgFlagState.OnBase;
            }

            return;
        }

        if (!IsPickedUp(flagTeam) || _flagKeepers[Idx(flagTeam)] != source)
        {
            return;
        }

        _flagKeepers[Idx(flagTeam)] = ObjectGuid.Empty;
        Ports.Spells.RemoveAura(source, carriedAura);
        _flagState[Idx(flagTeam)] = WsgFlagState.OnGround;
        Ports.Spells.CastOnSelf(source, droppedSpell);

        UpdateFlagState(team, 1);
        if (team == Team.Alliance)
        {
            // vmangos colours the drop text with the dropped flag's team (BattleGroundWS.cpp:334,343).
            Host.Announce(BattlegroundTexts.WsDroppedHordeFlag, BattlegroundChatKind.Horde, source);
            Host.UpdateWorldState(WorldStateFlagTakenHorde, uint.MaxValue);
        }
        else
        {
            Host.Announce(BattlegroundTexts.WsDroppedAllianceFlag, BattlegroundChatKind.Alliance, source);
            Host.UpdateWorldState(WorldStateFlagTakenAlliance, uint.MaxValue);
        }

        _flagsDropTimer[Idx(flagTeam)] = (int)FlagDropTimeMs;
    }

    /// <summary>
    /// A player used a flag game object (vmangos <c>EventPlayerClickedOnFlag</c>, BattleGroundWS.cpp:354-480): taking the enemy flag from
    /// its base, or taking or returning a dropped one. A guid that is not in the match is ignored.
    /// </summary>
    public void OnFlagClicked(ObjectGuid source, WsgFlagObject flag)
    {
        if (Status != BattlegroundStatus.InProgress || PlayerTeam(source) is not { } team)
        {
            return;
        }

        uint messageId = 0;
        BattlegroundChatKind kind = BattlegroundChatKind.Neutral;
        byte ev = flag.Event1;

        // The Alliance flag taken from its base. The stand must be spawned: a capture despawns both stands until the 23 s respawn, and
        // vmangos relies on the client being unable to use a despawned object (BattleGroundWS.cpp:225-227).
        if (team == Team.Horde && FlagState(Team.Alliance) == WsgFlagState.OnBase && ev == EventFlagAlliance && IsActiveEvent(EventFlagAlliance, 0))
        {
            messageId = BattlegroundTexts.WsPickedUpAllianceFlag;
            kind = BattlegroundChatKind.Horde;
            TakeFlag(source, Team.Alliance);
        }

        // The Horde flag taken from its base.
        if (team == Team.Alliance && FlagState(Team.Horde) == WsgFlagState.OnBase && ev == EventFlagHorde && IsActiveEvent(EventFlagHorde, 0))
        {
            messageId = BattlegroundTexts.WsPickedUpHordeFlag;
            kind = BattlegroundChatKind.Alliance;
            TakeFlag(source, Team.Horde);
        }

        // The Alliance flag on the ground (returned, or picked up again).
        if (FlagState(Team.Alliance) == WsgFlagState.OnGround && flag.WithinTenYards && flag.Entry == AllianceFlagGroundEntry)
        {
            if (team == Team.Alliance)
            {
                messageId = BattlegroundTexts.WsReturnedAllianceFlag;
                kind = BattlegroundChatKind.Alliance;
                ReturnFlag(source, Team.Alliance);
            }
            else
            {
                messageId = BattlegroundTexts.WsPickedUpAllianceFlag;
                kind = BattlegroundChatKind.Horde;
                TakeFlag(source, Team.Alliance);
            }
        }

        // The Horde flag on the ground.
        if (FlagState(Team.Horde) == WsgFlagState.OnGround && flag.WithinTenYards && flag.Entry == HordeFlagGroundEntry)
        {
            if (team == Team.Horde)
            {
                messageId = BattlegroundTexts.WsReturnedHordeFlag;
                kind = BattlegroundChatKind.Horde;
                ReturnFlag(source, Team.Horde);
            }
            else
            {
                messageId = BattlegroundTexts.WsPickedUpHordeFlag;
                kind = BattlegroundChatKind.Alliance;
                TakeFlag(source, Team.Horde);
            }
        }

        if (messageId != 0)
        {
            Host.Announce(messageId, kind, source);
        }
    }

    /// <summary>The enemy takes <paramref name="flagTeam"/>'s flag (from its base or from the ground).</summary>
    private void TakeFlag(ObjectGuid source, Team flagTeam)
    {
        Team carrierTeam = BattlegroundConstants.OtherTeam(flagTeam);
        Host.PlaySoundToAll(flagTeam == Team.Alliance ? SoundAllianceFlagPickedUp : SoundHordeFlagPickedUp);
        SpawnEvent(flagTeam == Team.Alliance ? EventFlagAlliance : EventFlagHorde, 0, spawn: false, forcedDespawn: true);
        _flagKeepers[Idx(flagTeam)] = source;
        _droppedFlagGuid[Idx(flagTeam)] = ObjectGuid.Empty;     // the clicked dropped-flag object is deleted by its use handler
        _flagState[Idx(flagTeam)] = WsgFlagState.OnPlayer;
        // The icon state of the carrier's team (the literal value 2 is "carrying", not a WsgFlagState).
        UpdateFlagState(carrierTeam, (uint)WsgFlagState.OnPlayer);
        Host.UpdateWorldState(flagTeam == Team.Alliance ? WorldStateFlagTakenAlliance : WorldStateFlagTakenHorde, 1);
        Ports.Spells.CastOnSelf(source, flagTeam == Team.Alliance ? SpellSilverwingFlag : SpellWarsongFlag);
    }

    /// <summary>The flag's own team returns it from the ground.</summary>
    private void ReturnFlag(ObjectGuid source, Team flagTeam)
    {
        // UpdateFlagState(other, WAIT_RESPAWN) is the literal value 1 of the other team's icon (BattleGroundWS.cpp:413).
        UpdateFlagState(BattlegroundConstants.OtherTeam(flagTeam), (uint)WsgFlagState.WaitRespawn);
        RespawnFlag(flagTeam, captured: false);

        // The flag is home: its taken state goes back to 0 as on the timeout return (BattleGroundWS.cpp:157). vmangos omits it on a player
        // return, so the client kept the ground marker; mangos-classic resets it (ProcessDroppedFlagActions).
        Host.UpdateWorldState(flagTeam == Team.Alliance ? WorldStateFlagTakenAlliance : WorldStateFlagTakenHorde, 0);
        _droppedFlagGuid[Idx(flagTeam)] = ObjectGuid.Empty;
        Host.PlaySoundToAll(SoundFlagReturned);
        UpdatePlayerScore(source, BattlegroundScoreType.FlagReturns, 1);
        ForceFlagAreaTrigger(flagTeam);
    }

    // ------------------------------------------------------------------ players

    protected override void OnPlayerRemoved(ObjectGuid guid, Team team, bool online)
    {
        // The aura is sometimes not removed (vmangos "sometimes flag aura not removed", BattleGroundWS.cpp:484-506).
        foreach (Team flagTeam in new[] { Team.Alliance, Team.Horde })
        {
            if (IsPickedUp(flagTeam) && _flagKeepers[Idx(flagTeam)] == guid)
            {
                if (!online)
                {
                    // Removing an offline player who has the flag: clear the carrier and respawn the flag. vmangos stops there and the
                    // clients keep the "carried" icon (2) and the taken state (1); reset both as a return does.
                    _flagKeepers[Idx(flagTeam)] = ObjectGuid.Empty;
                    RespawnFlag(flagTeam, captured: false);
                    UpdateFlagState(BattlegroundConstants.OtherTeam(flagTeam), (uint)WsgFlagState.WaitRespawn);
                    Host.UpdateWorldState(flagTeam == Team.Alliance ? WorldStateFlagTakenAlliance : WorldStateFlagTakenHorde, 0);
                }
                else
                {
                    DropFlag(guid, team);
                }
            }
        }
    }

    /// <inheritdoc />
    public override void HandleKillPlayer(ObjectGuid victim, ObjectGuid? killer)
    {
        if (Status != BattlegroundStatus.InProgress)
        {
            return;
        }

        OnPlayerDroppedFlag(victim);
        base.HandleKillPlayer(victim, killer);
    }

    /// <inheritdoc />
    public override void UpdatePlayerScore(ObjectGuid player, BattlegroundScoreType type, uint value)
    {
        if (ScoreOf(player) is not WsgScore score)
        {
            return;
        }

        switch (type)
        {
            case BattlegroundScoreType.FlagCaptures:
                score.FlagCaptures += value;
                break;
            case BattlegroundScoreType.FlagReturns:
                score.FlagReturns += value;
                break;
            default:
                base.UpdatePlayerScore(player, type, value);
                break;
        }
    }

    // ------------------------------------------------------------------ the end

    /// <inheritdoc />
    public override void EndBattleground(Team? winner)
    {
        bool weekend = Ports.Calendar.IsBattlegroundWeekend(Type);

        // Completing bonus during holidays: both factions receive honor, win or lose (BattleGroundWS.cpp:622-628).
        if (weekend)
        {
            RewardHonorToTeam(WinMatchHonorBonusCompleteHoliday(Bracket), Team.Alliance);
            RewardHonorToTeam(WinMatchHonorBonusCompleteHoliday(Bracket), Team.Horde);
        }

        if (winner is { } w)
        {
            RewardHonorToTeam(WinMatchHonor(Bracket), w);
            if (weekend)
            {
                RewardHonorToTeam(WinMatchHonorHoliday(Bracket), w);
            }
        }

        base.EndBattleground(winner);
    }

    // ------------------------------------------------------------------ triggers, graveyards, world states

    /// <inheritdoc />
    public override bool HandleAreaTrigger(ObjectGuid player, uint areaTriggerId)
    {
        if (Status != BattlegroundStatus.InProgress || PlayerTeam(player) is not { } team)
        {
            return false;
        }

        switch (areaTriggerId)
        {
            case 3686:      // Alliance elixir of speed spawn
            case 3687:      // Horde elixir of speed spawn
            case 3706:      // Alliance elixir of regeneration spawn
            case 3708:      // Horde elixir of regeneration spawn
            case 3707:      // Alliance elixir of berserk spawn
            case 3709:      // Horde elixir of berserk spawn
                return true;
            case AreaTriggerAllianceFlagSpawn:
                // The Horde flag is away and the Alliance flag is home: the carrier scores (BattleGroundWS.cpp:539-543).
                if (_flagState[Idx(Team.Horde)] != WsgFlagState.OnBase && _flagState[Idx(Team.Alliance)] == WsgFlagState.OnBase
                    && _flagKeepers[Idx(Team.Horde)] == player)
                {
                    CaptureFlag(player, team);
                }

                return true;
            case AreaTriggerHordeFlagSpawn:
                if (_flagState[Idx(Team.Alliance)] != WsgFlagState.OnBase && _flagState[Idx(Team.Horde)] == WsgFlagState.OnBase
                    && _flagKeepers[Idx(Team.Alliance)] == player)
                {
                    CaptureFlag(player, team);
                }

                return true;
            case 3669:      // Horde exit (client patch 1.7.0: back to the battlemaster)
                if (team == Team.Horde)
                {
                    Host.LeaveBattleground(player);
                    return true;
                }

                return false;
            case 3671:      // Alliance exit
                if (team == Team.Alliance)
                {
                    Host.LeaveBattleground(player);
                    return true;
                }

                return false;
            default:
                Host.UnhandledAreaTrigger(player, areaTriggerId);
                return false;
        }
    }

    /// <inheritdoc />
    public override uint ClosestGraveyard(Team team)
    {
        // Repop at the flag room while the match has not started (BattleGroundWS.cpp:677-691).
        bool running = Status == BattlegroundStatus.InProgress;
        return team == Team.Alliance
            ? (running ? GraveyardMainAlliance : GraveyardFlagRoomAlliance)
            : (running ? GraveyardMainHorde : GraveyardFlagRoomHorde);
    }

    /// <inheritdoc />
    public override IReadOnlyList<(uint Id, int Value)> InitialWorldStates()
    {
        static int Taken(WsgFlagState state) => state switch
        {
            WsgFlagState.OnGround => -1,
            WsgFlagState.OnPlayer => 1,
            _ => 0,
        };

        // The icon states 2338/2339 are the literals 2 ("carried") and 1 (BattleGroundWS.cpp:714-722), not WsgFlagState values.
        return
        [
            (WorldStateFlagCapturesAlliance, TeamScore(Team.Alliance)),
            (WorldStateFlagCapturesHorde, TeamScore(Team.Horde)),
            (WorldStateFlagTakenAlliance, Taken(FlagState(Team.Alliance))),
            (WorldStateFlagTakenHorde, Taken(FlagState(Team.Horde))),
            (WorldStateFlagCapturesMax, MaxTeamScore),
            (WorldStateFlagStateAlliance, FlagState(Team.Horde) == WsgFlagState.OnPlayer ? 2 : 1),
            (WorldStateFlagStateHorde, FlagState(Team.Alliance) == WsgFlagState.OnPlayer ? 2 : 1),
        ];
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Playerbots;

/// <summary>Deterministic quest ownership over the ordinary quest handlers and live journal.</summary>
internal sealed class PlayerbotQuestGoals(WorldSession session, PlayerbotOptions options)
{
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly PlayerbotQuestTripFeasibility _trip = new();

    internal int TripEvaluations => _trip.Evaluations;
    private PlayerbotRoute? _route;

    /// <summary>The wait for navigation tiles still being read (<see cref="PlanOutcome.Pending"/>) of this controller's plans.</summary>
    private TileWait _tileWait;
    private ObjectGuid _stageNpc;
    private uint _stageQuest;
    private uint _stageDeadline;
    private uint _backoffUntil;
    private Stage _stage;
    // Quests whose exchange the server did not complete within the stage deadline (an accept refused because the quest's
    // source item does not fit, a reward refused for the bags): asked again at once they are refused again. On the replayed
    // live state Ironwander stood at Hands Springsprocket for good, accepting quest 2160 (Supplies to Tannok) every 17 seconds.
    private readonly Dictionary<uint, uint> _refused = [];

    /// <summary>How long a quest whose exchange the server refused is left alone.</summary>
    internal const uint RefusedQuestMs = 600_000;

    /// <summary>The most refused quests remembered.</summary>
    private const int MaxRefused = 64;

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Quest;

    /// <summary>Quest givers and quests a stalled bot set aside (<see cref="PlayerbotStallWatch"/>).</summary>
    internal PlayerbotSuspensions? Suspensions { get; set; }

    /// <summary>
    /// Objectives the bot leaves to a group (quest id, creature entry): not chosen as the creature to hunt alone, so the bot goes on
    /// with what it can do by itself while it waits for partners (<see cref="Groups.PlayerbotGroupCoordinator"/>).
    /// </summary>
    internal Func<uint, uint, bool>? HeldForGroup { get; set; }
    internal Func<Player, uint, uint, bool>? AllSpawnsVisited { get; set; }
    internal uint QuestId { get; private set; }
    internal uint TargetEntry { get; private set; }
    internal uint PreferredCreatureEntry { get; private set; }
    internal uint PreferredQuestId { get; private set; }
    internal uint PreferredItemUseId { get; private set; }
    internal uint PreferredItemUseQuestId { get; private set; }
    internal string StageName => _stage.ToString();

    /// <summary>How long a creature on which the bot just used a quest item is skipped before it can be targeted again.</summary>
    internal const uint ItemUseTargetCooldownMs = 60_000;
    private readonly Dictionary<ObjectGuid, uint> _recentlyUsedItemTargets = [];
    private ObjectGuid _itemUseTargetGuid;

    /// <summary>The last turn-in refusal recorded for a completed quest at its ender (quest, ender entry, human reason, compact QA code).</summary>
    internal (uint QuestId, uint EnderEntry, string Reason, string Code)? LastTurnInRefusal { get; private set; }

    /// <summary>Whether a quest turn-in was refused because the bot's inventory had no room for the reward.</summary>
    public bool NeedsBagSpaceForTurnIn { get; private set; }

    private uint _approachFailQuest;
    private uint _approachFailEntry;
    private System.Numerics.Vector3 _approachFailSpot;
    private int _approachFailCount;
    private const int ApproachConfirmFailures = 3;
    private const float ApproachSpotYards = 3f;
    private (uint QuestId, uint Entry, uint Progress) _lastNoProgressEpisode;

    /// <summary>The bot's home area, or null. New quests are taken only inside it; distant completed returns may be abandoned.</summary>
    internal PlayerbotHomeArea? Home { get; set; }

    /// <summary>Whether a quest exchange is under way (the brain does not pull the bot away from it to walk home).</summary>
    internal bool Busy => _stage != Stage.None;

    /// <summary>
    /// The completed quest the bot is walking home to finish (zero when it is on no such trip). While it is set and no exchange is under way,
    /// the bot deals with a quest NPC other than that quest's ender (a giver, or the ender of another completed quest) only when it is
    /// already within reach: it does not detour to a visible quest NPC that is not on the way (GitHub w27: the Flame Crest NPCs starved
    /// quest 5601's trip home). The brain sets it every think that reaches the quest-return check, so it can be stale across the early
    /// returns before that check (death, recovery, escape): harmless, because nothing calls <see cref="Update"/> then and the next think
    /// that reaches the check rewrites it. It is also overwritten in the very think that gives a stall up (the give-up suspends the quest,
    /// then the same think re-evaluates the return): the stall's own view of the trip is <see cref="PlayerbotBrain.StallReturnQuest"/>.
    /// </summary>
    internal uint ReturnQuest { get; set; }

    /// <summary>Maximum flat walking distance to a completed quest's ender for an unhomed bot when no taxi route reaches it.</summary>
    internal const float MaxUnhomedReturnWalkYards = 3_000f;

    internal bool HasCandidate(Player player) => FindCandidate(player) is not null;

    internal uint CompletedReturnQuest(Player player)
    {
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        CreatureContent? content = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content;
        if (services?.StateOf(player) is not { Loaded: true } state || content is null) return 0;
        // Called on every brain update: gather the completed, unrewarded quests first (usually none), so the sort and the
        // ender search only run when there is something to return. Same answer as the former OrderBy chain (lowest id first).
        List<uint>? candidates = null;
        foreach (KeyValuePair<uint, QuestStatusData> row in state.Quests.Statuses)
        {
            if (row.Value is { Status: QuestStatus.Complete, Rewarded: false })
            {
                (candidates ??= []).Add(row.Key);
            }
        }

        if (candidates is null) return 0;
        candidates.Sort();
        foreach (uint questId in candidates)
        {
            if (services.IsRewardable(questId) && !IsRefused(questId) && !IsGiverWait(questId) && HasReachableEnder(player, services, content, questId))
                return questId;
        }

        return 0;
    }

    private bool HasReachableEnder(Player player, QuestNpcServices services, CreatureContent content, uint questId)
    {
        foreach (uint entry in services.Quests.CreatureEndersOf(questId))
        {
            foreach (CreatureSpawn spawn in content.GetSpawns(player.MapId, entry))
            {
                if (CanReturnToEnder(player, spawn)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="player"/> should pursue a completed-quest return to <paramref name="spawn"/> right now: the ender is
    /// present on the bot's map and either within walking return range (inside the home or within 600 yards, or within
    /// <see cref="PlayerbotOptions.MaxDeliveryYards"/> for a homed bot / <see cref="MaxUnhomedReturnWalkYards"/> for an unhomed bot, or
    /// with the distance limit disabled) or reachable by a known, affordable flight path (<see cref="PlayerbotTaxi.CanTravelByTaxi"/>).
    /// Otherwise the completed quest stays held in the log until the bot is closer or learns the taxi nodes.
    /// </summary>
    internal bool CanReturnToEnder(Player player, CreatureSpawn spawn)
    {
        if (spawn.MapId != player.MapId || !PlayerbotWorldDestinations.IsPresent(player, spawn)) return false;
        if (Home is { } home && ReasonableReturnTrip(home, player.MapId, player.X, player.Y, spawn)) return true;
        float maxWalk = Home is not null
            ? _options.MaxDeliveryYards
            : (_options.MaxDeliveryYards > 0f ? Math.Max(_options.MaxDeliveryYards, MaxUnhomedReturnWalkYards) : 0f);
        if (maxWalk <= 0f) return true;
        float dx = spawn.X - player.X, dy = spawn.Y - player.Y;
        if ((dx * dx) + (dy * dy) <= maxWalk * maxWalk) return true;
        return PlayerbotTaxi.CanTravelByTaxi(_session, player, player.MapId, new System.Numerics.Vector3(spawn.X, spawn.Y, spawn.Z), _options);
    }

    /// <summary>
    /// A home-bound bot keeps a completed quest if its ender is in its home or within a short local trip. A bot without a
    /// home also abandons a proven infeasible ender trip.
    /// Use the ordinary client abandon packet so the journal, source items and persistence follow the same path as a player.
    /// </summary>
    internal bool AbandonDistantCompletedQuest(Player player)
    {
        if (player.Combat.IsInCombat || !player.IsAlive) return false;
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        CreatureContent? content = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content;
        if (services?.StateOf(player) is not { Loaded: true } state || content is null) return false;
        foreach ((uint id, QuestStatusData status) in state.Quests.Statuses.OrderBy(row => row.Key))
        {
            if (status.Status != QuestStatus.Complete || status.Rewarded
                || services.Quests.Get(id) is not { IsActive: true } || !services.IsRewardable(id)) continue;
            // A refused abandon packet must not be sent on every think, regardless of home.
            if (IsRefused(id)) continue;
            if (!_trip.IsInfeasible(player.MapId, player.X, player.Y, services.Quests, content, id,
                Home is null ? _options.MaxDeliveryYards : 0, player, completed: true))
            {
                if (Home is not { } home) continue;
                // A leave-home quest a graduated home allowed is meant to be turned in outside the home (within PlayerbotHomeArea.LeaveTurnInYards of it).
                // Bounded: after MaxReturnSetAsides stalled trips for it the quest falls through to the abandon below.
                if (services.Quests.Get(id) is { } held && home.AllowsLeaveQuests && home.QuestLeavesHome(services.Quests, content, held)
                    && (Suspensions?.QuestSetAsideCount(id) ?? 0) < PlayerbotSuspensions.MaxReturnSetAsides) continue;
                bool reasonable = services.Quests.CreatureEndersOf(id)
                    .SelectMany(entry => content.GetSpawns(player.MapId, entry))
                    .Where(spawn => PlayerbotWorldDestinations.IsPresent(player, spawn))
                    .Any(spawn => ReasonableReturnTrip(home, player.MapId, player.X, player.Y, spawn));
                if (reasonable) continue;
            }
            if (TryAbandonCompletedQuest(_session, player, id, out bool attempted))
            {
                // A homed bot abandons on its own position and never gets a durable mark. An unhomed bot only abandons
                // what static content proved infeasible and the bot cannot reach from where it stands.
                if (Home is null) Suspensions?.MarkUnreachableQuest(id, _session.World.NowMs);
                return true;
            }
            if (attempted) Refuse(id, _session.World.NowMs);
            return false;
        }
        return false;
    }

    /// <summary>Both distant returns and repeated steep-return failures abandon through vmangos
    /// <c>WorldSession::HandleQuestLogRemoveQuest</c>, QuestHandler.cpp:327, and verify the journal changed.</summary>
    internal static bool TryAbandonCompletedQuest(WorldSession session, Player player, uint questId, out bool attempted)
    {
        attempted = false;
        if (session.Services.GetService<QuestNpcFeature>()?.Services.StateOf(player) is not { Loaded: true } state
            || state.Quests.Get(questId) is not { Status: QuestStatus.Complete, Rewarded: false }) return false;
        int slot = state.Quests.FindSlot(questId);
        if (slot < 0 || slot >= QuestConstants.MaxQuestLogSize
            || !session.TryManagedAction(WorldOpcode.CmsgQuestlogRemoveQuest, [(byte)slot])) return false;
        attempted = true;
        // Managed dispatch means only that the session stayed InWorld; the handler can still refuse the packet.
        return state.Quests.Get(questId) is not { Status: QuestStatus.Complete, Rewarded: false };
    }

    /// <summary>
    /// Whether the quest log has a free slot (vmangos <c>FindQuestSlot(0) &lt; MAX_QUEST_LOG_SIZE</c>, the first check of CanAddQuest): with
    /// none the server refuses every accept with SMSG_QUESTLOG_FULL, so a new quest is neither travelled to (the destinations' giver
    /// choice) nor asked for (<see cref="FindCandidate(Player, QuestNpcServices, PlayerNpcState)"/>). GitHub #786: thousands of accepts
    /// refused as QuestLogFull.
    /// </summary>
    internal static bool HasFreeLogSlot(PlayerNpcState state) => state.Quests.FindSlot(0) < QuestConstants.MaxQuestLogSize;

    /// <summary>
    /// The quest a bot with a full log gives up to make room, as a player does: a failed quest first (dead weight until abandoned), then
    /// the lowest grey one still incomplete (quest level at or below the player's grey level, vmangos <c>MaNGOS::XP::GetGrayLevel</c>:
    /// next to no experience). Completed quests are kept for their turn-in, and a quest whose level follows the player's (level 0 or
    /// less) is never grey. Zero when nothing qualifies: the bot then takes no new quest until one is turned in.
    /// </summary>
    internal static uint LowValueQuestToShed(Player player, QuestNpcServices services, PlayerNpcState state, Func<uint, bool>? skip = null)
    {
        uint best = 0;
        int bestRank = int.MaxValue;
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint id = state.Quests.SlotQuestId(slot);
            if (id == 0 || skip?.Invoke(id) == true || state.Quests.Get(id) is not { } status || status.Rewarded) continue;
            int rank;
            if (status.Status == QuestStatus.Failed) rank = -1;
            else if (status.Status == QuestStatus.Incomplete && services.Quests.Get(id) is { } quest && IsGrey(player, quest))
                rank = quest.QuestLevel;
            else continue;
            if (rank < bestRank || (rank == bestRank && id < best)) { best = id; bestRank = rank; }
        }

        return best;
    }

    /// <summary>
    /// A full log sheds one low-value quest (<see cref="LowValueQuestToShed"/>) through the ordinary client abandon packet (vmangos
    /// <c>WorldSession::HandleQuestLogRemoveQuest</c>), so the slot, source item and persistence follow a player's path. A refused
    /// abandon sets the quest aside so the packet is not sent on every think. True when a packet was sent.
    /// </summary>
    private bool TryMakeRoomInFullLog(Player player, QuestNpcServices services, PlayerNpcState state, uint now)
    {
        if (HasFreeLogSlot(state) || player.Combat.IsInCombat || !player.IsAlive) return false;
        uint id = LowValueQuestToShed(player, services, state, IsRefused);
        if (id == 0) return false;
        int slot = state.Quests.FindSlot(id);
        if (slot >= QuestConstants.MaxQuestLogSize
            || !_session.TryManagedAction(WorldOpcode.CmsgQuestlogRemoveQuest, [(byte)slot])) return false;
        // Set aside whether the abandon landed or was refused: a shed quest is not taken straight back from a giver in sight, and a
        // refused abandon is not sent on every think.
        Refuse(id, now);
        return true;
    }

    /// <summary>Set by <see cref="FindCandidate(Player, QuestNpcServices, PlayerNpcState)"/>: a giver in sight offers a quest worth taking that only the full log keeps from the bot.</summary>
    private bool _roomWanted;

    /// <summary>
    /// Whether <paramref name="quest"/> is grey to <paramref name="player"/>: its level at or below the grey level (vmangos
    /// <c>MaNGOS::XP::GetGrayLevel</c>, next to no experience). A quest whose level follows the player's (0 or less) never is.
    /// </summary>
    internal static bool IsGrey(Player player, Quest quest)
        => quest.QuestLevel > 0 && (uint)quest.QuestLevel <= ArcaneCore.Game.Progression.ExperienceFormulas.GrayLevel(player.Level);

    internal static bool ReasonableReturnTrip(PlayerbotHomeArea home, uint mapId, float playerX, float playerY,
        ArcaneCore.Kernel.WorldData.Creatures.CreatureSpawn ender)
    {
        if (ender.MapId != mapId) return false;
        float dx = ender.X - playerX, dy = ender.Y - playerY;
        return home.Contains(ender.MapId, ender.X, ender.Y, ender.Z) || dx * dx + dy * dy <= 600f * 600f;
    }

    /// <summary>Whether the 2-second quest interaction backoff is currently active at <paramref name="now"/>.</summary>
    internal bool IsBackingOff(uint now) => _backoffUntil != 0 && unchecked(now - _backoffUntil) > int.MaxValue;

    internal bool Update(Player player, uint elapsedMs)
    {
        if (!_options.Enabled || !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat)
            return false;

        uint now = _session.World.NowMs;
        if (IsBackingOff(now))
            return false;
        if (_backoffUntil != 0)
            _backoffUntil = 0;
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services?.StateOf(player) is not { Loaded: true } state)
            return false;
        RefreshObjectiveEntry(player, services, state);
        if (NeedsBagSpaceForTurnIn && PlayerbotTownGoals.FreeBagSlots(player) > 0)
            NeedsBagSpaceForTurnIn = false;
        if (_awaitingBagSpace.Count > 0 && PlayerbotTownGoals.FreeBagSlots(player) > 0)
            _awaitingBagSpace.Clear();
        if (_stage == Stage.AcceptConfirmation)
        {
            if (state.Quests.Get(_stageQuest) is { Status: QuestStatus.Incomplete or QuestStatus.Complete })
            {
                ClearStage();
                return false;
            }
        }
        else if (_stage == Stage.RewardConfirmation)
        {
            if (state.Quests.Get(_stageQuest)?.Rewarded == true)
            {
                Suspensions?.QuestSucceeded(_stageQuest);
                NeedsBagSpaceForTurnIn = false;
                ClearStage();
                return false;
            }
        }

        // The exchange is a conversation held in reach of the NPC (the client closes the quest window when its player walks away):
        // a bot pulled away mid-exchange (a fight, a flight from danger) left it, the server refused nothing. It starts again
        // from the greeting when it is back, instead of timing the stage out at a distance and reporting the giver as refusing it
        // "out of interaction range" (GitHub #786: quest 3376 at giver 3209 from 44 yards) and setting the quest aside.
        if (_stage != Stage.None
            && services.Deps.Creatures?.Find(player, _stageNpc) is { IsAlive: true } stageNpc && !WithinReach(player, stageNpc))
        {
            ClearStage();
            _route = null;
            return false;
        }

        if (_stage is Stage.AcceptDetails or Stage.CompleteResponse or Stage.RewardOffer)
        {
            bool confirmed = false;
            foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgQuestgiverQuestDetails,
                WorldOpcode.SmsgQuestgiverRequestItems, WorldOpcode.SmsgQuestgiverOfferReward))
            {
                if (packet.Payload.Length < 12) continue;
                var response = new PacketReader(packet.Payload);
                if (response.ReadUInt64() != _stageNpc.Value || response.ReadUInt32() != _stageQuest) continue;
                if (_stage == Stage.AcceptDetails && packet.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails)
                {
                    _stage = Stage.Accept;
                    _stageDeadline = unchecked(now + 15_000);
                    confirmed = true;
                }
                else if (_stage == Stage.CompleteResponse && packet.Opcode == WorldOpcode.SmsgQuestgiverRequestItems)
                {
                    if (!QuestPackets.IsRequestItemsCompletable(packet.Payload))
                    {
                        string reason = services.DiagnoseTurnInRefusal(player, _stageNpc, _stageQuest) ?? "quest requirements not met at ender";
                        RefuseTurnIn(player, _stageQuest, TargetEntry, reason, TurnInRefusalCode(reason, "IncompleteAtEnder"), now);
                        return false;
                    }
                    _stage = Stage.RewardRequest;
                    _stageDeadline = unchecked(now + 15_000);
                    confirmed = true;
                }
                else if ((_stage is Stage.CompleteResponse or Stage.RewardOffer) && packet.Opcode == WorldOpcode.SmsgQuestgiverOfferReward)
                {
                    _stage = Stage.RewardChoice;
                    _stageDeadline = unchecked(now + 15_000);
                    confirmed = true;
                }
            }
            if (!confirmed)
            {
                if (services.Deps.Creatures?.Find(player, _stageNpc) is not { IsAlive: true })
                {
                    GiverGone(player, services, _stageQuest, _stageNpc, TargetEntry, "giver-despawned-or-dead", now);
                    return false;
                }
                if (_stageDeadline != 0 && unchecked(now - _stageDeadline) <= int.MaxValue)
                {
                    if (_stage is Stage.CompleteResponse or Stage.RewardOffer)
                    {
                        string reason = services.DiagnoseTurnInRefusal(player, _stageNpc, _stageQuest) ?? $"turn-in timed out in stage {_stage}";
                        RefuseTurnIn(player, _stageQuest, TargetEntry, reason, TurnInRefusalCode(reason, "StageTimeout"), now);
                        return false;
                    }
                    ReportAcceptRefused(player, services, state, _stageNpc, _stageQuest, TargetEntry, "StageTimeout", $"accept timed out in stage {_stage}");
                    Refuse(_stageQuest, now);
                    ClearStage();
                    _backoffUntil = unchecked(now + 2_000);
                    return false;
                }
                return true;
            }
        }

        if (_stage is Stage.Accept or Stage.RewardRequest or Stage.RewardChoice
            && services.Deps.Creatures?.Find(player, _stageNpc) is not { IsAlive: true })
        {
            GiverGone(player, services, _stageQuest, _stageNpc, TargetEntry, "giver-despawned-or-dead", now);
            return false;
        }

        if (_stage != Stage.None && _stageDeadline != 0 && unchecked(now - _stageDeadline) <= int.MaxValue)
        {
            if (_stage is Stage.CompleteResponse or Stage.RewardRequest or Stage.RewardOffer or Stage.RewardChoice or Stage.RewardConfirmation)
            {
                string reason = services.DiagnoseTurnInRefusal(player, _stageNpc, _stageQuest) ?? $"turn-in timed out in stage {_stage}";
                RefuseTurnIn(player, _stageQuest, TargetEntry, reason, TurnInRefusalCode(reason, "StageTimeout"), now);
                return false;
            }
            ReportAcceptRefused(player, services, state, _stageNpc, _stageQuest, TargetEntry, "StageTimeout", $"accept timed out in stage {_stage}");
            Refuse(_stageQuest, now);
            ClearStage();
            _backoffUntil = unchecked(now + 2_000);
            return false;
        }

        if (_stage is Stage.AcceptConfirmation or Stage.RewardConfirmation)
            return true;

        QuestTarget? target = FindCandidate(player, services, state);
        if (target is null && _roomWanted && _stage == Stage.None && TryMakeRoomInFullLog(player, services, state, now))
            return true;
        if (target is null)
            return _stage != Stage.None || TryUseObjectiveItem(player, elapsedMs, now);

        QuestId = target.Quest.Id;
        TargetEntry = target.Npc.Entry;
        if (target.Npc.Guid != _stageNpc || target.Quest.Id != _stageQuest)
            _route = null;

        if (!WithinReach(player, target.Npc))
        {
            PlanOutcome planned = PlanOutcome.Route;
            bool returning = target.Action >= QuestAction.Complete
                && state.Quests.Get(target.Quest.Id) is { Status: QuestStatus.Complete, Rewarded: false };
            if (_route is null)
            {
                var enderPos = new System.Numerics.Vector3(target.Npc.X, target.Npc.Y, target.Npc.Z);
                bool friendlyEnder = returning && FriendlyTownEnder(player, target.Npc);
                planned = PlayerbotNavigation.PlanExact(player, enderPos, _options, ref _tileWait, now, out _route,
                    completedQuestReturn: returning, friendlyTownEnder: friendlyEnder);
                if (planned == PlanOutcome.Refused && returning)
                {
                    planned = PlayerbotNavigation.PlanToward(player, enderPos, _options, ref _tileWait, now, out _route,
                        completedQuestReturn: true, friendlyTownEnder: friendlyEnder);
                    if (planned == PlanOutcome.Route && _route is { Points.Count: > 0 } partial)
                    {
                        // A toward-leg that is not the mesh's own dead end (PartialToGoal) may leave the straight line
                        // first: a region portal, or the road around a building. That is the way, not a refusal. Only a
                        // dead-end partial that does not close is no route (quest 428 at Rane Yorick, NoRouteToVisibleEnder,
                        // then goal Quest with no quest).
                        float curDistSq = System.Numerics.Vector3.DistanceSquared(new(player.X, player.Y, player.Z), enderPos);
                        float endDistSq = System.Numerics.Vector3.DistanceSquared(partial.Points[^1], enderPos);
                        if (partial.PartialToGoal && endDistSq + 1f >= curDistSq)
                        {
                            _route = null;
                            planned = PlanOutcome.Refused;
                        }
                    }
                }
            }
            if (planned == PlanOutcome.Pending)
            {
                // A tile of the way is still being read: no backoff, the same giver is planned to again next think.
                Goal = PlayerbotGoalKind.Quest;
                return true;
            }
            if (planned == PlanOutcome.Refused)
            {
                if (returning)
                {
                    NoteCloseApproachFailure(player, target.Quest.Id, target.Npc.Entry, now);
                    return false;
                }
                Backoff(now);
                return false;
            }

            Goal = PlayerbotGoalKind.Quest;
            if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, now))
            {
                _route = null;
                if (returning && NoteCloseApproachFailure(player, target.Quest.Id, target.Npc.Entry, now))
                    return false;
            }
            else if (returning && System.Numerics.Vector3.DistanceSquared(
                new System.Numerics.Vector3(player.X, player.Y, player.Z), _approachFailSpot) > ApproachSpotYards * ApproachSpotYards)
            {
                _approachFailCount = 0;
            }
            return true;
        }

        _route = null;
        _approachFailCount = 0;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        // A giver that is gone is not asked: the bot changes goal and looks again later (GiverGone). Other refusals are the turn-in's, as before.
        if (services.Deps.Creatures?.Find(player, target.Npc.Guid) is not { IsAlive: true })
        {
            GiverGone(player, services, target.Quest.Id, target.Npc.Guid, target.Npc.Entry, "giver-not-found", now);
            return false;
        }

        if (target.Action >= QuestAction.Complete
            && services.InteractableNpc(player, target.Npc.Guid, NpcFlags.QuestGiver, out string? interactRefusal) is null)
        {
            if (target.Npc.IsInCombat)
            {
                Backoff(now);
                return false;
            }
            RefuseTurnIn(player, target.Quest.Id, target.Npc.Entry, interactRefusal ?? "ender not interactable",
                TurnInRefusalCode(interactRefusal, "EnderNotInteractable"), now, suspendEntry: true);
            return false;
        }

        byte[] questRequest = QuestPayload(target.Npc.Guid.Value, target.Quest.Id);
        switch (target.Action)
        {
            case QuestAction.AcceptQuery:
                if (_session.TryManagedAction(WorldOpcode.CmsgQuestgiverQueryQuest, questRequest))
                {
                    _stage = Stage.AcceptDetails;
                    ArmStage(target);
                }
                return true;
            case QuestAction.Accept:
                if (_session.TryManagedAction(WorldOpcode.CmsgQuestgiverAcceptQuest, questRequest))
                { _stage = Stage.AcceptConfirmation; ArmStage(target); }
                return true;
            case QuestAction.Complete:
                if (_session.TryManagedAction(WorldOpcode.CmsgQuestgiverCompleteQuest, questRequest))
                {
                    _stage = Stage.CompleteResponse;
                    ArmStage(target);
                }
                return true;
            case QuestAction.RewardRequest:
                if (_session.TryManagedAction(WorldOpcode.CmsgQuestgiverRequestReward, questRequest))
                {
                    _stage = Stage.RewardOffer;
                    ArmStage(target);
                }
                return true;
            case QuestAction.RewardChoice:
                uint choice = RewardChoice(player, target.Quest);
                if (_session.TryManagedAction(WorldOpcode.CmsgQuestgiverChooseReward,
                        QuestChoicePayload(target.Npc.Guid.Value, target.Quest.Id, choice)))
                {
                    if (!player.IsQuestSettlementPending && state.Quests.Get(target.Quest.Id)?.Rewarded != true)
                    {
                        _ = _session.DrainManagedPackets(WorldOpcode.SmsgQuestgiverOfferReward,
                            WorldOpcode.SmsgQuestgiverQuestFailed, WorldOpcode.SmsgQuestgiverQuestInvalid);
                        string reason = services.DiagnoseTurnInRefusal(player, target.Npc.Guid, target.Quest.Id, choice) ?? "reward choice refused";
                        RefuseTurnIn(player, target.Quest.Id, target.Npc.Entry, reason, TurnInRefusalCode(reason, "RewardRefused"), now);
                        return false;
                    }
                    NeedsBagSpaceForTurnIn = false;
                    _stage = Stage.RewardConfirmation;
                    ArmStage(target);
                }
                return true;
            default:
                return false;
        }
    }

    private QuestTarget? FindCandidate(Player player)
    {
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        return services?.StateOf(player) is { Loaded: true } state
            ? FindCandidate(player, services, state) : null;
    }

    private QuestTarget? FindCandidate(Player player, QuestNpcServices services, PlayerNpcState state)
    {
        ArcaneCore.Kernel.WorldData.Loot.LootContent? loot = _session.Services.GetService<GameObjectLootFeature>()?.LootContent;
        CreatureContent? content = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content;
        bool returning = ReturnQuest != 0 && _stage == Stage.None;
        _roomWanted = false;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (services.Deps.Creatures?.Find(player, guid, NpcFlags.QuestGiver) is not { IsAlive: true, IsHostile: false, IsNotSelectable: false } npc
                || (npc.NpcFlags & NpcFlags.QuestGiver) == 0)
                continue;
            if (_stage != Stage.None && npc.Guid != _stageNpc)
                continue;
            bool entrySuspended = Suspensions?.IsEntrySuspended(npc.Entry, _session.World.NowMs) == true;

            foreach (uint id in services.Quests.EndersOf(npc.Entry))
            {
                if (!services.IsRewardable(id) || IsRefused(id) || IsGiverWait(id)) continue;
                EndGiverWait(id, npc.Guid);
                if (_stage != Stage.None && id != _stageQuest)
                    continue;
                if (returning && id != ReturnQuest && !WithinReach(player, npc))
                    continue;
                if (services.Quests.Get(id) is not { } quest || state.Quests.Get(id) is not { } status)
                    continue;
                if (status.Status == QuestStatus.Complete && !status.Rewarded)
                {
                    QuestAction action = _stage switch
                    {
                        Stage.RewardRequest => QuestAction.RewardRequest,
                        Stage.RewardChoice => QuestAction.RewardChoice,
                        _ => QuestAction.Complete,
                    };
                    return new QuestTarget(npc, quest, action);
                }
            }

            if (entrySuspended || (returning && !WithinReach(player, npc)))
                continue;

            foreach (uint id in services.Quests.StartersOf(npc.Entry))
            {
                if (!services.IsRewardable(id) || IsRefused(id) || IsGiverWait(id)) continue;
                EndGiverWait(id, npc.Guid);
                if (_stage != Stage.None && id != _stageQuest)
                    continue;
                if (services.Quests.Get(id) is not { } quest)
                    continue;
                // A new quest follows the rule the travel planner uses too (home, item-only repeatables, journal, trip feasibility).
                // Mid-exchange the quest was already chosen: only the journal is asked again, as before.
                if (_stage == Stage.None)
                {
                    if (!PlayerbotWorldDestinations.MayTakeQuest(player, services, state, content, loot, Home,
                            Home?.Contains(player.Map?.MapId ?? player.MapId, npc.X, npc.Y, npc.Z) ?? true, quest, _trip,
                            Home is null ? _options.MaxDeliveryYards : 0))
                        continue;
                    // A full log is refused by the server (SMSG_QUESTLOG_FULL; GitHub #786): the giver is not asked. A quest worth
                    // taking (not grey) is what a full log makes room for (Update, TryMakeRoomInFullLog); a grey one is skipped.
                    if (!HasFreeLogSlot(state))
                    {
                        if (!IsGrey(player, quest)) _roomWanted = true;
                        continue;
                    }
                    // A source item the full bags cannot take has the accept refused (vmangos CanGiveQuestSourceItemIfNeed; GitHub #786, ~180/h on
                    // live): the giver is not asked until a slot frees, the same rule quest travel follows (PlayerbotWorldDestinations).
                    if (!SourceItemFits(player, quest))
                        continue;
                }
                else if (_stage != Stage.Accept && (services.CanTakeQuest(player, id) != true
                    || state.Quests.Get(id) is { Status: QuestStatus.Incomplete or QuestStatus.Complete }))
                    continue;
                return new QuestTarget(npc, quest,
                    _stage == Stage.Accept ? QuestAction.Accept : QuestAction.AcceptQuery);
            }
        }

        return null;
    }

    /// <summary>
    /// A visible completed-quest ender the return may cross a death or retreat mark beside the road to reach: not a game object,
    /// not hostile, and a friendly faction template (<see cref="PlayerbotNavigation.IsFriendlyTownNpc"/>).
    /// </summary>
    private bool FriendlyTownEnder(Player player, NpcInfo npc)
    {
        if (npc.IsGameObject || npc.IsHostile) return false;
        uint faction = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content.FindTemplate(npc.Entry)?.Faction ?? 0;
        FactionTemplateCatalog? factions = _session.Services.GetService<QuestNpcFeature>()?.FactionTemplates
            ?? _session.Services.GetService<FactionTemplateCatalog>();
        return PlayerbotNavigation.IsFriendlyTownNpc(factions, player.FactionTemplate, faction);
    }

    /// <summary>
    /// The server's creature reach: vmangos <c>WorldObject::IsWithinDist</c> (Object.cpp:1738) adds both bounding radii and compares strict
    /// 3D distance. The last walkable point can already be in reach. <see cref="Update"/> and the return-trip filter share it.
    /// A quest-giving game object is reached as the server reaches it (<see cref="QuestNpcServices.InteractableNpc(Player, ObjectGuid, NpcFlags)"/>,
    /// vmangos GameObject::IsAtInteractDistance): its own per-type distance to its centre, inclusive, with no bounding radii; with the
    /// creature rule a large object's radius put the bot "in reach" where the server refused it as out of range.
    /// </summary>
    internal static bool WithinReach(Player player, NpcInfo npc)
    {
        float dx = npc.X - player.X, dy = npc.Y - player.Y, dz = npc.Z - player.Z;
        float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
        if (npc.IsGameObject)
        {
            float objectReach = npc.GameObjectInteractionDistance;
            return float.IsFinite(distanceSquared) && float.IsFinite(objectReach) && objectReach >= 0
                && distanceSquared <= objectReach * objectReach;
        }
        float reach = QuestNpcServices.InteractionDistance + npc.BoundingRadius + player.BoundingRadius;
        return float.IsFinite(distanceSquared) && float.IsFinite(reach) && reach > 0 && distanceSquared < reach * reach;
    }

    /// <summary>
    /// How this bot's contention state (<see cref="PlayerbotContention"/>: the quest creatures it set aside for gaining no credit) and
    /// the fleet's claims reach the quest goals; null in tests that build the goals alone.
    /// </summary>
    internal PlayerbotContention? Contention { get; set; }

    /// <summary>Called with the creature entry and its quest when the bot set its last objective aside for gaining no credit and no other is left.</summary>
    internal Action<uint, uint>? OnContentionExhausted { get; set; }

    /// <summary>A quest item with more droppers than this is a good everyone drops (cloth, ore): not hunted on purpose.</summary>
    internal const int MaxItemDroppers = 24;

    private struct Objective
    {
        public uint Entry;
        public uint Quest;
        public uint Progress;
        public uint ItemUseId;
        public ulong Key;
        public bool Visited;
    }

    /// <summary>
    /// The creature the bot hunts for its quests (<see cref="PreferredCreatureEntry"/>), 0 for none. Among the unfinished objectives the
    /// bot takes the one a stable hash of its own guid ranks first, so bots with the same quests spread over the objectives instead of all
    /// hunting the first. A kill objective is hunted by its creature; an item objective by a creature that drops the item
    /// (<see cref="PlayerbotDropIndex"/>, from the loot tables, cached once). An objective that gained no credit for
    /// <see cref="PlayerbotContention.NoCreditMs"/> is set aside for the bot (<see cref="PlayerbotContention.ObserveObjective"/>) and the
    /// next one is taken; none left is reported through <see cref="OnContentionExhausted"/>.
    /// </summary>
    private void RefreshObjectiveEntry(Player player, QuestNpcServices services, PlayerNpcState state)
    {
        uint now = _session.World.NowMs;
        Objective chosen = Pick(player, services, state, now);
        if (Contention is { } contention)
        {
            if (chosen.Entry == 0) contention.ClearObjective();
            else if (contention.ObserveObjective(chosen.Entry, chosen.Progress, now))
            {
                (uint stalled, uint stalledQuest, uint stalledProgress) = (chosen.Entry, chosen.Quest, chosen.Progress);
                ReportObjectiveNoProgress(player, stalledQuest, stalled, stalledProgress, "no-credit-timeout");
                chosen = Pick(player, services, state, now); // the stalled entry is set aside now
                if (chosen.Entry == 0) OnContentionExhausted?.Invoke(stalled, stalledQuest);
            }
        }
        if (chosen.Entry != 0 && chosen.Visited)
            ReportObjectiveNoProgress(player, chosen.Quest, chosen.Entry, chosen.Progress, "all-spawns-visited");

        PreferredCreatureEntry = chosen.Entry;
        PreferredQuestId = chosen.Quest;
        PreferredItemUseId = chosen.ItemUseId;
        PreferredItemUseQuestId = chosen.ItemUseId != 0 ? chosen.Quest : 0u;
    }

    private Objective Pick(Player player, QuestNpcServices services, PlayerNpcState state, uint now)
    {
        var feature = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>();
        ArcaneCore.Kernel.WorldData.Creatures.CreatureContent? content = feature?.Content;
        ArcaneCore.Kernel.WorldData.Loot.LootContent? loot = null;
        bool lootLoaded = false;
        Objective best = default;
        ulong bot = player.Guid.Value;
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses)
        {
            // vmangos Quest::IsActive: a quest of a game event that is not running (game_event_quest, the server's event state) is
            // neither offered nor turned in (PrepareQuestMenu skips it for the giver and the ender alike; IsRewardable already keeps the
            // bot from walking to its ender). One in the log is kept for when the event comes back, but its objectives are not hunted.
            if (status.Status != QuestStatus.Incomplete || services.Quests.Get(questId) is not { IsActive: true } quest)
                continue;
            if (Suspensions?.IsQuestSuspended(questId, now) == true)
                continue;
            for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
            {
                int entry = quest.ReqCreatureOrGOId[index];
                uint required = quest.ReqCreatureOrGOCount[index];
                if (entry <= 0 || status.CreatureOrGOCount[index] >= required)
                    continue;
                if (quest.ReqSpell[index] == 0 && Huntable(player, services, content, questId, (uint)entry, now))
                    Consider(ref best, bot, questId, (uint)entry, status.CreatureOrGOCount[index], 0,
                        AllSpawnsVisited?.Invoke(player, (uint)entry, now) == true);
                else if (IsItemUseCreatureObjective(quest, index)
                    && player.Inventory.GetItemCount(quest.Template.SrcItemId) > 0
                    && ItemUseHuntable(content, questId, (uint)entry, now))
                    Consider(ref best, bot, questId, (uint)entry, status.CreatureOrGOCount[index], quest.Template.SrcItemId,
                        AllSpawnsVisited?.Invoke(player, (uint)entry, now) == true);
            }

            // An item objective is hunted at the creatures that drop the item.
            for (int index = 0; index < quest.ReqItemId.Count; index++)
            {
                uint item = quest.ReqItemId[index];
                uint needed = quest.ReqItemCount[index];
                if (item == 0 || needed == 0 || content is null) continue;
                uint have = player.Inventory.GetItemCount(item);
                if (have >= needed) continue;
                if (!lootLoaded)
                {
                    loot = _session.Services.GetService<GameObjectLootFeature>()?.LootContent;
                    lootLoaded = true;
                }

                if (loot is null) continue;
                IReadOnlyList<uint> droppers = PlayerbotDropIndex.DroppersOf(loot, item);
                if (droppers.Count == 0 || droppers.Count > MaxItemDroppers) continue;
                for (int d = 0; d < droppers.Count; d++)
                {
                    uint dropper = droppers[d];
                    if (content.GetSpawns(player.MapId, dropper).Count == 0 || content.FindTemplate(dropper) is not { } template
                        || template.Rank != 0 || template.MinLevel > player.Level + 2) continue;
                    if (Huntable(player, services, content, questId, dropper, now))
                        Consider(ref best, bot, questId, dropper, have, 0,
                            AllSpawnsVisited?.Invoke(player, dropper, now) == true);
                }
            }
        }

        return best;
    }

    private bool Huntable(Player player, QuestNpcServices services, ArcaneCore.Kernel.WorldData.Creatures.CreatureContent? content,
        uint questId, uint entry, uint now)
        => HeldForGroup?.Invoke(questId, entry) != true
            && Suspensions?.IsEntrySuspended(entry, now) != true
            && Contention?.IsEntrySuspended(entry, now) != true
            && IsKillObjective(player, services, entry)
            && (Home is not { } home || content is not null && home.HasSpawnInside(content, entry));

    private bool ItemUseHuntable(ArcaneCore.Kernel.WorldData.Creatures.CreatureContent? content, uint questId, uint entry, uint now)
        => HeldForGroup?.Invoke(questId, entry) != true
            && Suspensions?.IsEntrySuspended(entry, now) != true
            && Contention?.IsEntrySuspended(entry, now) != true
            && (Home is not { } home || content is not null && home.HasSpawnInside(content, entry));

    private static void Consider(ref Objective best, ulong bot, uint questId, uint entry, uint progress, uint itemUseId = 0, bool visited = false)
    {
        ulong key = PlayerbotContention.Mix(bot, ((ulong)questId << 32) | entry);
        if (best.Entry != 0)
        {
            if (!best.Visited && visited) return;
            if (best.Visited == visited && key >= best.Key) return;
        }
        best = new Objective { Entry = entry, Quest = questId, Progress = progress, ItemUseId = itemUseId, Key = key, Visited = visited };
    }

    /// <summary>
    /// Whether <paramref name="quest"/> objective slot <paramref name="index"/> is a creature objective credited by using the quest's
    /// supplied source item (<c>SrcItemId != 0</c>) on the creature (such as quest 5441 "Lazy Peons": item 16114 Foreman's Blackjack on
    /// creature 10556 Lazy Peon).
    /// </summary>
    internal static bool IsItemUseCreatureObjective(Quest quest, int index)
        => quest.Template.SrcItemId != 0
            && index >= 0 && index < quest.ReqCreatureOrGOId.Count
            && quest.ReqCreatureOrGOId[index] > 0
            && quest.ReqCreatureOrGOCount[index] > 0;

    internal static bool IsItemUseCreatureObjective(Player player, QuestNpcServices services, Quest quest, int index)
    {
        if (!IsItemUseCreatureObjective(quest, index)) return false;
        if (services.StateOf(player) is { Loaded: true } state
            && state.Quests.Get(quest.Id) is { Status: QuestStatus.Incomplete })
            return player.Inventory.GetItemCount(quest.Template.SrcItemId) > 0;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="creature"/> has already had the quest objective item used on it within
    /// <see cref="ItemUseTargetCooldownMs"/> or is an awake Lazy Peon (<see cref="ArcaneCore.Game.Creatures.Scripts.LazyPeonAI"/>).
    /// </summary>
    internal bool IsUnavailableItemTarget(Creature creature, uint nowMs = 0)
    {
        if (creature.AI is ArcaneCore.Game.Creatures.Scripts.LazyPeonAI { IsAsleep: false })
            return true;
        if (!_recentlyUsedItemTargets.TryGetValue(creature.Guid, out uint until))
            return false;
        uint checkMs = nowMs != 0 ? nowMs : (uint)(_session.World?.NowMs ?? 0);
        if (unchecked((int)(until - checkMs)) > 0)
            return true;
        _recentlyUsedItemTargets.Remove(creature.Guid);
        return false;
    }

    private bool TryUseObjectiveItem(Player player, uint elapsedMs, uint now)
    {
        if (PreferredItemUseId == 0 || PreferredCreatureEntry == 0 || player.Map is not { } map)
            return false;
        ArcaneCore.Game.Items.Item? item = null;
        foreach (ArcaneCore.Game.Items.Item candidateItem in player.Inventory.AllItems)
        {
            if (candidateItem.Entry == PreferredItemUseId)
            {
                item = candidateItem;
                break;
            }
        }
        if (item is null)
            return false;

        foreach (ObjectGuid expired in _recentlyUsedItemTargets.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _recentlyUsedItemTargets.Remove(expired);

        Creature? best = null;
        bool bestSleeping = false;
        float bestDistSq = float.MaxValue;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not Creature creature
                || creature.Entry != PreferredCreatureEntry
                || !creature.IsAlive || !creature.IsInWorld
                || !ReferenceEquals(creature.Map, map)
                || (creature.UnitFlags & UnitFlags.NotSelectable) != 0
                || IsUnavailableItemTarget(creature, now))
                continue;

            bool sleeping = creature.StandState == StandState.Sleep
                || creature.AI is ArcaneCore.Game.Creatures.Scripts.LazyPeonAI { IsAsleep: true };
            float dx = creature.X - player.X, dy = creature.Y - player.Y, dz = creature.Z - player.Z;
            float distSq = (dx * dx) + (dy * dy) + (dz * dz);
            if (!float.IsFinite(distSq)) continue;
            if (best is null || (sleeping && !bestSleeping) || (sleeping == bestSleeping && distSq < bestDistSq))
            {
                best = creature;
                bestSleeping = sleeping;
                bestDistSq = distSq;
            }
        }

        if (best is null)
            return false;

        QuestId = PreferredItemUseQuestId;
        TargetEntry = best.Entry;
        Goal = PlayerbotGoalKind.Quest;
        if (best.Guid != _itemUseTargetGuid || _stageNpc != default)
        {
            _itemUseTargetGuid = best.Guid;
            _route = null;
        }

        float reach = QuestNpcServices.InteractionDistance + best.BoundingRadius + player.BoundingRadius;
        if (!(float.IsFinite(bestDistSq) && float.IsFinite(reach) && reach > 0f && bestDistSq < reach * reach))
        {
            PlanOutcome planned = PlanOutcome.Route;
            if (_route is null)
                planned = PlayerbotNavigation.PlanExact(player, new System.Numerics.Vector3(best.X, best.Y, best.Z),
                    _options, ref _tileWait, now, out _route);
            if (planned == PlanOutcome.Pending)
                return true;
            if (planned == PlanOutcome.Refused)
            {
                _recentlyUsedItemTargets[best.Guid] = unchecked(now + ItemUseTargetCooldownMs);
                _route = null;
                return false;
            }
            if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, now))
                _route = null;
            return true;
        }

        _route = null;
        if (player.StandState != StandState.Stand)
        {
            _session.TryManagedAction(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes((uint)StandState.Stand));
            return true;
        }
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;
        if (_session.Services.GetService<ArcaneCore.World.Spells.SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting })
            return true;

        float angle = MathF.Atan2(best.Y - player.Y, best.X - player.X);
        PlayerbotMotion.Face(_session, player, angle);

        byte spellIndex = 0;
        for (byte i = 0; i < item.Template.Spells.Count; i++)
        {
            if (item.Template.Spells[i] is { SpellId: > 0, Trigger: 0 })
            {
                spellIndex = i;
                break;
            }
        }

        uint beforeProgress = CurrentObjectiveProgress(player, PreferredItemUseQuestId, PreferredCreatureEntry);
        if (_session.TryManagedAction(WorldOpcode.CmsgUseItem,
            PlayerbotNavigation.UseItemOnUnitPayload(item.BagSlot, item.Slot, best.Guid, spellIndex)))
        {
            _recentlyUsedItemTargets[best.Guid] = unchecked(now + ItemUseTargetCooldownMs);
            uint afterProgress = CurrentObjectiveProgress(player, PreferredItemUseQuestId, PreferredCreatureEntry);
            bool casting = _session.Services.GetService<ArcaneCore.World.Spells.SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
                { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting };
            if (!casting && afterProgress <= beforeProgress)
                ReportObjectiveNoProgress(player, PreferredItemUseQuestId, PreferredCreatureEntry, afterProgress, "item-use-no-credit");
        }
        return true;
    }

    /// <summary>
    /// Whether <paramref name="player"/> and <paramref name="other"/> both have an unfinished kill objective on creature
    /// <paramref name="entry"/> in the same quest: what makes a mob another player tagged a shared errand (a group would serve both).
    /// </summary>
    internal bool SharesKillQuest(Player player, Player other, uint entry)
    {
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services is null || services.StateOf(player) is not { Loaded: true } mine || services.StateOf(other) is not { Loaded: true } theirs)
            return false;
        foreach ((uint questId, QuestStatusData status) in mine.Quests.Statuses)
        {
            if (status.Status != QuestStatus.Incomplete || theirs.Quests.Get(questId) is not { Status: QuestStatus.Incomplete } their
                || services.Quests.Get(questId) is not { } quest) continue;
            for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
                if (quest.ReqCreatureOrGOId[index] == (int)entry && status.CreatureOrGOCount[index] < quest.ReqCreatureOrGOCount[index]
                    && their.CreatureOrGOCount[index] < quest.ReqCreatureOrGOCount[index]) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the bot implements the objective mechanics <paramref name="quest"/> requires when accepted: an auto-complete quest needs
    /// none; otherwise bots skip exploration/escort/event-script quests (<see cref="QuestSpecialFlags.ExplorationOrEvent"/>), GameObject
    /// objectives (<c>ReqCreatureOrGOId</c> negative, such as quest 5382 "Doctor Theolen Krastinov, the Butcher"), spell-cast or friendly
    /// creature objectives that are not backed by a quest source item (<see cref="IsItemUseCreatureObjective(Player, QuestNpcServices, Quest, int)"/>),
    /// and creature objectives whose spawns exist only on another continent or an instance without an entrance trigger on the bot's map.
    /// </summary>
    internal static bool CanExecuteObjectives(WorldSession? session, Player player, QuestNpcServices services,
        ArcaneCore.Kernel.WorldData.Creatures.CreatureContent? content, Quest quest)
    {
        if (quest.IsAutoComplete) return true;
        if (quest.HasSpecialFlag(QuestSpecialFlags.ExplorationOrEvent)) return false;
        for (int i = 0; i < quest.ReqCreatureOrGOId.Count; i++)
        {
            uint required = quest.ReqCreatureOrGOCount[i];
            int target = quest.ReqCreatureOrGOId[i];
            if (quest.ReqSpell[i] != 0)
            {
                if (required == 0 || target <= 0 || !IsItemUseCreatureObjective(player, services, quest, i))
                    return false;
                uint spellEntry = (uint)target;
                if (content is not null && !HasReachableSpawn(session, player, content, spellEntry))
                    return false;
                continue;
            }
            if (required == 0 || target == 0) continue;
            if (target < 0) return false;
            uint entry = (uint)target;
            if (!IsKillObjective(session, player, services, content, entry)
                && !IsItemUseCreatureObjective(player, services, quest, i)) return false;
            if (content is not null && !HasReachableSpawn(session, player, content, entry))
            {
                return false;
            }
        }

        ArcaneCore.Kernel.WorldData.Loot.LootContent? loot = null;
        for (int i = 0; i < quest.ReqItemId.Count; i++)
        {
            uint item = quest.ReqItemId[i];
            uint needed = quest.ReqItemCount[i];
            if (item == 0 || needed == 0 || item == quest.Template.SrcItemId) continue;
            if (player.Inventory.GetItemCount(item) >= needed || services.Npcs.SellsItem(item)) continue;
            loot ??= session?.Services.GetService<GameObjectLootFeature>()?.LootContent;
            if (loot is not { RowCount: > 0 }) continue;
            if (loot.CreatureInfoCount > 0
                ? PlayerbotDropIndex.DroppersOf(loot, item).Count == 0
                : !loot.HasLootItem(item))
                return false;
        }

        return true;
    }

    private static bool HasReachableSpawn(WorldSession? session, Player player,
        ArcaneCore.Kernel.WorldData.Creatures.CreatureContent content, uint entry)
    {
        if (content.GetSpawns(player.MapId, entry).Count > 0) return true;
        if (!HasAnySpawn(content, entry)) return true;
        if (session?.World is not { } world) return false;
        ArcaneCore.Game.Maps.Templates.WorldMaps maps = ArcaneCore.Game.Maps.Templates.WorldMaps.Of(world);
        var here = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        foreach (uint mapId in content.MapsWithSpawns)
        {
            if (maps.Registry.Find(mapId) is not { IsDungeon: true }) continue;
            if (content.GetSpawns(mapId, entry).Count == 0) continue;
            if (Groups.PlayerbotGroupCoordinator.EntranceTo(maps, player.MapId, mapId, here) is not null) return true;
        }

        return false;
    }

    private static bool HasAnySpawn(ArcaneCore.Kernel.WorldData.Creatures.CreatureContent content, uint entry)
    {
        foreach (uint map in content.MapsWithSpawns)
        {
            if (content.GetSpawns(map, entry).Count > 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether creatures of <paramref name="entry"/> are something the bot can kill for a quest's creature objective: not friendly to
    /// it. A friendly objective is credited another way, an item or a spell used on it (quest 5441 "Lazy Peons": the Foreman's
    /// Blackjack on a sleeping peon of the bot's own faction), which the bot does not do; hunted, it walked from peon to peon it could
    /// not attack (live stress test 2026-10-08: goal Grind/Explore target 10556 in the Valley of Trials, and on out of it to its
    /// death). An entry whose reaction cannot be resolved stays an objective.
    /// </summary>
    internal bool IsKillObjective(Player player, QuestNpcServices services, uint entry)
        => IsKillObjective(_session, player, services,
            _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content, entry);

    internal static bool IsKillObjective(WorldSession? session, Player player, QuestNpcServices services,
        ArcaneCore.Kernel.WorldData.Creatures.CreatureContent? content, uint entry)
        => (content ?? session?.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content)?.FindTemplate(entry) is not { } template
            || IsKillable(player, template.Faction,
                session?.Services.GetService<QuestNpcFeature>()?.FactionTemplates ?? session?.Services.GetService<FactionTemplateCatalog>(),
                services.Deps.Reputation as ArcaneCore.Game.Reputation.INpcReactionSource);

    /// <summary>
    /// Whether a creature of faction template <paramref name="npcFactionTemplate"/> is not friendly to <paramref name="player"/>
    /// (<see cref="IsKillObjective(Player, QuestNpcServices, uint)"/>): its reputation reaction below Friendly, or without reputation its template neither resolved
    /// as friendly. Unresolved: true.
    /// </summary>
    internal static bool IsKillable(Player player, uint npcFactionTemplate, FactionTemplateCatalog? factions,
        ArcaneCore.Game.Reputation.INpcReactionSource? reactions)
    {
        if (factions?.Find(npcFactionTemplate) is not { } npc || factions.Find(player.FactionTemplate) is not { } self) return true;
        if (reactions is not null)
            return !reactions.TryGetNpcReaction(player, npc, self, out ArcaneCore.Game.Reputation.ReputationRank reaction)
                || reaction < ArcaneCore.Game.Reputation.ReputationRank.Friendly;
        return !factions.TryNpcHostility(npcFactionTemplate, player.FactionTemplate, out bool hostile) || hostile
            || !npc.IsFriendlyTo(self);
    }

    /// <summary>
    /// The reward to take: the usable item that gains this build most over what it wears, else the one that sells for most
    /// (<see cref="PlayerbotItemScore.ChooseQuestReward"/>); choice 0 when the quest offers no choice.
    /// </summary>
    internal static uint RewardChoice(Player player, Quest quest)
        => quest.RewChoiceItemsCount == 0 ? 0u : PlayerbotItemScore.ChooseQuestReward(player, quest.RewChoiceItemId, quest.RewChoiceItemCount,
            PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights);

    /// <summary>
    /// Whether the quest's source item (<c>SrcItemId</c>, given on accept) fits the bags: the server counts what the player already owns
    /// (bank included) towards <c>SrcItemCount</c> and needs room for the rest (vmangos <c>Player::CanGiveQuestSourceItemIfNeed</c>,
    /// Player.cpp:13643-13676), refusing the accept otherwise. A quest without a source item always fits.
    /// </summary>
    internal static bool SourceItemFits(Player player, Quest quest)
    {
        uint entry = quest.Template.SrcItemId;
        if (entry == 0 || !player.Inventory.IsLoaded)
            return true;
        uint wanted = quest.Template.SrcItemCount == 0 ? 1u : quest.Template.SrcItemCount;
        uint owned = player.Inventory.GetItemCount(entry, inBankAlso: true);
        return owned >= wanted
            || player.Inventory.CanStoreNewItem(entry, wanted - owned, [], out _) == ArcaneCore.Game.Items.InventoryResult.Ok;
    }

    /// <summary>Whether the server refused this quest's exchange lately (<see cref="RefusedQuestMs"/>); travel to it is pointless too.</summary>
    internal bool IsRefused(uint questId)
    {
        if (Suspensions?.IsQuestSuspended(questId, _session.World.NowMs) == true) return true;
        if (_awaitingBagSpace.Contains(questId)) return true;
        if (!_refused.TryGetValue(questId, out uint until)) return false;
        if (unchecked((int)(until - _session.World.NowMs)) > 0) return true;
        _refused.Remove(questId);
        return false;
    }

    /// <summary>Sets <paramref name="questId"/> aside for <see cref="RefusedQuestMs"/>: neither this controller nor quest travel (<see cref="IsRefused"/>) takes it up.</summary>
    internal void SetAside(uint questId) => Refuse(questId, _session.World.NowMs);

    internal void RecordTurnInRefusal(Player player, uint questId, uint enderEntry, string reason, string code, uint now, bool suspendEntry = false)
        => RefuseTurnIn(player, questId, enderEntry, reason, code, now, suspendEntry);

    private bool NoteCloseApproachFailure(Player player, uint questId, uint enderEntry, uint now)
    {
        var spot = new System.Numerics.Vector3(player.X, player.Y, player.Z);
        if (questId != _approachFailQuest || enderEntry != _approachFailEntry
            || System.Numerics.Vector3.DistanceSquared(spot, _approachFailSpot) > ApproachSpotYards * ApproachSpotYards)
        {
            _approachFailQuest = questId;
            _approachFailEntry = enderEntry;
            _approachFailSpot = spot;
            _approachFailCount = 0;
        }
        if (++_approachFailCount < ApproachConfirmFailures) return false;
        _approachFailCount = 0;
        RefuseTurnIn(player, questId, enderEntry, "no exact route to visible ender", "NoRouteToVisibleEnder", now, suspendEntry: true);
        return true;
    }

    private void RefuseTurnIn(Player player, uint questId, uint enderEntry, string reason, string code, uint now, bool suspendEntry = false)
    {
        ObjectGuid enderGuid = _stageNpc;
        LastTurnInRefusal = (questId, enderEntry, reason, code);
        bool inventoryFull = code == "InventoryFull"
            || reason.Contains("inventory full", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("InventoryFull", StringComparison.OrdinalIgnoreCase);
        if (inventoryFull)
            NeedsBagSpaceForTurnIn = true;

        bool missingUnlistedReq = (code is "DontHaveReq" or "IncompleteAtEnder" or "MissingDeliveryItem"
                || reason.Contains("DontHaveReq", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("requirements not met", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("missing required delivery item", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("autocomplete requirement", StringComparison.OrdinalIgnoreCase))
            && _session.Services.GetService<QuestNpcFeature>()?.Services.Quests.Get(questId) is { } quest
            && (quest.IsAutoComplete || (quest.ReqItemCount.All(c => c == 0) && quest.ReqCreatureOrGOCount.All(c => c == 0)));
        if (missingUnlistedReq)
            Suspensions?.MarkUnreachableQuest(questId, now);

        Refuse(questId, now);
        Suspensions?.SuspendQuest(questId, now);
        if (suspendEntry && enderEntry != 0) Suspensions?.SuspendEntry(enderEntry, now);
        if (ReturnQuest == questId) ReturnQuest = 0;
        QuestId = 0;
        TargetEntry = 0;
        if (!_session.IsInChaosProbe)
        {
            PlayerbotAnomalies.Report(player, "quest", "turnin-refused",
                $"quest={questId} ender={enderEntry} guid=0x{enderGuid.Value:X} code={code} reason={reason}",
                inventoryFull ? AnomalySeverity.Info : AnomalySeverity.Warning);
        }
        Backoff(now);
        if (_session.Services.GetService<Microsoft.Extensions.Logging.ILogger<PlayerbotQuestGoals>>() is { } log)
        {
            if (inventoryFull || missingUnlistedReq)
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log,
                    "Playerbot {Bot} sets aside completed quest {Quest} at ender {Entry} ({X:F1},{Y:F1},{Z:F1} map {Map}): turn-in refused ({Reason})",
                    player.Name, questId, enderEntry, player.X, player.Y, player.Z, player.MapId, reason);
            }
            else
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(log,
                    "Playerbot {Bot} sets aside completed quest {Quest} at ender {Entry} ({X:F1},{Y:F1},{Z:F1} map {Map}): turn-in refused ({Reason})",
                    player.Name, questId, enderEntry, player.X, player.Y, player.Z, player.MapId, reason);
            }
        }
    }

    /// <summary>Report (only) an accept that did not land by its deadline: the bot's reaction to it is unchanged.</summary>
    private void ReportAcceptRefused(Player player, QuestNpcServices services, PlayerNpcState state, ObjectGuid giverGuid, uint questId,
        uint giverEntry, string fallbackCode, string fallbackReason)
    {
        if (_session.IsInChaosProbe) return;
        (string code, string reason) = DiagnoseAcceptRefusal(player, services, state, giverGuid, questId, fallbackCode, fallbackReason);
        // The bags have no room for the quest's source item: asking again, on the ten minute timer, would be refused again for as long as
        // the bags stay full (10k accept-refused on the live server, the same quest per bot every ten minutes). It waits for a free slot.
        if (code == "InventoryFull")
            _awaitingBagSpace.Add(questId);
        PlayerbotAnomalies.Report(player, "quest", "accept-refused",
            $"quest={questId} giver={giverEntry} guid=0x{giverGuid.Value:X} code={code} reason={reason}");
    }

    private (string Code, string Reason) DiagnoseAcceptRefusal(Player player, QuestNpcServices services, PlayerNpcState state,
        ObjectGuid giverGuid, uint questId, string fallbackCode, string fallbackReason)
    {
        string? packetCode = DrainQuestFailureCode(questId);
        if (state.Quests.FindSlot(0) >= QuestConstants.MaxQuestLogSize)
            return ("QuestLogFull", "quest log is full");
        if (services.InteractableNpc(player, giverGuid, NpcFlags.QuestGiver, out string? interactErr) is null)
            return (TurnInRefusalCode(interactErr, packetCode ?? "GiverNotInteractable"), interactErr ?? "quest giver is not interactable");
        if (services.Quests.Get(questId) is { Template.SrcItemId: > 0 } quest
            && PlayerbotTownGoals.FreeBagSlots(player) <= 0
            && player.Inventory.GetItemCount(quest.Template.SrcItemId) == 0)
            return ("InventoryFull", $"inventory full for source item {quest.Template.SrcItemId}");
        if (services.CanTakeQuest(player, questId) == false)
            return (packetCode ?? "CannotTakeQuest", packetCode is not null ? $"player cannot take quest ({packetCode})" : "player does not meet quest requirements");
        if (packetCode is not null)
            return (packetCode, $"quest accept refused ({packetCode})");
        return (fallbackCode, fallbackReason);
    }

    private string? DrainQuestFailureCode(uint questId)
    {
        string? code = null;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(
            WorldOpcode.SmsgQuestgiverQuestFailed, WorldOpcode.SmsgQuestgiverQuestInvalid))
        {
            if (packet.Opcode == WorldOpcode.SmsgQuestgiverQuestInvalid && packet.Payload.Length >= 4)
            {
                var reader = new PacketReader(packet.Payload);
                uint invalidReason = reader.ReadUInt32();
                code = Enum.IsDefined((QuestInvalidReason)invalidReason)
                    ? ((QuestInvalidReason)invalidReason).ToString()
                    : $"InvalidReason{invalidReason}";
            }
            else if (packet.Opcode == WorldOpcode.SmsgQuestgiverQuestFailed && packet.Payload.Length >= 8)
            {
                var reader = new PacketReader(packet.Payload);
                uint failedQuest = reader.ReadUInt32();
                uint failedReason = reader.ReadUInt32();
                if (questId == 0 || failedQuest == questId)
                {
                    code = Enum.IsDefined((ArcaneCore.Game.Items.InventoryResult)failedReason)
                        ? ((ArcaneCore.Game.Items.InventoryResult)failedReason).ToString()
                        : $"FailedReason{failedReason}";
                }
            }
        }
        return code;
    }

    /// <summary>One quest whose giver or ender is dead or gone: when the bot looks at it again, and how often it found it still so.</summary>
    private sealed class GiverWait
    {
        public ObjectGuid Giver;
        public uint RetryAt;
        public int Attempts;
    }

    private readonly Dictionary<uint, GiverWait> _giverWaits = [];

    /// <summary>Quests whose accept the server refused for want of bag room for the source item: set aside until a bag slot is free (<see cref="IsRefused"/>).</summary>
    private readonly HashSet<uint> _awaitingBagSpace = [];

    /// <summary>The first look at a quest whose giver is dead and whose respawn is unknown, and the step by which later looks are spaced.</summary>
    internal const uint GiverRetryMinMs = 60_000;

    /// <summary>The longest wait between two looks at a giver that stays dead.</summary>
    internal const uint GiverRetryMaxMs = 300_000;

    /// <summary>Added to a known respawn time before the bot looks again.</summary>
    internal const uint GiverRespawnMarginMs = 5_000;

    /// <summary>The longest known respawn the bot waits for in one go.</summary>
    internal const uint GiverRespawnCapMs = 900_000;

    private const int MaxGiverWaits = 64;

    /// <summary>Whether the giver of <paramref name="questId"/> was found dead or gone and it is not yet time to look again. Only this quest's step is skipped.</summary>
    internal bool IsGiverWait(uint questId)
        => _giverWaits.TryGetValue(questId, out GiverWait? wait) && unchecked((int)(wait.RetryAt - _session.World.NowMs)) > 0;

    /// <summary>The giver of <paramref name="questId"/> is in sight and alive after its wait: the episode is over.</summary>
    private void EndGiverWait(uint questId, ObjectGuid giver)
    {
        if (_giverWaits.TryGetValue(questId, out GiverWait? wait) && wait.Giver == giver) _giverWaits.Remove(questId);
    }

    /// <summary>
    /// The giver (or ender) of the quest step in hand is dead or gone. The bot drops the stage at once and the quest as its goal, and looks again later:
    /// at the creature's respawn time plus a margin when its spawn row gives one, otherwise after 60 s, doubling to 5 min while it stays dead.
    /// The quest is neither abandoned nor refused, and the giver is not suspended; only this quest's step waits. Reported once per episode.
    /// </summary>
    private void GiverGone(Player player, QuestNpcServices services, uint questId, ObjectGuid giver, uint entry, string reason, uint now)
    {
        var content = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content;
        var spawn = entry == 0 ? null : content?.GetSpawns(player.MapId, entry).FirstOrDefault(s => s.Guid == giver.Low);
        uint respawnMs = spawn is { SpawnTimeMaxSeconds: > 0 } && spawn.SpawnTimeMaxSeconds != Game.Creatures.Creature.RespawnNeverSeconds
            ? (uint)Math.Min((ulong)spawn.SpawnTimeMaxSeconds * 1000UL + GiverRespawnMarginMs, GiverRespawnCapMs)
            : 0;
        bool newEpisode = !_giverWaits.TryGetValue(questId, out GiverWait? wait) || wait.Giver != giver;
        if (newEpisode)
        {
            if (_giverWaits.Count >= MaxGiverWaits) _giverWaits.Remove(_giverWaits.OrderBy(w => w.Value.RetryAt).First().Key);
            wait = new GiverWait { Giver = giver };
            _giverWaits[questId] = wait;
        }

        uint delay = wait!.Attempts == 0 && respawnMs != 0
            ? respawnMs
            : Math.Min(GiverRetryMinMs << Math.Min(wait.Attempts, 3), GiverRetryMaxMs);
        wait.Attempts++;
        wait.RetryAt = unchecked(now + delay);

        ClearStage();
        _route = null;
        QuestId = 0;
        TargetEntry = 0;
        if (ReturnQuest == questId) ReturnQuest = 0;
        if (newEpisode && !_session.IsInChaosProbe)
            PlayerbotAnomalies.Report(player, "quest", "giver-not-found",
                $"quest={questId} giver={entry} guid=0x{giver.Value:X} reason={reason} retryInMs={delay}", AnomalySeverity.Info);
    }

    private void ReportObjectiveNoProgress(Player player, uint questId, uint entry, uint progress, string reason)
    {
        if (_session.IsInChaosProbe || questId == 0 || entry == 0) return;
        var episode = (questId, entry, progress);
        if (_lastNoProgressEpisode == episode) return;
        _lastNoProgressEpisode = episode;
        PlayerbotAnomalies.Report(player, "quest", "objective-no-progress",
            $"quest={questId} entry={entry} progress={progress} reason={reason}");
    }

    private uint CurrentObjectiveProgress(Player player, uint questId, uint entry)
    {
        if (questId == 0 || entry == 0
            || _session.Services.GetService<QuestNpcFeature>()?.Services is not { } services
            || services.StateOf(player) is not { Loaded: true } state
            || state.Quests.Get(questId) is not { } status
            || services.Quests.Get(questId) is not { } quest)
            return 0;
        for (int i = 0; i < quest.ReqCreatureOrGOId.Count; i++)
        {
            if (quest.ReqCreatureOrGOId[i] == (int)entry)
                return status.CreatureOrGOCount[i];
        }
        return 0;
    }

    internal static string TurnInRefusalCode(string? reason, string fallback)
    {
        if (string.IsNullOrEmpty(reason)) return fallback;
        if (reason.Contains("inventory full", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("InventoryFull", StringComparison.OrdinalIgnoreCase)) return "InventoryFull";
        if (reason.Contains("DontHaveReq", StringComparison.OrdinalIgnoreCase)) return "DontHaveReq";
        if (reason.Contains("missing required delivery item", StringComparison.OrdinalIgnoreCase)) return "MissingDeliveryItem";
        if (reason.Contains("not enough money", StringComparison.OrdinalIgnoreCase)) return "NotEnoughMoney";
        if (reason.Contains("duplicate unique", StringComparison.OrdinalIgnoreCase)) return "DuplicateUniqueItem";
        if (reason.Contains("reputation", StringComparison.OrdinalIgnoreCase)) return "ReputationRefused";
        if (reason.Contains("out of interaction range", StringComparison.OrdinalIgnoreCase)) return "OutOfRange";
        if (reason.Contains("no exact route", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("no walkable close approach", StringComparison.OrdinalIgnoreCase)) return "NoRouteToVisibleEnder";
        if (reason.Contains("objectives incomplete", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("status is not Complete", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("requirements not met", StringComparison.OrdinalIgnoreCase)) return "IncompleteAtEnder";
        if (reason.Contains("dead", StringComparison.OrdinalIgnoreCase)) return "EnderDead";
        if (reason.Contains("hostile", StringComparison.OrdinalIgnoreCase)) return "EnderHostile";
        if (reason.Contains("combat", StringComparison.OrdinalIgnoreCase)) return "EnderInCombat";
        return fallback;
    }

    private void Refuse(uint questId, uint now)
    {
        if (questId == 0) return;
        foreach (uint expired in _refused.Where(entry => unchecked((int)(entry.Value - now)) <= 0).Select(entry => entry.Key).ToArray())
            _refused.Remove(expired);
        if (_refused.Count >= MaxRefused) _refused.Remove(_refused.OrderBy(entry => entry.Value).First().Key);
        _refused[questId] = unchecked(now + RefusedQuestMs);
    }

    private void ArmStage(QuestTarget target)
    {
        _stageNpc = target.Npc.Guid;
        _stageQuest = target.Quest.Id;
        _stageDeadline = unchecked(_session.World.NowMs + 15_000);
    }

    private void ClearStage()
    {
        _stage = Stage.None;
        _stageNpc = default;
        _stageQuest = 0;
        _stageDeadline = 0;
    }

    private void Backoff(uint now)
    {
        ClearStage();
        _route = null;
        _backoffUntil = unchecked(now + 2_000);
    }

    private static byte[] QuestPayload(ulong guid, uint quest)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(guid);
        writer.WriteUInt32(quest);
        return writer.ToArray();
    }

    private static byte[] QuestChoicePayload(ulong guid, uint quest, uint choice)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(guid);
        writer.WriteUInt32(quest);
        writer.WriteUInt32(choice);
        return writer.ToArray();
    }

    private sealed record QuestTarget(NpcInfo Npc, Quest Quest, QuestAction Action);
    private enum QuestAction { AcceptQuery, Accept, Complete, RewardRequest, RewardChoice }
    private enum Stage { None, AcceptDetails, Accept, AcceptConfirmation, CompleteResponse, RewardRequest, RewardOffer, RewardChoice, RewardConfirmation }
}

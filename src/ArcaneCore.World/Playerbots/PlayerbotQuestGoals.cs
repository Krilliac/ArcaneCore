using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
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

namespace ArcaneCore.World.Playerbots;

/// <summary>Deterministic quest ownership over the ordinary quest handlers and live journal.</summary>
internal sealed class PlayerbotQuestGoals(WorldSession session, PlayerbotOptions options)
{
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly PlayerbotQuestTripFeasibility _trip = new();

    internal int TripEvaluations => _trip.Evaluations;
    private PlayerbotRoute? _route;
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
    internal uint QuestId { get; private set; }
    internal uint TargetEntry { get; private set; }
    internal uint PreferredCreatureEntry { get; private set; }
    internal string StageName => _stage.ToString();

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

    internal bool HasCandidate(Player player) => FindCandidate(player) is not null;

    internal uint CompletedReturnQuest(Player player)
    {
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        CreatureContent? content = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content;
        if (services?.StateOf(player) is not { Loaded: true } state || content is null) return 0;
        return state.Quests.Statuses.OrderBy(row => row.Key)
            .Where(row => row.Value is { Status: QuestStatus.Complete, Rewarded: false }
                && services.IsRewardable(row.Key) && !IsRefused(row.Key))
            .Where(row => services.Quests.CreatureEndersOf(row.Key)
                .Any(entry => content.GetSpawns(player.MapId, entry).Any(spawn => PlayerbotWorldDestinations.IsPresent(player, spawn))))
            .Select(row => row.Key).FirstOrDefault();
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
                if (Home is null) Suspensions?.MarkUnreachableQuest(id);
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

    internal static bool ReasonableReturnTrip(PlayerbotHomeArea home, uint mapId, float playerX, float playerY,
        ArcaneCore.Kernel.WorldData.Creatures.CreatureSpawn ender)
    {
        if (ender.MapId != mapId) return false;
        float dx = ender.X - playerX, dy = ender.Y - playerY;
        return home.Contains(ender.MapId, ender.X, ender.Y, ender.Z) || dx * dx + dy * dy <= 600f * 600f;
    }

    internal bool Update(Player player, uint elapsedMs)
    {
        if (!_options.Enabled || !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat)
            return false;

        uint now = _session.World.NowMs;
        if (_backoffUntil != 0 && unchecked(now - _backoffUntil) > int.MaxValue)
            return false;
        if (_backoffUntil != 0)
            _backoffUntil = 0;
        if (_stage != Stage.None && _stageDeadline != 0 && unchecked(now - _stageDeadline) <= int.MaxValue)
        {
            Refuse(_stageQuest, now);
            ClearStage();
            _backoffUntil = unchecked(now + 2_000);
            return false;
        }

        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services?.StateOf(player) is not { Loaded: true } state)
            return false;
        RefreshObjectiveEntry(player, services, state);
        if (_stage == Stage.AcceptConfirmation)
        {
            if (state.Quests.Get(_stageQuest) is { Status: QuestStatus.Incomplete or QuestStatus.Complete }) ClearStage();
            return _stage != Stage.None;
        }
        if (_stage == Stage.RewardConfirmation)
        {
            if (state.Quests.Get(_stageQuest)?.Rewarded == true) ClearStage();
            return _stage != Stage.None;
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
                { _stage = Stage.Accept; confirmed = true; }
                else if (_stage == Stage.CompleteResponse && packet.Opcode == WorldOpcode.SmsgQuestgiverRequestItems)
                { _stage = Stage.RewardRequest; confirmed = true; }
                else if ((_stage is Stage.CompleteResponse or Stage.RewardOffer) && packet.Opcode == WorldOpcode.SmsgQuestgiverOfferReward)
                { _stage = Stage.RewardChoice; confirmed = true; }
            }
            if (!confirmed) return true;
        }

        QuestTarget? target = FindCandidate(player, services, state);
        if (target is null)
            return _stage != Stage.None;

        QuestId = target.Quest.Id;
        TargetEntry = target.Npc.Entry;
        if (target.Npc.Guid != _stageNpc || target.Quest.Id != _stageQuest)
            _route = null;

        if (!WithinReach(player, target.Npc))
        {
            if (_route is null && !PlayerbotNavigation.TryPlan(player,
                    new System.Numerics.Vector3(target.Npc.X, target.Npc.Y, target.Npc.Z), _options, out _route,
                    completedQuestReturn: target.Action >= QuestAction.Complete
                        && state.Quests.Get(target.Quest.Id) is { Status: QuestStatus.Complete, Rewarded: false }))
            {
                Backoff(now);
                return false;
            }

            Goal = PlayerbotGoalKind.Quest;
            if (!PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, now))
                _route = null;
            return true;
        }

        _route = null;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

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
                if (_session.TryManagedAction(WorldOpcode.CmsgQuestgiverChooseReward,
                        QuestChoicePayload(target.Npc.Guid.Value, target.Quest.Id, RewardChoice(player, target.Quest))))
                { _stage = Stage.RewardConfirmation; ArmStage(target); }
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
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (services.Deps.Creatures?.Find(player, guid) is not { IsAlive: true, IsHostile: false, IsNotSelectable: false } npc
                || (npc.NpcFlags & NpcFlags.QuestGiver) == 0)
                continue;
            if (_stage != Stage.None && npc.Guid != _stageNpc)
                continue;
            if (Suspensions?.IsEntrySuspended(npc.Entry, _session.World.NowMs) == true)
                continue;

            foreach (uint id in services.Quests.EndersOf(npc.Entry))
            {
                if (!services.IsRewardable(id) || IsRefused(id)) continue;
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

            if (returning && !WithinReach(player, npc))
                continue;

            foreach (uint id in services.Quests.StartersOf(npc.Entry))
            {
                if (!services.IsRewardable(id) || IsRefused(id)) continue;
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
    /// The server's creature reach: vmangos <c>WorldObject::IsWithinDist</c> (Object.cpp:1738) adds both bounding radii and compares strict
    /// 3D distance. The last walkable point can already be in reach. <see cref="Update"/> and the return-trip filter share it.
    /// </summary>
    private static bool WithinReach(Player player, NpcInfo npc)
    {
        float dx = npc.X - player.X, dy = npc.Y - player.Y, dz = npc.Z - player.Z;
        float reach = QuestNpcServices.InteractionDistance + npc.BoundingRadius + player.BoundingRadius;
        float distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
        return float.IsFinite(distanceSquared) && float.IsFinite(reach) && reach > 0 && distanceSquared < reach * reach;
    }

    private void RefreshObjectiveEntry(Player player, QuestNpcServices services, PlayerNpcState state)
    {
        PreferredCreatureEntry = 0;
        ArcaneCore.Kernel.WorldData.Creatures.CreatureContent? content = Home is null ? null
            : _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content;
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses)
        {
            // vmangos Quest::IsActive: a quest of a game event that is not running (game_event_quest, the server's event state) is
            // neither offered nor turned in (PrepareQuestMenu skips it for the giver and the ender alike; IsRewardable already keeps the
            // bot from walking to its ender). One in the log is kept for when the event comes back, but its objectives are not hunted.
            if (status.Status != QuestStatus.Incomplete || services.Quests.Get(questId) is not { IsActive: true } quest)
                continue;
            if (Suspensions?.IsQuestSuspended(questId, _session.World.NowMs) == true)
                continue;
            for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
            {
                int entry = quest.ReqCreatureOrGOId[index];
                uint required = quest.ReqCreatureOrGOCount[index];
                if (entry > 0 && status.CreatureOrGOCount[index] < required
                    && HeldForGroup?.Invoke(questId, (uint)entry) != true
                    && Suspensions?.IsEntrySuspended((uint)entry, _session.World.NowMs) != true
                    && IsKillObjective(player, services, (uint)entry)
                    && (Home is not { } home || content is not null && home.HasSpawnInside(content, (uint)entry)))
                {
                    PreferredCreatureEntry = (uint)entry;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Whether creatures of <paramref name="entry"/> are something the bot can kill for a quest's creature objective: not friendly to
    /// it. A friendly objective is credited another way, an item or a spell used on it (quest 5441 "Lazy Peons": the Foreman's
    /// Blackjack on a sleeping peon of the bot's own faction), which the bot does not do; hunted, it walked from peon to peon it could
    /// not attack (live stress test 2026-10-08: goal Grind/Explore target 10556 in the Valley of Trials, and on out of it to its
    /// death). An entry whose reaction cannot be resolved stays an objective.
    /// </summary>
    internal bool IsKillObjective(Player player, QuestNpcServices services, uint entry)
        => _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content.FindTemplate(entry) is not { } template
            || IsKillable(player, template.Faction, _session.Services.GetService<QuestNpcFeature>()?.FactionTemplates,
                services.Deps.Reputation as ArcaneCore.Game.Reputation.INpcReactionSource);

    /// <summary>
    /// Whether a creature of faction template <paramref name="npcFactionTemplate"/> is not friendly to <paramref name="player"/>
    /// (<see cref="IsKillObjective"/>): its reputation reaction below Friendly, or without reputation its template neither resolved
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

    /// <summary>Whether the server refused this quest's exchange lately (<see cref="RefusedQuestMs"/>); travel to it is pointless too.</summary>
    internal bool IsRefused(uint questId)
    {
        if (Suspensions?.IsQuestSuspended(questId, _session.World.NowMs) == true) return true;
        if (!_refused.TryGetValue(questId, out uint until)) return false;
        if (unchecked((int)(until - _session.World.NowMs)) > 0) return true;
        _refused.Remove(questId);
        return false;
    }

    /// <summary>Sets <paramref name="questId"/> aside for <see cref="RefusedQuestMs"/>: neither this controller nor quest travel (<see cref="IsRefused"/>) takes it up.</summary>
    internal void SetAside(uint questId) => Refuse(questId, _session.World.NowMs);

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

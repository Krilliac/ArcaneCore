using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>Deterministic quest ownership over the ordinary quest handlers and live journal.</summary>
internal sealed class PlayerbotQuestGoals(WorldSession session, PlayerbotOptions options)
{
    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private PlayerbotRoute? _route;
    private ObjectGuid _stageNpc;
    private uint _stageQuest;
    private uint _stageDeadline;
    private uint _backoffUntil;
    private Stage _stage;

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Quest;
    internal uint QuestId { get; private set; }
    internal uint TargetEntry { get; private set; }
    internal uint PreferredCreatureEntry { get; private set; }
    internal string StageName => _stage.ToString();

    internal bool HasCandidate(Player player) => FindCandidate(player) is not null;

    internal uint CompletedReturnQuest(Player player)
    {
        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        CreatureContent? content = _session.Services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>()?.Content;
        if (services?.StateOf(player) is not { Loaded: true } state || content is null) return 0;
        return state.Quests.Statuses.OrderBy(row => row.Key)
            .Where(row => row.Value is { Status: QuestStatus.Complete, Rewarded: false }
                && services.IsRewardable(row.Key))
            .Where(row => services.Quests.CreatureEndersOf(row.Key)
                .Any(entry => content.GetSpawns(player.MapId, entry).Count > 0))
            .Select(row => row.Key).FirstOrDefault();
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
            ClearStage();
            _backoffUntil = unchecked(now + 2_000);
            return false;
        }

        QuestNpcServices? services = _session.Services.GetService<QuestNpcFeature>()?.Services;
        if (services?.StateOf(player) is not { Loaded: true } state)
            return false;
        RefreshObjectiveEntry(services, state);
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

        float dx = target.Npc.X - player.X, dy = target.Npc.Y - player.Y, dz = target.Npc.Z - player.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > QuestNpcServices.InteractionDistance * QuestNpcServices.InteractionDistance)
        {
            if (_route is null && !PlayerbotNavigation.TryPlan(player,
                    new System.Numerics.Vector3(target.Npc.X, target.Npc.Y, target.Npc.Z), _options, out _route))
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
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (services.Deps.Creatures?.Find(player, guid) is not { IsAlive: true, IsHostile: false, IsNotSelectable: false } npc
                || (npc.NpcFlags & NpcFlags.QuestGiver) == 0)
                continue;
            if (_stage != Stage.None && npc.Guid != _stageNpc)
                continue;

            foreach (uint id in services.Quests.EndersOf(npc.Entry))
            {
                if (!services.IsRewardable(id)) continue;
                if (_stage != Stage.None && id != _stageQuest)
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

            foreach (uint id in services.Quests.StartersOf(npc.Entry))
            {
                if (!services.IsRewardable(id)) continue;
                if (_stage != Stage.None && id != _stageQuest)
                    continue;
                if (services.Quests.Get(id) is not { } quest
                    || (_stage != Stage.Accept && services.CanTakeQuest(player, id) != true))
                    continue;
                if (_stage != Stage.Accept && state.Quests.Get(id) is { Status: QuestStatus.Incomplete or QuestStatus.Complete })
                    continue;
                return new QuestTarget(npc, quest,
                    _stage == Stage.Accept ? QuestAction.Accept : QuestAction.AcceptQuery);
            }
        }

        return null;
    }

    private void RefreshObjectiveEntry(QuestNpcServices services, PlayerNpcState state)
    {
        PreferredCreatureEntry = 0;
        foreach ((uint questId, QuestStatusData status) in state.Quests.Statuses)
        {
            if (status.Status != QuestStatus.Incomplete || services.Quests.Get(questId) is not { } quest)
                continue;
            for (int index = 0; index < quest.ReqCreatureOrGOId.Count; index++)
            {
                int entry = quest.ReqCreatureOrGOId[index];
                uint required = quest.ReqCreatureOrGOCount[index];
                if (entry > 0 && status.CreatureOrGOCount[index] < required)
                {
                    PreferredCreatureEntry = (uint)entry;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The reward to take: the usable item that gains this build most over what it wears, else the one that sells for most
    /// (<see cref="PlayerbotItemScore.ChooseQuestReward"/>); choice 0 when the quest offers no choice.
    /// </summary>
    internal static uint RewardChoice(Player player, Quest quest)
        => quest.RewChoiceItemsCount == 0 ? 0u : PlayerbotItemScore.ChooseQuestReward(player, quest.RewChoiceItemId, quest.RewChoiceItemCount,
            PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights);

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

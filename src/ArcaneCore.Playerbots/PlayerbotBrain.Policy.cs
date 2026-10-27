using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Policy;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The goal policy's one hook in the ladder (<see cref="PlayerbotPolicyEngine"/>; docs/areas/playbots.md, Goal policy). Without an engine
/// nothing here runs and the ladder decides alone.
/// </summary>
internal sealed partial class PlayerbotBrain
{
    private PlayerbotPolicyAgent? _policyAgent;
    private readonly float[] _policyFeatures = new float[PlayerbotPolicySchema.FeatureCount];
    private readonly PlayerbotWeighedTarget[] _policyTargets = new PlayerbotWeighedTarget[8];

    /// <summary>The promoted opportunity goal chose a guard to fight, so the think goes on to the combat path (set by <see cref="TryPromotedGoal"/>, read once by the ladder).</summary>
    private bool _policyPulled;

    /// <summary>The goal policy engine (set by the feature before the first think; null: no policy, the ladder alone decides).</summary>
    internal PlayerbotPolicyEngine? PolicyEngine { get; set; }

    /// <summary>This bot's side of the engine (null before its first policy think).</summary>
    internal PlayerbotPolicyAgent? PolicyAgent => _policyAgent;

    /// <summary>The session's services (the policy telemetry reads the quest log through them).</summary>
    internal IServiceProvider Services => _session.Services;

    /// <summary>The bot's session (the policy telemetry reads its auras through it).</summary>
    internal WorldSession Session => _session;

    /// <summary>
    /// The policy hook, at the top of the out-of-combat goal ladder (world thread). Decides from the current snapshot's scores (the ladder
    /// decides when they are late, missing or not finite), takes a new snapshot when one is due, then tries the promoted goal, if any, the
    /// way its rung would. True when that goal acted and this think is done; false leaves the ladder to run as it always does.
    /// </summary>
    private bool TryPolicyGoal(Player player, uint interval)
    {
        _policyPulled = false;
        if (PolicyEngine is not { } engine) return false;
        PlayerbotPolicyAgent agent = _policyAgent ??= engine.CreateAgent(player.Guid.Value);
        uint now = _session.World.NowMs;
        PlayerbotPolicyVerdict verdict = agent.Decide(now, ThinkCount);
        // The snapshot's probes are optional work: a think out of its search budget takes it on a later think.
        if (agent.RequestDue(now) && (PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true))
        {
            PlayerbotPolicyGoalMask open = PolicySnapshot(player, _policyFeatures);
            agent.Request(player.MapId, _policyFeatures, open, now, ThinkCount);
        }
        if (verdict.Promote is not { } goal) return false;
        Opportunities.ScanDeferred = false;
        if (TryPromotedGoal(player, goal, interval)) return true;
        // A find's route is still being searched: no verdict yet, so nothing is refused and the promotion stands for the next think.
        if (goal == PlayerbotPolicyGoal.Opportunity && Opportunities.ScanDeferred) return false;
        // Its rules said no (sickness, a quest target in sight, nothing to do there): the ladder decides for the rest of this snapshot.
        agent.Refuse();
        return false;
    }

    /// <summary>
    /// The bot's features (<see cref="PlayerbotPolicySchema.FeatureNames"/>) into <paramref name="features"/>, and the goals open now: the
    /// same probes the ladder's rungs check before acting (world thread, once per snapshot).
    /// </summary>
    internal PlayerbotPolicyGoalMask PolicySnapshot(Player player, Span<float> features)
    {
        PlayerbotThinkProbe.Phase("policy snapshot");
        PlayerbotPolicyGoalMask open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Explore);
        if (_quests.HasCandidate(player)) open = open.With(PlayerbotPolicyGoal.Quest);
        if (HasOptionalThinkBudget(15.0) && _town.HasCandidate(player)) open = open.With(PlayerbotPolicyGoal.Town);
        Opportunities.Home = _quests.Home;
        if (HasOptionalThinkBudget(15.0) && Opportunities.HasCandidate(player)) open = open.With(PlayerbotPolicyGoal.Opportunity);
        Creature? seen = FindTarget(player, _quests.PreferredCreatureEntry, IsSkipped);
        // The ladder's own guards, as of now: the verdict is applied on later thinks, where the promotion rechecks them.
        bool visibleQuestTarget = _quests.PreferredCreatureEntry != 0 && seen is not null;
        if (!visibleQuestTarget && HasOptionalThinkBudget(15.0) && _trainers.HasCandidate(player)) open = open.With(PlayerbotPolicyGoal.Train);
        if (!visibleQuestTarget && HasOptionalThinkBudget(15.0) && _riding.HasCandidate(player)) open = open.With(PlayerbotPolicyGoal.Mount);
        if (_quests.PreferredCreatureEntry == 0 && player.Combat.Victim is null && HasOptionalThinkBudget(15.0) && _destinations.HasQuestCandidate(player))
            open = open.With(PlayerbotPolicyGoal.QuestTravel);
        if (seen is not null) open = open.With(PlayerbotPolicyGoal.Grind);

        int inLog = 0, complete = 0;
        if (_session.Services.GetService<Npc.QuestNpcFeature>()?.Services.StateOf(player) is { Loaded: true } state)
        {
            foreach (ArcaneCore.Game.Quests.QuestStatusData status in state.Quests.Statuses.Values)
            {
                if (status.Rewarded || status.Status == ArcaneCore.Game.Quests.QuestStatus.None) continue;
                inLog++;
                if (status.Status == ArcaneCore.Game.Quests.QuestStatus.Complete) complete++;
            }
        }
        int targets = CollectTargets(player, 0, IsSkipped, 1, null, _policyTargets);
        Array.Clear(_policyTargets);
        features[0] = player.Level / 60f;
        features[1] = MathF.Log10(1f + player.Money) / 7f;
        features[2] = PlayerbotTownGoals.FreeBagSlots(player) / 32f;
        features[3] = inLog / 20f;
        features[4] = complete / 20f;
        features[5] = _suspensions.ActiveExpiringCount(_session.World.NowMs) / 16f; // set-asides with time left (a kept strike count has none)
        features[6] = Math.Min(1f, _stall.Count / 10f);
        features[7] = player.ZoneId / 4096f;
        features[8] = player.MapId / 1024f;
        features[9] = targets / 8f;
        features[10] = player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 0f;
        features[11] = _home is { } home && home.Contains(player) ? 1f : 0f;
        return open;
    }

    /// <summary>
    /// Try the goal the policy put ahead of the ladder, under the guards its own rung has (world thread). The controller rechecks the live
    /// rules, as it does in the ladder; false (it did not act) leaves the ladder to run, so a refused promotion costs one extra check.
    /// The guards of the rungs the promotion jumps are rechecked too: resurrection sickness stands every rung below town down
    /// (<see cref="StandDownWhileSick"/>), and a quest creature in sight keeps a bot off the trainer and riding trips.
    /// </summary>
    private bool TryPromotedGoal(Player player, PlayerbotPolicyGoal goal, uint interval)
    {
        bool optionalBudget = HasOptionalThinkBudget(15.0);
        PlayerbotThinkProbe.Phase(PolicyPhase(goal));
        if (goal != PlayerbotPolicyGoal.Town
            && (_quests.NeedsBagSpaceForTurnIn
                || (PlayerbotTownGoals.HasBrokenWornGear(player) && _town.HasUrgentRepairOrSell(player))
                || PlayerbotSickness.Holder(_session, player) is { IsPermanent: false, Duration: > 0 }))
            return false;
        switch (goal)
        {
            case PlayerbotPolicyGoal.Town:
                if (!_town.Update(player, interval)) return false;
                RetireIdleCombatRoute();
                _goal = _town.Goal;
                TargetEntry = _town.TargetEntry;
                return true;

            case PlayerbotPolicyGoal.Opportunity:
                // The ladder's rung sits after the town, crafting and auction errands and the sickness stand-down, and before the trainer
                // trip. Like the trainer trip it starts only from a standing start under the optional budget, and only for a find in reach.
                if (!optionalBudget || _attacking || player.Combat.Victim is not null || !Opportunities.HasCandidate(player)) return false;
                if (!TakeOpportunity(player, interval, out bool opportunityPull)) return false;
                // A guard to pull: the think goes on to the combat path, as the ladder's own rung does (the caller reads this once).
                _policyPulled = opportunityPull;
                return true;

            case PlayerbotPolicyGoal.Train:
                // Only from a standing start: the ladder's own rung stops an idle fight first, which is not a choice a policy makes.
                if (!optionalBudget || _attacking || player.Combat.Victim is not null || QuestTargetInSight(player) || !_trainers.HasCandidate(player))
                    return false;
                _target = null;
                AbandonRoute();
                if (!_trainers.Update(player, interval)) return false;
                _goal = PlayerbotGoalKind.Train;
                TargetEntry = _trainers.TargetEntry;
                return true;

            case PlayerbotPolicyGoal.Mount:
                if (!optionalBudget || _attacking || player.Combat.Victim is not null || QuestTargetInSight(player) || !_riding.HasCandidate(player))
                    return false;
                _target = null;
                AbandonRoute();
                if (!_riding.Update(player, interval)) return false;
                _goal = PlayerbotGoalKind.Mount;
                TargetEntry = _riding.TargetEntry;
                return true;

            case PlayerbotPolicyGoal.QuestTravel:
                if (!optionalBudget || _quests.PreferredCreatureEntry != 0 || player.Combat.Victim is not null
                    || _session.ManagedBudget is { Remaining: <= 0 } || !_destinations.HasQuestCandidate(player))
                    return false;
                if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
                    { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting })
                    return false;
                _planningGoal = PlayerbotGoalKind.Quest;
                bool planned = _destinations.Update(player, 0, interval);
                _planningGoal = null;
                if (!planned) return _destinations.WaitingForTiles; // the route's tiles are being read: keep the goal, plan again next think
                QuestTravelDriven = true;
                _target = null;
                AbandonRoute();
                _attacking = false;
                _goal = PlayerbotGoalKind.Quest;
                TargetEntry = _destinations.TargetEntry;
                QuestId = _destinations.QuestId;
                return true;

            case PlayerbotPolicyGoal.Explore:
                _target = null;
                Explore(player, interval);
                return true;

            default:
                // Quest is the ladder's first rung (never a promotion) and Grind its floor (never moved ahead).
                return false;
        }
    }

    private bool QuestTargetInSight(Player player)
        => _quests.PreferredCreatureEntry != 0 && FindTarget(player, _quests.PreferredCreatureEntry, IsSkipped) is not null;

    private static string PolicyPhase(PlayerbotPolicyGoal goal) => goal switch
    {
        PlayerbotPolicyGoal.Town => "policy town",
        PlayerbotPolicyGoal.Train => "policy trainers",
        PlayerbotPolicyGoal.Mount => "policy riding",
        PlayerbotPolicyGoal.QuestTravel => "policy quest destinations",
        PlayerbotPolicyGoal.Explore => "policy explore",
        PlayerbotPolicyGoal.Opportunity => "policy opportunities",
        _ => "policy",
    };
}

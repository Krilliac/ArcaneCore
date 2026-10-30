using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Rules.Bot;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>What a ladder rung did this think.</summary>
internal enum LadderStep : byte
{
    /// <summary>It did not act: the next rung is tried (the ladder falls through).</summary>
    Continue,

    /// <summary>It acted and the think is done.</summary>
    Return,

    /// <summary>It chose a guard to fight: the think goes on to the combat path (the way a rung used to jump past the rest of the ladder).</summary>
    Pull,
}

/// <summary>Runs one rung's driver (the brain, or a scripted stand-in in the ladder replay test).</summary>
internal interface IPlayerbotRungRunner
{
    LadderStep Run(PlayerbotLadderRung rung);
}

/// <summary>The ladder loop: rungs in rank order, each tried until one acts.</summary>
internal static class PlayerbotLadder
{
    /// <summary>The first rung that did not <see cref="LadderStep.Continue"/>, or Continue when every rung fell through.</summary>
    internal static LadderStep Dispatch(IPlayerbotRungRunner runner, ReadOnlySpan<PlayerbotLadderRung> order)
    {
        foreach (PlayerbotLadderRung rung in order)
        {
            LadderStep step = runner.Run(rung);
            if (step != LadderStep.Continue) return step;
        }

        return LadderStep.Continue;
    }

    /// <summary>The same loop over the ranked candidates of a rule pass (best first).</summary>
    internal static LadderStep Dispatch(IPlayerbotRungRunner runner, ReadOnlySpan<BotCandidate> candidates)
    {
        foreach (BotCandidate candidate in candidates)
        {
            LadderStep step = runner.Run(candidate.Rung);
            if (step != LadderStep.Continue) return step;
        }

        return LadderStep.Continue;
    }
}

/// <summary>
/// The out-of-combat goal ladder's rungs (docs/superpowers/specs/2026-10-28-rule-engine-design.md, section 11): each rung is a driver that
/// keeps its own guards and returns whether it acted. The order they are tried in is data (the bot goal rules).
/// </summary>
internal sealed partial class PlayerbotBrain : IPlayerbotRungRunner
{
    private Player? _ladderPlayer;
    private uint _ladderInterval;
    private bool _ladderTownBegun;
    private bool? _ladderVisibleQuestTarget;
    private PlayerbotRuleSubject? _ruleSubject;
    private PlayerbotRuleLadder? _ruleHost;
    private BotGoalLadder? _goalLadder;

    private LadderStep RunLadder(Player player, uint interval)
    {
        _ladderPlayer = player;
        _ladderInterval = interval;
        _ladderTownBegun = false;
        _ladderVisibleQuestTarget = null;
        try
        {
            PlayerbotRuleLadder host = _ruleHost ??= PlayerbotRuleLadder.For(_session.Services);
            BotGoalLadder ladder = _goalLadder ??= host.CreateLadder(player.Guid.Value);
            PlayerbotRuleSubject subject = _ruleSubject ??= new PlayerbotRuleSubject();
            subject.Bind(player);
            ladder.Choose(subject, host.Catalog(), _session.World.NowMs);
            return PlayerbotLadder.Dispatch(this, subject.Candidates);
        }
        finally
        {
            _ladderPlayer = null;
            _ruleSubject?.Unbind();
        }
    }

    LadderStep IPlayerbotRungRunner.Run(PlayerbotLadderRung rung)
    {
        Player player = _ladderPlayer!;
        // The town controller starts its think once, before the first rung that uses it (every rung from the urgent errand down).
        if (!_ladderTownBegun && rung is not (PlayerbotLadderRung.HelpErrand or PlayerbotLadderRung.Equipment or PlayerbotLadderRung.ClassUpkeep or PlayerbotLadderRung.ReturnHome))
        {
            PlayerbotThinkProbe.Phase("town");
            _town.BeginThink();
            _ladderTownBegun = true;
        }

        return RunRung(rung, player, _ladderInterval);
    }

    private LadderStep RunRung(PlayerbotLadderRung rung, Player player, uint interval)
    {
        switch (rung)
        {
            case PlayerbotLadderRung.HelpErrand:
            {
                // A request from the social pass to lend a hand to a stranger (docs/areas/playbots-social.md, Helping): carried out ahead of the goal ladder.
                if (_helpKind == PlayerbotHelpKind.None) return LadderStep.Continue;
                PlayerbotThinkProbe.Phase("help");
                int help = TakeHelpErrand(player);
                return help == 2 ? LadderStep.Pull : help == 1 ? LadderStep.Return : LadderStep.Continue;
            }

            case PlayerbotLadderRung.Equipment:
                PlayerbotThinkProbe.Phase("equipment");
                if (!_equipment.Update(player)) return LadderStep.Continue;
                _target = null;
                AbandonRoute();
                _attacking = false;
                TargetEntry = 0;
                _goal = PlayerbotGoalKind.Explore;
                return LadderStep.Return;

            case PlayerbotLadderRung.ClassUpkeep:
                // Class upkeep between fights (forms, buffs on the bot and its group, the pet, heals, stealth before a pull),
                // vmangos UpdateOutOfCombatAI_<Class>; the creature the bot is closing in on is its pull target.
                PlayerbotThinkProbe.Phase("class upkeep");
                _combatSpells.PullTarget = _target is { IsAlive: true } pull && ReferenceEquals(pull.Map, player.Map) ? pull : null;
                return _combatSpells.UpdateOutOfCombat(player, interval) ? LadderStep.Return : LadderStep.Continue;

            case PlayerbotLadderRung.ReturnHome:
                PlayerbotThinkProbe.Phase("home");
                return _home is { } home && TryReturnHome(player, home, interval) ? LadderStep.Return : LadderStep.Continue;

            case PlayerbotLadderRung.UrgentTown:
                if (!_quests.NeedsBagSpaceForTurnIn && !(PlayerbotTownGoals.HasBrokenWornGear(player) && _town.HasUrgentRepairOrSell(player)))
                    return LadderStep.Continue;
                if (!_town.Update(player, interval)) return LadderStep.Continue;
                RetireIdleCombatRoute();
                _goal = _town.Goal;
                QuestId = 0;
                TargetEntry = _town.TargetEntry;
                return LadderStep.Return;

            case PlayerbotLadderRung.Policy:
                // The goal policy may put another open rung ahead of the ladder's choice (PlayerbotBrain.Policy.cs); its controller rechecks the live rules.
                PlayerbotThinkProbe.Phase("policy");
                if (!TryPolicyGoal(player, interval)) return LadderStep.Continue;
                return _policyPulled ? LadderStep.Pull : LadderStep.Return;

            case PlayerbotLadderRung.Quests:
                PlayerbotThinkProbe.Phase("quests");
                if (_quests.Update(player, interval))
                {
                    RetireIdleCombatRoute();
                    _goal = _quests.Goal;
                    QuestId = _quests.QuestId;
                    TargetEntry = _quests.TargetEntry;
                    return LadderStep.Return;
                }

                QuestId = 0;
                return LadderStep.Continue;

            case PlayerbotLadderRung.Town:
                PlayerbotThinkProbe.Phase("town");
                if (!_town.Update(player, interval)) return LadderStep.Continue;
                RetireIdleCombatRoute();
                _goal = _town.Goal;
                TargetEntry = _town.TargetEntry;
                return LadderStep.Return;

            case PlayerbotLadderRung.SicknessStandDown:
                // Under resurrection sickness no new fight: the quest exchanges and town errands above (a repair among them) still run.
                return StandDownWhileSick(player) ? LadderStep.Return : LadderStep.Continue;

            case PlayerbotLadderRung.Crafting:
                PlayerbotThinkProbe.Phase("crafting");
                _crafting.Suspensions = _suspensions;
                _crafting.GatherVeto = Opportunities.NodeVeto;
                if (!_crafting.Update(player, interval)) return LadderStep.Continue;
                RetireIdleCombatRoute();
                _goal = _crafting.Goal;
                TargetEntry = _crafting.TargetEntry;
                return LadderStep.Return;

            case PlayerbotLadderRung.Economy:
                PlayerbotThinkProbe.Phase("economy");
                if (!_economy.Update(player, interval)) return LadderStep.Continue;
                RetireIdleCombatRoute();
                _goal = _economy.Goal;
                TargetEntry = _economy.TargetEntry;
                return LadderStep.Return;

            case PlayerbotLadderRung.Bank:
                // Staying at its capital home (town progression): deposit what it does not need, buy a bank slot when the bank is nearly full.
                // A Vendor errand: a stall sets the banker's entry aside like a vendor's.
                PlayerbotThinkProbe.Phase("bank");
                if (_home is not { Stay: Population.TownStay.Capital } capitalHome
                    || !_bank.Update(player, capitalHome, item => _town.IsProtected(player, item), interval))
                    return LadderStep.Continue;
                RetireIdleCombatRoute();
                _goal = PlayerbotGoalKind.Vendor;
                TargetEntry = _bank.TargetEntry;
                return LadderStep.Return;

            case PlayerbotLadderRung.Opportunities:
                PlayerbotThinkProbe.Phase("opportunities");
                if (!TakeOpportunity(player, interval, out bool opportunityPull)) return LadderStep.Continue;
                return opportunityPull ? LadderStep.Pull : LadderStep.Return;

            case PlayerbotLadderRung.Trainers:
                PlayerbotThinkProbe.Phase("trainers");
                if (LadderQuestTargetVisible(player) || !(PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true) || !_trainers.HasCandidate(player))
                    return LadderStep.Continue;
                // An abandoned idle-grind target must not keep reopening combat instead
                // of travelling to a useful trainer. Stop through the ordinary handler;
                // active combat, casts, quest returns and pending loot keep priority.
                if (_attacking || player.Combat.Victim is not null)
                {
                    if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                    { _attacking = false; _target = null; AbandonRoute(); }
                    _goal = PlayerbotGoalKind.Train;
                    return LadderStep.Return;
                }

                _target = null;
                AbandonRoute();
                if (!_trainers.Update(player, interval)) return LadderStep.Continue;
                _goal = PlayerbotGoalKind.Train;
                TargetEntry = _trainers.TargetEntry;
                return LadderStep.Return;

            case PlayerbotLadderRung.Riding:
                PlayerbotThinkProbe.Phase("riding");
                if (LadderQuestTargetVisible(player) || !(PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true) || !_riding.HasCandidate(player))
                    return LadderStep.Continue;
                // Saved for riding training or a mount and the money is there: the same priority as a class trainer trip.
                if (_attacking || player.Combat.Victim is not null)
                {
                    if (_session.TryManagedAction(WorldOpcode.CmsgAttackstop, []))
                    { _attacking = false; _target = null; AbandonRoute(); }
                    _goal = PlayerbotGoalKind.Mount;
                    return LadderStep.Return;
                }

                _target = null;
                AbandonRoute();
                if (!_riding.Update(player, interval)) return LadderStep.Continue;
                _goal = PlayerbotGoalKind.Mount;
                TargetEntry = _riding.TargetEntry;
                return LadderStep.Return;

            case PlayerbotLadderRung.QuestDestinations:
            {
                PlayerbotThinkProbe.Phase("quest destinations");
                if (_quests.PreferredCreatureEntry != 0 || player.Combat.Victim is not null
                    || !(PlayerbotThinkProbe.Active?.HasOptionalPlanBudget ?? true)
                    || !_destinations.HasQuestCandidate(player))
                    return LadderStep.Continue;
                // Available quest travel precedes optional idle grinding. Actual combat,
                // visible interactions, completed returns, supplies and trainers keep their
                // existing priority; path failure still permits the ordinary fallback.
                SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
                if (spellFeature?.System.GetState(player.Guid)?.CurrentCast is
                    { State: ArcaneCore.Game.Spells.SpellCastState.Preparing or ArcaneCore.Game.Spells.SpellCastState.Casting }) return LadderStep.Return;
                if (_session.ManagedBudget is { Remaining: <= 0 }) return LadderStep.Return;
                _planningGoal = PlayerbotGoalKind.Quest;
                bool questPlanned = _destinations.Update(player, 0, interval);
                _planningGoal = null;
                if (questPlanned)
                {
                    QuestTravelDriven = true;
                    _target = null;
                    AbandonRoute();
                    _attacking = false;
                    _goal = PlayerbotGoalKind.Quest;
                    TargetEntry = _destinations.TargetEntry;
                    QuestId = _destinations.QuestId;
                    return LadderStep.Return;
                }

                return _destinations.WaitingForTiles ? LadderStep.Return : LadderStep.Continue; // the route's tiles are being read: keep the goal, plan again next think
            }

            default:
                return LadderStep.Continue;
        }
    }

    /// <summary>A quest creature in sight keeps the bot off the trainer and riding trips; looked up once per ladder pass.</summary>
    private bool LadderQuestTargetVisible(Player player)
        => _ladderVisibleQuestTarget ??= _quests.PreferredCreatureEntry != 0 && FindTarget(player, _quests.PreferredCreatureEntry, IsSkipped) is not null;
}

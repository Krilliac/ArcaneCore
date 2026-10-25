using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Playerbots.Combat;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// A bot's risk against reward (<see cref="PlayerbotRiskOptions"/>, docs/areas/playbots-risk.md): the world side of
/// <see cref="PlayerbotRiskModel"/>. Before a pull it gathers the facts of each candidate (who would join it, by the creatures' own
/// aggro and assistance rules, its health, the bot's health, mana, damage and escapes, the experience, quest credit and loot it
/// gives, and what the bot remembers) and takes the first one worth it, walks round a pack on the way, rests first, or passes. In a
/// fight it watches the damage rates (<see cref="PlayerbotFightTracker"/>) and retreats from a lost one
/// (<see cref="PlayerbotRetreat"/>), remembering the creatures and the place (<see cref="PlayerbotDangerMemory"/>), then recovers to
/// <see cref="PlayerbotRiskOptions.RecoverHealthPct"/> before it pulls again. World thread; owned by one bot's brain.
/// </summary>
internal sealed partial class PlayerbotRisk
{
    /// <summary>At most this many candidates are weighed per decision (each costs a path query).</summary>
    internal const int MaxCandidates = 5;

    /// <summary>A verdict on a creature stands this long before it is weighed again.</summary>
    internal const uint VerdictMs = 5_000;

    /// <summary>The longest wait to recover (out of combat) after a retreat or before a pull.</summary>
    internal const uint MaxWaitMs = 90_000;

    /// <summary>
    /// A wait in which neither health nor mana rose for this long recovers nothing (ten of vmangos' two-second regeneration ticks,
    /// Player::RegenerateAll): it ends, and no new wait begins for <see cref="MaxWaitMs"/> unless health or mana rises meanwhile. Under
    /// resurrection sickness (spell 15007, every stat -75%) a warrior, rogue, hunter or shaman regenerates no health at all out of
    /// combat (GetRegenHPPerSpirit goes below zero), and a wait for <see cref="PlayerbotRiskOptions.RecoverHealthPct"/> could only
    /// run out; the next pull verdict began it again, and the bot stood with goal Rest until the stall watch reported it (live
    /// 2026-10-10: Wfbotcx at the Darkshire graveyard after a spirit-healer revive at level 14).
    /// </summary>
    internal const uint FruitlessWaitMs = 20_000;

    /// <summary>Non-objectives at most this many levels above the bot are weighed at all (a red creature is not).</summary>
    internal const int MaxLevelsAbove = 3;

    private const uint SkinningSkill = 393;
    private readonly WorldSession _session;
    private readonly PlayerbotOptions _options;
    private readonly PlayerbotCombatSpells _spells;
    private readonly Dictionary<(ObjectGuid Creature, bool Quest), (PlayerbotEngagement Verdict, uint UntilMs)> _verdicts = [];
    private uint _waitUntilMs;
    private uint _waitGainMs;
    private ulong _waitHealth;
    private ulong _waitMana;
    private uint _noWaitUntilMs;
    private ulong _fruitlessHealth;
    private ulong _fruitlessMana;
    private bool _cornered;

    /// <summary>
    /// When the bot was first seen in the combat it is in (<see cref="Track"/>; 0 out of combat): the danger memory written since, by a
    /// retreat of this very fight that ended cornered, is not older knowledge (<see cref="CheckKnownDanger"/>).
    /// </summary>
    private uint _fightSinceMs;
    private (ObjectGuid Guid, uint Entry)[] _fledFrom = [];
    private (uint MapId, Vector3 Where) _fledAt;

    internal PlayerbotRisk(WorldSession session, PlayerbotOptions options, PlayerbotCombatSpells spells)
    {
        _session = session;
        _options = options;
        _spells = spells;
        Retreat = new PlayerbotRetreat(session, options, spells, Hazards);
    }

    internal bool Enabled => _options.Risk.Enabled;

    internal PlayerbotBreadcrumbs Breadcrumbs { get; } = new();

    internal PlayerbotFightTracker Tracker { get; } = new();

    internal PlayerbotDangerMemory Memory { get; } = new();

    /// <summary>The places the bot keeps out of on its walks (<see cref="PlayerbotHazards"/>).</summary>
    internal PlayerbotHazards Hazards { get; } = new();

    /// <summary>
    /// Whether creatures beyond the bot's strength alone are hazards on its walks (<see cref="ScanHazards"/>): a bot on its own, yes;
    /// a bot-led group's leader no (the group goes for creatures too strong for one of its members alone, and its leader chose the
    /// way), nor a party bot (it follows its master, without hazards).
    /// </summary>
    internal bool AvoidsStrongCreatures { get; init; } = true;

    /// <summary>A strong creature's hazard lapses this long after the last look that saw it (three scans).</summary>
    internal const uint StrongHazardLifeMs = 3 * HazardScanMs;

    /// <summary>
    /// A bot whose goal walks are refused for strong hazards and that has not moved <see cref="BoxedProgressYards"/> for this long of
    /// world time is boxed in, when the fan of straight lines round it confirms it (<see cref="Refused"/>); also the least time
    /// between two escapes.
    /// </summary>
    internal const uint BoxedMs = 3 * HazardScanMs;

    /// <summary>Moving this far from where the boxed-in clock started is progress (the stall watch's measure).</summary>
    internal const float BoxedProgressYards = PlayerbotStallWatch.ProgressYards;

    /// <summary>The longest an escape route is walked (<see cref="EscapeRoute"/>).</summary>
    internal const uint EscapeLifeMs = 20_000;

    /// <summary>The exits planned at most for one escape (the least exposed first, <see cref="PlayerbotHazards.FindGaps"/>).</summary>
    internal const int MaxEscapePlans = 3;

    /// <summary>The gate waits up to this much longer than <see cref="BoxedMs"/>, by the bot's guid (bots revived together spread).</summary>
    internal const uint MaxBoxedPhaseMs = 2_000;

    /// <summary>The most escapes in <see cref="EscapeWindowMs"/>.</summary>
    internal const int MaxEscapesPerWindow = 3;

    /// <summary>The window <see cref="MaxEscapesPerWindow"/> counts in.</summary>
    internal const uint EscapeWindowMs = 300_000;

    /// <summary>
    /// A planned escape route may pass this much deeper into the aggro reaches (summed over its samples, yards) than its straight line
    /// and still count as keeping to it (the navigation's corners round off a line by a fraction of a yard).
    /// </summary>
    internal const float RouteExposureSlack = 1f;

    /// <summary>How often the visible creatures are looked over for hazards.</summary>
    internal const uint HazardScanMs = 2_000;
    private uint _nextScanMs;
    private readonly PlayerbotPhantomCombatWatch _phantomCombat = new();
    private readonly PlayerbotRetreatLoopWatch _retreatLoop = new();
    private uint _retreatDiedMs;

    internal PlayerbotRetreat Retreat { get; }

    /// <summary>Where this bot's retreat outcomes and refusals are counted (the fleet's counters unless a test replaces them).</summary>
    internal PlayerbotRetreatStats RetreatStats { get; set; } = PlayerbotRetreatStats.Shared;

    /// <summary>The last pre-engagement verdict (the target taken, or the best one passed over).</summary>
    internal PlayerbotEngagement? LastEngagement { get; private set; }

    /// <summary>Told each pull candidate assessed and each in-fight verdict (the goal policy's combat telemetry; null: off).</summary>
    internal Policy.IPlayerbotCombatSink? CombatSink { get; set; }

    /// <summary>A telemetry fault never changes a decision: it is counted and the choice goes on.</summary>
    private static void SinkFault(Exception error) => FaultLedger.Shared.Record("playerbot.policy-telemetry", error);

    /// <summary>A route round the creatures on the way to the target just chosen (taken once by the brain).</summary>
    internal PlayerbotRoute? Detour { get; set; }

    /// <summary>Waiting to recover before the next pull.</summary>
    internal bool Waiting => _waitUntilMs != 0;

    /// <summary>
    /// The last quest objective the bot passed over as too strong for it alone but feasible for a small group
    /// (<see cref="PlayerbotRiskModel.NeededGroupSize"/>): the group coordinator turns it into a "needs a group" goal. Null when none.
    /// </summary>
    internal PlayerbotGroupSignal? GroupSignal { get; private set; }

    /// <summary>The largest group a pull is weighed for by default (a party; <see cref="Groups.PlayerbotRecruitment.SignalLimit"/> raises it for a raid escalation).</summary>
    internal const int MaxSignalGroupSize = 5;

    /// <summary>Forget the group signal (the coordinator took it).</summary>
    internal void ClearGroupSignal() => GroupSignal = null;

    /// <summary>The group-size estimates the last <see cref="Assess(Player, Creature, bool, out PlayerbotRoute?)"/> made for a group signal (tests bound their cost).</summary>
    internal int LastGroupProbes { get; private set; }

    /// <summary>The line BOTINSPECT and <c>.playerbot status</c> show: the retreat, else the fight, else the last pull verdict.</summary>
    internal string Report
    {
        get
        {
            if (Retreat.Active)
                return $"retreat reason={Retreat.Reason} escapes={(Retreat.UsedEscapes.Count == 0 ? "none" : string.Join('+', Retreat.UsedEscapes.Select(s => s.Replace(' ', '_'))))}";
            // The status snapshot reads this every tick for every bot: the same verdict gives back the same text instead of a new one
            // (the verdicts are immutable record structs, compared by value).
            if (Waiting)
                return Memo(1, default, LastEngagement, Retreat.Reason,
                    static (_, engagement, reason) => "recover " + (engagement?.ToString() ?? $"after retreat ({reason})"));
            if (Tracker.LastVerdict is { } fight) return Memo(2, fight, null, null, static (verdict, _, _) => verdict.ToString());
            return Memo(3, default, LastEngagement, null, static (_, engagement, _) => engagement?.ToString() ?? "decision=none");
        }
    }

    private int _reportKind;
    private PlayerbotFightVerdict _reportVerdict;
    private PlayerbotEngagement? _reportEngagement;
    private string? _reportReason;
    private string _reportText = string.Empty;

    private string Memo(int kind, PlayerbotFightVerdict verdict, PlayerbotEngagement? engagement, string? reason,
        Func<PlayerbotFightVerdict, PlayerbotEngagement?, string?, string> format)
    {
        if (kind != _reportKind || !verdict.Equals(_reportVerdict) || engagement != _reportEngagement
            || !string.Equals(reason, _reportReason, StringComparison.Ordinal))
        {
            _reportKind = kind;
            _reportVerdict = verdict;
            _reportEngagement = engagement;
            _reportReason = reason;
            _reportText = format(verdict, engagement, reason);
        }

        return _reportText;
    }

    /// <summary>Lay the trail a retreat runs back along; out of combat, close a finished fight.</summary>
    internal void Track(Player player)
    {
        // The trail runs on through a pull (a bot is in combat from its first swing, often well short of where it fights).
        Breadcrumbs.Track(player);
        ScanHazards(player);
        if (!_session.IsInChaosProbe) _phantomCombat.Look(player, _session.World.NowMs);
        if (player.Combat.IsInCombat)
        {
            if (_fightSinceMs == 0) _fightSinceMs = Math.Max(1u, _session.World.NowMs);
            EndEscape(); // aggroed on the way out: the fight is judged as any other
            return;
        }

        if (!player.IsAlive) return;
        _fightSinceMs = 0;
        _cornered = false;
        _refusalCounted = false;
        if (_refusedPending)
        {
            _refusedPending = false;
            if (Enabled) RetreatStats.RecordRefusedEnd(died: false);
        }
        if (Tracker.Active) Tracker.End(PlayerbotRiskModel.PriorBotDps(player.Level));
    }

    /// <summary>The bot died: remember its killers and the place, and forget the fight.</summary>
    /// <param name="attackers">The creatures attacking the bot at its last living update (the server clears a dead player's attackers).</param>
    internal void OnDeath(Player player, IEnumerable<(ObjectGuid Guid, uint Entry)>? attackers = null)
    {
        // The killers: those attackers, and the enemies of the fight being watched (not those of an earlier one).
        (ObjectGuid Guid, uint Entry)[] killers = [.. (attackers ?? [])
            .Concat((Tracker.Active ? Tracker.LastEnemies : []).Select(c => (c.Guid, c.Entry))).Distinct()];
        CheckKnownDanger(player, killers);
        if (Enabled && killers.Length > 0)
        {
            Memory.Remember(player.MapId, new Vector3(player.X, player.Y, player.Z), killers, _session.World.NowMs,
                _options.Risk.DangerMemorySeconds);
            RememberLairs(player, killers);
        }
        _verdicts.Clear();
        if (Enabled)
            Hazards.Add(player.MapId, new Vector3(player.X, player.Y, player.Z), PlayerbotHazards.DeathYards, _session.World.NowMs,
                _options.Risk.DangerMemorySeconds, "death");
        // A death on the way out of a retreat is the retreat's end: the place where the fight turned is remembered as for any other.
        if (Retreat.Active) NoteRetreatEnd(player, "died");
        else if (_refusedPending && Enabled) RetreatStats.RecordRefusedEnd(died: true);
        _refusedPending = false;

        Retreat.Stop("died");
        Tracker.End(PlayerbotRiskModel.PriorBotDps(player.Level));
        Breadcrumbs.Clear();
        Hazards.ClearStrong();
        EndEscape();
        _boxedSinceMs = 0;
        Detour = null;
        _waitUntilMs = 0;
        _fightSinceMs = 0;
        _retreatEndedMs = 0;
        _pulledTargetSinceRetreat = true;
        _recentRetreatEntries.Clear();
        _lastRetreat = default;
        _retreatLoop.Reset();
    }

    private uint _retreatEndedMs;
    private bool _pulledTargetSinceRetreat = true;
    private readonly HashSet<uint> _recentRetreatEntries = [];

    /// <summary>
    /// <c>risk/died-in-known-hazard</c>: the bot died to a creature it had fled from or died to before (the danger memory), or inside the
    /// reach of a creature hazard the risk model held (a creature with a lethal spell, or one fled from:
    /// <see cref="PlayerbotHazards"/>). Strong-creature hazards, place marks and a death during a retreat (which walks where it must)
    /// do not count: a pull of a strong quest creature and a reclaim at the body are by design. Cost: only at a death.
    /// </summary>
    private void CheckKnownDanger(Player player, (ObjectGuid Guid, uint Entry)[] killers)
    {
        uint now = _session.World.NowMs;
        // A retreat that ended in this death remembers its enemies first (NoteRetreatEnd): that memory is not older knowledge.
        if (!Enabled || Retreat.Active || _cornered || _session.IsInChaosProbe
            || (_retreatDiedMs != 0 && unchecked(now - _retreatDiedMs) < 30_000)
            || (_retreatEndedMs != 0 && unchecked(now - _retreatEndedMs) < 120_000)) return;
        var here = new Vector3(player.X, player.Y, player.Z);
        bool atDeathSpot = false;
        foreach (PlayerbotHazard active in Hazards.Active(player.MapId, now))
            if (active.Kind == "death" && active.Covers(here))
            {
                atDeathSpot = true;
                break;
            }

        // Only what the bot knew before this fight began: a retreat that ended cornered remembers the very creatures the bot then fights
        // on against, and a death in that fight is no death to a known danger (live 2026-10-16: 509 of 1218 reports came within two
        // minutes of the same bot's risk/retreat-cornered). A death at the corpse reclaim spot ("death" place mark) is also by design.
        if (!atDeathSpot)
        {
            foreach ((ObjectGuid guid, uint entry) in killers)
                if (Memory.RememberedBefore(guid, _fightSinceMs, now))
                {
                    PlayerbotAnomalies.Report(player, "risk", "died-in-known-hazard",
                        $"killed by creature {entry} that the danger memory already held (fled from or died to before)");
                    return;
                }
        }

        uint memoryMs = (uint)(_options.Risk.DangerMemorySeconds * 1000);
        foreach (PlayerbotHazard hazard in Hazards.Active(player.MapId, now))
        {
            if (hazard.Creature.IsEmpty || hazard.Kind == PlayerbotHazards.StrongKind || !hazard.Covers(here)) continue;
            if (hazard.Kind == "fled" && (atDeathSpot || !Memory.RememberedBefore(hazard.Creature, _fightSinceMs, now))) continue;
            uint createdMs = unchecked(hazard.UntilMs - memoryMs);
            if (_fightSinceMs > 0 && unchecked((int)(createdMs - _fightSinceMs)) >= 0) continue;
            PlayerbotAnomalies.Report(player, "risk", "died-in-known-hazard",
                $"died inside a '{hazard.Kind}' hazard of creature {hazard.Creature.Value:X} (radius {hazard.Radius:F1})");
            return;
        }
    }

    internal bool IsRemembered(Creature creature) => Enabled && Memory.IsRemembered(creature.Guid, _session.World.NowMs);

    internal bool CoversPullHazard(Player player, Creature creature)
    {
        if (!Enabled) return false;
        if (SpellThreat(creature).IsLethalTo(player.MaxHealth)) return true;
        uint now = _session.World.NowMs;
        var pos = new Vector3(creature.X, creature.Y, creature.Z);
        if (Hazards.CoversDeathLoop(creature.MapId, pos, now)) return true;
        var here = new Vector3(player.X, player.Y, player.Z);
        float range = _spells.PreferredRange(player);
        float dist = Vector3.Distance(here, pos);
        Vector3 spot = dist <= range || dist < 0.01f ? here : pos + ((here - pos) * (range / dist));
        foreach (PlayerbotHazard hazard in Hazards.Active(creature.MapId, now))
        {
            if (hazard.Kind is PlayerbotHazards.StrongKind or "death") continue;
            if (hazard.Covers(pos) || hazard.Covers(spot)) return true;
        }
        return false;
    }

    // --- before a pull ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The first candidate (quest objectives first, then nearest) whose reward justifies its risk; null when none does. A candidate
    /// fine at full health and mana starts a wait to recover (<see cref="Waiting"/>) when nothing else is worth it.
    /// </summary>
    internal Creature? ChooseTarget(Player player, uint preferredEntry, Func<Creature, bool> skip, bool questObjective,
        PlayerbotContention? contention = null)
    {
        PlayerbotEngagement? first = null, rest = null;
        // One pass keeps the best few (the soft claims of the other bots weigh into the order, PlayerbotContention.Weigh); the filter
        // delegate is built once and reads the caller's skip from a field, so a choice allocates nothing.
        _chooseSkip = skip;
        _chooseFilter ??= creature => _chooseSkip!(creature) || IsRemembered(creature)
            || Enabled && Hazards.CoversDeathLoop(creature.MapId, new Vector3(creature.X, creature.Y, creature.Z), _session.World.NowMs);
        int found = PlayerbotBrain.CollectTargets(player, preferredEntry, _chooseFilter, MaxLevelsAbove, contention, _candidates);
        try
        {
            for (int index = 0; index < found; index++)
            {
                if (index > 0 && PlayerbotThinkProbe.Active is { Refused: > 0 }) break;
                Creature candidate = _candidates[index].Creature!;
                PlayerbotEngagement verdict = Assess(player, candidate, questObjective && candidate.Entry == preferredEntry, out PlayerbotRoute? detour);
                if (CoversPullHazard(player, candidate))
                    verdict = verdict with { Decision = PlayerbotEngageDecision.Avoid, Reason = "hazard" };
                if (CombatSink is { } sink)
                {
                    bool taken = verdict.Decision == PlayerbotEngageDecision.Engage || verdict.Decision == PlayerbotEngageDecision.Detour && detour is not null;
                    try { sink.Assessed(player, candidate, verdict, taken, _session.World.NowMs); }
                    catch (Exception error) when (error is not OutOfMemoryException) { SinkFault(error); }
                }
                first ??= verdict;
                switch (verdict.Decision)
                {
                    case PlayerbotEngageDecision.Engage:
                        LastEngagement = verdict;
                        _pulledTargetSinceRetreat = true;
                        return candidate;
                    case PlayerbotEngageDecision.Detour when detour is not null:
                        LastEngagement = verdict;
                        Detour = detour;
                        _pulledTargetSinceRetreat = true;
                        return candidate;
                    case PlayerbotEngageDecision.Rest:
                        rest ??= verdict;
                        break;
                }
            }
        }
        finally
        {
            Array.Clear(_candidates);
            _chooseSkip = null;
        }

        LastEngagement = rest ?? first ?? LastEngagement;
        if (rest is not null) BeginWait(player);
        return null;
    }

    private Func<Creature, bool>? _chooseSkip;
    private Func<Creature, bool>? _chooseFilter;
    private readonly PlayerbotWeighedTarget[] _candidates = new PlayerbotWeighedTarget[MaxCandidates];

    /// <summary>
    /// Raise a group signal from outside the pull verdict (<see cref="PlayerbotContention"/>: a mob another player tagged for the same
    /// quest, or a quest creature the bot gains no credit on); the coordinator takes it like any other.
    /// </summary>
    internal void RaiseGroupSignal(PlayerbotGroupSignal signal) => GroupSignal = signal;

    /// <summary>
    /// Start waiting to recover (at most <see cref="MaxWaitMs"/>), unless the last wait recovered nothing (<see cref="FruitlessWaitMs"/>)
    /// less than <see cref="MaxWaitMs"/> ago and health and mana have not risen since. A wait already running goes on.
    /// </summary>
    internal void BeginWait(Player player)
    {
        if (_waitUntilMs != 0) return;
        uint now = _session.World.NowMs;
        (ulong health, ulong mana) = Vitals(player);
        if (unchecked((int)(_noWaitUntilMs - now)) > 0 && health <= _fruitlessHealth && mana <= _fruitlessMana) return;
        _noWaitUntilMs = now;
        _waitUntilMs = Math.Max(1u, unchecked(now + MaxWaitMs));
        _waitGainMs = now;
        (_waitHealth, _waitMana) = (health, mana);
    }

    /// <summary>
    /// Still waiting: below the recovery threshold, within the wait, and health or mana rose within <see cref="FruitlessWaitMs"/>.
    /// False ends the wait.
    /// </summary>
    internal bool KeepWaiting(Player player)
    {
        if (_waitUntilMs == 0) return false;
        uint now = _session.World.NowMs;
        bool timeLeft = unchecked((int)(_waitUntilMs - now)) > 0;
        (ulong health, ulong mana) = Vitals(player);
        if (health > _waitHealth || mana > _waitMana) _waitGainMs = now;
        (_waitHealth, _waitMana) = (Math.Max(health, _waitHealth), Math.Max(mana, _waitMana));
        bool recovered = Recovered(player);
        bool fruitless = unchecked(now - _waitGainMs) >= FruitlessWaitMs;
        if (Enabled && timeLeft && !recovered && !fruitless) return true;
        if (fruitless && !recovered)
        {
            _noWaitUntilMs = unchecked(now + MaxWaitMs);
            (_fruitlessHealth, _fruitlessMana) = (health, mana);
        }

        _waitUntilMs = 0;
        return false;
    }

    /// <summary>Health and mana now (mana 0 for a bot without it).</summary>
    private static (ulong Health, ulong Mana) Vitals(Player player)
        => (player.Health, player.PowerType == PowerType.Mana ? SpellSystem.GetPower(player, PowerType.Mana) : 0UL);

    /// <summary>Health and mana at or above <see cref="PlayerbotRiskOptions.RecoverHealthPct"/>.</summary>
    internal bool Recovered(Player player)
    {
        float wanted = _options.Risk.RecoverHealthPct;
        if (player.MaxHealth > 0 && player.Health * 100f / player.MaxHealth < wanted) return false;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        return player.PowerType != PowerType.Mana || maxMana == 0 || SpellSystem.GetPower(player, PowerType.Mana) * 100f / maxMana >= wanted;
    }

    /// <summary>The verdict on one candidate (cached for <see cref="VerdictMs"/>); a detour route when the verdict is to walk round.</summary>
    internal PlayerbotEngagement Assess(Player player, Creature target, bool questObjective, out PlayerbotRoute? detour)
    {
        detour = null;
        uint now = _session.World.NowMs;
        if (_verdicts.TryGetValue((target.Guid, questObjective), out var cached) && unchecked((int)(cached.UntilMs - now)) > 0
            && cached.Verdict.Decision != PlayerbotEngageDecision.Detour)
            return cached.Verdict;
        if (_verdicts.Count >= 64)
            foreach ((ObjectGuid, bool) old in _verdicts.Where(v => unchecked((int)(v.Value.UntilMs - now)) <= 0).Select(v => v.Key).ToArray())
                _verdicts.Remove(old);

        PlayerbotEngagementFacts facts = Facts(player, target, questObjective, out Vector3 spot, out HashSet<ObjectGuid> fight);
        PlayerbotEngagement verdict = PlayerbotRiskModel.Assess(facts, _options.Risk);
        // Too strong alone, but a few players would take it: a "needs a group of N" goal instead of abandoning the objective.
        // Only a rare elite or a boss (rank 2 or 3) may ask for more than a party: a plain elite or any other creature needs five or fewer or is passed over.
        LastGroupProbes = 0;
        int size = 0;
        if (verdict.Decision == PlayerbotEngageDecision.Avoid && questObjective && _options.Groups.Enabled)
        {
            size = PlayerbotRiskModel.NeededGroupSize(facts, _options.Risk, target.Template.Rank is 2 or 3
                ? Groups.PlayerbotRecruitment.SignalLimit(_options.Groups) : Groups.PlayerbotRecruitment.PartyLimit, out int probes);
            LastGroupProbes = probes;
        }

        if (size > 1)
        {
            verdict = verdict with { Reason = $"group-of-{size}" };
            GroupSignal = new PlayerbotGroupSignal(target.Entry, size, player.MapId, new Vector3(target.X, target.Y, target.Z), now);
        }

        if (verdict.Decision == PlayerbotEngageDecision.Detour)
        {
            detour = FindDetour(player, spot, fight);
            if (detour is null) verdict = verdict with { Decision = PlayerbotEngageDecision.Avoid, Reason = $"pack-of-{facts.Enemies.Count}" };
        }

        if (verdict.Decision == PlayerbotEngageDecision.Engage || verdict.Decision == PlayerbotEngageDecision.Detour)
            Tracker.Prime(facts.Enemies.Sum(e => e.Dps), facts.BotDps * PlayerbotRiskModel.ManaFactor(facts.ManaDependence, facts.BotManaPct));
        if (_verdicts.Count < 64) _verdicts[(target.Guid, questObjective)] = (verdict, unchecked(now + VerdictMs));
        return verdict;
    }

    /// <param name="fight">The creatures the fight itself brings (the target, its assistance, those covering the fight spot), not those on the way.</param>
    private PlayerbotEngagementFacts Facts(Player player, Creature target, bool questObjective, out Vector3 spot,
        out HashSet<ObjectGuid> fight)
    {
        Map? map = player.Map;
        CreatureMapSystem? system = map?.FindUpdater<CreatureMapSystem>();
        Vector3 here = new(player.X, player.Y, player.Z), there = new(target.X, target.Y, target.Z);
        float range = _spells.PreferredRange(player);
        float distance = Vector3.Distance(here, there);
        spot = distance <= range || distance < 0.01f ? here : there + ((here - there) * (range / distance));

        var enemies = new List<RiskEnemy> { Enemy(target, RiskJoin.Target, player) with { Remembered = IsRemembered(target) } };
        var counted = new HashSet<ObjectGuid> { target.Guid };
        List<PlayerbotThreat> threats = PlayerbotRecovery.Threats(player);

        // vmangos Creature::CallAssistance: the target calls idle creatures it may call within its assistance radius (a defensive
        // creature answers too, so this looks at every visible creature, not only the ones that aggro on sight).
        if (system is not null && map is not null && system.AssistanceRadiusOf(target.Template) is var assist && assist > 0)
            foreach (ObjectGuid guid in player.VisibleObjects)
                if (map.FindObject(guid) is Creature helper && !counted.Contains(helper.Guid) && helper.IsAlive
                    && system.CanAssist(helper, target, player, assist))
                {
                    counted.Add(helper.Guid);
                    enemies.Add(Enemy(helper, RiskJoin.Assist, player) with { Remembered = IsRemembered(helper) });
                }

        // vmangos BasicAI::MoveInLineOfSight: any creature whose aggro radius covers where the bot will fight.
        foreach (PlayerbotThreat threat in threats)
            if (threat.Source is { } creature && !counted.Contains(creature.Guid) && threat.Reaches(spot, 0f))
            {
                counted.Add(creature.Guid);
                enemies.Add(Enemy(creature, RiskJoin.FightSpot, player) with { Remembered = IsRemembered(creature) });
            }

        fight = [.. counted];

        // ... and those whose radius covers the way there (the planned route, sampled every 3 yards).
        if (PlayerbotNavigation.TryPlan(player, spot, _options, out PlayerbotRoute? approach, trial: true) && approach is not null)
            foreach (PlayerbotThreat threat in threats)
                if (threat.Source is { } creature && !counted.Contains(creature.Guid) && Covers(threat, approach.Points))
                {
                    counted.Add(creature.Guid);
                    enemies.Add(Enemy(creature, RiskJoin.Path, player) with { Remembered = IsRemembered(creature) });
                }

        float? mana = null;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        if (player.PowerType == PowerType.Mana && maxMana > 0) mana = SpellSystem.GetPower(player, PowerType.Mana) * 100f / maxMana;
        uint now = _session.World.NowMs;
        return new PlayerbotEngagementFacts
        {
            BotLevel = player.Level,
            BotHealth = player.Health,
            BotMaxHealth = player.MaxHealth,
            BotManaPct = mana,
            ManaDependence = ManaDependence(player.Class),
            BotDps = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level),
            ReadyEscapes = ReadyEscapes(player),
            Enemies = enemies,
            QuestObjective = questObjective,
            LootValue = LootValue(player, target.Template),
            DangerHits = Enabled ? Memory.Hits(player.MapId, there, target.Entry, now) : 0,
            Remembered = IsRemembered(target),
            LostToKind = Enabled && Memory.LostAlone(target.Entry, now),
        };
    }

    /// <summary>
    /// The bot's own half of a pull's facts for <paramref name="enemies"/> (<see cref="PlayerbotOpportunities"/> weighs a whole guard pack
    /// as one pull): level, health, mana, observed damage, ready escapes. Quest, loot and danger memory are the caller's.
    /// </summary>
    internal PlayerbotEngagementFacts BotFacts(Player player, IReadOnlyList<RiskEnemy> enemies)
    {
        float? mana = null;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        if (player.PowerType == PowerType.Mana && maxMana > 0) mana = SpellSystem.GetPower(player, PowerType.Mana) * 100f / maxMana;
        return new PlayerbotEngagementFacts
        {
            BotLevel = player.Level,
            BotHealth = player.Health,
            BotMaxHealth = player.MaxHealth,
            BotManaPct = mana,
            ManaDependence = ManaDependence(player.Class),
            BotDps = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level),
            ReadyEscapes = ReadyEscapes(player),
            Enemies = enemies,
        };
    }

    internal RiskEnemy Enemy(Creature creature, RiskJoin join, Player bot) => Enemy(creature, join, bot.MaxHealth, SpellThreat(creature));

    private static RiskEnemy Enemy(Creature creature, RiskJoin join, uint botMaxHealth, CreatureSpellThreat spells)
    {
        CreatureTemplate t = creature.Template;
        bool elite = t.Rank is 1 or 2 or 3;
        return new RiskEnemy(creature.Entry, creature.Level, t.Rank, creature.Health,
            PlayerbotRiskModel.CreatureDps(t.MinMeleeDamage, t.MaxMeleeDamage, t.MeleeBaseAttackTime, creature.Level, elite) + spells.Dps,
            join, spells.IsLethalTo(botMaxHealth));
    }

    /// <summary>The creature's own spells (<see cref="PlayerbotCreatureSpells"/>).</summary>
    internal CreatureSpellThreat SpellThreat(Creature creature)
        => PlayerbotCreatureSpells.Of(creature, _session.Services.GetService<SpellFeature>()?.System.Store);

    private static bool Covers(PlayerbotThreat threat, IReadOnlyList<Vector3> points)
    {
        for (int index = 1; index < points.Count; index++)
        {
            Vector3 a = points[index - 1], b = points[index];
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 3f));
            for (int step = 0; step <= steps; step++)
                if (threat.Reaches(Vector3.Lerp(a, b, (float)step / steps), 0f)) return true;
        }

        return false;
    }

    /// <summary>
    /// A way round the creatures on the approach: a waypoint to either side of the straight line (15, 25 or 35 yards off its
    /// middle), reached on the navigation, from which the route and the last straight leg to the fight spot stay out of the aggro
    /// reach (with a 2-yard margin) of every visible creature that would attack the bot and is not part of the fight anyway, and out of
    /// the bot's hazards. Null when none does.
    /// <para>
    /// Every such creature, not only those on the straight approach: a way round one pack can run into another that stood clear of the
    /// straight line (vmangos BasicAI::MoveInLineOfSight aggroes on whatever the bot walks past). One already reaching where the bot
    /// stands is no reason to refuse every way (it is part of where the bot is, not of the way).
    /// </para>
    /// </summary>
    private PlayerbotRoute? FindDetour(Player player, Vector3 spot, HashSet<ObjectGuid> fight)
    {
        Vector3 here = new(player.X, player.Y, player.Z);
        List<PlayerbotThreat> avoid = [.. PlayerbotRecovery.Threats(player)
            .Where(threat => threat.Source is { } creature && !fight.Contains(creature.Guid) && !threat.Reaches(here, 0f))];
        Vector3 flat = new(spot.X - here.X, spot.Y - here.Y, 0);
        if (flat.Length() < 1f) return null;
        Vector3 side = Vector3.Normalize(new Vector3(-flat.Y, flat.X, 0));
        Vector3 middle = (here + spot) / 2f;
        using PlayerbotThinkProbe.FallbackScope fallback = PlayerbotThinkProbe.Fallback();
        foreach (float offset in (float[])[15f, 25f, 35f])
            foreach (float sign in (float[])[1f, -1f])
            {
                if (PlayerbotThinkProbe.Active is { Refused: > 0 }) return null;
                Vector3 waypoint = middle + (side * offset * sign);
                if (!PlayerbotNavigation.TryPlan(player, waypoint, _options, out PlayerbotRoute? route, trial: true) || route is null) continue;
                List<Vector3> points = [.. route.Points, spot];
                if (avoid.Any(threat => Covers(threat with { Radius = threat.Radius + 2f }, points))) continue;
                if (Blocking(player, points) is not null) continue;
                return route;
            }

        return null;
    }

    private int ReadyEscapes(Player player)
    {
        PlayerbotAbilities escapes = _spells.Escapes(player);
        int ready = 0;
        foreach (string name in PlayerbotEscapes.All)
            if (escapes[name] is { } spell && spell.Effects.All(e => e.TargetA != SpellImplicitTarget.UnitEnemy) && _spells.CanCastNow(player, spell, player))
                ready++;
        return ready;
    }

    /// <summary>How much of a class's damage needs mana: casters all of it, hybrids part, rage and energy none.</summary>
    internal static float ManaDependence(Class playerClass) => playerClass switch
    {
        Class.Mage or Class.Priest or Class.Warlock => 1f,
        Class.Druid or Class.Shaman or Class.Paladin => 0.4f,
        Class.Hunter => 0.3f,
        _ => 0f,
    };

    /// <summary>Loot and skinning: a humanoid carries coin and cloth, a beast meat and a hide (more for a skinner).</summary>
    private static float LootValue(Player player, CreatureTemplate template) => template.CreatureType switch
    {
        7 => 0.2f,
        1 => player.Skills?.Has(SkinningSkill) == true ? 0.3f : 0.1f,
        _ => 0.05f,
    };

    // --- a ghost's body ----------------------------------------------------------------------------------------------

    /// <summary>
    /// A creature round a ghost's body that the revived bot could not take even at full health, or null: the risk part of
    /// <see cref="PlayerbotRecovery.JudgeBody"/>. The candidates are the creatures that would attack the revived bot on sight
    /// (<see cref="PlayerbotRecovery.Threats"/>) and whose aggro reach covers part of the reclaim disc
    /// (<see cref="CombatConstants.CorpseReclaimRadius"/> round the body, anywhere a reclaim may revive it). Each is weighed as a pull
    /// is (<see cref="PlayerbotRiskModel.Assess"/>), with the creatures that would answer its call for assistance (vmangos
    /// Creature::CallAssistance, as <see cref="Facts"/>), the bot at the health and mana a reclaim gives back (half: vmangos
    /// HandleReclaimCorpseOpcode, <c>ResurrectPlayer(0.5f)</c>, <see cref="CombatConstants.CorpseReclaimRestorePercent"/>), no escape
    /// counted (a ghost has none ready), and as a matter of strength only: no quest credit, no loot, no danger memory (the bot died to
    /// these, and the memory alone would avoid every one of them). The model answers Rest for a fight that only needs full health and
    /// mana first, so Avoid means not even then. Null with the estimate off.
    /// <para>
    /// Strength, not worth: a pull's bar falls with its reward (<see cref="PlayerbotRiskModel.Acceptable"/>), and a creature round a body
    /// gives none here (no quest, no loot), so the pull bar refused ordinary same-zone creatures: in the first six minutes of the live
    /// run of 2026-10-11 (08:04-08:10, world.log) 31 of 80 bodies were given up as "too strong", among them level 9-13 Defias and gnolls
    /// against level-14 bots (entries 95, 474, 478, 504, 1118). A creature counts only when the model avoids it and it is beyond the bot's
    /// strength (<see cref="PlayerbotRiskModel.BeyondStrength"/>): a creature that kills outright, an elite well above it, or a fight
    /// whose risk at full health and mana is above the highest share any pull is ever taken at.
    /// </para>
    /// <para>
    /// The bot as the corpse run revives it, not as it is now: a reclaim gives no resurrection sickness, and a sickness the bot carries
    /// from an earlier spirit-healer revival is temporary (<see cref="PlayerbotSickness"/>), so the bot's health is its health without
    /// it (<see cref="PlayerbotSickness.UnsickenedMaxHealth"/>) and its damage the average of the fights it fought well
    /// (<see cref="PlayerbotFightTracker.End"/> leaves the sick ones out). Live 2026-10-11 (analysis 4.1): judged with the sickened
    /// health and damage, 47% of 4,633 "too strong" verdicts were against creatures at or below the bot's level; each sent the bot to
    /// the healer, which sickened it again.
    /// </para>
    /// </summary>
    internal PlayerbotEngagement? TooStrongNear(Player player, Vector3 body)
        => TooStrongWithin(player, PlayerbotRecovery.Threats(player), body, CombatConstants.CorpseReclaimRadius, unsickened: true);

    /// <summary>
    /// A creature too strong for the bot (<see cref="TooStrongNear"/>'s rule) whose aggro reach covers <paramref name="at"/> itself: one
    /// that attacks a bot that comes to life there. The spirit healer's landing (<see cref="PlayerbotRecovery.LandingUnsafe"/>): the
    /// creatures round the place itself, seen by the bot or not (<see cref="PlayerbotRecovery.ThreatsAround"/>; the landing can be a
    /// graveyard out of the ghost's sight).
    /// </summary>
    internal PlayerbotEngagement? TooStrongAt(Player player, Vector3 at)
        => TooStrongWithin(player, PlayerbotRecovery.ThreatsAround(player, at, PlayerbotRecovery.BodyThreatYards), at, 0f, unsickened: false);

    /// <param name="unsickened">Judge the bot without its resurrection sickness (a body: <see cref="TooStrongNear"/>); else as it is
    /// (the spirit healer's landing, where the bot stands sick).</param>
    private PlayerbotEngagement? TooStrongWithin(Player player, IReadOnlyList<PlayerbotThreat> threats, Vector3 body, float radius,
        bool unsickened)
    {
        if (!Enabled || player.Map is not { } map) return null;
        CreatureMapSystem? system = map.FindUpdater<CreatureMapSystem>();
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        float? mana = player.PowerType == PowerType.Mana && maxMana > 0 ? CombatConstants.CorpseReclaimRestorePercent * 100f : null;
        uint maxHealth = unsickened ? PlayerbotSickness.UnsickenedMaxHealth(_session, player) : player.MaxHealth;
        uint health = Math.Max(1u, (uint)(maxHealth * CombatConstants.CorpseReclaimRestorePercent));
        // A landing or corpse spot can aggro several unrelated creatures at once. The pull estimate counts all creatures at
        // its fight spot; judging each one alone here let ordinary packs camp graveyards and repeatedly kill revived bots.
        List<RiskEnemy> reaching = _reachingEnemies;
        reaching.Clear();
        foreach (PlayerbotThreat threat in threats)
            if (threat.Source is { } creature && threat.Reaches(body, PlayerbotRecovery.CampMarginYards))
                reaching.Add(Enemy(creature, RiskJoin.FightSpot, player));
        if (reaching.Count > 1)
        {
            var pack = new PlayerbotEngagementFacts
            {
                BotLevel = player.Level, BotHealth = health, BotMaxHealth = maxHealth, BotManaPct = mana,
                ManaDependence = ManaDependence(player.Class),
                BotDps = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level), Enemies = reaching,
            };
            PlayerbotEngagement verdict = PlayerbotRiskModel.Assess(pack, _options.Risk);
            if (FatalOrBeyondStrength(verdict, pack, radius)) return verdict;
        }
        foreach (PlayerbotThreat threat in threats)
        {
            if (threat.Source is not { } creature
                || (radius > 0 ? Vector3.Distance(threat.Position, body) >= radius + threat.Radius
                    : !threat.Reaches(body, PlayerbotRecovery.CampMarginYards))) continue;
            var enemies = new List<RiskEnemy> { Enemy(creature, RiskJoin.Target, player) };
            if (system is not null && system.AssistanceRadiusOf(creature.Template) is var assist && assist > 0)
                foreach (ObjectGuid guid in player.VisibleObjects)
                    if (map.FindObject(guid) is Creature helper && helper.Guid != creature.Guid && helper.IsAlive
                        && system.CanAssist(helper, creature, player, assist))
                        enemies.Add(Enemy(helper, RiskJoin.Assist, player));
            var facts = new PlayerbotEngagementFacts
            {
                BotLevel = player.Level,
                BotHealth = health,
                BotMaxHealth = maxHealth,
                BotManaPct = mana,
                ManaDependence = ManaDependence(player.Class),
                BotDps = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level),
                Enemies = enemies,
            };
            PlayerbotEngagement verdict = PlayerbotRiskModel.Assess(facts, _options.Risk);
            if (FatalOrBeyondStrength(verdict, facts, radius)) return verdict;
        }

        return null;
    }

    /// <summary>
    /// Whether a verdict makes the place unsafe. Always when the fight is beyond the bot's strength at full health; at an exact
    /// landing (<paramref name="radius"/> 0: the spirit healer's, no clear spot elsewhere in a reclaim disc) also when the fight is
    /// predicted to take all of the health the resurrection gives back (<c>facts.BotHealth</c>, half of the current maximum, which
    /// resurrection sickness already lowers), even if the bot could win it after resting.
    /// </summary>
    private bool FatalOrBeyondStrength(PlayerbotEngagement verdict, PlayerbotEngagementFacts facts, float radius)
        => (radius == 0f && verdict.Risk >= 1f)
            || (verdict.Decision == PlayerbotEngageDecision.Avoid && PlayerbotRiskModel.BeyondStrength(facts, _options.Risk));

    // --- hazards on the way -------------------------------------------------------------------------------------

    /// <summary>
    /// Every <see cref="HazardScanMs"/> (alive or a ghost): each visible creature that would attack the bot and has a spell that kills it outright, or that
    /// the bot fled from or died to, is a hazard round its aggro reach (moving with it while seen); while the bot is alive, so is each
    /// one beyond its strength alone (<see cref="BeyondStrength(Player, Creature, CreatureSpellThreat)"/>, kind <c>strong</c>, with
    /// <see cref="AvoidsStrongCreatures"/>), for as long as a look sees it alive and strong (<see cref="PlayerbotHazards.EndStrongScan"/>)
    /// and at most <see cref="StrongHazardLifeMs"/> after the last one. A ghost keeps no strong hazards: its routes are never blocked
    /// and its revive spot does not count them.
    /// <para>
    /// The strong ones: the pull estimate never chooses such a creature, but a creature attacks whoever walks into its reach (vmangos
    /// BasicAI::MoveInLineOfSight), and a walk (quest travel, exploring, an errand) did not look. Live w23 2026-10-11 11:35-12:13
    /// (world.log): five level 1-3 bots without a home, revived at the Goldshire spirit healer, gave a body up to it 58 times on their
    /// walks round it, among Defias Cutpurses (94) and Mangy Wolves (525) of level 5-6 that each of those verdicts judged
    /// <c>decision=avoid reason=too-strong</c> (risk 2.9 to 28).
    /// </para>
    /// <para>
    /// Cost (about a thousand bots, one world thread): the threats go into a list kept for the purpose, the strong hazards are updated
    /// in place, the strength test allocates nothing (<see cref="PlayerbotRiskModel.BeyondStrengthAlone"/>), and after the first look a
    /// bot's looks are spread over the period by its guid (bots that log in on the same tick do not all look on the same tick after).
    /// </para>
    /// </summary>
    internal void ScanHazards(Player player)
    {
        if (!Enabled || !player.IsInWorld) return;
        uint now = _session.World.NowMs;
        if (unchecked((int)(_nextScanMs - now)) > 0) return;
        PackExposureEvaluations = 0;
        uint phase = _scanned ? 0u : (uint)((player.Guid.Low * 2654435761UL) % HazardScanMs);
        _scanned = true;
        _nextScanMs = unchecked(now + HazardScanMs + phase);
        _lairEpoch = _lairEpoch == uint.MaxValue ? 1u : _lairEpoch + 1u;
        int seconds = _options.Risk.DangerMemorySeconds;
        SpellStore? store = _session.Services.GetService<SpellFeature>()?.System.Store;
        bool strong = AvoidsStrongCreatures && player.IsAlive;
        float botDps = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level);
        if (strong) Hazards.BeginStrongScan();
        PlayerbotRecovery.Threats(player, _threats);
        if (strong) PreparePackExposures(player, botDps);
        for (int index = 0; index < _threats.Count; index++)
        {
            PlayerbotThreat threat = _threats[index];
            if (threat.Source is not { } creature) continue;
            MarkLairSeen(creature.Guid);
            CreatureSpellThreat spells = PlayerbotCreatureSpells.Of(creature, store);
            if (spells.IsLethalTo(player.MaxHealth))
                Hazards.Add(player.MapId, threat.Position, threat.Radius + PlayerbotHazards.LethalMarginYards, now, seconds, "lethal", creature.Guid);
            else if (Memory.IsRemembered(creature.Guid, now))
                Hazards.Add(player.MapId, threat.Position, threat.Radius + PlayerbotHazards.FledMarginYards, now, seconds, "fled", creature.Guid);
            else if (strong)
            {
                bool alone = BeyondStrength(player, creature, spells, botDps);
                if (alone || BeyondStrengthWithNearby(index))
                    Hazards.SetStrong(player.MapId, threat.Position, threat.Radius + PlayerbotHazards.StrongMarginYards,
                        now, StrongHazardLifeMs, creature.Guid,
                        returnPassable: !alone && creature.Level <= player.Level + MaxLevelsAbove);
            }
        }

        _threats.Clear();
        if (strong) Hazards.EndStrongScan();
        // A remembered creature this look did not see is back at its lair (it evades home, and a stealthed one hides there). The hazard
        // was following the last place the bot saw it; leaving it there opened the lair, and the bot died to the creature its memory
        // already held (bot-risk/died-in-known-hazard) or walked back into the same fight (bot-risk/retreat-loop).
        RestoreUnseenLairs(player, now);
    }

    private readonly List<PlayerbotThreat> _threats = [];
    private readonly List<RiskEnemy> _reachingEnemies = []; // world-thread-only scratch for resurrection checks
    private float[] _packExposures = [];
    /// <summary>Number of melee exposure calculations in the last hazard scan (cost regression seam).</summary>
    internal int PackExposureEvaluations { get; private set; }
    private bool _scanned;

    /// <summary>
    /// Whether the bot could not take <paramref name="creature"/> alone even at full health and mana
    /// (<see cref="PlayerbotRiskModel.BeyondStrengthAlone"/>, the verdict of <see cref="PlayerbotRiskModel.BeyondStrength"/>: an elite well
    /// above it, or a risk above the highest share any pull is taken at), judged on the creature alone (its assistance would only add
    /// to it), from the same template damage and bot damage as a pull.
    /// </summary>
    internal bool BeyondStrength(Player player, Creature creature, CreatureSpellThreat spells)
        => BeyondStrength(player, creature, spells, Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level));

    private bool BeyondStrength(Player player, Creature creature, CreatureSpellThreat spells, float botDps)
    {
        RiskEnemy enemy = Enemy(creature, RiskJoin.Target, player.MaxHealth, spells);
        return PlayerbotRiskModel.BeyondStrengthAlone(player.Level, player.MaxHealth, botDps, enemy, _options.Risk);
    }

    /// <summary>
    /// Lower bound on the risk of the pack at this creature's position. Every neighboring threat that reaches the position
    /// would aggro there even without an assistance call (vmangos BasicAI::MoveInLineOfSight). The sum of each creature's
    /// solo damage while being killed is no greater than the real sequential fight, so crossing the maximum pull threshold
    /// is enough to mark the overlapping reaches without sorting or allocating in the frequent hazard scan.
    /// </summary>
    private void PreparePackExposures(Player player, float botDps)
    {
        if (_packExposures.Length < _threats.Count)
            Array.Resize(ref _packExposures, Math.Max(_threats.Count, _packExposures.Length * 2));
        float denominator = MathF.Max(0.1f, botDps) * Math.Max(1u, player.MaxHealth);
        for (int index = 0; index < _threats.Count; index++)
        {
            if (_threats[index].Source is not { } creature)
            {
                _packExposures[index] = 0;
                continue;
            }

            CreatureTemplate template = creature.Template;
            PackExposureEvaluations++;
            float melee = PlayerbotRiskModel.CreatureDps(template.MinMeleeDamage, template.MaxMeleeDamage,
                template.MeleeBaseAttackTime, creature.Level, template.Rank is 1 or 2 or 3);
            int above = creature.Level - player.Level;
            _packExposures[index] = melee * PlayerbotRiskModel.CrushFactor(above) * creature.Health / (denominator * PlayerbotRiskModel.HitFactor(above));
        }
    }

    private bool BeyondStrengthWithNearby(int atIndex)
    {
        PlayerbotThreat at = _threats[atIndex];
        float exposure = 0;
        int count = 0;
        for (int index = 0; index < _threats.Count; index++)
        {
            if (_packExposures[index] <= 0 || (index != atIndex && !_threats[index].Overlaps(at))) continue;
            exposure += _packExposures[index];
            count++;
        }

        return count > 1 && exposure > PlayerbotRiskModel.Acceptable(float.PositiveInfinity, _options.Risk.Tolerance);
    }

    /// <summary>
    /// Whether a living bot may walk <paramref name="points"/>: none of them inside a hazard it did not start in. A retreat walks
    /// where it must; a dead bot (a ghost) is not attacked. Without <paramref name="strong"/> the creatures beyond the bot's strength
    /// are not held against it: only for the escape route itself (<see cref="EscapeRoute"/>, <see cref="PlayerbotNavigation.TryAdvance"/>);
    /// a completed quest return also relaxes pack-only hazards of individually manageable, in-range creatures.
    /// Deaths, retreats, lethal creatures and those the bot fled from always hold, except when
    /// <paramref name="crossBesidePlaceMarks"/>: a death or retreat mark whose centre stands off the walked line
    /// (<see cref="PlaceMarkBesideRoadYards"/>) and does not cover the end of it. That is a mark beside the only road, not the
    /// fight itself, and it must not refuse the walk to a friendly quest ender in town (live 2026-10-12: quest 8 ender 5688,
    /// bots near 2176,891 map 0; quest 2160 ender 6806, Dun Morogh around -5600). A mark on the road, one covering the ender,
    /// a death-loop zone, and every creature hazard still hold.
    /// </summary>
    internal PlayerbotHazard? Blocking(Player player, IReadOnlyList<Vector3> points, bool strong = true,
        bool completedReturn = false, bool crossBesidePlaceMarks = false, bool chase = false)
    {
        if (!Enabled || !player.IsAlive || Retreat.Active) return null;
        uint now = _session.World.NowMs;
        PlayerbotHazard? hit = Hazards.FirstOnRoute(player.MapId, points, now, strong, completedReturn, chase);
        if (!crossBesidePlaceMarks || hit is not { } first || !IsPlaceMarkBesideRoad(first, points)) return hit;
        return NextAfterBesidePlaceMarks(player, points, now, strong, completedReturn);
    }

    /// <summary>
    /// How far (flat yards) a death or retreat mark's centre must stand from the walked line before it is beside the road rather
    /// than on it. Inside this, the bot died or fled on the road and the mark still blocks.
    /// </summary>
    internal const float PlaceMarkBesideRoadYards = 8f;

    /// <summary>
    /// A remembered death or retreat (no creature of its own) whose centre is off the walked line but whose radius still covers it,
    /// and which does not cover the end of the walk.
    /// </summary>
    internal static bool IsPlaceMarkBesideRoad(in PlayerbotHazard hazard, IReadOnlyList<Vector3> points)
    {
        if (points.Count < 2 || !hazard.Creature.IsEmpty) return false;
        if (hazard.Kind is not ("death" or "retreat")) return false;
        if (hazard.Covers(points[^1])) return false;
        float closest = ClosestFlat(hazard.At, points);
        return closest >= PlaceMarkBesideRoadYards && closest < hazard.Radius;
    }

    /// <summary>
    /// <see cref="PlayerbotHazards.FirstOnRoute"/> again, skipping place marks <see cref="IsPlaceMarkBesideRoad"/> would let a
    /// friendly town return cross. The first hit was one of those; a later strong, lethal, fled or on-road mark still blocks.
    /// </summary>
    private PlayerbotHazard? NextAfterBesidePlaceMarks(Player player, IReadOnlyList<Vector3> points, uint now, bool strong,
        bool completedReturn)
    {
        IReadOnlyList<PlayerbotHazard> active = Hazards.Active(player.MapId, now);
        if (active.Count == 0 || points.Count < 2) return null;
        Vector3 start = points[0], end = points[^1];
        foreach (PlayerbotHazard hazard in active)
            if (hazard.Kind == PlayerbotHazards.DeathLoopKind && hazard.Covers(start) && hazard.Covers(end))
                return hazard;
        for (int index = 1; index < points.Count; index++)
        {
            Vector3 a = points[index - 1], b = points[index];
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / 2f));
            for (int step = 1; step <= steps; step++)
            {
                Vector3 point = Vector3.Lerp(a, b, (float)step / steps);
                foreach (PlayerbotHazard hazard in active)
                {
                    if (hazard.Kind == PlayerbotHazards.StrongKind)
                    {
                        if (!strong || (completedReturn && hazard.ReturnPassable)) continue;
                    }

                    if (!hazard.Covers(point) || hazard.Covers(start) || IsPlaceMarkBesideRoad(hazard, points)) continue;
                    return hazard;
                }
            }
        }

        return null;
    }

    private static float ClosestFlat(Vector3 at, IReadOnlyList<Vector3> points)
    {
        float best = float.MaxValue;
        for (int index = 1; index < points.Count; index++)
        {
            float distance = SegmentDistance(at.X, at.Y, points[index - 1].X, points[index - 1].Y, points[index].X, points[index].Y);
            if (distance < best) best = distance;
        }

        return best;
    }

    private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float abx = bx - ax, aby = by - ay, ab2 = (abx * abx) + (aby * aby);
        float t = ab2 <= 1e-6f ? 0f : Math.Clamp(((px - ax) * abx + (py - ay) * aby) / ab2, 0f, 1f);
        float dx = ax + (abx * t) - px, dy = ay + (aby * t) - py;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// The route out of a ring of strong creatures (<see cref="Refused"/>), walked by the brain before any goal (as a retreat is), or
    /// null. It ends when complete, after <see cref="EscapeLifeMs"/>, in combat, or at a death.
    /// </summary>
    internal PlayerbotRoute? EscapeRoute
    {
        get
        {
            if (_escapeRoute is { } route
                && (route.Complete || unchecked((int)(_escapeUntilMs - _session.World.NowMs)) <= 0)) EndEscape();
            return _escapeRoute;
        }
    }

    /// <summary>Whether <paramref name="route"/> is the escape route (the one route exempt from strong hazards).</summary>
    internal bool IsEscape(PlayerbotRoute route) => _escapeRoute is not null && ReferenceEquals(route, EscapeRoute);

    /// <summary>Stop escaping (the route walked, given up, the bot in combat or dead).</summary>
    internal void EndEscape()
    {
        _escapeRoute = null;
        _escapeUntilMs = 0;
    }

    /// <summary>The escapes granted so far (inspection, tests).</summary>
    internal int Escapes { get; private set; }

    private Vector3 _boxedAt;
    private uint _boxedMap;
    private uint _boxedSinceMs;
    private PlayerbotRoute? _escapeRoute;
    private uint _escapeUntilMs;

    /// <summary>
    /// A goal's walk was refused for <paramref name="hazard"/> (<see cref="PlayerbotNavigation"/>: no way round it; trial plans of the
    /// pull estimate and of detours are not told). A low-level bot revived among higher creatures can stand in a ring of their reaches,
    /// every goal walk refused and every goal only backing off; this is the way out, under three conditions:
    /// <list type="bullet">
    /// <item>evidence of a ring, not of one guarded destination: the hazard is a strong creature's, the bot has not moved
    /// <see cref="BoxedProgressYards"/> from where the clock started for <see cref="BoxedMs"/> of world time plus a phase of up to
    /// <see cref="MaxBoxedPhaseMs"/> by its guid (bots revived together do not all run the fan on one tick; any number of refusals in
    /// one tick count for nothing), and no straight line round it is clear for its first <see cref="PlayerbotHazards.GapOpenYards"/>
    /// (<see cref="PlayerbotHazards.FindGaps"/>; two creatures guarding a quest giver leave open lines: the goal backs off and the
    /// stall watch sets it aside, as before);</item>
    /// <item>the least exposed way out: one fan (<see cref="PlayerbotHazards.FindGaps"/>) gives the <see cref="MaxEscapePlans"/> lines
    /// that pass least deep into the creatures' aggro reach (the gaps between them), never through a death, retreat, lethal or fled
    /// hazard; each is planned on the navigation and checked against those again, and held to its line
    /// (<see cref="PlayerbotHazards.RouteExposure"/>): a route the navigation bent deeper into the reaches than its line goes only when
    /// no route keeps to its line;</item>
    /// <item>bounded: one route, pinned (<see cref="EscapeRoute"/>, exempt from strong hazards only), walked for at most
    /// <see cref="EscapeLifeMs"/>, ended by combat or a death, at most one escape per <see cref="BoxedMs"/> and at most
    /// <see cref="MaxEscapesPerWindow"/> per <see cref="EscapeWindowMs"/>.</item>
    /// </list>
    /// </summary>
    internal void Refused(Player player, PlayerbotHazard hazard)
    {
        if (hazard.Kind == PlayerbotHazards.StrongKind) StrongRefusals++;
        if (hazard.Kind != PlayerbotHazards.StrongKind || !Enabled || !player.IsAlive || EscapeRoute is not null) return;
        uint now = _session.World.NowMs;
        var here = new Vector3(player.X, player.Y, player.Z);
        if (_boxedSinceMs == 0 || _boxedMap != player.MapId || Vector3.Distance(here, _boxedAt) >= BoxedProgressYards)
        {
            _boxedAt = here;
            _boxedMap = player.MapId;
            _boxedSinceMs = Math.Max(1u, now);
            return;
        }

        // Bots revived together would reach the gate on the same tick: each waits a little longer, by its guid (0 to 2 s).
        uint phase = BoxedPhaseMs(player.Guid.Low);
        if (unchecked(now - _boxedSinceMs) < BoxedMs + phase) return;
        _boxedSinceMs = Math.Max(1u, now);
        if (!MayEscape(now)) return;
        Span<PlayerbotGap> gaps = stackalloc PlayerbotGap[MaxEscapePlans];
        int found = Hazards.FindGaps(player.MapId, here, now, 0, gaps);
        PlayerbotRoute? chosen = null;
        bool chosenStrays = true;
        float chosenExposure = float.PositiveInfinity;
        for (int i = 0; i < found; i++)
        {
            if (!PlayerbotNavigation.TryPlanUnguarded(player, gaps[i].Exit, _options, out PlayerbotRoute? route) || route is null) continue;
            if (Hazards.FirstOnRoute(player.MapId, route.Points, now, strong: false) is not null) continue;
            // The navigation may bend the way into a reach its straight line kept out of: such a route goes last, after any that keeps
            // to its line, and among those of a kind the least exposed goes first.
            float exposure = Hazards.RouteExposure(player.MapId, route.Points, now);
            bool strays = exposure > gaps[i].Exposure + RouteExposureSlack;
            if (chosen is not null && (strays, exposure).CompareTo((chosenStrays, chosenExposure)) >= 0) continue;
            (chosen, chosenStrays, chosenExposure) = (route, strays, exposure);
            if (!strays) break; // the best line's own route keeps to it
        }

        if (chosen is null) return;
        _escapeRoute = chosen;
        _escapeUntilMs = Math.Max(1u, unchecked(now + EscapeLifeMs));
        _escapeTimes[Escapes % MaxEscapesPerWindow] = Math.Max(1u, now);
        Escapes++;
        LastEscapeStrays = chosenStrays;
    }

    /// <summary>
    /// At most <see cref="MaxEscapesPerWindow"/> escapes in <see cref="EscapeWindowMs"/>: a bot that cannot get clear (a slow walker,
    /// a ring that re-forms round it) stops escaping and leaves its goal to the stall watch.
    /// </summary>
    private bool MayEscape(uint now)
    {
        if (Escapes < MaxEscapesPerWindow) return true;
        uint oldest = _escapeTimes[Escapes % MaxEscapesPerWindow];
        return unchecked(now - oldest) >= EscapeWindowMs;
    }

    private readonly uint[] _escapeTimes = new uint[MaxEscapesPerWindow];

    /// <summary>The bot's extra wait at the boxed-in gate (0 to <see cref="MaxBoxedPhaseMs"/>, by its guid: a Knuth multiplicative hash).</summary>
    internal static uint BoxedPhaseMs(ulong guidLow) => (uint)((guidLow * 2654435761UL) % MaxBoxedPhaseMs);

    /// <summary>Whether the last escape's route strayed into more aggro reach than its straight line (inspection, tests).</summary>
    internal bool LastEscapeStrays { get; private set; }

    /// <summary>
    /// The count of walks refused for a strong creature's reach (<see cref="Refused"/>), escaping or not: a caller compares it around a
    /// plan to tell a refusal by a ring of creatures from one by the mesh (<see cref="PlayerbotBrain"/>'s explore ray).
    /// </summary>
    internal int StrongRefusals { get; private set; }

    /// <summary>When the boxed-in clock started (0: not started; tests).</summary>
    internal uint BoxedSinceMs => _boxedSinceMs;

    /// <summary>
    /// The hazards as threats for the ghost's revive spot (<see cref="PlayerbotRecovery"/>): a revive in reach of a creature that kills
    /// outright, or of one the bot fled from or died to, is camped. Remembered places are not: the body lies at the place of death
    /// itself, and a camp that has gone is no reason to give the body up. Nor are the creatures beyond the bot's strength (kind
    /// <c>strong</c>, for its walks): the recovery weighs those itself (<see cref="TooStrongNear"/>, <see cref="TooStrongAt"/>) and a
    /// revive spot keeps out of their aggro reach by <see cref="PlayerbotRecovery.Threats(Player)"/> already.
    /// </summary>
    internal IEnumerable<PlayerbotThreat> HazardThreats(Player player)
        => !Enabled ? [] : Hazards.Active(player.MapId, _session.World.NowMs).Where(h => !h.Creature.IsEmpty && h.Kind != PlayerbotHazards.StrongKind)
            .Select(h => new PlayerbotThreat(h.At, h.Radius, IgnoresHeight: true, Radii: 0));

    // --- in a fight ------------------------------------------------------------------------------------------------

    /// <summary>
    /// One look at the fight against <paramref name="target"/>: the verdict from the observed rates; a losing bot starts a retreat
    /// (true). Only while the bot is in combat.
    /// </summary>
    internal bool ObserveFight(Player player, Creature target)
    {
        // A cornered bot (its retreat did not shake the pursuers off) fights this one out.
        if (!Enabled || !player.Combat.IsInCombat || Retreat.Active || _cornered) return false;
        var enemies = new List<Creature>();
        if (target.IsAlive && ReferenceEquals(target.Map, player.Map)) enemies.Add(target);
        foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
            if (unit is Creature creature && creature.IsAlive && ReferenceEquals(creature.Map, player.Map)
                && !creature.IsInEvadeMode && !enemies.Contains(creature))
                enemies.Add(creature);
        // Until the window is long enough the estimate's priors stand in: the creatures' template damage and the bot's damage.
        float priorIn = enemies.Sum(e => Enemy(e, RiskJoin.Target, player).Dps);
        float priorOut = Tracker.ObservedDps ?? PlayerbotRiskModel.PriorBotDps(player.Level);
        // A fight under resurrection sickness (every blow a quarter) says nothing of the bot's damage once well.
        if (PlayerbotSickness.IsSick(_session, player)) Tracker.MarkSick();
        uint now = _session.World.NowMs;
        bool knownDanger = enemies.Any(e => Memory.RememberedBefore(e.Guid, _fightSinceMs, now));
        PlayerbotFightFacts facts = Tracker.Observe(player, enemies, target, now, priorIn, priorOut)
            with { Lethal = knownDanger || enemies.Any(e => SpellThreat(e).IsLethalTo(player.MaxHealth)) };
        PlayerbotFightVerdict verdict = PlayerbotRiskModel.Judge(facts, _options.Risk);
        if (verdict.Retreat && !facts.Lethal)
        {
            // The bot would run: can it get away before it dies? Looked at only now (it plans the goal and asks the spellbook).
            float? fought = _fightSinceMs == 0 ? null : unchecked(_session.World.NowMs - _fightSinceMs) / 1000f;
            PlayerbotPursuit pursuit = Retreat.Assess(player, enemies, Tracker.Anchors, Breadcrumbs.Points, e => Enemy(e, RiskJoin.Target, player).Dps, fought);
            verdict = PlayerbotRiskModel.Judge(facts with { Pursuit = pursuit }, _options.Risk);
            _retreatPlan = new RetreatPlan(facts.BotMaxHealth == 0 ? 0f : facts.BotHealth * 100f / facts.BotMaxHealth,
                facts.BotHealth == 0 ? float.PositiveInfinity : PlayerbotRiskModel.RetreatDamage(pursuit, facts.DpsIn) / facts.BotHealth,
                pursuit.EscapeYards / MathF.Max(0.1f, pursuit.BotRunSpeed * pursuit.Kit.SelfSpeed), pursuit.MinContactSeconds,
                pursuit.FleeDamageFactor, fought ?? 0f);
            if (!verdict.Retreat && !_refusalCounted)
            {
                _refusalCounted = true;
                _refusedPending = true;
                RetreatStats.RecordRefused();
            }
        }

        else _retreatPlan = null;

        Tracker.Record(verdict);
        try { CombatSink?.Judged(player, facts, verdict, _session.World.NowMs); }
        catch (Exception error) when (error is not OutOfMemoryException) { SinkFault(error); }
        if (!verdict.Retreat) return false;
        StartRetreat(player, enemies, verdict.Reason);
        return true;
    }

    /// <summary>The refusal of a retreat is counted once per fight (<see cref="Track"/> clears it out of combat).</summary>
    private bool _refusalCounted;

    /// <summary>
    /// What the estimate said of the retreat about to begin (or begun): the bot's health in percent, the predicted damage on the run as
    /// a share of its health, the seconds of the run, the leash floor, the from-behind factor and the seconds fought. Null for a retreat
    /// not weighed (lethal, a party wipe). For the outcome counts by predicted share and the retreat-died line.
    /// </summary>
    private readonly record struct RetreatPlan(float HealthPct, float PredictedShare, float RunSeconds, float LeashSeconds, float FleeFactor, float FoughtSeconds);

    private RetreatPlan? _retreatPlan;

    /// <summary>A retreat was refused in this fight and no retreat began since: the fight's end is counted against the refusal.</summary>
    private bool _refusedPending;
    private RetreatPlan? _activePlan;
    private Vector3 _retreatFrom;
    private string? _retreatAttackers;

    /// <summary>A retreat ended (by <see cref="UpdateRetreat"/> or by the bot's death in <see cref="OnDeath"/>): remember the flight and count the outcome. Gated on <see cref="Enabled"/> in one place.</summary>
    private void NoteRetreatEnd(Player player, string? outcome)
    {
        if (!Enabled) return;
        _retreatEndedMs = Math.Max(1u, _session.World.NowMs);
        if (outcome == "died")
        {
            _retreatDiedMs = Math.Max(1u, _session.World.NowMs);
            PlayerbotAnomalies.Report(player, "risk", "retreat-died",
                $"died during a retreat begun for '{Retreat.Reason}' after escapes [{string.Join('+', Retreat.UsedEscapes)}]; {DescribePlan(player)}",
                AnomalySeverity.Info);
        }

        RememberFlight(player);
        RetreatStats.Record(outcome, _activePlan?.PredictedShare);
        _activePlan = null;
    }

    /// <summary>The retreat's estimate against what happened, for the retreat-died line (QA-13: which part of the estimate was wrong).</summary>
    private string DescribePlan(Player player)
    {
        uint elapsed = Retreat.StartedMs == 0 ? 0 : unchecked(_session.World.NowMs - Retreat.StartedMs);
        float ran = Vector3.Distance(_retreatFrom, new Vector3(player.X, player.Y, player.Z));
        string actual = string.Create(CultureInfo.InvariantCulture, $"died after {elapsed / 1000f:F1} s, {ran:F0} yd from the start");
        actual += "; last seen " + (_retreatAttackers ?? "attackers unknown");
        _retreatAttackers = null;
        return _activePlan is not { } plan
            ? $"not weighed; {actual}"
            : string.Create(CultureInfo.InvariantCulture,
                $"began at {plan.HealthPct:F0}% health after {plan.FoughtSeconds:F1} s of fight, predicted {plan.PredictedShare:F2} of health lost over a {plan.RunSeconds:F1} s run (leash floor {plan.LeashSeconds:F1} s, from-behind x{plan.FleeFactor:F2}); {actual}");
    }

    /// <summary>
    /// Who was on the bot when it died, against the soft leash (QA-13: on the 21:11 pack bots died 85 to 130 yd from where the retreat
    /// began, past the threat radius): each creature attacking it (up to three), whether the bot had fled from it or it joined on the
    /// run, its and the bot's distance from where its fight began, the threat radius and the leash clock's age.
    /// </summary>
    private string DescribeAttackers(Player player)
    {
        CreatureMapSystem? creatures = player.Map?.FindUpdater<CreatureMapSystem>();
        var parts = new List<string>();
        int count = 0;
        foreach (Unit unit in player.Combat.Attackers)
        {
            if (unit is not Creature creature) continue;
            count++;
            if (parts.Count == 3) continue;
            bool fled = Array.Exists(_fledFrom, f => f.Guid == creature.Guid);
            string leash = creatures?.GetLeashFacts(creature, player) is { } facts
                ? string.Create(CultureInfo.InvariantCulture,
                    $"start {facts.CreatureFromStart:F0}/{facts.TargetFromStart:F0} yd of r{facts.Radius:F0}, clock {facts.ClockAgeSeconds} s{(facts.OutOfArea ? ", out" : "")}")
                : "no leash";
            parts.Add($"{creature.Entry}{(fled ? " fled" : " new")} ({leash})");
        }

        return $"attackers {count} [{string.Join("; ", parts)}]";
    }

    /// <summary>
    /// Remember where the fight turned and the creatures fled from: the danger memory and the hazard at <c>_fledAt</c>. A retreat
    /// that ends in the bot's death does so too (it is the most common ending, live w29), whichever of the retreat's update and
    /// <see cref="OnDeath"/> sees the death first.
    /// </summary>
    internal const float RetreatCampYards = 80f;
    private (bool Valid, uint MapId, Vector3 At, uint AtMs) _lastRetreat;

    private void RememberFlight(Player player)
    {
        uint now = _session.World.NowMs;
        bool inWindow = _lastRetreat.Valid && unchecked(now - _lastRetreat.AtMs) <= PlayerbotRetreatLoopWatch.WindowMs;
        if (!inWindow) _recentRetreatEntries.Clear();
        bool repeatCamp = inWindow && _lastRetreat.MapId == _fledAt.MapId
            && Vector3.Distance(_lastRetreat.At, _fledAt.Where) <= RetreatCampYards;
        _lastRetreat = (true, _fledAt.MapId, _fledAt.Where, now);
        // A retreat from one creature is the estimate proved wrong about its kind (a death alone is not: one death to a kind still
        // leaves it a target, the stall watch sets it aside at the second, PlayerbotStallWatch.RecordDeath).
        Memory.Remember(_fledAt.MapId, _fledAt.Where, _fledFrom, now, _options.Risk.DangerMemorySeconds,
            alone: _fledFrom.Length == 1);
        if (inWindow && _fledFrom.Length > 1)
        {
            foreach ((ObjectGuid guid, uint entry) in _fledFrom)
                if (_recentRetreatEntries.Contains(entry))
                    Memory.Remember(_fledAt.MapId, _fledAt.Where, [(guid, entry)], now, _options.Risk.DangerMemorySeconds, alone: true);
        }
        foreach ((_, uint entry) in _fledFrom) _recentRetreatEntries.Add(entry);
        _verdicts.Clear(); // weighed before the loss: the memory now says more about every candidate near it or of its kind
        Hazards.Add(_fledAt.MapId, _fledAt.Where, PlayerbotHazards.RetreatYards, now, _options.Risk.DangerMemorySeconds, "retreat");
        if (repeatCamp)
            Hazards.Add(_fledAt.MapId, _fledAt.Where, RetreatCampYards, now, _options.Risk.DangerMemorySeconds, PlayerbotHazards.DeathLoopKind);
        RememberLairs(player, _fledFrom);
    }

    /// <summary>
    /// The creatures just remembered (fled from or died to) are kept out of at once, each at its home with its aggro reach plus
    /// <see cref="PlayerbotHazards.FledMarginYards"/> (kind "fled"), as the hazard scan keeps a remembered creature it sees. The scan
    /// follows the creature from then on while it sees it; one it never sees again is put back at its home
    /// (<see cref="RestoreUnseenLairs"/>), where it evades to. A stealthed ambusher is the case: it evades home and hides again, out of
    /// the bot's sight and, from more than <see cref="PlayerbotRecovery.StealthedHazardYards"/>, of the scan's stealthed pass too. A look
    /// that had followed it out of the lair used to leave the hazard there, so a later walk went straight through the spawn (live
    /// 2026-10-16: Nightsaber Stalkers, 2043, killed bots that already remembered them). A creature that loses its target evades home
    /// (vmangos <c>CreatureAI::EnterEvadeMode</c>, as <see cref="PlayerbotRetreat"/> notes).
    /// </summary>
    private void RememberLairs(Player player, IEnumerable<(ObjectGuid Guid, uint Entry)> creatures)
    {
        if (player.Map is not { } map) return;
        uint now = _session.World.NowMs;
        foreach ((ObjectGuid guid, uint _) in creatures)
        {
            if (map.FindObject(guid) is not Creature creature || !ReferenceEquals(creature.Map, map)) continue;
            NoteLair(guid);
            AddFledLair(player, creature, now);
        }
    }

    /// <summary>A "fled" hazard on <paramref name="creature"/>'s home (where it evades to), its aggro reach plus the fled margin.</summary>
    private void AddFledLair(Player player, Creature creature, uint now)
    {
        CreatureMapSystem? system = player.Map?.FindUpdater<CreatureMapSystem>();
        float reach = (system?.GetAttackDistance(creature, player) ?? CreatureTemplate.DefaultDetectionRange)
            + creature.BoundingRadius + player.BoundingRadius;
        float radius = reach + PlayerbotHazards.FledMarginYards;
        Hazards.Add(player.MapId, new Vector3(creature.X, creature.Y, creature.Z), radius,
            now, _options.Risk.DangerMemorySeconds, "fled", creature.Guid);
        Hazards.Add(player.MapId, new Vector3(creature.Home.X, creature.Home.Y, creature.Home.Z), radius,
            now, _options.Risk.DangerMemorySeconds, "fled", creature.Guid);
    }

    private struct LairMark
    {
        internal ObjectGuid Guid;
        internal uint Seen;
    }

    /// <summary>Lairs written by <see cref="RememberLairs"/> (bounded like the danger memory). <see cref="LairMark.Seen"/> is the scan epoch.</summary>
    private readonly LairMark[] _lairs = new LairMark[32];

    private int _lairCount;
    private uint _lairEpoch;

    private void NoteLair(ObjectGuid guid)
    {
        for (int i = 0; i < _lairCount; i++)
            if (_lairs[i].Guid == guid) return;
        if (_lairCount >= _lairs.Length)
        {
            Array.Copy(_lairs, 1, _lairs, 0, _lairCount - 1);
            _lairCount--;
        }

        _lairs[_lairCount++] = new LairMark { Guid = guid };
    }

    private void MarkLairSeen(ObjectGuid guid)
    {
        for (int i = 0; i < _lairCount; i++)
            if (_lairs[i].Guid == guid)
            {
                _lairs[i].Seen = _lairEpoch;
                return;
            }
    }

    /// <summary>
    /// Remembered creatures this scan did not see: the hazard goes back to the lair. One the scan did see keeps the hazard just written
    /// where it stands. A guid the danger memory has dropped leaves the list (no allocation; the scan runs for every bot).
    /// </summary>
    private void RestoreUnseenLairs(Player player, uint now)
    {
        if (_lairCount == 0 || player.Map is not { } map) return;
        int kept = 0;
        for (int i = 0; i < _lairCount; i++)
        {
            LairMark lair = _lairs[i];
            if (!Memory.IsRemembered(lair.Guid, now)) continue;
            if (kept != i) _lairs[kept] = lair;
            kept++;
            if (lair.Seen == _lairEpoch) continue;
            if (map.FindObject(lair.Guid) is not Creature creature || !creature.IsAlive || !ReferenceEquals(creature.Map, map)) continue;
            AddFledLair(player, creature, now);
        }

        _lairCount = kept;
    }

    /// <summary>
    /// Retreat from <paramref name="enemies"/> (also the party AI's wipe). When it ends the bot remembers them and the place where
    /// the fight turned, from then on (a death on the way is remembered by <see cref="OnDeath"/>).
    /// </summary>
    internal void StartRetreat(Player player, IReadOnlyList<Creature> enemies, string reason)
    {
        _fledFrom = [.. enemies.Select(c => (c.Guid, c.Entry))];
        _fledAt = (player.MapId, new Vector3(player.X, player.Y, player.Z));
        foreach (Creature enemy in enemies)
        {
            _verdicts.Remove((enemy.Guid, false));
            _verdicts.Remove((enemy.Guid, true));
        }
        Detour = null;
        _refusedPending = false;
        _activePlan = _retreatPlan;
        _retreatPlan = null;
        _retreatFrom = new Vector3(player.X, player.Y, player.Z);
        Retreat.Start(player, enemies, Tracker.Anchors, Breadcrumbs.Points, reason);
        bool sameFightSpot = !_lastRetreat.Valid || (_lastRetreat.MapId == _fledAt.MapId
            && Vector3.Distance(_lastRetreat.At, _fledAt.Where) <= PlayerbotHazards.RetreatYards);
        if (_pulledTargetSinceRetreat && sameFightSpot)
            _retreatLoop.Started(player, _session.World.NowMs, reason);
        _pulledTargetSinceRetreat = false;
    }

    /// <summary>One think of a running retreat; true while it owns the bot. A retreat that got away starts the recovery wait.</summary>
    internal bool UpdateRetreat(Player player, uint interval)
    {
        if (!Retreat.Active) return false;
        // The attackers while the bot still lives: at its death combat is already over (the retreat-died line, QA-13).
        if (player.IsAlive && Enabled) _retreatAttackers = DescribeAttackers(player);
        if (Retreat.Update(player, interval)) return true;
        NoteRetreatEnd(player, Retreat.Outcome);
        Tracker.End(PlayerbotRiskModel.PriorBotDps(player.Level));
        if (Retreat.Outcome == "safe") BeginWait(player);
        else if (Retreat.Outcome == "cornered")
        {
            _cornered = true;
            PlayerbotAnomalies.Report(player, "risk", "retreat-cornered",
                $"retreat for '{Retreat.Reason}' ended with the bot still threatened (gave up after {Retreat.UsedEscapes.Count} escapes); it fights on",
                AnomalySeverity.Info);
        }
        return false;
    }
}

/// <summary>
/// A quest objective too strong for the bot alone, feasible for <paramref name="Size"/> players (<see cref="PlayerbotRisk.GroupSignal"/>).
/// <paramref name="Source"/> says who raised it: "risk" (the pull verdict) or "contention" (another player's tag on the same quest creature, or
/// no credit on it, <see cref="PlayerbotContention"/>); the group goal carries it.
/// </summary>
internal readonly record struct PlayerbotGroupSignal(uint Entry, int Size, uint MapId, Vector3 At, uint AtMs, string Source = PlayerbotGroupSignal.RiskSource, uint QuestId = 0)
{
    internal const string RiskSource = "risk";

    internal const string ContentionSource = "contention";
}

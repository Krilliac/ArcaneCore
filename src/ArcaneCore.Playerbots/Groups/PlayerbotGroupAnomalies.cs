using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Playerbots.Party;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>
/// The group coordinator's outcome checks (area <c>group</c>, subsystem <c>bot-group</c>): a bot-led group, a dungeon or raid run or a
/// member that did not do what the group's own state says it should. The coordinator already ends a group that overruns a step bound;
/// these checks name what no bound catches (a state whose clock is reset, a run step that never advances, a member nobody waits for) and
/// the post-conditions of its own actions (an invitation sent, a member released). The group-level ones are reported against the
/// group's leader, the member-level ones against the member, so <see cref="PlayerbotAnomalies.CaptureFor"/> finds them by bot.
/// Cost: a state compare per group per tick and one pass over a group's members once a second.
/// </summary>
internal static class PlayerbotGroupAnomalies
{
    /// <summary>The area of every key here.</summary>
    internal const string Area = "group";

    /// <summary>A group stayed in one state past that state's budget (see <see cref="PlayerbotGroupWatch.StateBudgetMs"/>).</summary>
    internal const string StateStuck = "state-stuck";

    /// <summary>A run stayed in one machine step past that step's budget (see <see cref="PlayerbotGroupWatch.RunStepBudgetMs"/>).</summary>
    internal const string RunStepStuck = "run-step-stuck";

    /// <summary>A run's route to its bosses made no progress (no boss died) for <see cref="PlayerbotGroupWatch.RouteStepBudgetMs"/>.</summary>
    internal const string RouteStepIncomplete = "route-step-incomplete";

    /// <summary>The leader invited a bot that never joined: the invitation was not delivered or refused, or went unanswered.</summary>
    internal const string InviteUnanswered = "invite-unanswered";

    /// <summary>A living member stayed on another map, or far from the leader, while the group travelled or fought.</summary>
    internal const string MemberLeftBehind = "member-left-behind";

    /// <summary>A member stayed dead for <see cref="PlayerbotGroupWatch.MemberRecoveryBudgetMs"/> while its group went on.</summary>
    internal const string MemberUnrecovered = "member-unrecovered";

    /// <summary>The group's leader logged out, left or stopped, and the group ended (expected now and then, worth counting).</summary>
    internal const string LeaderGone = "leader-gone";

    /// <summary>A member released from a finished group is still in the server's group (the leave did not take).</summary>
    internal const string MemberStillGrouped = "member-still-grouped";

    /// <summary>An unhandled exception escaped while advancing a bot-led group on the coordinator tick.</summary>
    internal const string CoordinatorFault = "coordinator-fault";

    /// <summary>An unhandled exception escaped during a player-to-bot leader handoff.</summary>
    internal const string HandoffFault = "handoff-fault";

    /// <summary>An unhandled exception escaped while loading run encounters or reporting a run failure.</summary>
    internal const string RunFault = "run-fault";

    /// <summary>An unhandled exception escaped while ticking an active siege.</summary>
    internal const string SiegeFault = "siege-fault";

    /// <summary>A world-boss raid pulled while some, but not enough to hold the pull, real players stood at the boss.</summary>
    internal const string WorldBossPullNearPlayers = "worldboss-pull-near-players";

    /// <summary>A world-boss raid at full size wiped as often as it may (its wipe cap): the boss is out of its reach.</summary>
    internal const string WorldBossWipedAtFullSize = "worldboss-wiped-full-size";

    /// <summary>A world-boss raid holds the tap of a corpse it never saw alive (a member killed the boss before the raid's first look at it).</summary>
    internal const string WorldBossCorpseTapUnseen = "worldboss-corpse-tap-unseen";

    /// <summary>The ledger key of <paramref name="code"/> (tests).</summary>
    internal static string Key(string code) => PlayerbotAnomalies.Key(Area, code);

    /// <summary>Report <paramref name="code"/> against <paramref name="bot"/> (failure path only).</summary>
    internal static void Report(Player bot, string code, string detail, AnomalySeverity severity = AnomalySeverity.Warning)
        => PlayerbotAnomalies.Report(bot, Area, code, detail, severity);

    /// <summary>
    /// Report <paramref name="code"/> against a bot that has no <see cref="Player"/> to read a position from (offline, or gone). The subject
    /// is written as <see cref="PlayerbotAnomalies.SubjectOf"/> writes it, so one bot is one subject whichever way it was reported.
    /// </summary>
    internal static void ReportNamed(string name, ObjectGuid guid, string code, string detail, AnomalySeverity severity = AnomalySeverity.Warning, object? origin = null)
        => AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + Area, code,
            string.Create(CultureInfo.InvariantCulture, $"bot {name} (guid {guid.Value:X})"), detail, severity, origin);

    /// <summary>Report a group-system anomaly without a single bot subject (or for a server group / siege).</summary>
    internal static void ReportSystem(string code, string subject, string detail, object? origin = null, AnomalySeverity severity = AnomalySeverity.Warning)
        => AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + Area, code, subject, detail, severity, origin);
}

/// <summary>What <see cref="PlayerbotGroupWatch.Observe"/> found this tick (flags: several can hold at once).</summary>
[Flags]
internal enum PlayerbotGroupFinding : byte
{
    None = 0,

    /// <summary>The group stayed in its state past the state's budget.</summary>
    StateStuck = 1,

    /// <summary>The run stayed in its step past the step's budget.</summary>
    RunStepStuck = 2,

    /// <summary>No boss died on the run's route for the route budget.</summary>
    RouteStepIncomplete = 4,
}

/// <summary>
/// The per-group clocks behind <c>group/state-stuck</c>, <c>group/run-step-stuck</c> and <c>group/route-step-incomplete</c> (pure
/// bookkeeping, world thread; one per <see cref="PlayerbotGroupCoordinator.BotGroup"/>). Times are the coordinator's 32-bit world
/// milliseconds; every difference is wrap-safe.
/// <para>
/// State: the dwell is measured from the tick the state was entered (<see cref="PlayerbotGroupCoordinator.BotGroup.StateEnteredMs"/>, which a
/// step's own retry such as <c>BringOut</c> does not reset - it resets the coordinator's <c>StateSinceMs</c>), against
/// <see cref="StateBudgetMs"/>: twice the step limit plus <see cref="GraceMs"/> for the steps the coordinator gives up on at the limit (so it
/// only reports a step whose give-up did not happen), the goal's own bound for engaging, <see cref="FormingBudgetMs"/> for forming (which has no
/// bound but its invitations) and <see cref="WipedBudgetMs"/> for a wipe. Reported once per entry into the state.
/// </para>
/// <para>
/// Run step: the dwell of <see cref="PlayerbotRunMachine.Step"/> against <see cref="RunStepBudgetMs"/> (travel and retry: twice the travel
/// budget; enter: twice the step limit; wiped: <see cref="WipedBudgetMs"/>; leave: the loot wait plus twice the step limit); clear and boss are
/// the route's. Once per entry into the step.
/// </para>
/// <para>
/// Route: in clear or boss, no boss died (<see cref="PlayerbotRunMachine.Killed"/> unchanged) for <see cref="RouteStepBudgetMs"/>. A wipe, a
/// retry or a boss death starts the clock over. Once per boss.
/// </para>
/// </summary>
internal sealed class PlayerbotGroupWatch
{
    /// <summary>Slack added to every bound the coordinator enforces itself, so the check reports the give-up that did not happen, not the one about to.</summary>
    internal const uint GraceMs = 60_000;

    /// <summary>Forming has no bound but its invitations (<see cref="PlayerbotGroupCoordinator.InviteTimeoutMs"/> each, one batch after another).</summary>
    internal const uint FormingBudgetMs = 300_000;

    /// <summary>A wipe lasts <see cref="PlayerbotGroupCoordinator.WipeSettleMs"/> and until nobody is in combat; nothing else bounds it but the goal.</summary>
    internal const uint WipedBudgetMs = 300_000;

    /// <summary>No boss dying for this long on a run's route is a route that is not being completed.</summary>
    internal const uint RouteStepBudgetMs = 1_800_000;

    /// <summary>A member dead this long (after the wait for a resurrection and a corpse run) while the group goes on has not recovered.</summary>
    internal const long MemberRecoveryBudgetMs = 15 * 60_000L;

    /// <summary>A living member this long on another map or far from the leader while the group travels or fights has been left behind.</summary>
    internal const long MemberLeftBehindBudgetMs = 300_000;

    /// <summary>"Far from the leader" for <see cref="MemberLeftBehindBudgetMs"/>.</summary>
    internal const float LeftBehindYards = 150f;

    /// <summary>The members are looked over at most this often per group.</summary>
    internal const uint MemberPassMs = 1_000;

    /// <summary>Observations further apart than this do not belong to one episode.</summary>
    internal const long MaxGapMs = 60_000;

    private bool _stateSeen;
    private uint _stateEntered;
    private bool _stateReported;
    private bool _runSeen;
    private PlayerbotRunStep _runStep;
    private uint _runStepSince;
    private bool _runStepReported;
    private int _killed;
    private uint _routeSince;
    private bool _routeReported;
    private bool _passed;
    private uint _lastPass;

    /// <summary>How long, at most, a group may stay in <paramref name="state"/> (<paramref name="stepLimitMs"/> is the coordinator's step limit now).</summary>
    internal static uint StateBudgetMs(PlayerbotGroupState state, uint stepLimitMs, uint goalTimeoutMs) => state switch
    {
        PlayerbotGroupState.Forming => FormingBudgetMs,
        PlayerbotGroupState.Engaging => Add(goalTimeoutMs, GraceMs),
        PlayerbotGroupState.Wiped => WipedBudgetMs,
        PlayerbotGroupState.Disbanding => GraceMs,
        _ => Add(Twice(stepLimitMs), GraceMs),
    };

    /// <summary>How long, at most, a run may stay in <paramref name="step"/> (0: the step is the route's, <see cref="RouteStepBudgetMs"/>).</summary>
    internal static uint RunStepBudgetMs(PlayerbotRunStep step, uint travelLimitMs, uint stepLimitMs) => step switch
    {
        PlayerbotRunStep.Travel or PlayerbotRunStep.Retry => Add(Twice(travelLimitMs), GraceMs),
        PlayerbotRunStep.Enter => Add(Twice(stepLimitMs), GraceMs),
        PlayerbotRunStep.Wiped => WipedBudgetMs,
        PlayerbotRunStep.Leave => Add(PlayerbotGroupCoordinator.LootSettleMs, Add(Twice(stepLimitMs), GraceMs)),
        PlayerbotRunStep.Complete or PlayerbotRunStep.GiveUp => GraceMs,
        _ => 0,
    };

    private static uint Twice(uint value) => value > uint.MaxValue / 2 ? uint.MaxValue : value * 2;

    private static uint Add(uint a, uint b) => a > uint.MaxValue - b ? uint.MaxValue : a + b;

    /// <summary>Look at the group once per coordinator tick (cost: a few compares while all is well).</summary>
    /// <param name="state">The group's state now.</param>
    /// <param name="stateEnteredMs">When the group entered that state.</param>
    /// <param name="stepLimitMs">The coordinator's step limit for this state now.</param>
    /// <param name="goalTimeoutMs">The coordinator's overall bound for this group now.</param>
    /// <param name="travelLimitMs">A run's travel budget (<c>TravelTimeoutMinutes</c>).</param>
    /// <param name="run">The run's machine, or null for a quest group.</param>
    /// <param name="nowMs">The coordinator's clock.</param>
    internal PlayerbotGroupFinding Observe(PlayerbotGroupState state, uint stateEnteredMs, uint stepLimitMs, uint goalTimeoutMs, uint travelLimitMs,
        PlayerbotRunMachine? run, uint nowMs)
    {
        PlayerbotGroupFinding found = PlayerbotGroupFinding.None;
        if (!_stateSeen || stateEnteredMs != _stateEntered)
        {
            _stateSeen = true;
            _stateEntered = stateEnteredMs;
            _stateReported = false;
        }

        if (!_stateReported && unchecked(nowMs - stateEnteredMs) > StateBudgetMs(state, stepLimitMs, goalTimeoutMs))
        {
            _stateReported = true;
            found |= PlayerbotGroupFinding.StateStuck;
        }

        if (run is null)
        {
            _runSeen = false;
            return found;
        }

        if (!_runSeen || run.Step != _runStep)
        {
            _runSeen = true;
            _runStep = run.Step;
            _runStepSince = nowMs;
            _runStepReported = false;
            _routeSince = nowMs;
            _routeReported = false;
            _killed = run.Killed;
        }

        uint stepBudget = RunStepBudgetMs(run.Step, travelLimitMs, PlayerbotGroupCoordinator.StepTimeoutMs);
        if (stepBudget != 0 && !_runStepReported && unchecked(nowMs - _runStepSince) > stepBudget)
        {
            _runStepReported = true;
            found |= PlayerbotGroupFinding.RunStepStuck;
        }

        if (run.Killed != _killed)
        {
            _killed = run.Killed;
            _routeSince = nowMs;
            _routeReported = false;
        }

        if (run.Step is PlayerbotRunStep.Clear or PlayerbotRunStep.Boss && !_routeReported && unchecked(nowMs - _routeSince) > RouteStepBudgetMs)
        {
            _routeReported = true;
            found |= PlayerbotGroupFinding.RouteStepIncomplete;
        }

        return found;
    }

    /// <summary>The seconds the current state has lasted (the report's detail).</summary>
    internal static uint Seconds(uint sinceMs, uint nowMs) => unchecked(nowMs - sinceMs) / 1000;

    /// <summary>Whether it is time to look over the members again (at most every <see cref="MemberPassMs"/>).</summary>
    internal bool MemberPassDue(uint nowMs)
    {
        if (_passed && unchecked(nowMs - _lastPass) < MemberPassMs) return false;
        _passed = true;
        _lastPass = nowMs;
        return true;
    }

    /// <summary>The seconds the run's current step has lasted (the report's detail).</summary>
    internal uint RunStepSeconds(uint nowMs) => unchecked(nowMs - _runStepSince) / 1000;

    /// <summary>The seconds since the run last killed a boss or changed step (the report's detail).</summary>
    internal uint RouteSeconds(uint nowMs) => unchecked(nowMs - _routeSince) / 1000;
}

/// <summary>The clocks of one member: <c>group/member-unrecovered</c> and <c>group/member-left-behind</c> (see <see cref="PlayerbotGroupAnomalies"/>).</summary>
internal sealed class PlayerbotMemberWatch
{
    private PlayerbotEpisode _dead;
    private PlayerbotEpisode _behind;

    /// <summary>
    /// Whether <paramref name="state"/> is one where a living member is expected to keep up with its leader: the group travels or
    /// engages. Gathering, regrouping, wipes and the walk out have their own bounds, forming has no leader to keep up with.
    /// </summary>
    internal static bool KeepsUp(PlayerbotGroupState state) => state is PlayerbotGroupState.Travelling or PlayerbotGroupState.Engaging;

    /// <summary>
    /// Whether a living member is left behind: on another map instance than the leader, or farther than
    /// <see cref="PlayerbotGroupWatch.LeftBehindYards"/> on the same one. Either map unknown (between maps) is no observation.
    /// </summary>
    internal static bool IsLeftBehind(PlayerbotGroupState state, bool memberAlive, bool mapsKnown, bool sameMap, float distance)
        => KeepsUp(state) && memberAlive && mapsKnown && (!sameMap || !float.IsFinite(distance) || distance > PlayerbotGroupWatch.LeftBehindYards);

    /// <summary>One observation of the member being dead; true once per death when it lasted <see cref="PlayerbotGroupWatch.MemberRecoveryBudgetMs"/>.</summary>
    internal bool ObserveDead(bool dead, long nowMs, out long deadMs)
    {
        bool fire = _dead.Observe(dead, nowMs, PlayerbotGroupWatch.MemberRecoveryBudgetMs, PlayerbotGroupWatch.MaxGapMs);
        deadMs = _dead.ElapsedMs(nowMs);
        return fire;
    }

    /// <summary>One observation of the member being left behind; true once per episode when it lasted <see cref="PlayerbotGroupWatch.MemberLeftBehindBudgetMs"/>.</summary>
    internal bool ObserveBehind(bool behind, long nowMs, out long behindMs)
    {
        bool fire = _behind.Observe(behind, nowMs, PlayerbotGroupWatch.MemberLeftBehindBudgetMs, PlayerbotGroupWatch.MaxGapMs);
        behindMs = _behind.ElapsedMs(nowMs);
        return fire;
    }

    /// <summary>Forget both episodes.</summary>
    internal void Reset()
    {
        _dead.Reset();
        _behind.Reset();
    }
}

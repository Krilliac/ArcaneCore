using System.Globalization;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>Where a dungeon or raid run is.</summary>
internal enum PlayerbotRunStep : byte
{
    /// <summary>Gathering and walking to the entrance, fighting what is in the way.</summary>
    Travel,

    /// <summary>Through the entrance trigger into the instance.</summary>
    Enter,

    /// <summary>Inside, clearing the trash on the way to the next boss.</summary>
    Clear,

    /// <summary>Fighting the current boss.</summary>
    Boss,

    /// <summary>Half the group or more fell: the survivors retreat, the dead run back, everyone rests and buffs.</summary>
    Wiped,

    /// <summary>After a wipe: back at the entrance and on the way in again; the killed bosses stay killed.</summary>
    Retry,

    /// <summary>Every boss is dead: out through the exit.</summary>
    Leave,

    /// <summary>The run finished: every boss of the wing died and the group is out.</summary>
    Complete,

    /// <summary>The run ended without finishing (<see cref="PlayerbotRunMachine.Reason"/> says why).</summary>
    GiveUp,
}

/// <summary>What the run's group observed.</summary>
internal enum PlayerbotRunEvent : byte
{
    /// <summary>The group stands at the entrance approach.</summary>
    Arrived,

    /// <summary>Every member is inside the instance.</summary>
    AllInside,

    /// <summary>The pack the group pulled is dead and nothing else is pulled yet (kept for the report: no step change).</summary>
    PackDead,

    /// <summary>The tank engaged the current boss.</summary>
    BossEngaged,

    /// <summary>A boss died (or was found already dead): <see cref="PlayerbotRunSignal.Entry"/>.</summary>
    BossDead,

    /// <summary>The group wiped.</summary>
    Wipe,

    /// <summary>After a wipe, everyone is alive, rested and gathered.</summary>
    Regrouped,

    /// <summary>Every member is out of the instance.</summary>
    Left,

    /// <summary>No way: <see cref="PlayerbotRunSignal.Text"/> says why (no path to the boss, no entrance).</summary>
    NoPath,

    /// <summary>A step took too long: <see cref="PlayerbotRunSignal.Text"/> names it (travel, enter, run).</summary>
    Timeout,

    /// <summary>The group ended the run for another reason: <see cref="PlayerbotRunSignal.Text"/>.</summary>
    Abort,
}

/// <summary>An event with its argument.</summary>
/// <param name="Event">What happened.</param>
/// <param name="Entry">The boss entry (<see cref="PlayerbotRunEvent.BossDead"/>).</param>
/// <param name="Text">The reason (<see cref="PlayerbotRunEvent.NoPath"/>, <see cref="PlayerbotRunEvent.Timeout"/>).</param>
internal readonly record struct PlayerbotRunSignal(PlayerbotRunEvent Event, uint Entry = 0, string? Text = null)
{
    public static PlayerbotRunSignal Arrived => new(PlayerbotRunEvent.Arrived);
    public static PlayerbotRunSignal AllInside => new(PlayerbotRunEvent.AllInside);
    public static PlayerbotRunSignal PackDead => new(PlayerbotRunEvent.PackDead);
    public static PlayerbotRunSignal BossEngaged => new(PlayerbotRunEvent.BossEngaged);
    public static PlayerbotRunSignal BossDead(uint entry) => new(PlayerbotRunEvent.BossDead, entry);
    public static PlayerbotRunSignal Wipe => new(PlayerbotRunEvent.Wipe);
    public static PlayerbotRunSignal Regrouped => new(PlayerbotRunEvent.Regrouped);
    public static PlayerbotRunSignal Left => new(PlayerbotRunEvent.Left);
    public static PlayerbotRunSignal NoPath(string reason) => new(PlayerbotRunEvent.NoPath, 0, reason);
    public static PlayerbotRunSignal Timeout(string kind) => new(PlayerbotRunEvent.Timeout, 0, kind);
    public static PlayerbotRunSignal Abort(string reason) => new(PlayerbotRunEvent.Abort, 0, reason);
}

/// <summary>
/// The state of one dungeon or raid run: the step, which of the plan's bosses are dead, the wipes. Events in, state out, so the
/// life of a run is testable without a world; <see cref="PlayerbotGroupCoordinator"/> feeds it what the group observes and acts on its
/// <see cref="Step"/>. Killed bosses stay killed across wipes: a retry resumes at the first boss still alive. The wiped step also
/// watches the clock (<see cref="NoteTime"/>): it takes the recovery transition itself once its budget has gone, so a regroup that
/// never finishes cannot leave the run sitting in <see cref="PlayerbotRunStep.Wiped"/>. World thread only.
/// </summary>
internal sealed class PlayerbotRunMachine(PlayerbotRunPlan plan, int maxWipes)
{
    private readonly HashSet<uint> _killed = [];
    private bool _timed;
    private PlayerbotRunStep _timedStep;
    private uint _stepSinceMs;

    /// <summary>The plan being run.</summary>
    public PlayerbotRunPlan Plan { get; } = plan ?? throw new ArgumentNullException(nameof(plan));

    /// <summary>Where the run is.</summary>
    public PlayerbotRunStep Step { get; private set; } = PlayerbotRunStep.Travel;

    /// <summary>Wipes so far.</summary>
    public int Wipes { get; private set; }

    /// <summary>The wipes the run survives; it is given up at this many.</summary>
    public int MaxWipes { get; set; } = maxWipes;

    /// <summary>Why the run was given up (null otherwise).</summary>
    public string? Reason { get; private set; }

    /// <summary>The step the run was in when it was given up (<see cref="Step"/> is <see cref="PlayerbotRunStep.GiveUp"/> from then on).</summary>
    public PlayerbotRunStep FailedStep { get; private set; }

    /// <summary>The bosses the plan routes.</summary>
    public int BossCount => Plan.Bosses.Count;

    /// <summary>The bosses dead so far.</summary>
    public int Killed => _killed.Count;

    /// <summary>Packs left in sight along the way to the current boss (set by the group AI, report only).</summary>
    public int PullsLeft { get; set; }

    /// <summary>The first boss still alive, or null when all are dead.</summary>
    public PlayerbotRunBoss? CurrentBoss => Plan.Bosses.FirstOrDefault(b => !_killed.Contains(b.Entry));

    /// <summary>Index of <see cref="CurrentBoss"/> in the route (<see cref="BossCount"/> when all are dead).</summary>
    public int BossIndex
    {
        get
        {
            for (int index = 0; index < Plan.Bosses.Count; index++)
                if (!_killed.Contains(Plan.Bosses[index].Entry)) return index;
            return Plan.Bosses.Count;
        }
    }

    /// <summary>Every routed boss is dead.</summary>
    public bool AllBossesDead => BossIndex >= BossCount;

    /// <summary>The run finished successfully.</summary>
    public bool Complete => Step == PlayerbotRunStep.Complete;

    /// <summary>The run is over (finished or given up).</summary>
    public bool Finished => Step is PlayerbotRunStep.Complete or PlayerbotRunStep.GiveUp;

    /// <summary>Whether the boss with <paramref name="entry"/> is dead.</summary>
    public bool IsKilled(uint entry) => _killed.Contains(entry);

    /// <summary>The step as the report shows it.</summary>
    public string StepName => Step.ToString().ToLowerInvariant();

    /// <summary>Apply what the group observed. Events that make no sense in the current step are ignored; a finished run ignores everything.</summary>
    public void Apply(PlayerbotRunSignal signal)
    {
        if (Finished) return;
        switch (signal.Event)
        {
            case PlayerbotRunEvent.Arrived:
                if (Step is PlayerbotRunStep.Travel or PlayerbotRunStep.Retry) Step = PlayerbotRunStep.Enter;
                break;
            case PlayerbotRunEvent.AllInside:
                if (Step == PlayerbotRunStep.Enter) Step = AllBossesDead ? PlayerbotRunStep.Leave : PlayerbotRunStep.Clear;
                break;
            case PlayerbotRunEvent.PackDead:
                break;
            case PlayerbotRunEvent.BossEngaged:
                if (Step == PlayerbotRunStep.Clear) Step = PlayerbotRunStep.Boss;
                break;
            case PlayerbotRunEvent.BossDead:
                if (Plan.Bosses.Any(b => b.Entry == signal.Entry) && Step is PlayerbotRunStep.Enter or PlayerbotRunStep.Clear or PlayerbotRunStep.Boss)
                {
                    _killed.Add(signal.Entry);
                    Step = AllBossesDead ? PlayerbotRunStep.Leave : PlayerbotRunStep.Clear;
                }

                break;
            case PlayerbotRunEvent.Wipe:
                if (Step == PlayerbotRunStep.Wiped) break;
                Wipes++;
                if (Wipes >= MaxWipes) GiveUp("wiped");
                else Step = PlayerbotRunStep.Wiped;
                break;
            case PlayerbotRunEvent.Regrouped:
                if (Step == PlayerbotRunStep.Wiped) Step = PlayerbotRunStep.Retry;
                break;
            case PlayerbotRunEvent.Left:
                if (Step == PlayerbotRunStep.Leave) Step = PlayerbotRunStep.Complete;
                break;
            case PlayerbotRunEvent.NoPath:
                GiveUp(string.IsNullOrEmpty(signal.Text) ? "no-path" : signal.Text);
                break;
            case PlayerbotRunEvent.Timeout:
                GiveUp(string.Create(CultureInfo.InvariantCulture, $"{signal.Text ?? "run"}-timeout"));
                break;
            case PlayerbotRunEvent.Abort:
                GiveUp(string.IsNullOrEmpty(signal.Text) ? "aborted" : signal.Text);
                break;
        }
    }

    /// <summary>
    /// One coordinator tick. The first time a step is seen, the clock starts; a later tick whose wiped step has lasted
    /// <paramref name="wipedBudgetMs"/> takes <see cref="PlayerbotRunSignal.Regrouped"/> (the recovery transition to
    /// <see cref="PlayerbotRunStep.Retry"/>). Without this, the step stays <see cref="PlayerbotRunStep.Wiped"/> until every member is
    /// alive and rested, while a run's regroup is allowed the travel budget, so the step sits past its own budget
    /// (<c>group/run-step-stuck</c>). A finished run, and any step but wiped, is left as it is. A new entry into wiped starts the
    /// budget over.
    /// </summary>
    public void NoteTime(uint nowMs, uint wipedBudgetMs)
    {
        if (!_timed || _timedStep != Step)
        {
            _timed = true;
            _timedStep = Step;
            _stepSinceMs = nowMs;
            return;
        }

        if (Step != PlayerbotRunStep.Wiped || unchecked(nowMs - _stepSinceMs) < wipedBudgetMs) return;
        Apply(PlayerbotRunSignal.Regrouped);
        _timedStep = Step;
        _stepSinceMs = nowMs;
    }

    private void GiveUp(string reason)
    {
        FailedStep = Step;
        Step = PlayerbotRunStep.GiveUp;
        Reason = reason;
    }
}

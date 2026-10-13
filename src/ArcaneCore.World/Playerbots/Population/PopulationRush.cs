using System.Globalization;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>What the brake of a rush did at an observation.</summary>
internal enum PopulationRushBrakeChange { None, Paused, Resumed }

/// <summary>
/// The safety brake of <c>.playerbot population rush</c>. Time is real: a window is <c>RushBrakeWindowSeconds</c> seconds of the
/// monotonic clock (the caller passes it in), however fast or slow the world ticks. The tick p95 over the ticks that arrived in a
/// window above the ceiling (<see cref="PlayerbotPopulationOptions.RushCeilingMs"/>) in <c>RushBrakeWindows</c> consecutive windows
/// pauses the rush; it resumes after as many consecutive windows under <c>ResumeBelowPct</c> of the ceiling. A slow world therefore
/// trips the brake after the same number of seconds as a fast one, not after the same number of ticks.
/// <para>Thread affinity: the world thread (the population feature's update).</para>
/// </summary>
internal sealed class PopulationRushBrake
{
    private long _windowStartMs = -1;
    private long _windowStartTicks;

    public bool Paused { get; private set; }

    /// <summary>Consecutive windows over the ceiling while running, or under the resume line while paused.</summary>
    public int Windows { get; private set; }

    /// <summary>The ticks that arrived since the current window began.</summary>
    public long TicksInWindow(long totalTicks) => Math.Max(0, totalTicks - _windowStartTicks);

    /// <summary>
    /// Whether the current window has run <paramref name="windowMs"/> and saw at least one tick (cheap: no sorting). The first call
    /// only starts the first window.
    /// </summary>
    public bool WindowDue(long nowMs, long totalTicks, int windowMs)
    {
        if (_windowStartMs < 0)
        {
            (_windowStartMs, _windowStartTicks) = (nowMs, totalTicks);
            return false;
        }

        return nowMs - _windowStartMs >= windowMs && totalTicks > _windowStartTicks;
    }

    public PopulationRushBrakeChange Observe(long nowMs, long totalTicks, double p95Ms, int ceilingMs, PlayerbotPopulationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        (_windowStartMs, _windowStartTicks) = (nowMs, totalTicks);
        if (!Paused)
        {
            Windows = p95Ms > ceilingMs ? Windows + 1 : 0;
            if (Windows < options.RushBrakeWindows) return PopulationRushBrakeChange.None;
            Paused = true;
            Windows = 0;
            return PopulationRushBrakeChange.Paused;
        }

        Windows = p95Ms < ceilingMs * options.ResumeBelowPct / 100.0 ? Windows + 1 : 0;
        if (Windows < options.RushBrakeWindows) return PopulationRushBrakeChange.None;
        Paused = false;
        Windows = 0;
        return PopulationRushBrakeChange.Resumed;
    }
}

/// <summary>Turns elapsed world time into whole bots to create at <c>perSecond</c>; a burst never exceeds two seconds' worth.</summary>
internal sealed class PopulationRushPacer
{
    private double _tokens;

    public void Add(uint elapsedMs, int perSecond)
    {
        double cap = Math.Max(1, perSecond) * 2.0;
        _tokens = Math.Min(cap, _tokens + (elapsedMs * (double)perSecond / 1000.0));
    }

    /// <summary>Take up to <paramref name="most"/> whole tokens.</summary>
    public int Take(int most)
    {
        int whole = (int)Math.Min(_tokens, most);
        _tokens -= whole;
        return whole;
    }

    public void Clear() => _tokens = 0;
}

/// <summary>How a rush stands. Immutable; read from any thread.</summary>
public sealed record PopulationRushStatus(string State, int Requested, int Created, int Failed, int PerSecond, double AchievedPerSecond,
    string Reason, int BrakeP95Ms, int BrakeWindows, int BrakeWindowsSeen);

/// <summary>
/// One <c>.playerbot population rush</c>: how many bots are wanted, how fast, how many exist so far, and why it ended. Created by the
/// command, advanced by the population feature (world thread: pacing and brake; pass thread: results), so every field is behind one lock.
/// </summary>
internal sealed class PopulationRush
{
    /// <summary>The most bots one rush pass creates (a pass creates them one after another).</summary>
    public const int MaxPerPass = 50;

    /// <summary>Why a pass stopped creating because the brake paused the rush; not a reason for the rush to end.</summary>
    public const string PausedNote = "rush paused";

    /// <summary>
    /// What a pass says when every start area is at <c>FreshStartZoneCap</c>: creation is deferred, not failed. The rush stays running
    /// (its reason says so) and goes on as bots graduate; an operator stops it with <c>.playerbot population rush stop</c>.
    /// </summary>
    public const string StartZonesFullNote = "start zones full";

    /// <summary>The most passes in a row that create nothing for a reason no cap explains before the rush gives up.</summary>
    public const int MaxFruitlessPasses = 3;

    private readonly object _gate = new();
    private string _state = "idle";
    private string _reason = "no rush has run";
    private int _requested, _created, _failed, _perSecond, _fruitless, _brakeWindows, _brakeCeiling;
    private long _startedMs, _endedMs;
    private bool _stopRequested;

    public PopulationRushPacer Pacer { get; } = new();

    public PopulationRushBrake Brake { get; private set; } = new();

    /// <summary>Running or paused by the brake (a pass may be due).</summary>
    public bool Active
    {
        get { lock (_gate) return _state is "running" or "paused"; }
    }

    public bool Paused
    {
        get { lock (_gate) return _state == "paused"; }
    }

    public bool StopRequested
    {
        get { lock (_gate) return _stopRequested; }
    }

    /// <summary>Why creation must stop now (checked between bots inside a pass), or null.</summary>
    public string? CreationBlock
    {
        get
        {
            lock (_gate) return _stopRequested ? "rush stopped" : _state == "paused" ? PausedNote : null;
        }
    }

    public int Remaining
    {
        get { lock (_gate) return Math.Max(0, _requested - _created); }
    }

    public int PerSecond
    {
        get { lock (_gate) return _perSecond; }
    }

    public void Begin(int count, int perSecond, long nowMs)
    {
        lock (_gate)
        {
            (_state, _reason, _requested, _created, _failed, _perSecond, _fruitless) = ("running", "creating bots", count, 0, 0, perSecond, 0);
            (_startedMs, _endedMs, _stopRequested) = (nowMs, 0, false);
            Pacer.Clear();
            Brake = new PopulationRushBrake();
        }
    }

    public void RequestStop(string reason, long nowMs)
    {
        lock (_gate)
        {
            if (_state is not ("running" or "paused")) return;
            _stopRequested = true;
            (_state, _reason, _endedMs) = ("stopped", reason, nowMs);
        }
    }

    public void Pause(string reason)
    {
        lock (_gate)
        {
            if (_state == "running") (_state, _reason) = ("paused", reason);
        }
    }

    public void Resume(string reason)
    {
        lock (_gate)
        {
            if (_state == "paused") (_state, _reason) = ("running", reason);
        }
    }

    public void SetBrake(int ceilingMs, int windows)
    {
        lock (_gate) (_brakeCeiling, _brakeWindows) = (ceilingMs, windows);
    }

    /// <summary>
    /// A pass asked for <paramref name="wanted"/> bots and made <paramref name="created"/>. <paramref name="note"/> is why creation
    /// stopped short (a cap, a hold), null when nothing explains a shortfall (a refusal). Returns true when the rush ended.
    /// </summary>
    public bool Report(int wanted, int created, string? note, string? lastError, long nowMs)
    {
        lock (_gate)
        {
            if (_state is not ("running" or "paused")) { _created += created; return false; }
            _created += created;
            if (_created >= _requested)
            {
                (_state, _reason, _endedMs) = ("done", string.Create(CultureInfo.InvariantCulture, $"created {_created} of {_requested}"), nowMs);
                return true;
            }

            if (created >= wanted)
            {
                _fruitless = 0;
                if (_reason.StartsWith("deferred: ", StringComparison.Ordinal)) _reason = "creating bots";
                return false;
            }

            if (note is not null && note.StartsWith(StartZonesFullNote, StringComparison.Ordinal))
            {
                (_reason, _fruitless) = ("deferred: " + note, 0);
                return false;
            }

            if (note is not null && note != PausedNote && !note.StartsWith("waiting for the graveyard", StringComparison.Ordinal))
            {
                (_state, _reason, _endedMs) = ("halted", note, nowMs);
                return true;
            }

            if (note is null)
            {
                _failed += wanted - created;
                if (created == 0 && ++_fruitless >= MaxFruitlessPasses)
                {
                    (_state, _reason, _endedMs) = ("halted", "creation keeps failing" + (lastError is null ? string.Empty : ": " + lastError), nowMs);
                    return true;
                }
            }

            return false;
        }
    }

    public PopulationRushStatus Snapshot(long nowMs)
    {
        lock (_gate)
        {
            long end = _endedMs != 0 ? _endedMs : nowMs;
            double seconds = Math.Max(0, end - _startedMs) / 1000.0;
            return new PopulationRushStatus(_state, _requested, _created, _failed, _perSecond, seconds > 0 ? _created / seconds : 0, _reason,
                _brakeCeiling, _brakeWindows, Brake.Windows);
        }
    }
}

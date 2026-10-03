namespace ArcaneCore.Game.Locomotion;

/// <summary>vmangos ShortIntervalTimer (shared/Timer.h:101-126): a counter against an interval, in milliseconds.</summary>
public sealed class ShortIntervalTimer
{
    public uint Interval { get; private set; }

    public uint Current { get; private set; }

    public void Update(uint diff) => Current += diff;

    public bool Passed => Current >= Interval;

    /// <summary>Subtract one interval if it has passed.</summary>
    public void Reset()
    {
        if (Current >= Interval)
        {
            Current -= Interval;
        }
    }

    public void SetCurrent(uint current) => Current = current;

    public void SetInterval(uint interval) => Interval = interval;
}

/// <summary>The kinds of mirror timer (vmangos MirrorTimer::Type; the first three are the client's TimerType).</summary>
public enum MirrorTimerType
{
    Fatigue = 0,
    Breath = 1,
    FeignDeath = 2,

    /// <summary>Server only (lava): never sent to the client (vmangos NUM_CLIENT_TIMERS).</summary>
    Environmental = 3,
}

/// <summary>What the client has to be told about a timer since the last <see cref="MirrorTimer.FetchStatus"/>.</summary>
public enum MirrorTimerStatus
{
    Unchanged = 0,
    FullUpdate = 1,
    StatusUpdate = 2,
}

/// <summary>
/// One mirror timer: the breath, fatigue and lava countdowns (vmangos MirrorTimer.cpp:19-150, MirrorTimer.h). A timer runs
/// out while its <see cref="Scale"/> is negative (one tick of real time costs |scale| ticks of the timer) and, when it expires,
/// <see cref="Update"/> reports a pulse once and then every two seconds; with a positive scale it regenerates at that many
/// times real time and stops at zero. The type, duration and flags are what the client's bar shows.
/// </summary>
public sealed class MirrorTimer(MirrorTimerType type)
{
    private readonly ShortIntervalTimer _tracker = new();
    private readonly ShortIntervalTimer _pulse = new();
    private MirrorTimerStatus _status;
    private bool _frozen;

    public MirrorTimerType Type { get; } = type;

    public int Scale { get; private set; } = -1;

    public uint SpellId { get; private set; }

    public bool IsActive { get; private set; }

    public bool IsRegenerating => Scale > 0;

    /// <summary>A frozen timer stands still (a game master); a regenerating one cannot be frozen.</summary>
    public bool IsFrozen => _frozen && !IsRegenerating;

    public uint Remaining => _tracker.Interval - _tracker.Current;

    public uint Duration => _tracker.Interval;

    public MirrorTimerStatus FetchStatus()
    {
        MirrorTimerStatus status = _status;
        _status = MirrorTimerStatus.Unchanged;
        return status;
    }

    public void Stop()
    {
        if (IsActive)
        {
            IsActive = false;
            _pulse.SetCurrent(0);
            _tracker.SetCurrent(0);
            _status = MirrorTimerStatus.StatusUpdate;
        }
    }

    /// <summary>Start running (only with a negative scale; otherwise it stops).</summary>
    public void Start(uint interval, uint spellId = 0)
    {
        if (Scale < 0)
        {
            IsActive = true;
            _pulse.SetCurrent(0);
            _pulse.SetInterval(2000);
            _tracker.SetCurrent(0);
            _tracker.SetInterval(interval);
            SpellId = spellId;
            _status = MirrorTimerStatus.FullUpdate;
        }
        else
        {
            Stop();
        }
    }

    public void SetRemaining(uint duration)
    {
        if (duration == 0)
        {
            Stop();
            return;
        }

        if (IsActive && duration != Remaining)
        {
            _status = MirrorTimerStatus.FullUpdate;
        }

        _tracker.SetCurrent(Duration - duration);
    }

    public void SetDuration(uint duration)
    {
        if (duration == 0)
        {
            Stop();
            return;
        }

        if (IsActive && duration != Duration)
        {
            _status = MirrorTimerStatus.FullUpdate;
        }

        _tracker.SetInterval(duration);
    }

    public void SetFrozen(bool state)
    {
        if (IsActive && state != IsFrozen)
        {
            _status = MirrorTimerStatus.StatusUpdate;
        }

        _frozen = state;
    }

    public void SetScale(int scale)
    {
        if (scale == 0)
        {
            SetFrozen(true);
            return;
        }

        if (IsActive && scale != Scale)
        {
            _status = MirrorTimerStatus.FullUpdate;
        }

        Scale = scale;
    }

    /// <summary>
    /// Advance by <paramref name="diff"/> milliseconds. Returns false when the timer pulses: when it has just run out (the
    /// instant tick on expiration) and then every two seconds while it stays expired; true otherwise.
    /// </summary>
    public bool Update(uint diff)
    {
        if (!IsActive || IsFrozen)
        {
            return true;
        }

        diff *= (uint)Math.Abs(Scale);

        if (Scale < 0)
        {
            _tracker.Update(diff);
            if (!_tracker.Passed)
            {
                return true;
            }

            uint interval = _tracker.Interval;
            uint overflow = _tracker.Current - interval;
            _tracker.SetCurrent(interval);

            if (overflow == diff)
            {
                // Pulse: subsequent ticks after the instant tick on expiration.
                _pulse.Update(overflow);
                if (!_pulse.Passed)
                {
                    return true;
                }

                _pulse.Reset();
            }

            return false;
        }

        uint current = _tracker.Current;
        if (current > diff)
        {
            _tracker.SetCurrent(current - diff);
            _pulse.SetCurrent(0);
        }
        else
        {
            Stop();
        }

        return true;
    }
}

/// <summary>The environment flags of a player (vmangos Player.h:75-86): where in the liquids it is.</summary>
[Flags]
public enum EnvironmentFlags : byte
{
    None = 0x00,

    /// <summary>Swimming or standing in water.</summary>
    InWater = 0x01,

    /// <summary>Swimming or standing in magma.</summary>
    InMagma = 0x02,

    /// <summary>Swimming or standing in slime.</summary>
    InSlime = 0x04,

    /// <summary>Anywhere inside a deep water area (fatigue).</summary>
    HighSea = 0x08,

    /// <summary>Swimming fully submerged in any liquid (breath).</summary>
    Underwater = 0x10,

    /// <summary>In any liquid deep enough to swim.</summary>
    HighLiquid = 0x20,

    /// <summary>Anywhere inside an area with any liquid.</summary>
    Liquid = 0x40,

    /// <summary>ENVIRONMENT_MASK_LIQUID_HAZARD: the liquids that hurt.</summary>
    MaskLiquidHazard = InMagma | InSlime,

    /// <summary>ENVIRONMENT_MASK_IN_LIQUID.</summary>
    MaskInLiquid = InWater | MaskLiquidHazard,

    /// <summary>ENVIRONMENT_MASK_LIQUID_FLAGS: everything the terrain query decides.</summary>
    MaskLiquidFlags = Underwater | MaskInLiquid | HighSea | Liquid | HighLiquid,
}

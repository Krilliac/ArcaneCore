namespace ArcaneCore.Game.AntiCheat;

/// <summary>What one sample fed to <see cref="SpeedHackDetector"/> decided.</summary>
/// <param name="Fire">The window ran hot long enough: report it.</param>
/// <param name="Severity">1..100, the percentage the client clock runs fast (only meaningful when <paramref name="Fire"/>).</param>
/// <param name="Ratio">The measured client/real ratio of the window (for a long-term drift fire, the ratio over the drifted span).</param>
/// <param name="LongTermDrift">The fire is the long-term drift (the accumulated excess of the client clock), not the sliding window.</param>
public readonly record struct SpeedHackDecision(bool Fire, int Severity, double Ratio, bool LongTermDrift = false);

/// <summary>
/// The independent-clock speed-hack detector, ported from the MaNGOS Zero anticheat fork
/// (feature/anticheat-detection-framework, src/gateway/SpeedHackDetector.cpp). Each movement packet is stamped with a
/// monotonic receive time the client cannot influence; over a sliding window the client's self-reported
/// <c>MovementInfo.time</c> deltas are compared with the receive deltas. A sustained ratio (client ms / real ms) above
/// 1 + tolerance is a fast clock, the speed hack that speeds the whole client up (and so passes any per-packet distance
/// check, because the client's own clock vouches for the extra distance).
/// <para>
/// Per-pair guards (as the fork): a pair received in the same millisecond carries no timing information, a pair further
/// apart than <see cref="SpeedClockOptions.MaxGapMs"/> means the player stood still or the client froze, and a single
/// client step more than MaxGapMs ahead of the real one is a desync; all three are discarded. Hysteresis: the hot counter
/// rises above the trip ratio, holds between half the tolerance and the trip ratio, and clears below; after a fire it
/// stays silent for the cooldown. Lag only stretches the real side (ratio below 1) and never fires.
/// </para>
/// <para>
/// Long-term drift (<see cref="SpeedClockOptions.LongTermDriftMs"/>, off by default): the window above only sees a clock that runs fast
/// by more than the tolerance. A client that runs a few percent fast, below the tolerance, gains time for as long as it plays. The excess is
/// measured as a span from an anchor: the client time elapsed since the anchor minus the real time elapsed since it, summed over every pair
/// including the ones the guards discard for the window (a signed client step, so a stamp slightly behind the last one counts as a small
/// negative step instead of wrapping). Because it is a difference of spans it telescopes across any stall, lag spike or catch-up burst: the
/// excess of an honest client returns to about zero once the held packets have arrived. It is floored at minus twice the limit, so a hacker
/// cannot bank unlimited negative time, and an honest stall longer than twice the limit leaves at most the part beyond it. Reaching the limit
/// (outside the cooldown) fires once and moves the anchor to the current packet.
/// </para>
/// Pure, single-player, single-threaded; times are milliseconds and wrap like the client's 32-bit clock.
/// </summary>
public sealed class SpeedHackDetector
{
    private readonly SpeedClockOptions _options;
    private readonly Sample[] _ring;
    private int _head;
    private int _count;
    private int _validCount;
    private double _sumClient;
    private double _sumReal;
    private bool _havePrevious;
    private uint _previousClient;
    private uint _previousReal;
    private int _consecutiveHot;
    private bool _inCooldown;
    private uint _cooldownUntil;
    private uint _credit;
    private long _drift;
    private long _driftRealMs;

    public SpeedHackDetector(SpeedClockOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _ring = new Sample[Math.Max(1, options.Window)];
    }

    /// <summary>The options this detector was built with (a reload builds a new detector).</summary>
    public SpeedClockOptions Options => _options;

    /// <summary>Forget everything (a teleport, a re-baseline). A reported time skip does not reset: it is credited (<see cref="Credit"/>).</summary>
    public void Reset()
    {
        Array.Clear(_ring);
        _head = 0;
        _count = 0;
        _validCount = 0;
        _sumClient = 0;
        _sumReal = 0;
        _havePrevious = false;
        _previousClient = 0;
        _previousReal = 0;
        _consecutiveHot = 0;
        _inCooldown = false;
        _cooldownUntil = 0;
        _credit = 0;
        _drift = 0;
        _driftRealMs = 0;
    }

    /// <summary>
    /// Credit skipped client time (a reported time skip, already capped by the evidence) to the next fed pair: its client
    /// delta is reduced by up to <paramref name="ms"/>, but never below its real delta, so a credit only cancels client time
    /// that ran ahead of the receive clock and can never make a pair read as slow. Credits add up until a pair consumes them.
    /// </summary>
    public void Credit(uint ms)
    {
        _credit = (uint)Math.Min(uint.MaxValue, (ulong)_credit + ms);
    }

    /// <summary>Feed one packet: the client's timestamp and the server's receive time.</summary>
    public SpeedHackDecision Feed(uint clientTime, uint receivedMs)
    {
        if (!_options.Enabled)
        {
            return default;
        }

        var sample = default(Sample);
        bool havePair = false;
        long driftStep = 0;
        if (_havePrevious)
        {
            uint clientDelta = unchecked(clientTime - _previousClient);
            uint realDelta = unchecked(receivedMs - _previousReal);

            // The drift's own step: signed (a stamp behind the last one is a small negative step, not a wrapped huge one), credited the same way,
            // and counted for every pair, discarded or not, so the excess is the difference of two spans.
            long signedClient = unchecked((int)clientDelta);
            if (_credit > 0 && signedClient > realDelta)
            {
                signedClient -= Math.Min(_credit, signedClient - realDelta);
            }

            havePair = true;
            driftStep = signedClient - realDelta;
            _driftRealMs += realDelta;
            if (_credit > 0 && clientDelta > realDelta)
            {
                clientDelta -= Math.Min(_credit, clientDelta - realDelta);
            }

            uint maxGap = (uint)Math.Max(0, _options.MaxGapMs);
            bool guarded = realDelta == 0
                || (maxGap > 0 && realDelta > maxGap)
                || (maxGap > 0 && clientDelta > (ulong)realDelta + maxGap);
            if (!guarded)
            {
                sample = new Sample(clientDelta, realDelta, true);
            }
        }

        // The previous sample always advances, even for a discarded pair, and a credit is spent by the pair it reached.
        _credit = 0;
        _previousClient = clientTime;
        _previousReal = receivedMs;
        _havePrevious = true;

        // Slide the ring: evict the oldest slot's contribution, write the new sample at the head.
        if (_count == _ring.Length)
        {
            Sample old = _ring[_head];
            if (old.Valid)
            {
                _sumClient -= old.ClientDelta;
                _sumReal -= old.RealDelta;
                _validCount--;
            }
        }
        else
        {
            _count++;
        }

        _ring[_head] = sample;
        _head = (_head + 1) % _ring.Length;
        if (sample.Valid)
        {
            _sumClient += sample.ClientDelta;
            _sumReal += sample.RealDelta;
            _validCount++;
        }

        // The cooldown ends once the receive clock reaches its deadline (wrap-safe signed comparison).
        if (_inCooldown && unchecked((int)(receivedMs - _cooldownUntil)) >= 0)
        {
            _inCooldown = false;
        }

        bool driftFired = false;
        long driftExcess = 0;
        long driftSpan = 0;
        long limit = _options.LongTermDriftMs;
        if (limit > 0 && havePair)
        {
            _drift = Math.Max(-2 * limit, _drift + driftStep);
            if (_drift >= limit && !_inCooldown)
            {
                driftFired = true;
                driftExcess = _drift;
                driftSpan = _driftRealMs;
                _drift = 0;
                _driftRealMs = 0;
                _consecutiveHot = 0;
                _inCooldown = true;
                _cooldownUntil = unchecked(receivedMs + (uint)Math.Max(0, _options.CooldownMs));
            }
        }

        if (driftFired)
        {
            double driftRatio = 1.0 + ((double)driftExcess / Math.Max(1, driftSpan));
            int driftSeverity = (int)Math.Clamp(Math.Round((driftRatio - 1.0) * 100.0, MidpointRounding.AwayFromZero), 1, 100);
            return new SpeedHackDecision(true, driftSeverity, driftRatio, LongTermDrift: true);
        }

        if (_validCount < _options.MinSamples || _sumReal <= 0)
        {
            return default;
        }

        double ratio = _sumClient / _sumReal;
        double tripHigh = 1.0 + (_options.TolerancePercent / 100.0);
        double clearLow = 1.0 + (_options.TolerancePercent / 2.0 / 100.0);
        if (ratio >= tripHigh)
        {
            _consecutiveHot++;
        }
        else if (ratio < clearLow)
        {
            _consecutiveHot = 0;
        }

        // Between clearLow and tripHigh: the hysteresis band holds the counter.
        if (_consecutiveHot >= _options.SustainWindows && !_inCooldown)
        {
            int severity = (int)Math.Clamp(Math.Round((ratio - 1.0) * 100.0, MidpointRounding.AwayFromZero), 1, 100);
            _consecutiveHot = 0;
            _inCooldown = true;
            _cooldownUntil = unchecked(receivedMs + (uint)Math.Max(0, _options.CooldownMs));
            return new SpeedHackDecision(true, severity, ratio);
        }

        return new SpeedHackDecision(false, 0, ratio);
    }

    private readonly record struct Sample(uint ClientDelta, uint RealDelta, bool Valid);
}

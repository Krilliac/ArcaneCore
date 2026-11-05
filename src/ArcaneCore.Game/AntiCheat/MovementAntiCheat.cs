using ArcaneCore.Protocol;

namespace ArcaneCore.Game.AntiCheat;

/// <summary>
/// The movement checks of one player (docs/areas/anticheat.md), ported from the MaNGOS Zero anticheat fork's
/// MovementAnticheat (feature/anticheat-tooling, feature/timesync) and cross-checked against vmangos'
/// Anticheat/MovementAnticheat (HandlePositionTests, HandleFlagTests). They observe and score; they never change or reject
/// a packet. Only low false-positive checks are here: the fork's acceleration gate and bot heuristic are not ported.
/// <list type="bullet">
/// <item>Speed and teleport: the distance of a step against the allowed speed over the step's time budget, which is the
/// client's own elapsed time capped by the server's receive interval plus a latency slack. Lag bunching stretches neither.</item>
/// <item>Fast clock: <see cref="SpeedHackDetector"/> over every packet.</item>
/// <item>Flags: water walk, hover and slow fall without a grant (living players), levitate, fly while swimming, a fake transport.</item>
/// <item>Root: moving or starting to move while an acknowledged root is in force.</item>
/// <item>Jump and fall: a jump while airborne, a damaging drop that ends without MSG_MOVE_FALL_LAND.</item>
/// <item>Timing: a client timestamp going back, a zero timestamp, a burst by both clocks, oversized or spammed time skips, an
/// acknowledgement timestamp going back.</item>
/// <item>Geometry (only where the data is loaded): swimming with no water, climbing into the air, walking through walls.</item>
/// </list>
/// A new baseline is taken (the next packet is trusted) on the first packet, after <see cref="NotifyServerRelocation"/>
/// (teleports, the server moving the player) and after a knockback. A silence, however long, is not a baseline: the step after it is
/// judged against the time that passed (the client's own elapsed time, capped by the receive interval plus the latency slack), so holding
/// packets and then jumping buys only what the silence covers. A time skip is not a baseline either: the step that spans it is judged against
/// real elapsed time (<see cref="NotifyTimeSkip(uint, uint, List{AntiCheatFinding})"/>). Pure and world-thread only.
/// </summary>
public sealed class MovementAntiCheat
{
    private const float AirborneSpeedFactor = 1.25f;
    private const float StoredPositionTolerance = 0.5f;
    private const float NoClipMinStep = 4.0f;
    private const uint NoClipWindowMs = 5000;
    private const int SwimOutOfWaterPackets = 4;
    private const uint KnockbackMaxMs = 6000;
    private const uint TimeSkipWindowMs = 10000;

    /// <summary>The most client-clock credit all the skips of one ten-second window earn together: jitter, not a licence.</summary>
    private const long TimeSkipClockCreditPerWindowMs = 250;

    private const MovementFlags Airborne = MovementFlags.Jumping | MovementFlags.FallingFar;
    private const MovementFlags Translating = MovementFlags.Forward | MovementFlags.Backward | MovementFlags.StrafeLeft | MovementFlags.StrafeRight;

    private readonly SpeedHackDetector _clock;
    private readonly uint[] _burstReceived;
    private readonly uint[] _burstClient;
    private int _burstCount;
    private int _burstHead;

    private bool _hasLast;
    private bool _trustNext;
    private float _lastX, _lastY, _lastZ;
    private uint _lastReceived;
    private bool _lastOnTransport;
    private bool _lastLegitTransport;
    private bool _lastAirborne;
    private uint _lastMoveClientTime;
    private bool _hasClientTime;
    private uint _lastClientTime;

    private bool _hasStored;
    private float _storedX, _storedY, _storedZ;

    private bool _hasValid;
    private float _validX, _validY, _validZ, _validO;

    private bool _airborne;
    private float _apexZ;
    private bool _knockback;
    private uint _knockbackSince;

    private float _lastAllowed;
    private float _graceSpeed;
    private uint _graceUntil;

    private int _swimOutOfWater;
    private bool _hasBlockedStep;
    private uint _lastBlockedStep;

    // The distance budget: yards the player may still have covered, refilled at the allowed speed by the time that passed.
    private bool _bucketActive;
    private float _bucketYards;

    // The ack's own timestamp check already scored this stamp: the block it carries is not scored for the same regression again.
    private bool _ackStampScored;
    private uint _ackStampScoredTime;

    private uint _skipWindowStart;
    private int _skipCount;
    private bool _skipWindowOpen;
    private long _skipSpanBudgetMs;
    private long _skipWindowSupportedMs;
    private long _skipWindowClockCreditMs;
    private bool _hasAckTime;
    private uint _lastAckTime;

    private bool _hasLastSeen;
    private uint _lastSeenMs;

    public MovementAntiCheat(AntiCheatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _clock = new SpeedHackDetector(options.SpeedClock);
        int burst = Math.Max(1, options.BurstPacketsPerSecond) + 1;
        _burstReceived = new uint[burst];
        _burstClient = new uint[burst];
    }

    /// <summary>The options these checks were built with (a reload builds new checks).</summary>
    public AntiCheatOptions Options { get; }

    /// <summary>Whether a baseline exists (diagnostics).</summary>
    public bool HasBaseline => _hasLast;

    /// <summary>
    /// The server moved the player (teleport, map change, a spell, the end of a taxi or charge): trust the next packet and
    /// forget the clock window, so the move is never scored as a blink.
    /// </summary>
    public void NotifyServerRelocation()
    {
        _trustNext = true;
        _clock.Reset();
        _swimOutOfWater = 0;
        _hasBlockedStep = false;
        _bucketActive = false;
    }

    /// <summary>A knockback was acknowledged: a new baseline, and no speed check until the player lands (or six seconds pass).</summary>
    public void NotifyKnockback()
    {
        NotifyServerRelocation();
        _knockback = true;
        _knockbackSince = _lastReceived;
    }

    /// <summary>
    /// Before a block is judged: the position the server holds for the player. When it is not where the last judged block
    /// left it, the server moved the player meanwhile, and that move is not the client's.
    /// </summary>
    public void CheckServerPosition(float x, float y, float z)
    {
        if (_hasStored && (MathF.Abs(x - _storedX) > StoredPositionTolerance || MathF.Abs(y - _storedY) > StoredPositionTolerance
            || MathF.Abs(z - _storedZ) > StoredPositionTolerance))
        {
            NotifyServerRelocation();
        }
    }

    /// <summary>
    /// For a unit the player controls instead of its own: make the position the server holds for the unit the baseline when there is
    /// none yet, or when the server moved the unit since the last judged block. The client's block is then judged against that
    /// position (with no client time elapsed, so only <see cref="AntiCheatOptions.TeleportDistance"/> of slack), never trusted.
    /// </summary>
    public void SeedFromServerPosition(float x, float y, float z, uint receivedMs, uint clientTime)
    {
        if (_hasLast && !_trustNext && _hasStored && MathF.Abs(x - _storedX) <= StoredPositionTolerance && MathF.Abs(y - _storedY) <= StoredPositionTolerance
            && MathF.Abs(z - _storedZ) <= StoredPositionTolerance)
        {
            return;
        }

        NotifyServerRelocation();
        _trustNext = false;
        _hasLast = true;
        _lastX = x;
        _lastY = y;
        _lastZ = z;
        _lastReceived = receivedMs;
        _lastOnTransport = false;
        _lastLegitTransport = false;
        _lastAirborne = false;
        _lastMoveClientTime = clientTime;
        _airborne = false;
        _apexZ = z;
        _knockback = false;
        _bucketActive = false;
        NotifyStored(x, y, z);
    }

    /// <summary>After a block was stored: the position the server now holds (observers may have corrected the client's).</summary>
    public void NotifyStored(float x, float y, float z)
    {
        _storedX = x;
        _storedY = y;
        _storedZ = z;
        _hasStored = true;
    }

    /// <summary>The last position that passed every position check (the rubberband target).</summary>
    public bool TryGetLastValid(out float x, out float y, out float z, out float orientation)
    {
        x = _validX;
        y = _validY;
        z = _validZ;
        orientation = _validO;
        return _hasValid;
    }

    /// <summary>Judge one client movement block; findings are appended to <paramref name="findings"/>.</summary>
    public void Observe(in MovementSample sample, List<AntiCheatFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        int before = findings.Count;
        MovementInfo m = sample.Movement;
        uint now = sample.ReceivedMs;
        bool onTransport = m.HasFlag(MovementFlags.OnTransport);
        bool airborne = (m.Flags & Airborne) != 0;
        _hasLastSeen = true;
        _lastSeenMs = now;

        bool ackStampScored = _ackStampScored && _ackStampScoredTime == m.Time;
        _ackStampScored = false;
        CheckFlags(sample, findings);
        CheckRoot(sample, findings);
        CheckClientTime(m.Time, ackStampScored, findings);
        CheckBurst(m.Time, now, findings);
        float allowed = AllowedSpeed(sample);
        SpeedHackDecision clock = _clock.Feed(m.Time, now);
        if (clock.Fire)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.TimeSync, Math.Clamp(clock.Severity * 0.5f, 5f, 25f), "client clock runs fast (independent clock)"));
        }

        long skipBudgetMs = _skipSpanBudgetMs;
        _skipSpanBudgetMs = 0;
        if (!_hasLast || _trustNext)
        {
            Baseline(m, now, airborne, onTransport, sample.Transport != TransportClaim.Fake);
            if (!HasPositionFinding(findings, before))
            {
                SetValid(m);
            }

            return;
        }

        if (_knockback && (unchecked(now - _knockbackSince) > KnockbackMaxMs || m.HasFlag(MovementFlags.Root)))
        {
            _knockback = false;
        }

        CheckJumpAndFall(sample, airborne, findings);

        float dx = m.X - _lastX;
        float dy = m.Y - _lastY;
        float dz = m.Z - _lastZ;
        float horizontal = MathF.Sqrt((dx * dx) + (dy * dy));
        bool legitTransport = onTransport && sample.Transport != TransportClaim.Fake;
        if (!legitTransport && !_lastLegitTransport && !_knockback)
        {
            CheckDistance(sample, allowed, horizontal, airborne || _lastAirborne, skipBudgetMs, findings);
        }
        else
        {
            _bucketActive = false;
        }

        if (!legitTransport && !_lastLegitTransport && Options.TerrainChecks && sample.Terrain is { } terrain)
        {
            CheckGeometry(sample, terrain, horizontal, dz, airborne, findings);
        }
        else
        {
            _swimOutOfWater = 0;
        }

        if (_knockback && (sample.Opcode == WorldOpcode.MsgMoveFallLand || m.HasFlag(MovementFlags.Swimming) || (!airborne && !_lastAirborne)))
        {
            _knockback = false; // landed
        }

        _lastX = m.X;
        _lastY = m.Y;
        _lastZ = m.Z;
        _lastReceived = now;
        _lastOnTransport = onTransport;
        _lastLegitTransport = legitTransport;
        _lastAirborne = airborne;
        _lastMoveClientTime = m.Time;
        if (!HasPositionFinding(findings, before))
        {
            SetValid(m);
        }
    }

    /// <summary>
    /// CMSG_MOVE_TIME_SKIPPED, with only the packets these checks saw as the evidence of the silence before it. See
    /// <see cref="NotifyTimeSkip(uint, uint, uint?, List{AntiCheatFinding})"/>.
    /// </summary>
    public void NotifyTimeSkip(uint skippedMs, uint receivedMs, List<AntiCheatFinding> findings)
        => NotifyTimeSkip(skippedMs, receivedMs, null, findings);

    /// <summary>
    /// CMSG_MOVE_TIME_SKIPPED: an oversized skip (beyond <see cref="AntiCheatOptions.MaxTimeSkipMs"/>) or more than
    /// <see cref="AntiCheatOptions.MaxTimeSkipsPer10Seconds"/> in ten seconds is scored (the time-sync fork's skip abuse:
    /// claiming skipped time buys movement budget), and so is a skip above <see cref="int.MaxValue"/> (no stall lasts 24.8
    /// days: a sign-confused forge).
    /// <para>
    /// A skip buys no baseline and resets no clock. The skip the evidence supports is the lag, capped by
    /// <see cref="AntiCheatOptions.MaxTimeSkipMs"/> and by the arrival silence before the skip (a stalled main loop sends
    /// nothing; no latency slack is added), and all the supported skips of one ten-second window together by
    /// <see cref="AntiCheatOptions.BaselineGapMs"/>. The step that spans a skip is judged against the client's own elapsed
    /// time plus the supported skip, capped by the receive interval alone (no slack): the skip never buys more than real elapsed time, so a client whose clock already counts the silence gains nothing and one whose clock left it out is made whole. The
    /// independent clock is credited at most 250 ms of that per window (jitter; an honest stall already shows in the receive
    /// side of the pair it spans), so a repeating cadence cannot sustain a fast clock. The silence runs from the later of
    /// <paramref name="previousPacketReceivedMs"/> (the previous inbound packet the world handler saw, when known; a CMSG_PING
    /// or a dropped packet is not stamped, so the silence can read longer than it was) and the last packet these checks saw.
    /// A zero or impossible skip adds no budget and no clock credit, but it still counts toward the spam threshold and advances the silence reference.
    /// The timestamp-regression checks keep running: a stalled client's clock only moves forward.
    /// </para>
    /// </summary>
    public void NotifyTimeSkip(uint skippedMs, uint receivedMs, uint? previousPacketReceivedMs, List<AntiCheatFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (!_skipWindowOpen || unchecked(receivedMs - _skipWindowStart) > TimeSkipWindowMs)
        {
            _skipWindowStart = receivedMs;
            _skipCount = 0;
            _skipWindowSupportedMs = 0;
            _skipWindowClockCreditMs = 0;
            _skipWindowOpen = true;
        }

        _skipCount++;
        if (skippedMs > (uint)Options.MaxTimeSkipMs)
        {
            float weight = Math.Min(30f, skippedMs / (float)Options.MaxTimeSkipMs * 8f);
            findings.Add(new AntiCheatFinding(AntiCheatViolation.TimeSync, weight, "oversized move time skip"));
        }

        bool impossible = unchecked((int)skippedMs) < 0;
        if (impossible)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.PacketTiming, 10f, "impossible move time skip"));
        }

        if (_skipCount == Options.MaxTimeSkipsPer10Seconds + 1)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.PacketTiming, 12f, "move time skip spam"));
        }

        if (skippedMs != 0 && !impossible)
        {
            long supported = Math.Min(SupportedSkip(skippedMs, receivedMs, previousPacketReceivedMs), Math.Max(0, Options.BaselineGapMs - _skipWindowSupportedMs));
            long clockCredit = Math.Min(supported, TimeSkipClockCreditPerWindowMs - _skipWindowClockCreditMs);
            _skipWindowSupportedMs += supported;
            _skipWindowClockCreditMs += clockCredit;
            _skipSpanBudgetMs += supported;
            _clock.Credit((uint)clockCredit);
        }

        _hasLastSeen = true;
        _lastSeenMs = receivedMs;
    }

    /// <summary>
    /// A movement acknowledgement (speed, flag, root, knockback) carries the client's clock too: one going back by more
    /// than <see cref="AntiCheatOptions.ClientTimeRegressionMs"/> is a manipulated clock (the time-sync fork). A reported time
    /// skip grants no exemption: a stalled client's clock only moves forward. Tracked apart from the movement packets' timestamps.
    /// </summary>
    public void NotifyAcknowledgement(uint clientTime, uint receivedMs, List<AntiCheatFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (_hasAckTime && unchecked((int)(_lastAckTime - clientTime)) > Options.ClientTimeRegressionMs)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.PacketTiming, 10f, "acknowledgement timestamp went back"));
            _ackStampScored = true;
            _ackStampScoredTime = clientTime;
        }

        _lastAckTime = clientTime;
        _hasAckTime = true;
        _hasLastSeen = true;
        _lastSeenMs = receivedMs;
    }

    /// <summary>
    /// The skipped client time the evidence supports: min(lag, <see cref="AntiCheatOptions.MaxTimeSkipMs"/>, arrival silence).
    /// With no earlier packet to measure the silence from, nothing is supported. Wrap-safe on the 32-bit receive clock; a
    /// "previous" stamp later than the skip itself measures a silence of zero.
    /// </summary>
    private long SupportedSkip(uint skippedMs, uint receivedMs, uint? previousPacketReceivedMs)
    {
        bool hasPrevious = _hasLastSeen;
        uint previous = _lastSeenMs;
        if (previousPacketReceivedMs is uint sessionPrevious && (!hasPrevious || unchecked((int)(sessionPrevious - previous)) > 0))
        {
            hasPrevious = true;
            previous = sessionPrevious;
        }

        if (!hasPrevious)
        {
            return 0;
        }

        long silence = Math.Max(0, unchecked((int)(receivedMs - previous)));
        return Math.Min(Math.Min((long)skippedMs, (long)Options.MaxTimeSkipMs), silence);
    }

    private void Baseline(in MovementInfo m, uint now, bool airborne, bool onTransport, bool transportClaimHolds)
    {
        _hasLast = true;
        _trustNext = false;
        _lastX = m.X;
        _lastY = m.Y;
        _lastZ = m.Z;
        _lastReceived = now;
        _lastOnTransport = onTransport;
        _lastLegitTransport = onTransport && transportClaimHolds;
        _lastAirborne = airborne;
        _lastMoveClientTime = m.Time;
        _airborne = airborne;
        _apexZ = m.Z;
        _knockbackSince = now;
    }

    private void SetValid(in MovementInfo m)
    {
        _validX = m.X;
        _validY = m.Y;
        _validZ = m.Z;
        _validO = m.Orientation;
        _hasValid = true;
    }

    private static bool HasPositionFinding(List<AntiCheatFinding> findings, int from)
    {
        for (int i = from; i < findings.Count; i++)
        {
            if (findings[i].Type is AntiCheatViolation.Speed or AntiCheatViolation.Teleport or AntiCheatViolation.Vertical
                or AntiCheatViolation.Physics or AntiCheatViolation.Flag)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// vmangos HandleFlagTests: a capability flag the server never granted. Water walk, hover and slow fall are judged for the
    /// living only (the fork; a ghost walks on water); levitating is a creature flag; flying together with swimming is the
    /// swim-fly hack (flying alone is left alone, as vmangos). A transport claim is judged when it starts.
    /// </summary>
    private void CheckFlags(in MovementSample sample, List<AntiCheatFinding> findings)
    {
        MovementFlags flags = sample.Movement.Flags;
        MovementFlags granted = sample.GrantedFlags;
        if (sample.Alive)
        {
            if ((flags & MovementFlags.WaterWalking) != 0 && (granted & MovementFlags.WaterWalking) == 0)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.Flag, 25f, "water walk without a grant"));
            }

            if ((flags & MovementFlags.Hover) != 0 && (granted & MovementFlags.Hover) == 0)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.Flag, 25f, "hover without a grant"));
            }

            if ((flags & MovementFlags.SafeFall) != 0 && (granted & MovementFlags.SafeFall) == 0)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.Flag, 15f, "slow fall without a grant"));
            }
        }

        if ((flags & MovementFlags.Levitating) != 0 && (granted & MovementFlags.Levitating) == 0)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Flag, 40f, "levitating"));
        }

        if ((flags & (MovementFlags.Swimming | MovementFlags.Flying)) == (MovementFlags.Swimming | MovementFlags.Flying)
            && (granted & MovementFlags.Flying) == 0)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Flag, 40f, "flying while swimming"));
        }

        bool onTransport = (flags & MovementFlags.OnTransport) != 0;
        if (onTransport && sample.Transport == TransportClaim.Fake && (!_hasLast || !_lastOnTransport || _trustNext))
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Flag, 20f, "transport flag without a transport"));
        }
    }

    /// <summary>The fork's root-break and opcode-legality checks against an acknowledged root (no false positive: no root is pending).</summary>
    private static void CheckRoot(in MovementSample sample, List<AntiCheatFinding> findings)
    {
        if (!sample.Rooted)
        {
            return;
        }

        if (sample.Opcode is WorldOpcode.MsgMoveStartForward or WorldOpcode.MsgMoveStartBackward or WorldOpcode.MsgMoveStartStrafeLeft
            or WorldOpcode.MsgMoveStartStrafeRight or WorldOpcode.MsgMoveStartSwim or WorldOpcode.MsgMoveJump)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Physics, 15f, "move start while rooted"));
        }
        else if ((sample.Movement.Flags & Translating) != 0)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Physics, 20f, "moving while rooted"));
        }
    }

    private void CheckClientTime(uint clientTime, bool alreadyScoredByAck, List<AntiCheatFinding> findings)
    {
        if (_hasClientTime)
        {
            if (unchecked((int)(_lastClientTime - clientTime)) > Options.ClientTimeRegressionMs)
            {
                if (!alreadyScoredByAck)
                {
                    findings.Add(new AntiCheatFinding(AntiCheatViolation.PacketTiming, 10f, "client timestamp went back"));
                }
            }
            else if (clientTime == 0)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.PacketTiming, 5f, "zero client timestamp"));
            }
        }

        _lastClientTime = clientTime;
        _hasClientTime = true;
    }

    /// <summary>
    /// More than <see cref="AntiCheatOptions.BurstPacketsPerSecond"/> packets within one second by the receive clock AND by
    /// the client's own clock. Packets bunched by lag were sent over a longer client time and do not count; stamps that stand still or
    /// go back span no client time at all (signed difference), so they count.
    /// </summary>
    private void CheckBurst(uint clientTime, uint now, List<AntiCheatFinding> findings)
    {
        int size = _burstReceived.Length;
        _burstReceived[_burstHead] = now;
        _burstClient[_burstHead] = clientTime;
        _burstHead = (_burstHead + 1) % size;
        if (_burstCount < size)
        {
            _burstCount++;
            if (_burstCount < size)
            {
                return;
            }
        }

        int oldest = _burstHead; // the slot written longest ago
        int newest = (_burstHead + size - 1) % size;
        if (unchecked(_burstReceived[newest] - _burstReceived[oldest]) < 1000 && unchecked((int)(_burstClient[newest] - _burstClient[oldest])) < 1000)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Burst, 15f, "movement packet burst"));
            _burstCount = 0; // at most once per window
        }
    }

    /// <summary>
    /// The allowed speed with the speed-change grace: when the allowed speed drops (an acknowledged decrease, an aura gone),
    /// the old speed still counts for <see cref="AntiCheatOptions.SpeedChangeGraceMs"/> plus the latency slack.
    /// </summary>
    private float AllowedSpeed(in MovementSample sample)
    {
        float allowed = Math.Max(0f, sample.AllowedSpeed);
        if (_lastAllowed > allowed)
        {
            _graceSpeed = Math.Max(_graceSpeed, _lastAllowed);
            _graceUntil = unchecked(sample.ReceivedMs + (uint)Options.SpeedChangeGraceMs + (uint)Slack(sample.LatencyMs));
        }

        _lastAllowed = allowed;
        if (_graceSpeed > allowed && unchecked((int)(sample.ReceivedMs - _graceUntil)) < 0)
        {
            return _graceSpeed;
        }

        _graceSpeed = 0f;
        return allowed;
    }

    private int Slack(int latencyMs)
        => Math.Clamp(Options.LatencySlackMs + (2 * Math.Max(0, latencyMs)), Options.LatencySlackMs, Math.Max(Options.LatencySlackMs, Options.MaxLatencySlackMs));

    /// <summary>
    /// Speed and teleport over one step. The budget is the client's elapsed time capped by the receive interval plus the
    /// latency slack; the step that spans a reported time skip may add the supported skip to the client side, but that sum is capped by
    /// the receive interval alone (no slack), so a skip never buys more than real elapsed time.
    /// </summary>
    private void CheckDistance(in MovementSample sample, float allowed, float horizontal, bool airborne, long skipBudgetMs, List<AntiCheatFinding> findings)
    {
        uint now = sample.ReceivedMs;
        int clientDelta = unchecked((int)(sample.Movement.Time - _lastMoveClientTime));
        if (clientDelta < 0)
        {
            // A stamp behind the previous block carries no usable client time, so the step gets none (as for an equal stamp):
            // only the teleport slack, plus the supported time skip. Returning here made a block stamped 1..500 ms behind the
            // last (under the regression finding's threshold) an unscored blink of any length.
            clientDelta = 0;
        }

        uint receivedDelta = unchecked(now - _lastReceived);
        int maxGapMs = Options.SpeedClock.MaxGapMs;
        bool stampAhead = maxGapMs > 0 && (long)clientDelta > (long)receivedDelta + maxGapMs;
        if (stampAhead)
        {
            // No honest clock runs that far ahead of real time (a stall only leaves client time out): the independent clock discards
            // such a pair, so it is scored here, and the step gets no latency slack (only the real interval).
            findings.Add(new AntiCheatFinding(AntiCheatViolation.TimeSync, 15f, "client timestamp far ahead of real time"));
            clientDelta = (int)Math.Min(receivedDelta, (uint)int.MaxValue);
        }

        long wallMs = (long)receivedDelta + (stampAhead ? 0 : Slack(sample.LatencyMs));
        long budgetMs = Math.Max(Math.Min((long)clientDelta, wallMs), Math.Min((long)clientDelta + skipBudgetMs, (long)receivedDelta));
        float speed = airborne ? allowed * AirborneSpeedFactor : allowed;
        float seconds = budgetMs / 1000f;
        float teleport = Options.TeleportDistance + (speed * seconds);
        if (horizontal > teleport)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Teleport, 25f, "teleport or blink"));
            _bucketActive = false;
            return;
        }

        float expected = (speed * (1f + (Options.SpeedTolerancePercent / 100f)) * seconds) + Options.SpeedSlackYards;
        if (speed > 0f && horizontal > expected)
        {
            float ratio = horizontal / Math.Max(0.01f, expected);
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Speed, Math.Clamp((ratio - 1f) * 50f, 5f, 25f), "faster than the allowed speed"));
            _bucketActive = false;
            return;
        }

        CheckDistanceBudget(sample, speed, horizontal, budgetMs, receivedDelta, findings);
    }

    /// <summary>
    /// The distance a player may have covered over many steps, whatever each step's own budget was. A step's budget alone is blind to a
    /// stream of packets stamped at or behind the last (no client time, so only <see cref="AntiCheatOptions.SpeedSlackYards"/> a step) and
    /// adds the latency slack again for every packet. Here the yards stand in a bucket that the allowed speed (with the tolerance) refills by
    /// the time that passed (the larger of the real interval and the step's own time budget, so lag bunching refills what it
    /// carries and a stamp going back or standing still refills only real time), at most <see cref="AntiCheatOptions.SpeedSlackYards"/>
    /// plus the speed over the latency slack (or one step's refill when that is more); every step spends its horizontal distance.
    /// An overdraft is a Speed finding, and the bucket restarts empty.
    /// </summary>
    private void CheckDistanceBudget(in MovementSample sample, float speed, float horizontal, long stepBudgetMs, uint receivedDelta, List<AntiCheatFinding> findings)
    {
        if (!(speed > 0f))
        {
            _bucketActive = false;
            return;
        }

        float perSecond = speed * (1f + (Options.SpeedTolerancePercent / 100f));
        long refillMs = Math.Max((long)receivedDelta, stepBudgetMs);
        float refill = perSecond * (refillMs / 1000f);
        float cap = Math.Max(Options.SpeedSlackYards + (perSecond * (Slack(sample.LatencyMs) / 1000f)), refill);
        if (!_bucketActive)
        {
            _bucketActive = true;
            _bucketYards = cap;
        }

        _bucketYards = Math.Min(cap, _bucketYards + refill) - horizontal;
        if (_bucketYards < 0f)
        {
            float overdraft = -_bucketYards;
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Speed, Math.Clamp(5f + overdraft, 5f, 25f), "faster than the allowed speed over several packets"));
            _bucketYards = 0f;
        }
    }

    /// <summary>The fork's jump/fall state machine: a jump while airborne, and a damaging drop that lands without MSG_MOVE_FALL_LAND.</summary>
    private void CheckJumpAndFall(in MovementSample sample, bool airborne, List<AntiCheatFinding> findings)
    {
        MovementInfo m = sample.Movement;
        if (sample.Opcode == WorldOpcode.MsgMoveJump)
        {
            if (_airborne)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.Jump, 30f, "jump while in the air"));
            }

            _airborne = true;
            _apexZ = m.Z;
            return;
        }

        if (sample.Opcode == WorldOpcode.MsgMoveStartSwim || m.HasFlag(MovementFlags.Swimming) || m.HasFlag(MovementFlags.Root))
        {
            _airborne = false;
            _apexZ = m.Z;
            return;
        }

        if (airborne)
        {
            _airborne = true;
            _apexZ = Math.Max(_apexZ, m.Z);
            return;
        }

        if (_airborne)
        {
            if (sample.Opcode != WorldOpcode.MsgMoveFallLand && _apexZ - m.Z >= Options.FallSuppressYards && sample.Alive)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.Fall, 25f, "landed without MSG_MOVE_FALL_LAND"));
            }

            _airborne = false;
        }

        _apexZ = m.Z;
    }

    private void CheckGeometry(in MovementSample sample, IAntiCheatTerrain terrain, float horizontal, float dz, bool airborne, List<AntiCheatFinding> findings)
    {
        MovementInfo m = sample.Movement;
        const MovementFlags NotGrounded = Airborne | MovementFlags.Swimming | MovementFlags.Hover | MovementFlags.Levitating | MovementFlags.Flying;

        // Swimming where the terrain and the models hold no liquid, several packets in a row (a shore cell can disagree once).
        // Not while the server granted flight (.cheat fly): that client swims in the air on purpose.
        if (m.HasFlag(MovementFlags.Swimming) && (sample.GrantedFlags & MovementFlags.Flying) == 0 && terrain.IsInLiquid(m.X, m.Y, m.Z) == false)
        {
            if (++_swimOutOfWater == SwimOutOfWaterPackets)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.Physics, 20f, "swimming out of water"));
            }
        }
        else
        {
            _swimOutOfWater = 0;
        }

        bool grounded = (m.Flags & NotGrounded) == 0 && !_lastAirborne && !airborne;

        // Climbing steeply into the air while claiming to stand: the floor under the new position is far below it.
        // A server-controlled unit (.cheat wallclimb) climbs walls on purpose (vmangos CheckWallClimb, MovementAnticheat.cpp:1178).
        if (grounded && !sample.ServerControlled && dz > 1f && dz > 2f * horizontal && terrain.FloorHeight(m.X, m.Y, m.Z) is { } floor && m.Z - floor > 3f)
        {
            findings.Add(new AntiCheatFinding(AntiCheatViolation.Vertical, 15f, "climbed into the air"));
        }

        // Walking through a wall: a long ground step without line of sight, twice within five seconds (one can be a corner).
        if (grounded && horizontal > NoClipMinStep
            && terrain.IsInLineOfSight(_lastX, _lastY, _lastZ + 1.5f, m.X, m.Y, m.Z + 1.5f) == false)
        {
            if (_hasBlockedStep && unchecked(sample.ReceivedMs - _lastBlockedStep) <= NoClipWindowMs)
            {
                findings.Add(new AntiCheatFinding(AntiCheatViolation.Physics, 15f, "moved through geometry"));
                _hasBlockedStep = false;
            }
            else
            {
                _hasBlockedStep = true;
                _lastBlockedStep = sample.ReceivedMs;
            }
        }
    }
}

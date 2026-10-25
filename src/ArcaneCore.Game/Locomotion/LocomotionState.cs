using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// The live auras of one unit that locomotion rules read, keyed by aura type. <see cref="SpellSystem"/> is a
/// world-daemon feature that map code cannot reach, so the locomotion aura modules (movement flag auras,
/// later speed auras) record every apply and removal here and the rules query this ledger instead
/// (vmangos GetTotalAuraModifier / HasAuraType, Unit.cpp:3019-3041, read the unit's aura lists the same way).
/// A stack-amount change is an un-apply followed by an apply of the same <see cref="SpellAura"/>, so the
/// ledger always holds the current amounts.
/// </summary>
public sealed partial class AuraLedger
{
    private readonly Dictionary<AuraType, List<SpellAura>> _auras = [];

    /// <summary>Record an applied aura (a second add of the same instance is ignored).</summary>
    public void Add(SpellAura aura)
    {
        ArgumentNullException.ThrowIfNull(aura);
        if (!_auras.TryGetValue(aura.Type, out List<SpellAura>? list))
        {
            list = [];
            _auras[aura.Type] = list;
        }

        if (!list.Contains(aura))
        {
            list.Add(aura);
        }
    }

    public void Remove(SpellAura aura)
    {
        ArgumentNullException.ThrowIfNull(aura);
        if (_auras.TryGetValue(aura.Type, out List<SpellAura>? list))
        {
            list.Remove(aura);
        }
    }

    /// <summary>vmangos Unit::HasAuraType.</summary>
    public bool Has(AuraType type) => _auras.TryGetValue(type, out List<SpellAura>? list) && list.Count > 0;

    /// <summary>vmangos Unit::GetTotalAuraModifier: the sum of the amounts.</summary>
    public int Total(AuraType type)
    {
        int total = 0;
        if (_auras.TryGetValue(type, out List<SpellAura>? list))
        {
            foreach (SpellAura aura in list)
            {
                total += aura.Amount;
            }
        }

        return total;
    }

    /// <summary>vmangos Unit::GetTotalAuraMultiplier: the product of (100 + amount) / 100 over the auras (1 when there are none).</summary>
    public float Multiplier(AuraType type)
    {
        float multiplier = 1.0f;
        if (_auras.TryGetValue(type, out List<SpellAura>? list))
        {
            foreach (SpellAura aura in list)
            {
                multiplier *= (100.0f + aura.Amount) / 100.0f;
            }
        }

        return multiplier;
    }

    /// <summary>vmangos Unit::GetMaxPositiveAuraModifier (0 when none is positive).</summary>
    public int MaxPositive(AuraType type)
    {
        int max = 0;
        if (_auras.TryGetValue(type, out List<SpellAura>? list))
        {
            foreach (SpellAura aura in list)
            {
                max = Math.Max(max, aura.Amount);
            }
        }

        return max;
    }

    /// <summary>vmangos Unit::GetMaxNegativeAuraModifier (0 when none is negative).</summary>
    public int MaxNegative(AuraType type)
    {
        int min = 0;
        if (_auras.TryGetValue(type, out List<SpellAura>? list))
        {
            foreach (SpellAura aura in list)
            {
                min = Math.Min(min, aura.Amount);
            }
        }

        return min;
    }
}

/// <summary>
/// The locomotion state of one unit, kept beside it (not on <see cref="Unit"/>, which other lanes edit) and
/// reached with <c>unit.Locomotion</c>. Nothing here is persisted: speeds, falls and pending acks are rebuilt at
/// login, as vmangos does (it saves none of them).
/// </summary>
public sealed partial class LocomotionState
{
    /// <summary>Server-ordered changes waiting for the client's ack.</summary>
    public PendingMovementChanges Pending { get; } = new();

    /// <summary>The auras locomotion rules read (see <see cref="AuraLedger"/>).</summary>
    public AuraLedger Auras { get; } = new();

    /// <summary>
    /// The configured speed rates this unit's speeds are set with (<see cref="LocomotionOptions.GetSpeedRates"/>, copied onto players at login and
    /// at a rate change by <see cref="SpeedRates.Apply"/>; <see cref="PlayerSpeedRates.Retail"/> for everything else).
    /// </summary>
    public PlayerSpeedRates ConfiguredSpeedRates { get; set; } = PlayerSpeedRates.Retail;

    /// <summary>
    /// Height where the current fall started. Meaningful while <see cref="IsFalling"/>. Zero is a real height
    /// (sea level, the deck of a transport whose local origin is 0), so it cannot also mean "not falling":
    /// vmangos stores both in <c>m_fallStartZ</c> and <c>!m_fallStartZ</c> (Player.h:1720-1726), which re-anchors
    /// a fall that begins at 0 on every later packet and under-counts it. Assigning this starts a fall;
    /// <see cref="ResetFall"/> is what ends one.
    /// </summary>
    public float FallStartZ
    {
        get => _fallStartZ;
        set
        {
            _fallStartZ = value;
            _falling = true;
        }
    }

    private float _fallStartZ;

    private bool _falling;

    public bool IsFalling => _falling;

    /// <summary>Server time when the current continuous fall episode started, or null when not falling.</summary>
    public uint? FallStartedAtMs { get; internal set; }

    /// <summary>Whether <c>move/fall-without-landing</c> has already been reported for the current fall episode.</summary>
    public bool FallWithoutLandingReported { get; internal set; }

    /// <summary>True while a synthetic chaos probe packet is being processed and anomaly ledger reports should be suppressed.</summary>
    public bool SuppressAnomalies { get; set; }

    /// <summary>Server timestamp of the last <c>move/non-finite</c> report for this unit.</summary>
    public uint? LastNonFiniteReportServerMs { get; set; }

    /// <summary>Whether <c>move/under-map</c> has already been reported for the current under-map episode.</summary>
    public bool UndermapReportedInEpisode { get; internal set; }

    /// <summary>Server timestamp of the last terrain-height under-map sample for this unit.</summary>
    public uint? LastUndermapSampleMs { get; internal set; }

    /// <summary>Server timestamp of the last <c>move/speed-violation</c> report for this unit.</summary>
    public uint? LastSpeedViolationReportMs { get; internal set; }

    /// <summary>Whether <c>move/speed-violation</c> has been reported for the current episode (ends after a run of legitimate packets or a quiet period).</summary>
    public bool SpeedViolationInEpisode { get; internal set; }

    /// <summary>Consecutive legitimate packets seen since the last violation of the current episode.</summary>
    public int SpeedLegitStreak { get; internal set; }

    /// <summary>Server timestamp of the last server-initiated teleport or knockback relocation.</summary>
    public uint? LastTeleportOrKnockbackMs { get; internal set; }

    /// <summary>Last unknown transport GUID reported by <c>move/client-transport-unknown</c> in the current episode.</summary>
    public ulong LastUnknownTransportGuid { get; internal set; }

    /// <summary>Whether <c>transport/passenger-desync</c> has already been reported in the current episode.</summary>
    public bool TransportDesyncReportedInEpisode { get; internal set; }

    /// <summary>Whether <c>transport/passenger-left-behind</c> has already been reported in the current episode.</summary>
    public bool PassengerLeftBehindReportedInEpisode { get; internal set; }

    /// <summary>Server time when tracking of the current ghost travel episode started.</summary>
    public uint? GhostTrackStartedAtMs { get; internal set; }

    /// <summary>Server time when the ghost last made meaningful progress toward its corpse or spirit healer.</summary>
    public uint? GhostLastProgressAtMs { get; internal set; }

    /// <summary>Map id for which the current ghost travel progress baseline was recorded.</summary>
    public uint GhostTrackedMapId { get; internal set; }

    /// <summary>Closest 2D distance to the corpse or entrance achieved during the current ghost travel episode.</summary>
    public float GhostBestTargetDistance { get; internal set; } = float.MaxValue;

    /// <summary>Anchor X position for ghost movement progress when no same-map target exists.</summary>
    public float GhostAnchorX { get; internal set; }

    /// <summary>Anchor Y position for ghost movement progress when no same-map target exists.</summary>
    public float GhostAnchorY { get; internal set; }

    /// <summary>Whether <c>death/ghost-stuck</c> has already been reported in the current ghost episode.</summary>
    public bool GhostStuckReported { get; internal set; }

    /// <summary>Whether <c>death/corpse-missing</c> has already been reported in the current ghost episode.</summary>
    public bool CorpseMissingReported { get; internal set; }

    /// <summary>Whether <c>death/spirit-healer-unreachable</c> has already been reported in the current ghost episode.</summary>
    public bool SpiritHealerUnreachableReported { get; internal set; }

    /// <summary>Where the player is in the liquids (vmangos Player::m_environmentFlags).</summary>
    public EnvironmentFlags Environment { get; set; }

    /// <summary>
    /// The mirror timers: fatigue, breath, feign death and the server-only environmental one (vmangos Player::m_mirrorTimers,
    /// Player.h:1836). Feign death is created but never started (the hunter lane owns that state).
    /// </summary>
    public MirrorTimer[] MirrorTimers { get; } = [new(MirrorTimerType.Fatigue), new(MirrorTimerType.Breath), new(MirrorTimerType.FeignDeath), new(MirrorTimerType.Environmental)];

    /// <summary>The factor on the breath time (vmangos Player::m_environmentBreathingMultiplier): 0 with a Water Breathing aura, 1 otherwise.</summary>
    public float BreathingMultiplier { get; set; } = 1.0f;

    /// <summary>vmangos' model collision height before any model data is read (Unit.cpp:117) or when the model has none (:9384).</summary>
    public const float DefaultCollisionHeight = 2.0f;

    /// <summary>
    /// The unit's model collision height in yards (vmangos Unit::m_modelCollisionHeight, Unit.cpp:9380-9386). Without the client's
    /// CreatureModelData it is vmangos' fallback of 2 yards for every race; the swim depth is three quarters of it. Line of sight
    /// looks from <see cref="Entities.Unit.CollisionHeight"/>, this height times the unit's scale override.
    /// </summary>
    public float CollisionHeight { get; set; } = DefaultCollisionHeight;

    /// <summary>The place the liquid flags were last evaluated for (null: never), so a tick only asks the terrain again after a move.</summary>
    internal (Maps.Map Map, float X, float Y, float Z)? LastEnvironmentSample { get; set; }

    internal void InvalidateEnvironmentSample() => LastEnvironmentSample = null;

    /// <summary>Forget the fall in progress (vmangos SetFallInformation(0)); called on teleport, swim, knock back and login.</summary>
    public void ResetFall()
    {
        _falling = false;
        _fallStartZ = 0.0f;
        FallStartedAtMs = null;
        FallWithoutLandingReported = false;
    }

    /// <summary>Clear per-death ghost anomaly episode flags when the player is alive or starts a fresh death cycle.</summary>
    public void ResetDeathTracking()
    {
        GhostTrackStartedAtMs = null;
        GhostLastProgressAtMs = null;
        GhostTrackedMapId = 0;
        GhostBestTargetDistance = float.MaxValue;
        GhostAnchorX = 0f;
        GhostAnchorY = 0f;
        GhostStuckReported = false;
        CorpseMissingReported = false;
        SpiritHealerUnreachableReported = false;
    }

    /// <summary>Acknowledgements that matched nothing the server sent (vmangos OnWrongAckData; counted, never punished here).</summary>
    public int WrongAckCount { get; private set; }

    /// <summary>Changes the client never acknowledged in time (vmangos OnFailedToAckChange).</summary>
    public int FailedAckCount { get; private set; }

    internal void NoteWrongAck() => WrongAckCount++;

    internal void NoteFailedAck() => FailedAckCount++;

    /// <summary>The active server-launched spline for this unit (e.g., Charge), or null when none is in flight.</summary>
    public CreatureSpline? ActiveSpline { get; private set; }

    /// <summary>Milliseconds elapsed along <see cref="ActiveSpline"/> since it was launched.</summary>
    public long SplineElapsedMs { get; private set; }

    /// <summary>Record a newly launched server spline on this unit.</summary>
    public void StartSpline(CreatureSpline spline)
    {
        ArgumentNullException.ThrowIfNull(spline);
        ActiveSpline = spline;
        SplineElapsedMs = 0;
    }

    /// <summary>Advance <see cref="ActiveSpline"/> by <paramref name="diffMs"/> and clear it once completed.</summary>
    public void AdvanceSpline(uint diffMs)
    {
        if (ActiveSpline is not { } spline)
        {
            return;
        }

        SplineElapsedMs += diffMs;
        if (spline.IsFinished(spline.StartClockMs + SplineElapsedMs))
        {
            ActiveSpline = null;
            SplineElapsedMs = 0;
        }
    }

    /// <summary>Clear any active server spline (e.g., when leaving a map or teleporting).</summary>
    public void ClearSpline()
    {
        ActiveSpline = null;
        SplineElapsedMs = 0;
    }

    /// <summary>
    /// Sample the unit's live position and smoothly blended orientation along <see cref="ActiveSpline"/> when one is in flight,
    /// or return the unit's stored <c>(X, Y, Z, Orientation)</c> otherwise.
    /// </summary>
    public (float X, float Y, float Z, float Orientation) SampleSpline(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (ActiveSpline is { } spline)
        {
            return spline.PositionAndOrientationAt(spline.StartClockMs + SplineElapsedMs, unit.Orientation);
        }

        return (unit.X, unit.Y, unit.Z, unit.Orientation);
    }
}

/// <summary><c>unit.Locomotion</c>: the unit's <see cref="LocomotionState"/> (created on first use).</summary>
public static class LocomotionStates
{
    private static readonly ConditionalWeakTable<Unit, LocomotionState> s_states = new();

    /// <summary>The state of <paramref name="unit"/> if it has been used, without creating one (map ticks use this).</summary>
    public static bool TryGet(Unit unit, out LocomotionState state)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return s_states.TryGetValue(unit, out state!);
    }

    extension(Unit unit)
    {
        /// <summary>Locomotion state (pending acks, aura ledger, fall tracking).</summary>
        public LocomotionState Locomotion => s_states.GetValue(unit, static _ => new LocomotionState());
    }
}

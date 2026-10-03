using System.Runtime.CompilerServices;
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
public sealed class AuraLedger
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
public sealed class LocomotionState
{
    /// <summary>Server-ordered changes waiting for the client's ack.</summary>
    public PendingMovementChanges Pending { get; } = new();

    /// <summary>The auras locomotion rules read (see <see cref="AuraLedger"/>).</summary>
    public AuraLedger Auras { get; } = new();

    /// <summary>
    /// Height where the current fall started, 0 when not falling (vmangos Player::m_fallStartZ;
    /// Player::IsFalling = non-zero, SetFallInformation).
    /// </summary>
    public float FallStartZ { get; set; }

    public bool IsFalling => FallStartZ != 0.0f;

    /// <summary>Forget the fall in progress (vmangos SetFallInformation(0)); called on teleport, swim, knock back and login.</summary>
    public void ResetFall() => FallStartZ = 0.0f;

    /// <summary>Acknowledgements that matched nothing the server sent (vmangos OnWrongAckData; counted, never punished here).</summary>
    public int WrongAckCount { get; private set; }

    /// <summary>Changes the client never acknowledged in time (vmangos OnFailedToAckChange).</summary>
    public int FailedAckCount { get; private set; }

    internal void NoteWrongAck() => WrongAckCount++;

    internal void NoteFailedAck() => FailedAckCount++;
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

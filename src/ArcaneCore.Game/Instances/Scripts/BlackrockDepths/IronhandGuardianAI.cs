using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Classic;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// npc_ironhand_guardian (entry 8982, six statues in the Iron Hall of Blackrock Depths), from the behaviour spec 230-npc_ironhand_guardian.md.
/// A stationary hazard that never fights: it takes no part in combat, does not chase and does not swing. While the Iron Hall
/// (<see cref="BlackrockDepthsInstance.TypeIronHall"/>) is NOT_STARTED it idles; once the encounter has started (Magmus in combat) it wakes up
/// (Stoned 10255 removed, the stoned channel visual 15533 cast on itself) and breathes Gout of Flame (15529) on a timer, for good: Fail and
/// Done do not send it back to sleep, only a NOT_STARTED instance does (spec section 3). Owned by its creature, world thread.
/// </summary>
public sealed class IronhandGuardianAI(Creature creature, BlackrockDepthsInstance instance) : CreatureAI(creature)
{
    /// <summary>The aura the statue starts with (spec section 3, Activate).</summary>
    public const uint SpellStoned = 10255;

    /// <summary>The stoned channel visual cast once per activation (spec section 4).</summary>
    public const uint SpellStonedChannel = 15533;

    /// <summary>Gout of Flame: the spell applies the periodic trigger 15538 in front of the caster by itself (spec section 4).</summary>
    public const uint SpellGoutOfFlame = 15529;

    /// <summary>The first Gout timer, rolled at reset in whole seconds (spec section 4).</summary>
    public const int FirstGoutMinSeconds = 4, FirstGoutMaxSeconds = 30;

    /// <summary>The Gout timer after a successful cast, in whole seconds (spec section 4).</summary>
    public const int RepeatGoutMinSeconds = 20, RepeatGoutMaxSeconds = 40;

    private enum Phase { Idle, Activate, Flaming }

    private Phase _phase = Phase.Idle;
    private uint _goutMs;

    /// <summary>Whether the statue is awake (Gout of Flame is on its timer).</summary>
    public bool IsFlaming => _phase == Phase.Flaming;

    /// <summary>The milliseconds left on the Gout of Flame timer.</summary>
    public uint GoutRemainingMs => _goutMs;

    /// <inheritdoc />
    /// <remarks>It never aggroes on sight: spec section 2 (passive react state).</remarks>
    public override bool AggroesOnSight => false;

    /// <summary>Spec section 7: no guard call either, it is a statue.</summary>
    protected override bool CallsGuardsOnSight => false;

    /// <summary>
    /// The statue never enters combat, even when a hostile player stands next to it or hits it (spec section 2: "It never enters combat and never
    /// chases"; its template flags also make it unselectable).
    /// </summary>
    public override bool AttackStart(Unit target) => false;

    public override void OnAttackedBy(Unit attacker)
    {
    }

    /// <summary>
    /// Reset (spec section 7), at the spawn and at every respawn: the Gout timer is re-rolled and "cannot enter combat, combat movement off,
    /// react state passive" is asserted. The phase is NOT cleared here, only a NOT_STARTED Iron Hall clears it.
    /// </summary>
    public override void OnRespawn()
    {
        _goutMs = (uint)(System?.RandomInt(FirstGoutMinSeconds, FirstGoutMaxSeconds) ?? FirstGoutMinSeconds) * 1000;
        CombatMovement = false;
        MeleeEnabled = false;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (instance.GetData(BlackrockDepthsInstance.TypeIronHall) == EncounterState.NotStarted)
        {
            _phase = Phase.Idle; // "reset to Idle every update while NotStarted"
            return;
        }

        if (_phase == Phase.Idle)
        {
            _phase = Phase.Activate;
        }

        if (_phase == Phase.Activate)
        {
            // Spec section 3: remove Stoned (a no-op when the statue does not carry it), cast the visual on itself, and go on to Flaming only on a
            // successful cast; otherwise the whole step is retried on the next update.
            System?.RemoveAuras(Me, SpellStoned);
            if (DoCast(Me, SpellStonedChannel) == CreatureCastResult.Ok)
            {
                _phase = Phase.Flaming;
            }

            return;
        }

        if (_goutMs < diffMs)
        {
            // "On a failed cast, the timer stays expired and retries every update."
            if (DoCast(Me, SpellGoutOfFlame) == CreatureCastResult.Ok)
            {
                _goutMs = (uint)(System?.RandomInt(RepeatGoutMinSeconds, RepeatGoutMaxSeconds) ?? RepeatGoutMinSeconds) * 1000;
            }
        }
        else
        {
            _goutMs -= diffMs;
        }
    }
}

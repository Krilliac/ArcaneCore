using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Locomotion;

/// <summary>
/// Physical damage taken modifiers for a fall (vmangos <c>GetTotalAuraMultiplierByMiscMask(SPELL_AURA_MOD_DAMAGE_PERCENT_TAKEN,
/// SPELL_SCHOOL_MASK_NORMAL)</c>, Player.cpp:20854-20855). The spell system's <see cref="SpellEnvironmentalMitigation"/> reads
/// those auras; the world daemon registers it with <see cref="LocomotionEnvironment.RegisterFallModifiers"/>. With nothing
/// registered the multiplier is 1 (<see cref="NoFallDamageModifiers"/>).
/// </summary>
public interface IFallDamageModifiers
{
    /// <summary>The product of the physical damage-taken-percent auras of the player (1 when there are none).</summary>
    float TakenDamageMultiplier(Player player);
}

/// <summary>The pass-through modifier of a world without a spell system: nothing changes fall damage.</summary>
public sealed class NoFallDamageModifiers : IFallDamageModifiers
{
    public static NoFallDamageModifiers Instance { get; } = new();

    public float TakenDamageMultiplier(Player player) => 1.0f;
}

/// <summary>The fall damage formula of vmangos <c>Player::HandleFall</c> (Player.cpp:20799-20870).</summary>
public static class FallDamageCalculator
{
    /// <summary>
    /// The normal fall time from 14.57 yards: a landing reported after less than this many milliseconds never hurts
    /// (Player.cpp:20832).
    /// </summary>
    public const uint MinFallTimeMs = 1229;

    /// <summary>Falls shorter than this never hurt; 14.57 is where the damage formula crosses zero (:20845).</summary>
    public const float MinFallDistance = 14.57f;

    /// <summary>
    /// Damage of a fall of <paramref name="zDiff"/> yards (:20845-20867): zero below 14.57 yards; otherwise
    /// <c>0.018 * (zDiff - safeFall) - 0.2426</c> of the maximum health, times the world's fall rate and the taken
    /// modifier, truncated to an integer and capped at the maximum health. Float arithmetic and the unsigned
    /// truncation are vmangos'.
    /// </summary>
    public static uint Damage(float zDiff, int safeFall, uint maxHealth, float rate = 1.0f, float takenMod = 1.0f)
    {
        if (zDiff < MinFallDistance)
        {
            return 0;
        }

        float dmgPct = (0.018f * (zDiff - safeFall)) - 0.2426f;
        if (!(dmgPct > 0))
        {
            return 0;
        }

        uint damage = (uint)(dmgPct * maxHealth * rate * takenMod);
        return Math.Min(damage, maxHealth);
    }
}

/// <summary>
/// Fall tracking and fall damage (vmangos HandleMovementOpcodes, MovementHandler.cpp:333-344, with
/// <c>Player::UpdateFallInformationIfNeed</c> and <c>Player::HandleFall</c>, Player.cpp:20799-20870). It runs before the
/// block is stored because HandleFall reads the previously stored flags.
/// <para>
/// Fall start: any block with the Jumping or FallingFar flag records the height where the fall began (and re-records
/// it when the player rises above it); a landing, a swim start, hover, safe fall or a block with neither flag forgets
/// the fall. Landing: <c>MSG_MOVE_FALL_LAND</c> hurts when the previous block was FallingFar, the client reports at
/// least 1229 ms of falling, the player is not above where it started, and the fall was at least 14.57 yards; not a
/// dead player, a game master, or a unit with a Hover or Feather Fall aura; Safe Fall auras (Safe Fall, Feline Grace)
/// reduce the distance by their amounts. The transport comparisons of vmangos (a changed transport GUID, a change of
/// the OnTransport flag) are applied to the transport fields the block carries (ships: docs/areas/transports.md).
/// </para>
/// </summary>
[MovementObserver(Order = 20)]
public sealed class FallObserver : IClientMovementObserver
{
    /// <summary>Maximum duration (ms) a player may continuously hold falling flags without landing before <c>move/fall-without-landing</c> is reported.</summary>
    public const uint MaxUnresolvedFallMs = 30_000;

    public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
    {
        Player player = context.Player;
        LocomotionState state = player.Locomotion;

        // .cheat fly / .cheat fixedz: the client swims or glides in the air and never falls; nothing is tracked, nothing hurts.
        if (player.HasMovementCheat)
        {
            if (state.IsFalling || state.FallStartedAtMs is not null)
            {
                state.ResetFall();
            }

            return;
        }

        // "ignore in flight case that can be triggered also at lags in moment teleportation to another map"
        if (context.Opcode == WorldOpcode.MsgMoveFallLand && (player.UnitFlags & UnitFlags.TaxiFlight) == 0)
        {
            HandleFall(context, state, previous, incoming);
        }

        UpdateFallInformation(player, context.World.NowMs, state, previous, incoming, context.Opcode);
    }

    /// <summary>vmangos Player::UpdateFallInformationIfNeed.</summary>
    private static void UpdateFallInformation(Player player, uint nowMs, LocomotionState state, in MovementInfo previous, in MovementInfo incoming, WorldOpcode opcode)
    {
        if (opcode is WorldOpcode.MsgMoveFallLand or WorldOpcode.MsgMoveStartSwim
            || (incoming.Flags & (MovementFlags.Hover | MovementFlags.SafeFall)) != 0
            || (incoming.Flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) == 0)
        {
            if (state.IsFalling || state.FallStartedAtMs is not null)
            {
                state.ResetFall();
            }

            return;
        }

        bool wasFalling = state.IsFalling;
        float currentZ = incoming.HasFlag(MovementFlags.OnTransport) ? incoming.TransportZ : incoming.Z;
        // A recorded start of 0 is a real height. Only "not falling" means there is nothing to update
        // (vmangos uses !m_fallStartZ for both, Player.cpp:20811, and loses a fall that begins at 0).
        if (!state.IsFalling || state.FallStartZ < currentZ || previous.TransportGuid != incoming.TransportGuid)
        {
            state.FallStartZ = currentZ;
        }

        if (!wasFalling || state.FallStartedAtMs is null)
        {
            state.FallStartedAtMs = nowMs;
            state.FallWithoutLandingReported = false;
        }

        CheckFallDuration(player, state, nowMs, incoming.FallTime, incoming.X, incoming.Y, incoming.Z, incoming.Flags);
    }

    internal static void CheckFallDuration(Player player, LocomotionState state, uint nowMs, uint clientFallTime, float x, float y, float z, MovementFlags flags)
    {
        if (state.SuppressAnomalies
            || player.IsGameMaster
            || player.HasMovementCheat
            || player.HasCheat(PlayerCheats.FeatherFall)
            || player.Transport is not null
            || (player.UnitFlags & UnitFlags.TaxiFlight) != 0
            || (flags & (MovementFlags.OnTransport | MovementFlags.Swimming | MovementFlags.Flying | MovementFlags.Hover | MovementFlags.SafeFall | MovementFlags.Levitating)) != 0
            || state.Auras.Has(AuraType.FeatherFall)
            || state.Auras.Has(AuraType.Hover))
        {
            state.FallStartedAtMs = null;
            state.FallWithoutLandingReported = false;
            return;
        }

        if ((flags & (MovementFlags.Jumping | MovementFlags.FallingFar)) == 0 && !state.IsFalling)
        {
            state.FallStartedAtMs = null;
            state.FallWithoutLandingReported = false;
            return;
        }

        state.FallStartedAtMs ??= nowMs;
        uint serverElapsed = unchecked(nowMs - state.FallStartedAtMs.Value);
        uint elapsedMs = Math.Max(serverElapsed, clientFallTime);
        if (!state.FallWithoutLandingReported && elapsedMs >= MaxUnresolvedFallMs)
        {
            state.FallWithoutLandingReported = true;
            AnomalyLedger.Shared.Report(
                "move",
                "fall-without-landing",
                $"player {player.Name} (guid {player.Guid.Low})",
                $"map={player.MapId} durationMs={elapsedMs} fallTime={clientFallTime} pos=({x:F2},{y:F2},{z:F2})",
                AnomalySeverity.Warning);
        }
    }

    /// <summary>vmangos Player::HandleFall.</summary>
    private static void HandleFall(MovementObserverContext context, LocomotionState state, in MovementInfo previous, in MovementInfo incoming)
    {
        if (!state.IsFalling
            || !previous.HasFlag(MovementFlags.FallingFar)
            || previous.TransportGuid != incoming.TransportGuid
            || previous.HasFlag(MovementFlags.OnTransport) != incoming.HasFlag(MovementFlags.OnTransport)
            || incoming.FallTime < FallDamageCalculator.MinFallTimeMs)
        {
            return;
        }

        float currentZ = incoming.HasFlag(MovementFlags.OnTransport) ? incoming.TransportZ : incoming.Z;
        if (state.FallStartZ < currentZ)
        {
            return;
        }

        Player player = context.Player;
        float zDiff = state.FallStartZ - currentZ;
        AuraLedger auras = player.Locomotion.Auras;
        if (zDiff < FallDamageCalculator.MinFallDistance || !player.IsAlive || player.IsGameMaster
            || player.HasCheat(PlayerCheats.FeatherFall)
            || auras.Has(AuraType.Hover) || auras.Has(AuraType.FeatherFall))
        {
            return;
        }

        int safeFall = auras.Total(AuraType.SafeFall);
        float takenMod = LocomotionEnvironment.FallModifiersFor(context.World).TakenDamageMultiplier(player);
        uint damage = FallDamageCalculator.Damage(zDiff, safeFall, player.MaxHealth, LocomotionEnvironment.For(context.World).Options.RateDamageFall, takenMod);
        if (damage > 0)
        {
            EnvironmentalDamage.Apply(context.World, player, EnvironmentalDamageType.Fall, damage);
        }
    }
}

using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Updates;
using ArcaneCore.World.Playerbots.AI.Actions;

namespace ArcaneCore.World.Playerbots.AI.Inference;

/// <summary>
/// Blittable per-bot observation snapshot used by <see cref="PlayerbotBatchCoordinator"/> to populate
/// the pinned native observation buffer without heap allocations.
/// </summary>
public readonly record struct PlayerbotBatchSlot(
    ulong BotGuid,
    float HealthPct,
    float ManaPct,
    float TargetDistance,
    float ThreatLevel,
    float WeatherIntensity,
    Vector3 DestinationPos,
    float MoveUrgencyLogit,
    uint PrimarySpellId,
    ulong TargetGuid,
    uint PetCommandId,
    ulong PetTargetGuid,
    bool HasPet = false,
    bool InCombat = false)
{
    /// <summary>Builds a blittable observation slot from a live world <see cref="Player"/> entity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PlayerbotBatchSlot FromPlayer(Player player)
    {
        float healthPct = player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 1.0f;
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1);
        float manaPct = maxMana > 0 ? (float)player.GetUInt32(UpdateFields.UnitFieldPower1) / maxMana : 1.0f;
        ulong targetGuid = player.Target.Value;
        bool inCombat = player.Combat.IsInCombat;
        float threatLevel = inCombat ? 0.75f : 0.0f;
        var pos = new Vector3(player.X, player.Y, player.Z);
        ulong petGuid = player.GetUInt64(UpdateFields.UnitFieldSummon);

        return new PlayerbotBatchSlot(
            BotGuid: player.Guid.Value,
            HealthPct: Math.Clamp(healthPct, 0f, 1f),
            ManaPct: Math.Clamp(manaPct, 0f, 1f),
            TargetDistance: targetGuid != 0 ? 15.0f : 0.0f,
            ThreatLevel: threatLevel,
            WeatherIntensity: 0.1f,
            DestinationPos: pos,
            MoveUrgencyLogit: inCombat ? 1.0f : -1.0f,
            PrimarySpellId: 0,
            TargetGuid: targetGuid,
            PetCommandId: 0,
            PetTargetGuid: petGuid,
            HasPet: petGuid != 0,
            InCombat: inCombat);
    }
}

/// <summary>Telemetry counters returned by a single zero-allocation batch inference tick.</summary>
public record struct PlayerbotBatchDispatchStats(
    int IdleCount,
    int MoveCount,
    int CastCount,
    int PetCommandCount)
{
    public readonly int TotalDispatched => IdleCount + MoveCount + CastCount + PetCommandCount;
}

/// <summary>
/// Coordinates the three-phase zero-allocation batched playerbot inference pass on the world tick:
/// (1) writes observations directly into 64-byte aligned native memory via <see cref="Span{T}"/>,
/// (2) executes <see cref="PlayerbotInferenceEngine.InferBatch"/> into a <c>stackalloc</c>
/// <see cref="PlayerbotAction"/> buffer, and
/// (3) dispatches each action through an exhaustive C# 15 <c>[Union]</c> <c>switch</c> expression
/// with zero boxing.
/// </summary>
public sealed class PlayerbotBatchCoordinator
{
    private long _totalBatchesRun;
    private long _totalBotsEvaluated;
    private PlayerbotBatchDispatchStats _lastTickStats;

    /// <summary>Total number of batched inference passes executed since startup.</summary>
    public long TotalBatchesRun => Interlocked.Read(ref _totalBatchesRun);

    /// <summary>Total number of bot observation slots evaluated across all batches.</summary>
    public long TotalBotsEvaluated => Interlocked.Read(ref _totalBotsEvaluated);

    /// <summary>Action telemetry from the most recent batch pass.</summary>
    public PlayerbotBatchDispatchStats LastTickStats => _lastTickStats;

    /// <summary>
    /// Runs a single batched inference pass for up to <see cref="PlayerbotInferenceEngine.MaxBatchSize"/>
    /// active bots with zero managed heap allocations.
    /// </summary>
    public PlayerbotBatchDispatchStats UpdatePlayerbotsTick(
        ReadOnlySpan<PlayerbotBatchSlot> activeBots,
        PlayerbotInferenceEngine inferenceEngine)
    {
        int batchCount = Math.Min(activeBots.Length, PlayerbotInferenceEngine.MaxBatchSize);
        if (batchCount <= 0)
        {
            _lastTickStats = default;
            return default;
        }

        // Phase 1: Write observations directly into pinned 64-byte-aligned native memory (0 allocations).
        for (int i = 0; i < batchCount; i++)
        {
            ref readonly PlayerbotBatchSlot bot = ref activeBots[i];
            Span<float> obs = inferenceEngine.GetBotObservationBuffer(i);
            obs.Clear();

            obs[0] = bot.HealthPct;
            obs[1] = bot.ManaPct;
            obs[2] = bot.TargetDistance;
            obs[3] = bot.ThreatLevel;
            obs[4] = bot.WeatherIntensity;
            obs[5] = bot.DestinationPos.X;
            obs[6] = bot.DestinationPos.Y;
            obs[7] = bot.DestinationPos.Z;
            obs[8] = bot.MoveUrgencyLogit;
            obs[9] = bot.PrimarySpellId;
            obs[10] = bot.TargetGuid;
            obs[11] = bot.PetCommandId;
            obs[12] = bot.PetTargetGuid;
            obs[14] = bot.HasPet ? 1.0f : 0.0f;
            obs[15] = bot.InCombat ? 1.0f : 0.0f;
        }

        // Phase 2: Allocate output action buffer on the stack (~3 KB for 128 * 24-byte union structs)
        // and run batched inference.
        Span<PlayerbotAction> actions = stackalloc PlayerbotAction[batchCount];
        inferenceEngine.InferBatch(batchCount, actions);

        // Phase 3: Zero-boxing exhaustive C# 15 [Union] pattern matching.
        PlayerbotBatchDispatchStats stats = default;
        for (int i = 0; i < batchCount; i++)
        {
            _ = DispatchAction(actions[i], ref stats);
        }

        _lastTickStats = stats;
        Interlocked.Increment(ref _totalBatchesRun);
        Interlocked.Add(ref _totalBotsEvaluated, batchCount);
        return stats;
    }

    /// <summary>
    /// Dispatches a single <see cref="PlayerbotAction"/> using an exhaustive C# 15 <c>[Union]</c>
    /// <c>switch</c> expression without boxing any value-type payload.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool DispatchAction(PlayerbotAction action, ref PlayerbotBatchDispatchStats stats)
    {
        return action switch
        {
            IdleAction idle => OnIdle(idle, ref stats),
            MoveToAction move => OnMove(move, ref stats),
            CastSpellAction cast => OnCast(cast, ref stats),
            PetCommandAction pet => OnPetCommand(pet, ref stats),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OnIdle(IdleAction idle, ref PlayerbotBatchDispatchStats stats)
    {
        if (idle.DurationSec >= 0f)
        {
            stats.IdleCount++;
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OnMove(MoveToAction move, ref PlayerbotBatchDispatchStats stats)
    {
        if (float.IsFinite(move.WorldPos.X) && move.Urgency >= 0f)
        {
            stats.MoveCount++;
            return true;
        }
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OnCast(CastSpellAction cast, ref PlayerbotBatchDispatchStats stats)
    {
        stats.CastCount++;
        return cast.SpellId != 0 || cast.TargetGuid == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool OnPetCommand(PetCommandAction pet, ref PlayerbotBatchDispatchStats stats)
    {
        stats.PetCommandCount++;
        return pet.CommandId != 0 || pet.PetTargetGuid == 0;
    }
}

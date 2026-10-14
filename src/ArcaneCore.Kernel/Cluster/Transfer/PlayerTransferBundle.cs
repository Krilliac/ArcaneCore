using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Kernel.Cluster.Transfer;

/// <summary>World-space or transport-local coordinates and facing orientation.</summary>
public readonly record struct TransferPosition(float X, float Y, float Z, float Orientation);

/// <summary>Client movement and time-sync acknowledgement sequence counters carried across handoff.</summary>
public readonly record struct TransferAckCounters(uint MovementAckSequence, uint TimeSyncSequence);

/// <summary>Authoritative unit movement speeds preserved across worker handoff.</summary>
public sealed record TransferSpeedRates(
    float Walk = 2.5f,
    float Run = 7.0f,
    float RunBack = 4.5f,
    float Swim = 4.722222f,
    float SwimBack = 2.5f,
    float Turn = 3.1415927f,
    float Flight = 7.0f,
    float FlightBack = 4.5f)
{
    public static TransferSpeedRates Default { get; } = new();
}

/// <summary>PvP flag state and remaining timer duration at handoff capture.</summary>
public readonly record struct TransferPvpState(
    bool IsPvpFlagged,
    bool IsFfaPvp = false,
    long PvpTimerRemainingMs = 0);

/// <summary>Rested XP pool and inn rest state at handoff capture.</summary>
public readonly record struct TransferRestState(
    uint RestedXpBonus = 0,
    byte RestStateId = 0,
    bool IsInsideInn = false);

/// <summary>Per-slot enchantment state on an item snapshot.</summary>
public readonly record struct TransferItemEnchantment(
    uint Slot,
    uint EnchantId,
    uint DurationMs,
    int Charges);

/// <summary>Complete item instance snapshot covering equipped, bag, backpack, bank, buyback, and keyring slots.</summary>
public sealed record TransferItemSnapshot(
    uint ItemGuid,
    uint Entry,
    uint ContainerGuid,
    byte Slot,
    uint Count,
    uint Durability,
    uint MaxDurability,
    uint Flags,
    int RandomPropertyId,
    ulong CreatorGuid,
    uint DurationSec,
    IReadOnlyList<int> SpellCharges,
    IReadOnlyList<TransferItemEnchantment> Enchantments,
    bool IsBuyback = false,
    uint BuybackPriceCopper = 0);

/// <summary>Quest log slot progress, objective counters, exploration state, and remaining timer in milliseconds.</summary>
public sealed record TransferQuestSlotSnapshot(
    byte Slot,
    uint QuestId,
    byte Status,
    bool Rewarded,
    bool Explored,
    long RemainingTimerMs,
    IReadOnlyList<uint> ObjectiveCounters,
    uint RewardChoice = 0);

/// <summary>Spell and category cooldown state with remaining millisecond durations rebased on the target worker.</summary>
public sealed record TransferCooldownSnapshot(
    uint SpellId,
    uint CategoryId,
    uint ItemEntry,
    long RemainingDurationMs,
    long CategoryRemainingDurationMs = 0);

/// <summary>Active aura snapshot preserved across cross-worker transfer.</summary>
public sealed record TransferAuraSnapshot(
    uint SpellId,
    ulong CasterGuid,
    byte EffIndexMask,
    int Charges,
    int StackAmount,
    int MaxDurationMs,
    int RemainingDurationMs,
    bool IsPeriodic,
    IReadOnlyList<int> BasePoints,
    int PeriodicTimerRemainingMs = 0);

/// <summary>Transient gameplay states that block cross-worker transfer until resolved or closed.</summary>
public sealed record TransferActiveBlockers(
    bool HasActiveTradeWindow = false,
    bool HasOpenLootWindow = false,
    bool HasActiveCharmOrMindControl = false,
    bool HasPendingQuestSettlement = false,
    bool HasPendingEconomySettlement = false,
    bool HasInFlightTaxiMovement = false)
{
    public static TransferActiveBlockers None { get; } = new();

    public bool HasAny =>
        HasActiveTradeWindow ||
        HasOpenLootWindow ||
        HasActiveCharmOrMindControl ||
        HasPendingQuestSettlement ||
        HasPendingEconomySettlement ||
        HasInFlightTaxiMovement;
}

/// <summary>
/// Complete versioned runtime state bundle captured when suspending a player on the source worker
/// and installed as a passive shadow on the target worker prior to commit.
/// </summary>
public sealed record PlayerTransferBundle
{
    public required Guid TransferId { get; init; }
    public required ulong CharacterGuid { get; init; }
    public int CharacterId { get; init; }
    public required int AccountId { get; init; }
    public required long SessionGeneration { get; init; }
    public required WorkerNodeId SourceWorkerId { get; init; }
    public required WorkerNodeId TargetWorkerId { get; init; }
    public required CharacterOwnershipToken SourceOwnershipToken { get; init; }
    public long CapturedRevision { get; init; } = 1;
    public long CapturedAtUtcMs { get; init; }

    public uint SourceMapId { get; init; }
    public uint SourceInstanceId { get; init; }
    public TransferPosition SourcePosition { get; init; }
    public required uint TargetMapId { get; init; }
    public uint TargetInstanceId { get; init; }
    public required TransferPosition TargetPosition { get; init; }
    public uint MovementFlags { get; init; }
    public ulong TransportGuid { get; init; }
    public TransferPosition? TransportOffset { get; init; }
    public uint FallTime { get; init; }
    public TransferAckCounters AckCounters { get; init; }
    public TransferSpeedRates SpeedRates { get; init; } = TransferSpeedRates.Default;

    public required uint Health { get; init; }
    public uint MaxHealth { get; init; } = 100;
    public required IReadOnlyList<uint> Powers { get; init; }
    public required byte Level { get; init; }
    public uint Xp { get; init; }
    public uint Money { get; init; }
    public uint Flags { get; init; }
    public byte StandState { get; init; }
    public byte ShapeshiftForm { get; init; }
    public byte ComboPoints { get; init; }
    public ulong ComboTargetGuid { get; init; }
    public TransferPvpState PvpState { get; init; }
    public TransferRestState RestState { get; init; }
    public bool IsGhost { get; init; }
    public long DeathExpireUnix { get; init; }

    public IReadOnlyList<TransferItemSnapshot> Items { get; init; } = [];
    public uint? SelectedAmmoId { get; init; }
    public byte BankBagSlotCount { get; init; }

    public IReadOnlyList<TransferQuestSlotSnapshot> Quests { get; init; } = [];

    public IReadOnlyList<uint> KnownSpells { get; init; } = [];
    public uint FreeTalentPoints { get; init; }
    public IReadOnlyList<TransferCooldownSnapshot> Cooldowns { get; init; } = [];
    public IReadOnlyList<TransferAuraSnapshot> Auras { get; init; } = [];

    public HomeBind HomeBind { get; init; }
    public CorpseSnapshot? Corpse { get; init; }
    public IReadOnlyList<uint> TaxiNodesMask { get; init; } = [];
    public long PresenceVersion { get; init; }

    public TransferActiveBlockers Blockers { get; init; } = TransferActiveBlockers.None;
}

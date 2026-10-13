using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Transfer;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Transfer;

public sealed class PlayerTransferValidatorTests
{
    internal static PlayerTransferBundle CreateSampleValidBundle(
        Guid? transferId = null,
        ulong characterGuid = 4242UL,
        WorkerNodeId? sourceWorker = null,
        WorkerNodeId? targetWorker = null,
        CharacterOwnershipToken? sourceToken = null,
        TransferActiveBlockers? blockers = null)
    {
        return new PlayerTransferBundle
        {
            TransferId = transferId ?? Guid.NewGuid(),
            CharacterGuid = characterGuid,
            CharacterId = (int)characterGuid,
            AccountId = 77,
            SessionGeneration = 5,
            SourceWorkerId = sourceWorker ?? new WorkerNodeId("worker-source"),
            TargetWorkerId = targetWorker ?? new WorkerNodeId("worker-target"),
            SourceOwnershipToken = sourceToken ?? new CharacterOwnershipToken("char:4242", 3, Guid.NewGuid()),
            CapturedRevision = 12,
            CapturedAtUtcMs = 1_760_000_000_000L,
            SourceMapId = 0,
            SourceInstanceId = 0,
            SourcePosition = new TransferPosition(-8949.95f, -132.49f, 83.53f, 0.5f),
            TargetMapId = 1,
            TargetInstanceId = 0,
            TargetPosition = new TransferPosition(1629.36f, -4373.39f, 31.25f, 3.14f),
            MovementFlags = 0x00000001u,
            TransportGuid = 20808UL,
            TransportOffset = new TransferPosition(1.5f, -0.5f, 2.0f, 1.0f),
            FallTime = 180,
            AckCounters = new TransferAckCounters(MovementAckSequence: 41, TimeSyncSequence: 19),
            SpeedRates = new TransferSpeedRates(Walk: 2.5f, Run: 7.5f, RunBack: 4.5f, Swim: 4.72f, SwimBack: 2.5f, Turn: 3.14f),
            Health = 1480,
            MaxHealth = 1800,
            Powers = [920u, 250u, 0u, 100u, 0u],
            Level = 44,
            Xp = 38_400,
            Money = 125_600,
            Flags = 0x00000008u,
            StandState = 0,
            ShapeshiftForm = 1,
            ComboPoints = 3,
            ComboTargetGuid = 99001UL,
            PvpState = new TransferPvpState(IsPvpFlagged: true, IsFfaPvp: false, PvpTimerRemainingMs: 185_000),
            RestState = new TransferRestState(RestedXpBonus: 14_200, RestStateId: 1, IsInsideInn: true),
            IsGhost = false,
            DeathExpireUnix = 0,
            Items =
            [
                new TransferItemSnapshot(
                    ItemGuid: 5001,
                    Entry: 19019,
                    ContainerGuid: 0,
                    Slot: 15,
                    Count: 1,
                    Durability: 95,
                    MaxDurability: 105,
                    Flags: 1,
                    RandomPropertyId: 0,
                    CreatorGuid: 4242UL,
                    DurationSec: 0,
                    SpellCharges: [-1, 0, 0, 0, 0],
                    Enchantments: [new TransferItemEnchantment(Slot: 0, EnchantId: 1900, DurationMs: 0, Charges: 0)]),
                new TransferItemSnapshot(
                    ItemGuid: 5002,
                    Entry: 13446,
                    ContainerGuid: 0,
                    Slot: 23,
                    Count: 5,
                    Durability: 0,
                    MaxDurability: 0,
                    Flags: 0,
                    RandomPropertyId: 0,
                    CreatorGuid: 0,
                    DurationSec: 0,
                    SpellCharges: [5, 0, 0, 0, 0],
                    Enchantments: []),
                new TransferItemSnapshot(
                    ItemGuid: 5003,
                    Entry: 4540,
                    ContainerGuid: 0,
                    Slot: 74,
                    Count: 2,
                    Durability: 0,
                    MaxDurability: 0,
                    Flags: 0,
                    RandomPropertyId: 0,
                    CreatorGuid: 0,
                    DurationSec: 0,
                    SpellCharges: [],
                    Enchantments: [],
                    IsBuyback: true,
                    BuybackPriceCopper: 25),
            ],
            SelectedAmmoId = 11285,
            BankBagSlotCount = 2,
            Quests =
            [
                new TransferQuestSlotSnapshot(
                    Slot: 0,
                    QuestId: 843,
                    Status: 3,
                    Rewarded: false,
                    Explored: true,
                    RemainingTimerMs: 420_000,
                    ObjectiveCounters: [8u, 3u, 0u, 0u, 4u, 0u, 0u, 0u],
                    RewardChoice: 1),
            ],
            KnownSpells = [133u, 116u, 2136u, 1459u],
            FreeTalentPoints = 2,
            Cooldowns =
            [
                new TransferCooldownSnapshot(
                    SpellId: 2136,
                    CategoryId: 44,
                    ItemEntry: 0,
                    RemainingDurationMs: 6_500,
                    CategoryRemainingDurationMs: 1_500),
            ],
            Auras =
            [
                new TransferAuraSnapshot(
                    SpellId: 1459,
                    CasterGuid: 4242UL,
                    EffIndexMask: 0x01,
                    Charges: 0,
                    StackAmount: 1,
                    MaxDurationMs: 1_800_000,
                    RemainingDurationMs: 1_420_000,
                    IsPeriodic: false,
                    BasePoints: [30, 0, 0],
                    PeriodicTimerRemainingMs: 0),
                new TransferAuraSnapshot(
                    SpellId: 139,
                    CasterGuid: 7777UL,
                    EffIndexMask: 0x01,
                    Charges: 0,
                    StackAmount: 1,
                    MaxDurationMs: 15_000,
                    RemainingDurationMs: 9_000,
                    IsPeriodic: true,
                    BasePoints: [45, 0, 0],
                    PeriodicTimerRemainingMs: 1_200),
            ],
            HomeBind = new HomeBind(MapId: 0, ZoneId: 12, X: -9464.0f, Y: 62.0f, Z: 56.0f),
            Corpse = null,
            TaxiNodesMask = [0x0000000Fu, 0u, 0u, 0u, 0u, 0u, 0u, 0u],
            PresenceVersion = 18,
            Blockers = blockers ?? TransferActiveBlockers.None,
        };
    }

    [Fact]
    public void ValidateBundle_AcceptsCompleteValidBundle()
    {
        PlayerTransferBundle bundle = CreateSampleValidBundle();
        PlayerTransferValidationResult result = PlayerTransferValidator.ValidateBundle(bundle);

        Assert.True(result.IsValid);
        Assert.Null(result.FailureCode);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(true, false, false, false, false, false, PlayerTransferValidator.ErrorActiveTradeWindow)]
    [InlineData(false, true, false, false, false, false, PlayerTransferValidator.ErrorOpenLootWindow)]
    [InlineData(false, false, true, false, false, false, PlayerTransferValidator.ErrorActiveCharmOrMindControl)]
    [InlineData(false, false, false, true, false, false, PlayerTransferValidator.ErrorPendingQuestSettlement)]
    [InlineData(false, false, false, false, true, false, PlayerTransferValidator.ErrorPendingEconomySettlement)]
    [InlineData(false, false, false, false, false, true, PlayerTransferValidator.ErrorInFlightTaxi)]
    public void ValidateBundle_RefusesUnsupportedNonTransferableGameplayStates(
        bool trade,
        bool loot,
        bool charm,
        bool questSettlement,
        bool economySettlement,
        bool taxi,
        string expectedFailureCode)
    {
        var blockers = new TransferActiveBlockers(
            HasActiveTradeWindow: trade,
            HasOpenLootWindow: loot,
            HasActiveCharmOrMindControl: charm,
            HasPendingQuestSettlement: questSettlement,
            HasPendingEconomySettlement: economySettlement,
            HasInFlightTaxiMovement: taxi);

        PlayerTransferBundle bundle = CreateSampleValidBundle(blockers: blockers);
        PlayerTransferValidationResult result = PlayerTransferValidator.ValidateBundle(bundle);

        Assert.False(result.IsValid);
        Assert.Equal(expectedFailureCode, result.FailureCode);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ValidateBundle_RejectsMissingOrCorruptFields()
    {
        // Same source & target worker
        var sameWorker = CreateSampleValidBundle(
            sourceWorker: new WorkerNodeId("worker-1"),
            targetWorker: new WorkerNodeId("worker-1"));
        Assert.Equal(PlayerTransferValidator.ErrorInvalidWorkers, PlayerTransferValidator.ValidateBundle(sameWorker).FailureCode);

        // Living player with 0 health
        PlayerTransferBundle zeroHealthAlive = CreateSampleValidBundle() with { Health = 0, IsGhost = false, Corpse = null };
        Assert.Equal(PlayerTransferValidator.ErrorInvalidVitals, PlayerTransferValidator.ValidateBundle(zeroHealthAlive).FailureCode);

        // Ghost player with 0 health is valid
        PlayerTransferBundle ghostPlayer = CreateSampleValidBundle() with
        {
            Health = 0,
            IsGhost = true,
            Corpse = new CorpseSnapshot(MapId: 0, X: -8900f, Y: -120f, Z: 82f, Orientation: 0f, GhostTimeUnix: 1_760_000_000L, Type: 1),
        };
        Assert.True(PlayerTransferValidator.ValidateBundle(ghostPlayer).IsValid);

        // Negative quest timer
        PlayerTransferBundle negativeQuestTimer = CreateSampleValidBundle() with
        {
            Quests = [new TransferQuestSlotSnapshot(0, 843, 3, false, false, -500, [1u])],
        };
        Assert.Equal(PlayerTransferValidator.ErrorInvalidQuests, PlayerTransferValidator.ValidateBundle(negativeQuestTimer).FailureCode);

        // Invalid aura (negative remaining duration)
        PlayerTransferBundle invalidAura = CreateSampleValidBundle() with
        {
            Auras = [new TransferAuraSnapshot(1459, 4242UL, 1, 0, 1, 10_000, -100, false, [30])],
        };
        Assert.Equal(PlayerTransferValidator.ErrorInvalidSpellsOrAuras, PlayerTransferValidator.ValidateBundle(invalidAura).FailureCode);
    }
}

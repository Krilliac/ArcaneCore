using ArcaneCore.Kernel.Cluster.Transfer;

namespace ArcaneCore.World.Cluster.Transfer;

/// <summary>Structured validation outcome for player transfer eligibility and bundle completeness checks.</summary>
public sealed record PlayerTransferValidationResult(
    bool IsValid,
    string? FailureCode,
    IReadOnlyList<string> Errors)
{
    public static PlayerTransferValidationResult Success { get; } = new(true, null, []);

    public static PlayerTransferValidationResult Fail(string code, params string[] errors) =>
        new(false, code, errors);
}

/// <summary>
/// Validates player eligibility prior to cross-worker transfer and verifies completeness/integrity
/// of a captured <see cref="PlayerTransferBundle"/> before passive installation and commit.
/// </summary>
public static class PlayerTransferValidator
{
    public const string ErrorNullBundle = "bundle-null";
    public const string ErrorActiveTradeWindow = "blocked-active-trade";
    public const string ErrorOpenLootWindow = "blocked-open-loot";
    public const string ErrorActiveCharmOrMindControl = "blocked-active-charm";
    public const string ErrorPendingQuestSettlement = "blocked-pending-quest-settlement";
    public const string ErrorPendingEconomySettlement = "blocked-pending-economy-settlement";
    public const string ErrorInFlightTaxi = "blocked-in-flight-taxi";
    public const string ErrorInvalidIdentity = "invalid-identity";
    public const string ErrorInvalidWorkers = "invalid-worker-endpoints";
    public const string ErrorInvalidOwnershipToken = "invalid-ownership-token";
    public const string ErrorInvalidVitals = "invalid-vitals";
    public const string ErrorInvalidInventory = "invalid-inventory";
    public const string ErrorInvalidQuests = "invalid-quests";
    public const string ErrorInvalidSpellsOrAuras = "invalid-spells-or-auras";

    /// <summary>
    /// Refuses handoff when the player is in an unsupported transient gameplay state (open trade window,
    /// open loot window, active mind-control/charm, pending settlement, or in-flight taxi).
    /// </summary>
    public static PlayerTransferValidationResult ValidateBlockers(TransferActiveBlockers? blockers)
    {
        if (blockers is null || !blockers.HasAny)
            return PlayerTransferValidationResult.Success;

        var errors = new List<string>();
        string? primaryCode = null;

        if (blockers.HasActiveTradeWindow)
        {
            primaryCode ??= ErrorActiveTradeWindow;
            errors.Add("Player has an active trade window and cannot be transferred across workers.");
        }

        if (blockers.HasOpenLootWindow)
        {
            primaryCode ??= ErrorOpenLootWindow;
            errors.Add("Player has an open loot window and cannot be transferred across workers.");
        }

        if (blockers.HasActiveCharmOrMindControl)
        {
            primaryCode ??= ErrorActiveCharmOrMindControl;
            errors.Add("Player is in an active charm or mind-control state and cannot be transferred across workers.");
        }

        if (blockers.HasPendingQuestSettlement)
        {
            primaryCode ??= ErrorPendingQuestSettlement;
            errors.Add("Player has an in-flight quest settlement transaction and cannot be transferred across workers.");
        }

        if (blockers.HasPendingEconomySettlement)
        {
            primaryCode ??= ErrorPendingEconomySettlement;
            errors.Add("Player has an in-flight economy settlement transaction and cannot be transferred across workers.");
        }

        if (blockers.HasInFlightTaxiMovement)
        {
            primaryCode ??= ErrorInFlightTaxi;
            errors.Add("Player is currently on an active taxi flight path that must land before cross-worker transfer.");
        }

        return new PlayerTransferValidationResult(false, primaryCode, errors);
    }

    /// <summary>
    /// Validates that <paramref name="bundle"/> has no active transfer blockers and carries complete,
    /// self-consistent identity, ownership, movement, vitals, inventory, quest timers, cooldowns, and aura state.
    /// </summary>
    public static PlayerTransferValidationResult ValidateBundle(PlayerTransferBundle? bundle)
    {
        if (bundle is null)
            return PlayerTransferValidationResult.Fail(ErrorNullBundle, "Transfer bundle must not be null.");

        PlayerTransferValidationResult blockerCheck = ValidateBlockers(bundle.Blockers);
        if (!blockerCheck.IsValid)
            return blockerCheck;

        var errors = new List<string>();
        string? primaryCode = null;

        void AddError(string code, string message)
        {
            primaryCode ??= code;
            errors.Add(message);
        }

        // 1. Identity & Security
        if (bundle.TransferId == Guid.Empty)
            AddError(ErrorInvalidIdentity, "TransferId must be non-empty.");
        if (bundle.CharacterGuid == 0)
            AddError(ErrorInvalidIdentity, "CharacterGuid must be non-zero.");
        if (bundle.AccountId <= 0)
            AddError(ErrorInvalidIdentity, "AccountId must be positive.");
        if (bundle.SessionGeneration <= 0)
            AddError(ErrorInvalidIdentity, "SessionGeneration must be positive.");

        // 2. Source & Target Workers
        if (bundle.SourceWorkerId.IsEmpty)
            AddError(ErrorInvalidWorkers, "SourceWorkerId must not be empty.");
        if (bundle.TargetWorkerId.IsEmpty)
            AddError(ErrorInvalidWorkers, "TargetWorkerId must not be empty.");
        if (!bundle.SourceWorkerId.IsEmpty && bundle.SourceWorkerId == bundle.TargetWorkerId)
            AddError(ErrorInvalidWorkers, "SourceWorkerId and TargetWorkerId must be distinct for cross-worker transfer.");

        // 3. Ownership Token
        if (bundle.SourceOwnershipToken is null)
        {
            AddError(ErrorInvalidOwnershipToken, "SourceOwnershipToken is required.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(bundle.SourceOwnershipToken.Scope))
                AddError(ErrorInvalidOwnershipToken, "SourceOwnershipToken.Scope must not be empty.");
            if (bundle.SourceOwnershipToken.Epoch <= 0)
                AddError(ErrorInvalidOwnershipToken, "SourceOwnershipToken.Epoch must be positive.");
            if (bundle.SourceOwnershipToken.Incarnation == Guid.Empty)
                AddError(ErrorInvalidOwnershipToken, "SourceOwnershipToken.Incarnation must be non-empty.");
        }

        // 4. Destination & Movement
        if (float.IsNaN(bundle.TargetPosition.X) || float.IsNaN(bundle.TargetPosition.Y) || float.IsNaN(bundle.TargetPosition.Z) ||
            float.IsInfinity(bundle.TargetPosition.X) || float.IsInfinity(bundle.TargetPosition.Y) || float.IsInfinity(bundle.TargetPosition.Z))
        {
            AddError(ErrorInvalidVitals, "TargetPosition coordinates must be finite numbers.");
        }

        if (bundle.SpeedRates is null || bundle.SpeedRates.Run <= 0f || bundle.SpeedRates.Walk <= 0f)
        {
            AddError(ErrorInvalidVitals, "SpeedRates must be present with positive Walk and Run speeds.");
        }

        // 5. Vitals & State
        if (bundle.Level == 0)
            AddError(ErrorInvalidVitals, "Player Level must be at least 1.");
        if (!bundle.IsGhost && bundle.Corpse is null && bundle.Health == 0)
            AddError(ErrorInvalidVitals, "Living player (not ghost and no released corpse) must have Health > 0.");
        if (bundle.Powers is null || bundle.Powers.Count < 5)
            AddError(ErrorInvalidVitals, "Powers list must contain all 5 power slots (mana, rage, focus, energy, happiness).");

        // 6. Inventory & Equipment
        if (bundle.Items is null)
        {
            AddError(ErrorInvalidInventory, "Items collection must not be null.");
        }
        else
        {
            var seenItemGuids = new HashSet<uint>();
            foreach (TransferItemSnapshot item in bundle.Items)
            {
                if (item.ItemGuid == 0 || item.Entry == 0 || item.Count == 0)
                {
                    AddError(ErrorInvalidInventory, $"Invalid item snapshot (Guid={item.ItemGuid}, Entry={item.Entry}, Count={item.Count}).");
                }
                else if (!seenItemGuids.Add(item.ItemGuid))
                {
                    AddError(ErrorInvalidInventory, $"Duplicate ItemGuid {item.ItemGuid} in transfer bundle.");
                }

                if (item.MaxDurability > 0 && item.Durability > item.MaxDurability)
                {
                    AddError(ErrorInvalidInventory, $"Item {item.ItemGuid} durability ({item.Durability}) exceeds max durability ({item.MaxDurability}).");
                }
            }
        }

        // 7. Quests & Active Timers
        if (bundle.Quests is null)
        {
            AddError(ErrorInvalidQuests, "Quests collection must not be null.");
        }
        else
        {
            foreach (TransferQuestSlotSnapshot quest in bundle.Quests)
            {
                if (quest.QuestId == 0)
                    AddError(ErrorInvalidQuests, $"Quest slot {quest.Slot} has QuestId 0.");
                if (quest.RemainingTimerMs < 0)
                    AddError(ErrorInvalidQuests, $"Quest {quest.QuestId} has negative RemainingTimerMs ({quest.RemainingTimerMs}).");
                if (quest.ObjectiveCounters is null)
                    AddError(ErrorInvalidQuests, $"Quest {quest.QuestId} has null ObjectiveCounters.");
            }
        }

        // 8. Spells, Cooldowns & Auras
        if (bundle.KnownSpells is null || bundle.Cooldowns is null || bundle.Auras is null)
        {
            AddError(ErrorInvalidSpellsOrAuras, "KnownSpells, Cooldowns, and Auras collections must not be null.");
        }
        else
        {
            foreach (TransferCooldownSnapshot cd in bundle.Cooldowns)
            {
                if (cd.SpellId == 0)
                    AddError(ErrorInvalidSpellsOrAuras, "Cooldown snapshot has SpellId 0.");
                if (cd.RemainingDurationMs < 0 || cd.CategoryRemainingDurationMs < 0)
                    AddError(ErrorInvalidSpellsOrAuras, $"Cooldown for spell {cd.SpellId} has negative remaining duration.");
            }

            foreach (TransferAuraSnapshot aura in bundle.Auras)
            {
                if (aura.SpellId == 0)
                    AddError(ErrorInvalidSpellsOrAuras, "Aura snapshot has SpellId 0.");
                if (aura.StackAmount <= 0)
                    AddError(ErrorInvalidSpellsOrAuras, $"Aura {aura.SpellId} must have StackAmount >= 1.");
                if (aura.MaxDurationMs > 0 && aura.RemainingDurationMs < 0)
                    AddError(ErrorInvalidSpellsOrAuras, $"Timed aura {aura.SpellId} has negative RemainingDurationMs ({aura.RemainingDurationMs}).");
                if (aura.BasePoints is null || aura.BasePoints.Count == 0)
                    AddError(ErrorInvalidSpellsOrAuras, $"Aura {aura.SpellId} must carry effect BasePoints.");
            }
        }

        return errors.Count == 0
            ? PlayerTransferValidationResult.Success
            : new PlayerTransferValidationResult(false, primaryCode, errors);
    }
}

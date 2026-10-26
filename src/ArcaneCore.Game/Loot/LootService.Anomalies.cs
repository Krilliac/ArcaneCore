using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Outcome checks for loot (H1), reported through <see cref="AnomalyLedger"/> as subsystem <c>loot</c>:
/// a template that guarantees a drop yielding an empty bag, an item or money paid out twice, a loot window left open
/// after its source is gone, and (in <see cref="LootRollManager"/>) a roll that outlived its timeout. World thread only.
/// </summary>
public sealed partial class LootService
{
    /// <summary>The anomaly subsystem of every loot check.</summary>
    public const string AnomalySubsystem = "loot";

    /// <summary>The template has a guaranteed (chance 100, ungrouped, unconditioned, known item) row yet the generated bag holds no item.</summary>
    public const string GuaranteedDropMissingCode = "guaranteed-drop-missing";

    /// <summary>An item stack was awarded that had already been taken (a duplicate); the award is refused.</summary>
    public const string ItemDuplicatedCode = "item-duplicated";

    /// <summary>The money of a bag was paid out after it had already been paid; the payout is refused.</summary>
    public const string MoneyTwiceCode = "money-looted-twice";

    /// <summary>A player's loot window stayed open after its source was despawned, decayed or replaced; it is closed.</summary>
    public const string WindowLeftOpenCode = "window-left-open";

    /// <summary>A roll was still running well past its timeout.</summary>
    public const string RollUnresolvedCode = "roll-unresolved";

    /// <summary>A chest-like object was opened and its loot is empty although its loot id promises something (or names no usable rows).</summary>
    public const string GameObjectLootEmptyCode = "gameobject-loot-empty";

    /// <summary>The most reference_loot_template hops <see cref="GuaranteeOf"/> follows.</summary>
    internal const int GuaranteeMaxReferenceDepth = 4;

    /// <summary>How often <see cref="AuditOpenWindows"/> runs from the roll manager's map update.</summary>
    internal const uint WindowAuditIntervalMs = 1000;

    /// <summary>Audits a window must fail in a row before it is reported (the source may be mid-despawn for a tick).</summary>
    internal const int WindowAuditStrikes = 2;

    private Dictionary<Player, (LootBag Bag, int Strikes)>? _windowStrikes;
    private List<(Player Player, LootBag Bag)>? _windowScratch;

    /// <summary>
    /// After a roll produced no item: report when the template holds a row that must always drop. Reads the rows only for an empty bag.
    /// </summary>
    internal void CheckGuaranteedDrops(ObjectGuid source, LootTableKind table, uint entry)
    {
        if (Items is null)
        {
            return; // without item templates every row is dropped as an unknown item
        }

        foreach (LootStoreRow row in Content.GetRows(table, entry))
        {
            if (row.MinCountOrRef > 0 && row.GroupId == 0 && row.ChanceOrQuestChance >= 100.0f && row.ConditionId == 0
                && Items.Find(row.Item) is not null)
            {
                AnomalyLedger.Shared.Report(AnomalySubsystem, GuaranteedDropMissingCode,
                    string.Create(CultureInfo.InvariantCulture, $"loot table {table} entry {entry}"),
                    string.Create(CultureInfo.InvariantCulture, $"item {row.Item} drops with chance {row.ChanceOrQuestChance} but the bag generated for source {source.Value:X} holds no item"));
                return;
            }
        }
    }

    /// <summary>
    /// The first row of template <paramref name="entry"/> that guarantees an item to at least one of <paramref name="recipients"/>, or null.
    /// Guaranteed means: an ungrouped item row with |chance| of 100 or more whose item is known and whose condition passes for a recipient
    /// (a quest row also needs a recipient who needs the item); a group whose explicit chances sum to 100 or more, or that has equal-chance
    /// rows, when every row of the group qualifies the same way; a reference row of chance 100 or more into a template that itself
    /// guarantees (at most <see cref="GuaranteeMaxReferenceDepth"/> hops; the reference's condition narrows the recipients).
    /// Allocates; call it from a failure branch only.
    /// </summary>
    internal LootStoreRow? GuaranteeOf(LootTableKind table, uint entry, IReadOnlyList<Player> recipients, int depth = 0)
    {
        if (Items is null || depth > GuaranteeMaxReferenceDepth)
        {
            return null;
        }

        IReadOnlyList<LootStoreRow> rows = Content.GetRows(table, entry);
        foreach (LootStoreRow row in rows)
        {
            if (row.GroupId != 0 && row.MinCountOrRef > 0)
            {
                continue; // a group row: judged as a group below
            }

            if (Math.Abs(row.ChanceOrQuestChance) < 100.0f)
            {
                continue;
            }

            if (row.MinCountOrRef < 0)
            {
                if (row.MaxCount > 0 && row.GroupId == 0)
                {
                    List<Player> passing = [.. recipients.Where(p => ConditionPasses(p, row.ConditionId))];
                    if (passing.Count > 0 && GuaranteeOf(LootTableKind.Reference, (uint)-row.MinCountOrRef, passing, depth + 1) is not null)
                    {
                        return row;
                    }
                }
            }
            else if (CanYield(row, recipients))
            {
                return row;
            }
        }

        foreach (IGrouping<byte, LootStoreRow> group in rows.Where(r => r.GroupId > 0 && r.MinCountOrRef > 0).GroupBy(r => r.GroupId))
        {
            float explicitSum = 0;
            bool hasEqual = false;
            bool allYield = true;
            foreach (LootStoreRow row in group)
            {
                float chance = Math.Abs(row.ChanceOrQuestChance);
                explicitSum += chance;
                hasEqual |= chance == 0;
                allYield &= CanYield(row, recipients);
            }

            if (allYield && (explicitSum >= 100.0f || hasEqual))
            {
                return group.First();
            }
        }

        return null;
    }

    private bool ConditionPasses(Player player, uint conditionId)
        => conditionId == 0 || Conditions?.Invoke(player, conditionId) == true;

    /// <summary>The row's item is known and some recipient passes its condition (and, for a quest row, needs the item).</summary>
    private bool CanYield(LootStoreRow row, IReadOnlyList<Player> recipients)
    {
        if (Items?.Find(row.Item) is null)
        {
            return false;
        }

        foreach (Player player in recipients)
        {
            if (ConditionPasses(player, row.ConditionId) && (row.ChanceOrQuestChance >= 0 || Quests?.NeedsQuestItem(player, row.Item) == true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// After a chest-like object generated an empty bag with no money: report when its loot id is real and either names no rows, names
    /// only unknown items, or <see cref="GuaranteeOf"/> finds a row that must have dropped. Loot id 0 (a deliberately lootless object) never reports.
    /// Strings are built only when a report is due.
    /// </summary>
    private void CheckGameObjectLootEmpty(Player opener, GameObject go, uint lootId, LootBag bag, IReadOnlyList<Player> recipients)
    {
        if (lootId == 0 || !bag.IsEmpty || bag.Gold != 0)
        {
            return;
        }

        IReadOnlyList<LootStoreRow> rows = Content.GetRows(LootTableKind.GameObject, lootId);
        string reason;
        string row = "-";
        if (rows.Count == 0)
        {
            reason = "no-rows";
        }
        else if (!rows.Any(r => r.MinCountOrRef < 0) && rows.All(r => Items?.Find(r.Item) is null))
        {
            reason = "all-unknown";
        }
        else if (GuaranteeOf(LootTableKind.GameObject, lootId, recipients) is { } guaranteed)
        {
            reason = "guaranteed-missing";
            row = string.Create(CultureInfo.InvariantCulture, $"item {guaranteed.Item} ref {guaranteed.MinCountOrRef} chance {guaranteed.ChanceOrQuestChance} group {guaranteed.GroupId}");
        }
        else
        {
            return;
        }

        string where = go.Spawn is { } spawn
            ? string.Create(CultureInfo.InvariantCulture, $"spawn {spawn.Guid}")
            : "no spawn";
        AnomalyLedger.Shared.Report(AnomalySubsystem, GameObjectLootEmptyCode,
            string.Create(CultureInfo.InvariantCulture, $"gameobject entry {go.Entry} loot {lootId}"),
            string.Create(CultureInfo.InvariantCulture,
                $"{where}, map {go.MapId} at {go.X:0.##} {go.Y:0.##} {go.Z:0.##}, type {go.Template.Type}, reason {reason}, row {row}, opener guid {opener.Guid.Low} opened it and the bag holds nothing"),
            AnomalySeverity.Error);
    }

    private static string SubjectOf(LootBag bag)
        => string.Create(CultureInfo.InvariantCulture, $"loot source {bag.Source.Value:X} ({bag.Kind})");

    /// <summary>
    /// Close the windows whose source is gone. A window is stale when its bag is no longer the one registered for its source, or the
    /// source creature left the world / stopped being a corpse, or the chest despawned, or the looter is in another map than the source.
    /// It must be seen stale on <see cref="WindowAuditStrikes"/> audits in a row, then it is reported once and released. A looter who is
    /// not in a world is skipped (<see cref="OnPlayerLeft"/> owns that). Cost: one pass over the open windows (a handful) per call.
    /// </summary>
    internal void AuditOpenWindows()
    {
        if (_open.Count == 0)
        {
            if (_windowStrikes is { Count: > 0 })
            {
                _windowStrikes.Clear();
            }

            return;
        }

        List<(Player Player, LootBag Bag)>? stale = null;
        foreach ((Player player, LootBag bag) in _open)
        {
            if (!player.IsInWorld || !IsWindowStale(player, bag))
            {
                if (_windowStrikes is { Count: > 0 })
                {
                    _windowStrikes.Remove(player);
                }

                continue;
            }

            _windowStrikes ??= new Dictionary<Player, (LootBag, int)>(ReferenceEqualityComparer.Instance);
            int strikes = _windowStrikes.TryGetValue(player, out (LootBag Bag, int Strikes) previous) && ReferenceEquals(previous.Bag, bag)
                ? previous.Strikes + 1
                : 1;
            _windowStrikes[player] = (bag, strikes);
            if (strikes >= WindowAuditStrikes)
            {
                (stale ??= _windowScratch ??= []).Add((player, bag));
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach ((Player player, LootBag bag) in stale)
        {
            _windowStrikes?.Remove(player);
            AnomalyLedger.Shared.Report(AnomalySubsystem, WindowLeftOpenCode, SubjectOf(bag),
                string.Create(CultureInfo.InvariantCulture, $"{player.Name} (guid {player.Guid.Low}) still had the loot window open after its source was gone; released"));
            Release(player, bag.Source);
        }

        stale.Clear();
    }

    private bool IsWindowStale(Player player, LootBag bag)
    {
        if (!_bags.TryGetValue(bag.Source, out var entry) || !ReferenceEquals(entry.Bag, bag))
        {
            return true;
        }

        return entry.Source switch
        {
            Creature creature => !creature.IsInWorld || !ReferenceEquals(player.Map, creature.Map)
                || (bag.ReleaseHandler is null && creature.DeathState != CreatureDeathState.Corpse),
            GameObject go => !go.IsSpawned || !ReferenceEquals(player.Map, go.Map),
            _ => false, // an item's loot lives with its owner's inventory
        };
    }
}

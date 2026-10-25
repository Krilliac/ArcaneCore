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

    /// <summary>Yards beyond the loot distance a looter may be before the audit treats the window as stale.</summary>
    internal const float WindowAuditDistanceMargin = 5f;

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
                || (bag.ReleaseHandler is null && creature.DeathState != CreatureDeathState.Corpse)
                || LooterCannotReach(player, creature, bag),
            GameObject go => !go.IsSpawned || !ReferenceEquals(player.Map, go.Map) || LooterCannotReach(player, go, bag),
            _ => false, // an item's loot lives with its owner's inventory
        };
    }

    /// <summary>
    /// The looter is dead, or further from the source than <see cref="Options"/>.LootDistance plus <see cref="WindowAuditDistanceMargin"/>.
    /// A live looter holding a window open past that is one whose client lost it (vmangos releases it on movement, on a cast and on a
    /// stun); left open it keeps a node "in use" for everyone else. Sources that ignore distance (a bobber's window) skip the distance test.
    /// </summary>
    private bool LooterCannotReach(Player player, WorldObject source, LootBag bag)
    {
        if (!player.IsAlive)
        {
            return true;
        }

        return !bag.IgnoreDistance && bag.SourceCheck is null && Distance3D(player, source) > Options.LootDistance + WindowAuditDistanceMargin;
    }
}

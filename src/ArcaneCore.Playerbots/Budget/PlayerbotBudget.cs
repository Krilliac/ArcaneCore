using System.Globalization;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// What a purchase is for, in priority order: a spend site asks the budget what it may spend for its kind, and the answer is the bot's
/// money less the costs of every kind before it (<see cref="PlayerbotBudgetMath.Spendable"/>). Professions are listed for completeness
/// only: managed bots have no professions (no bot code trains or uses one), so there is no cost to reserve.
/// </summary>
internal enum PlayerbotSpendKind : byte
{
    /// <summary>Repairs: never blocked, and reserved against everything else while the gear is badly worn.</summary>
    Repair,

    /// <summary>A small stock of food, drink and ammunition: always allowed up to <see cref="PlayerbotBudgetOptions.EssentialsAllowanceCopper"/>.</summary>
    Essentials,

    /// <summary>The class spells due now and at the next levels.</summary>
    ClassTraining,

    /// <summary>The mount of a bot that has learned to ride.</summary>
    Mount,

    /// <summary>The next riding tier's training and the cheapest mount that tier lets the bot use.</summary>
    Riding,

    /// <summary>Profession training (nothing reserves for it: bots have no professions).</summary>
    Profession,

    /// <summary>Everything optional: auction house upgrade purchases.</summary>
    Discretionary,
}

/// <summary>One cost a bot saves for.</summary>
internal readonly record struct PlayerbotBudgetLine(PlayerbotSpendKind Kind, uint Cost);

/// <summary>The budget's arithmetic over plain values (tested without a world).</summary>
internal static class PlayerbotBudgetMath
{
    /// <summary>The sum of the costs of the lines that rank before <paramref name="kind"/>, saturating at <see cref="uint.MaxValue"/>.</summary>
    internal static uint ReservedAbove(ReadOnlySpan<PlayerbotBudgetLine> lines, PlayerbotSpendKind kind)
    {
        ulong sum = 0;
        foreach (PlayerbotBudgetLine line in lines)
        {
            if (line.Kind < kind) sum += line.Cost;
        }

        return (uint)Math.Min(sum, uint.MaxValue);
    }

    /// <summary>The sum of two costs, saturating at <see cref="uint.MaxValue"/>.</summary>
    internal static uint Sum(uint a, uint b) => (uint)Math.Min((ulong)a + b, uint.MaxValue);

    /// <summary>
    /// What a purchase of <paramref name="kind"/> may cost. Repairs: all the money. Food, drink and ammunition: all the money the
    /// reserves leave over, or the allowance when that is more (a saving bot never starves). Discretionary: the money the reserves leave
    /// over, or <paramref name="floorFraction"/> of the money when that is more (a reserve the bot may never reach does not stop its
    /// optional buying for good). Anything else: the money less the lines that rank before it.
    /// </summary>
    internal static uint Spendable(ReadOnlySpan<PlayerbotBudgetLine> lines, PlayerbotSpendKind kind, uint money, uint essentialsAllowance,
        float floorFraction = 0f)
    {
        switch (kind)
        {
            case PlayerbotSpendKind.Repair:
                return money;
            case PlayerbotSpendKind.Essentials:
                uint afterRepair = money - Math.Min(money, ReservedAbove(lines, PlayerbotSpendKind.Essentials));
                uint left = money - Math.Min(money, ReservedAbove(lines, PlayerbotSpendKind.Discretionary));
                return Math.Min(afterRepair, Math.Max(left, essentialsAllowance));
            case PlayerbotSpendKind.Discretionary:
                uint over = money - Math.Min(money, ReservedAbove(lines, kind));
                // The floor never takes a worn bot below its repair cost.
                uint floorCap = money - Math.Min(money, ReservedAbove(lines, PlayerbotSpendKind.Essentials));
                uint floor = !float.IsFinite(floorFraction) || floorFraction <= 0f ? 0u : Math.Min((uint)Math.Min(Math.Floor(money * (double)Math.Min(floorFraction, 1f)), money), floorCap);
                return Math.Max(over, floor);
            default:
                return money - Math.Min(money, ReservedAbove(lines, kind));
        }
    }
}

/// <summary>Where a bot stands on the way to riding (<see cref="PlayerbotBudget.Riding"/>).</summary>
internal enum PlayerbotRidingStep : byte
{
    /// <summary>Mounts are off, or the data or the bot's map has no riding trainer or no mount vendor it can reach, or a trainer refuses it.</summary>
    None,

    /// <summary>The next riding tier is more than <see cref="PlayerbotMountOptions.SaveLevelsAhead"/> levels away: nothing is reserved yet.</summary>
    Distant,

    /// <summary>The bot keeps the tier's training and mount cost aside and has not got it all (or has not the level) yet.</summary>
    Saving,

    /// <summary>The level and the money are there: the next step is the riding trainer.</summary>
    Train,

    /// <summary>The bot can ride but owns no mount of its tier: the next step is a mount vendor.</summary>
    BuyMount,

    /// <summary>Every riding tier is learned and the bot has a mount of the highest.</summary>
    Done,
}

/// <summary>The riding plan of a bot, refreshed with its budget; one instance per bot, reused (no allocation per refresh).</summary>
internal sealed class PlayerbotRidingStatus
{
    internal PlayerbotRidingStep Step { get; set; }
    internal uint TierLevel { get; set; }
    /// <summary>The tier of the next riding training.</summary>
    internal int TierIndex { get; set; }

    /// <summary>The tier the planned mount belongs to (the highest learned tier when the bot can ride; else the next training's): a mount of another tier is not what it buys.</summary>
    internal int MountTierIndex { get; set; }
    internal uint TeachingSpell { get; set; }
    internal uint TrainerEntry { get; set; }
    internal uint TrainingCost { get; set; }
    internal uint MountCost { get; set; }
    internal uint MountItem { get; set; }
    internal uint MountVendor { get; set; }
    internal bool HasMount { get; set; }

    internal void Reset()
    {
        Step = PlayerbotRidingStep.None;
        TierLevel = 0; TierIndex = 0; MountTierIndex = 0; TeachingSpell = 0; TrainerEntry = 0; TrainingCost = 0; MountCost = 0; MountItem = 0; MountVendor = 0;
        HasMount = false;
    }
}

/// <summary>
/// One bot's budget: the money it saves for, as an ordered list of costs read from the world data (<see cref="PlayerbotServiceIndex"/>),
/// and the answer to "how much may this purchase cost?" for every spend site (<see cref="Spendable"/>). Owned by the bot's brain; world
/// thread only. Recomputed when the bot's level or map changes, a spend site calls <see cref="Invalidate"/>, or
/// <see cref="PlayerbotBudgetOptions.RefreshSeconds"/> have passed, never per think: a steady-state call is a few comparisons and
/// allocates nothing; a recomputation allocates a little (the trainer offer lists), once per refresh interval. Without a budget (a null
/// one in a spend site) or with <c>Budget:Enabled</c> off, every spend is allowed to the full money.
/// </summary>
internal sealed class PlayerbotBudget(WorldSession session, PlayerbotOptions options)
{
    private const int MaxLines = 5;
    private const int MaxTrainerTries = 4;

    private readonly PlayerbotBudgetLine[] _lines = new PlayerbotBudgetLine[MaxLines];
    private int _count;
    private bool _dirty = true;
    private bool _pinned;
    private uint _level;
    private uint _mapId = uint.MaxValue;
    private uint _nextRefreshAt;
    private uint _classIdleLevel = uint.MaxValue;
    private int _classIdleAhead = -1;
    private PlayerbotServiceIndex? _classIdleIndex;
    private uint _classIdleMap = uint.MaxValue;

    /// <summary>The riding plan.</summary>
    internal PlayerbotRidingStatus Riding { get; } = new();

    /// <summary>What the class trainer will teach: the learnable spells and those up to <see cref="PlayerbotBudgetOptions.ClassLevelsAhead"/> levels ahead.</summary>
    internal TrainerDue ClassDue { get; private set; }

    /// <summary>The cost of repairing all worn gear while it is badly worn (<see cref="PlayerbotTownGoals.NeedsRepairTrip"/>), else 0.</summary>
    internal uint RepairCost { get; private set; }

    /// <summary>The reserved lines, highest priority first.</summary>
    internal ReadOnlySpan<PlayerbotBudgetLine> Lines => _lines.AsSpan(0, _count);

    /// <summary>Sum of all lines.</summary>
    internal uint Reserved => PlayerbotBudgetMath.ReservedAbove(Lines, PlayerbotSpendKind.Discretionary);

    internal bool Enabled => options.Budget.Enabled;

    /// <summary>Recompute at the next ask (a level, a learned spell or a purchase changed what is due).</summary>
    internal void Invalidate() => _dirty = true;

    /// <summary>Holds the budget at exactly these lines and never recomputes them (tests that need a reserve the data cannot make).</summary>
    internal void Pin(params PlayerbotBudgetLine[] lines)
    {
        _pinned = true;
        _count = 0;
        foreach (PlayerbotBudgetLine line in lines.Take(MaxLines)) _lines[_count++] = line;
    }

    /// <summary>What a purchase of <paramref name="kind"/> may cost the bot now.</summary>
    internal uint Spendable(PlayerbotSpendKind kind, Player player)
    {
        if (!options.Budget.Enabled) return player.Money;
        Refresh(player);
        return PlayerbotBudgetMath.Spendable(Lines, kind, player.Money, options.Budget.EssentialsAllowanceCopper,
            options.Budget.DiscretionaryFloorFraction);
    }

    /// <summary>Whether a purchase of <paramref name="price"/> of <paramref name="kind"/> is within what the bot may spend.</summary>
    internal bool MayAfford(PlayerbotSpendKind kind, uint price, Player player) => price <= Spendable(kind, player);

    /// <summary>Riding training is due: the bot has the level, and its spendable money covers the tier's training and its mount.</summary>
    internal bool TrainingDue(Player player)
    {
        if (!options.Budget.Enabled || !options.Mounts.Enabled) return false;
        Refresh(player);
        PlayerbotRidingStatus r = Riding;
        return r.Step is PlayerbotRidingStep.Saving or PlayerbotRidingStep.Train && player.Level >= r.TierLevel
            && PlayerbotBudgetMath.Spendable(Lines, PlayerbotSpendKind.Riding, player.Money, 0) >= PlayerbotBudgetMath.Sum(r.TrainingCost, r.MountCost);
    }

    /// <summary>A mount purchase is due: the bot can ride, owns no mount of its tier, and its spendable money covers the cheapest it can use.</summary>
    internal bool MountPurchaseDue(Player player)
    {
        if (!options.Budget.Enabled || !options.Mounts.Enabled) return false;
        Refresh(player);
        return Riding.Step == PlayerbotRidingStep.BuyMount && Riding.MountCost != 0
            && PlayerbotBudgetMath.Spendable(Lines, PlayerbotSpendKind.Mount, player.Money, 0) >= Riding.MountCost;
    }

    /// <summary>Failed walks to one mount vendor before the plan goes to the next cheapest one.</summary>
    internal const int VendorFailureLimit = 3;

    /// <summary>How long a vendor that failed <see cref="VendorFailureLimit"/> times is left out of the plan.</summary>
    internal const uint VendorExclusionMs = 600_000;

    private readonly Dictionary<uint, (int Failures, uint Until)> _vendorFailures = [];

    /// <summary>A trip to mount vendor <paramref name="entry"/> was set aside (refused, stalled, unreachable). After <see cref="VendorFailureLimit"/> the plan prices another vendor.</summary>
    internal void NoteVendorFailure(uint entry)
    {
        uint now = session.World.NowMs;
        (int failures, uint until) = _vendorFailures.GetValueOrDefault(entry);
        if (failures >= VendorFailureLimit && unchecked((int)(now - until)) >= 0) failures = 0;
        failures++;
        _vendorFailures[entry] = (failures, failures >= VendorFailureLimit ? unchecked(now + VendorExclusionMs) : 0);
        if (failures >= VendorFailureLimit)
        {
            _dirty = true;
            if (!session.IsInChaosProbe && session.Player is { } player)
                PlayerbotAnomalies.Report(player, "mount", "riding-refused", $"mount vendor {entry} excluded after {failures} failed visits");
        }
    }

    /// <summary>The vendor has failed too often lately to plan a mount purchase at.</summary>
    internal bool VendorExcluded(uint entry)
        => _vendorFailures.TryGetValue(entry, out (int Failures, uint Until) state) && state.Failures >= VendorFailureLimit
            && unchecked((int)(session.World.NowMs - state.Until)) < 0;

    /// <summary>Recompute the lines when they are stale.</summary>
    internal void Refresh(Player player)
    {
        if (_pinned) return;
        uint now = session.World.NowMs;
        uint map = player.Map?.MapId ?? player.MapId;
        if (!_dirty && player.Level == _level && map == _mapId && unchecked((int)(now - _nextRefreshAt)) < 0) return;
        _dirty = false;
        _level = player.Level;
        _mapId = map;
        _nextRefreshAt = unchecked(now + (uint)options.Budget.RefreshSeconds * 1000u);
        _count = 0;
        ClassDue = default;
        RepairCost = 0;
        Riding.Reset();
        if (!options.Budget.Enabled || session.Services.GetService<QuestNpcFeature>()?.Services is not { } services
            || services.Deps.Spells is not { } learner)
            return;

        if (PlayerbotTownGoals.NeedsRepairTrip(player) && PlayerbotTownGoals.TotalWornRepair(player, services) is { } repair and > 0)
        {
            RepairCost = repair;
            Add(PlayerbotSpendKind.Repair, repair);
        }

        PlayerbotServiceIndex? index = PlayerbotServiceIndex.Get(session, services, player);
        if (index is null) return;
        FactionTemplateCatalog? factions = session.Services.GetService<QuestNpcFeature>()?.FactionTemplates;
        ClassDue = ClassTrainingDue(player, services, index, factions);
        Add(PlayerbotSpendKind.ClassTraining, ClassDue.Total);
        if (options.Mounts.Enabled) PlanRiding(player, services, learner, index, factions);
    }

    private void Add(PlayerbotSpendKind kind, uint cost)
    {
        if (cost != 0 && _count < MaxLines) _lines[_count++] = new PlayerbotBudgetLine(kind, cost);
    }

    private TrainerDue ClassTrainingDue(Player player, QuestNpcServices services, PlayerbotServiceIndex index, FactionTemplateCatalog? factions)
    {
        int ahead = options.Budget.ClassLevelsAhead;
        // Nothing due stays nothing due until the level (or the index, or the setting) changes: the trainer's rows only open with levels.
        uint map = player.Map?.MapId ?? player.MapId;
        if (_classIdleLevel == player.Level && _classIdleAhead == ahead && _classIdleMap == map && ReferenceEquals(_classIdleIndex, index)) return default;
        int tries = 0;
        foreach (PlayerbotServiceIndex.Site site in index.ClassTrainers((byte)player.Class))
        {
            if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, site.FactionTemplate)) continue;
            if (OnMap(site.Spawns, map) is not { } spawn) continue; // a trainer on another map is not one the bot can walk to
            if (++tries > MaxTrainerTries) break;
            NpcInfo hint = PlayerbotServiceIndex.Hint(site, spawn, NpcFlags.Trainer);
            TrainerDue due = services.DueTrainerCost(player, hint, TrainerType.Class, player.Level + (uint)ahead);
            if (due.Count > 0) return due;
        }

        _classIdleLevel = player.Level;
        _classIdleAhead = ahead;
        _classIdleIndex = index;
        _classIdleMap = map;
        return default;
    }

    private static CreatureSpawn? OnMap(IReadOnlyList<CreatureSpawn> spawns, uint map)
    {
        for (int i = 0; i < spawns.Count; i++)
        {
            if (spawns[i].MapId == map) return spawns[i];
        }

        return null;
    }

    private void PlanRiding(Player player, QuestNpcServices services, ISpellLearner learner, PlayerbotServiceIndex index, FactionTemplateCatalog? factions)
    {
        PlayerbotRidingStatus r = Riding;
        if (session.Services.GetService<SpellFeature>()?.System.Store is not { } store) return;
        uint map = player.Map?.MapId ?? player.MapId;
        foreach (PlayerbotServiceIndex.Site site in index.MountsTrainers((byte)player.Race))
        {
            // A trainer the bot cannot walk to (none on its map) is no plan: reserving for it would save for ever.
            if (!PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, site.FactionTemplate)
                || OnMap(site.Spawns, map) is not { } spawn) continue;
            NpcInfo hint = PlayerbotServiceIndex.Hint(site, spawn, NpcFlags.Trainer);
            IReadOnlyList<TrainerOffer> offers = services.TrainerOffers(player, hint, TrainerType.Mounts, includeKnown: true);
            if (offers.Count == 0) continue;
            r.TrainerEntry = site.Entry;
            int learned = 0, next = -1;
            for (int i = 0; i < offers.Count; i++)
            {
                if (offers[i].State == TrainerSpellState.Gray) learned++;
                else if (next < 0) next = i;
            }

            if (learned > 0)
            {
                // The mount must be one of the highest learned tier: a mount of an earlier tier does not count (Journeyman wants the epic one).
                int tier = learned - 1;
                r.HasMount = PlayerbotMountRules.HasMountOfTier(player, store, learner, index.RankFloor(tier));
                if (!r.HasMount)
                {
                    r.TierIndex = tier;
                    r.MountTierIndex = tier;
                    r.TierLevel = player.Level;
                    PriceMount(player, services, index, factions, tier, planning: false);
                    if (r.MountCost == 0) { r.Step = PlayerbotRidingStep.None; return; } // no vendor of the bot's map sells it one
                    r.Step = PlayerbotRidingStep.BuyMount;
                    Add(PlayerbotSpendKind.Mount, r.MountCost);
                    if (next >= 0) AddNextTier(player, offers[next], next, services, learner, store, index, factions, withMount: false);
                    return;
                }
            }

            if (next < 0)
            {
                r.Step = PlayerbotRidingStep.Done;
                return;
            }

            AddNextTier(player, offers[next], next, services, learner, store, index, factions, withMount: true);
            return;
        }
    }

    private void AddNextTier(Player player, TrainerOffer offer, int tier, QuestNpcServices services, ISpellLearner learner, SpellStore store,
        PlayerbotServiceIndex index, FactionTemplateCatalog? factions, bool withMount)
    {
        PlayerbotRidingStatus r = Riding;
        uint levelsToGo = offer.RequiredLevel > player.Level ? offer.RequiredLevel - player.Level : 0;
        if (levelsToGo > (uint)options.Mounts.SaveLevelsAhead)
        {
            if (withMount) r.Step = PlayerbotRidingStep.Distant;
            return;
        }

        // Red for a reason no level mends (a skill it lacks): the trip would loop, so nothing is saved for it.
        if (offer.State == TrainerSpellState.Red && offer.RequiredLevel <= player.Level)
        {
            if (withMount) r.Step = PlayerbotRidingStep.None;
            return;
        }

        uint mountCost = r.MountCost;
        if (withMount) { r.MountTierIndex = tier; }
        r.TierIndex = tier;
        r.TierLevel = offer.RequiredLevel;
        r.TeachingSpell = offer.TeachingSpell;
        r.TrainingCost = offer.Cost;
        if (withMount)
        {
            r.MountCost = 0;
            r.HasMount = PlayerbotMountRules.HasMountOfTier(player, store, learner, index.RankFloor(tier));
            if (!r.HasMount)
            {
                PriceMount(player, services, index, factions, tier, planning: true);
                if (r.MountCost == 0) { r.Step = PlayerbotRidingStep.None; r.TrainingCost = 0; return; } // no reachable vendor: nothing to save for
            }
        }
        else
        {
            r.MountCost = mountCost;
        }

        Add(PlayerbotSpendKind.Riding, PlayerbotBudgetMath.Sum(offer.Cost, withMount ? r.MountCost : 0));
        if (withMount) r.Step = player.Level >= offer.RequiredLevel && offer.State == TrainerSpellState.Green ? PlayerbotRidingStep.Train : PlayerbotRidingStep.Saving;
    }

    /// <summary>
    /// The cheapest mount of the tier a vendor on the bot's map sells it (<paramref name="planning"/>: before the training, by the tier's
    /// required level; else by what its inventory lets it use now). A mount of the tier asks a riding skill rank above the previous tier's
    /// and up to this tier's (<see cref="PlayerbotMountRules.FitsTier"/>).
    /// </summary>
    private void PriceMount(Player player, QuestNpcServices services, PlayerbotServiceIndex index, FactionTemplateCatalog? factions, int tier, bool planning)
    {
        PlayerbotRidingStatus r = Riding;
        r.MountCost = 0; r.MountItem = 0; r.MountVendor = 0;
        uint raceMask = 1u << ((int)player.Race - 1), classMask = 1u << ((int)player.Class - 1);
        uint rankCap = index.RankForTier(tier), rankFloor = index.RankFloor(tier);
        uint tierLevel = planning ? Math.Max(r.TierLevel, 1) : player.Level;
        uint map = player.Map?.MapId ?? player.MapId;
        int quoted = 0;
        uint best = uint.MaxValue;
        foreach (PlayerbotServiceIndex.MountOffer offer in index.MountOffers)
        {
            ItemTemplate item = offer.Item;
            if (quoted >= 6 && best != uint.MaxValue) break;
            if ((item.AllowableRace & raceMask) == 0 || (item.AllowableClass & classMask) == 0
                || !PlayerbotMountRules.FitsTier(item, rankFloor, rankCap)) continue;
            bool fits = planning
                ? item.RequiredLevel <= tierLevel
                : player.Inventory.CanUseItem(item) == ArcaneCore.Game.Items.InventoryResult.Ok;
            if (!fits || !PlayerbotTrainerDestinations.CanApproachTrainer(player, services, factions, offer.Vendor.FactionTemplate)) continue;
            if (VendorExcluded(offer.Vendor.Entry) || OnMap(offer.Vendor.Spawns, map) is not { } spawn) continue;
            quoted++;
            if (services.QuoteVendorPrice(player, PlayerbotServiceIndex.Hint(offer.Vendor, spawn, NpcFlags.Vendor), item.Entry) is not { } price) continue;
            if (price < best)
            {
                best = price;
                r.MountCost = price; r.MountItem = item.Entry; r.MountVendor = offer.Vendor.Entry;
            }
        }
    }

    /// <summary>The budget in words for <c>.playerbot inspect</c>: money, what is reserved and why, what is spendable, and the next step.</summary>
    internal string Report(Player player)
    {
        if (!options.Budget.Enabled) return "off";
        Refresh(player);
        var text = new StringBuilder();
        uint money = player.Money, reserved = Reserved;
        text.Append(CultureInfo.InvariantCulture, $"money:{money} reserved:{reserved} saved:{Math.Min(money, reserved)} free:{money - Math.Min(money, reserved)}");
        text.Append(CultureInfo.InvariantCulture, $" repair:{RepairCost} class:{ClassDue.Total}({ClassDue.Count} spells)");
        PlayerbotRidingStatus r = Riding;
        text.Append(CultureInfo.InvariantCulture, $" riding:{r.Step.ToString().ToLowerInvariant()}");
        if (r.Step is PlayerbotRidingStep.Saving or PlayerbotRidingStep.Train or PlayerbotRidingStep.BuyMount)
            text.Append(CultureInfo.InvariantCulture, $" training-tier:{r.TierIndex}@{r.TierLevel} training:{r.TrainingCost} mount-tier:{r.MountTierIndex} mount:{r.MountCost}(item {r.MountItem} vendor {r.MountVendor}) trainer:{r.TrainerEntry}");
        text.Append(" next:").Append(NextStep(player));
        return text.ToString();
    }

    /// <summary>The next thing the bot is saving towards, in words.</summary>
    internal string NextStep(Player player)
    {
        if (!options.Budget.Enabled) return "none";
        Refresh(player);
        if (RepairCost != 0) return "repair";
        if (ClassDue.Count > 0) return "class-training";
        return Riding.Step switch
        {
            PlayerbotRidingStep.Train => TrainingDue(player) ? "riding-trainer" : "save-for-riding",
            PlayerbotRidingStep.Saving => "save-for-riding",
            PlayerbotRidingStep.BuyMount => MountPurchaseDue(player) ? "mount-vendor" : "save-for-mount",
            _ => "none",
        };
    }
}

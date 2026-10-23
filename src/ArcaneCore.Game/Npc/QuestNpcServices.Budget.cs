using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// One teaching offer of a trainer as an advisory caller (a bot saving money) sees it.
/// </summary>
/// <param name="TeachingSpell">npc_trainer.spell.</param>
/// <param name="LearnedSpell">The spell the teaching spell learns.</param>
/// <param name="Cost">The cost after the player's reputation and honor discount at this trainer.</param>
/// <param name="RequiredLevel">npc_trainer.reqlevel, else the learned spell's level.</param>
/// <param name="State">The trainer window's state for the player: Green (learnable now), Red (a prerequisite is missing), never Gray (known offers are left out).</param>
public sealed record TrainerOffer(uint TeachingSpell, uint LearnedSpell, uint Cost, uint RequiredLevel, TrainerSpellState State);

/// <summary>What a trainer will still teach a player by a level: the sum of the discounted costs and how many offers that is.</summary>
public readonly record struct TrainerDue(uint Total, int Count)
{
    /// <summary>The cost of the whole due list, saturating at <see cref="uint.MaxValue"/>.</summary>
    public static TrainerDue Add(TrainerDue due, uint cost)
        => new((uint)Math.Min((ulong)due.Total + cost, uint.MaxValue), due.Count + 1);
}

/// <summary>
/// Advisory trainer and vendor pricing for callers that plan ahead (the playerbot budget). Nothing here talks to the NPC, teaches a
/// spell or changes money; it reads the same rules the trainer window and the vendor list use (<see cref="IsTrainerOf"/>,
/// <see cref="GetTrainerSpellState"/>, the reputation discount).
/// </summary>
public sealed partial class QuestNpcServices
{
    private const int MaxAdvisoryTrainerRows = 128;

    /// <summary>
    /// The offers of a trainer of <paramref name="type"/> (class or mounts) the player has not learned yet and may be taught
    /// (the trainer is of the player's class, or of its race / exalted for mounts; the learned spell fits the player's class and race),
    /// lowest required level first (with <paramref name="includeKnown"/> the ones it has learned too, state Gray). Offers the player cannot learn for a reason a higher level does not mend (a missing skill) still
    /// appear Red: the caller decides what counts. Empty when the trainer refuses the player.
    /// </summary>
    public IReadOnlyList<TrainerOffer> TrainerOffers(Player player, NpcInfo npc, TrainerType type, bool includeKnown = false)
    {
        var offers = new List<TrainerOffer>();
        ForEachOffer(player, npc, type, (_, offer) => { offers.Add(offer); return true; }, includeKnown);
        offers.Sort((a, b) => a.RequiredLevel != b.RequiredLevel ? a.RequiredLevel.CompareTo(b.RequiredLevel) : a.TeachingSpell.CompareTo(b.TeachingSpell));
        return offers;
    }

    /// <summary>
    /// What a trainer of <paramref name="type"/> will teach the player by <paramref name="maxLevel"/> that it has not learned: offers the
    /// player can learn now (Green) and offers a later level than the player's opens up (up to <paramref name="maxLevel"/>); an offer
    /// already open to the player's level yet Red is left out (a skill or chain it cannot get here would be saved for for ever).
    /// Allocates (a closure and the enumeration of the trainer's rows): call it at refresh time, not per think.
    /// </summary>
    public TrainerDue DueTrainerCost(Player player, NpcInfo npc, TrainerType type, uint maxLevel)
    {
        TrainerDue due = default;
        ForEachOffer(player, npc, type, (_, offer) =>
        {
            bool counts = offer.State == TrainerSpellState.Green
                || (offer.State == TrainerSpellState.Red && offer.RequiredLevel > player.Level && offer.RequiredLevel <= maxLevel);
            if (counts)
            {
                due = TrainerDue.Add(due, offer.Cost);
            }

            return true;
        });
        return due;
    }

    /// <summary>
    /// The riding training the player could buy at a Mounts trainer right now (the same GREEN rule as the trainer window; the trainer must
    /// teach the player: its race or Exalted with its faction, <see cref="IsTrainerOf"/>) and can pay. Null for any other trainer.
    /// With <paramref name="maxCost"/> an offer dearer than that is skipped.
    /// </summary>
    public ClassTrainerQuote? GetRidingTrainerQuote(Player player, NpcInfo npc, uint teachingSpell = 0, uint? maxCost = null)
    {
        if (npc is null || npc.IsGameObject || npc.TrainerType != TrainerType.Mounts)
        {
            return null;
        }

        ClassTrainerQuote? quote = null;
        uint lowest = 0;
        ForEachOffer(player, npc, TrainerType.Mounts, (_, offer) =>
        {
            if (offer.State != TrainerSpellState.Green || (teachingSpell != 0 && offer.TeachingSpell != teachingSpell)
                || player.Money < offer.Cost || offer.Cost > (maxCost ?? uint.MaxValue))
            {
                return true;
            }

            // The lowest level first, as the learn order is.
            if (quote is null || offer.RequiredLevel < lowest)
            {
                quote = new ClassTrainerQuote(offer.TeachingSpell, offer.LearnedSpell, offer.Cost);
                lowest = offer.RequiredLevel;
            }

            return true;
        });
        return quote;
    }

    /// <summary>
    /// The price a vendor asks the player for one bundle of an item, by the same rules as <see cref="GetVendorPurchasePrice"/> (the row is
    /// visible to the player, no honor rank, the reputation gate) but with no check of the player's money, bag room or the vendor's stock:
    /// what a bot has to save for. Null when the vendor does not sell it to this player.
    /// </summary>
    public uint? QuoteVendorPrice(Player player, NpcInfo npc, uint itemId)
    {
        if (Ready(player) is null || npc is null || npc.IsGameObject || (npc.NpcFlags & NpcFlags.Vendor) == 0
            || Deps.Items is not { } items || items.GetItem(itemId) is not { } proto)
        {
            return null;
        }

        VendorItem? row = VisibleVendorRow(player, npc, itemId);
        if (row is null || proto.BuyCount == 0 || proto.RequiredHonorRank != 0)
        {
            return null;
        }

        if (!MeetsVendorReputation(player, npc, proto))
        {
            return null;
        }

        return Discounted(proto.BuyPrice, PriceDiscount(player, npc));
    }

    private void ForEachOffer(Player player, NpcInfo npc, TrainerType type, Func<TrainerSpell, TrainerOffer, bool> visit, bool includeKnown = false)
    {
        if (npc is null || Ready(player) is not { } state || npc.IsGameObject || npc.TrainerType != type
            || !IsTrainerOf(state, npc, false) || Deps.Spells is not { } spells)
        {
            return;
        }

        float discount = PriceDiscount(player, npc);
        IReadOnlyList<TrainerSpell> rows = Npcs.TrainerSpells(npc.Entry);
        for (int index = 0; index < rows.Count && index < MaxAdvisoryTrainerRows; index++)
        {
            TrainerSpell row = rows[index];
            if (spells.DescribeTrainerSpell(row.Spell) is not { } info || info.LearnedSpell == 0
                || !spells.IsSpellFitByClassAndRace(player, info.LearnedSpell))
            {
                continue;
            }

            TrainerSpellState offerState = GetTrainerSpellState(player, row, info);
            if (offerState == TrainerSpellState.Gray && !includeKnown)
            {
                continue;
            }

            uint level = row.ReqLevel != 0 ? row.ReqLevel : info.SpellLevel;
            if (!visit(row, new TrainerOffer(row.Spell, info.LearnedSpell, Discounted(row.SpellCost, discount), level, offerState)))
            {
                return;
            }
        }
    }
}

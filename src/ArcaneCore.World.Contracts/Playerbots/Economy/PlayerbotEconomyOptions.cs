namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>World:Playerbots:Economy</c>: managed bots use the faction auction house like players do (docs/areas/playbots-economy.md).
/// A bot visits the auctioneer of its own faction every <see cref="VisitIntervalMinutes"/>, lists the loot it has no use for,
/// buys one gear upgrade within a budget, and collects what sold, expired or was won from the mailbox. Every action is a
/// client packet through the ordinary handlers. Every key is live: <c>.reload config</c> changes this object in place and
/// <c>PlayerbotEconomy</c> reads it at every decision.
/// </summary>
public sealed class PlayerbotEconomyOptions
{
    /// <summary>The highest <see cref="MaxListingsPerBot"/> accepted.</summary>
    public const int MaxListingsCeiling = 50;

    /// <summary>The longest <see cref="VisitIntervalMinutes"/> accepted (one day).</summary>
    public const int MaxVisitIntervalMinutes = 1440;

    /// <summary>Let bots list, buy and collect at the auction house (on by default). Off: no visit starts; a running one is dropped.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The chance in percent (0..100) that a visit lists anything: one roll per visit, then the bot lists up to its free listing
    /// slots. 0 lists nothing (a bot may still buy and collect); 100 always lists.
    /// </summary>
    public int ListChancePerVisit { get; set; } = 50;

    /// <summary>The most auctions one bot has listed at once (0..50), counted from the auction house's own owner list; 0 lists nothing.</summary>
    public int MaxListingsPerBot { get; set; } = 5;

    /// <summary>
    /// The share of the money a bot holds at the start of a visit that the visit may spend in all (0..1): listing deposits and the
    /// buyout of an upgrade count against it. 0 spends nothing.
    /// </summary>
    public float BuyUpgradeBudgetFraction { get; set; } = 0.25f;

    /// <summary>Minutes between two visits of one bot (1..1440). Bots start at a random point of the first interval.</summary>
    public int VisitIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Deposit goods the bot does not need (trade goods and reagents no spell of its own uses, gems, recipes, stackable curios) at a banker
    /// while it stays at its capital home (on by default). Gear, food, potions, ammunition, bags, grays and quest items are never deposited.
    /// </summary>
    public bool BankDeposit { get; set; } = true;

    /// <summary>
    /// Buy the next bank bag slot at a banker while the bot stays at its capital home when the bank has fewer than 5 free slots and the price
    /// is at most 40% of the bot's gold (on by default). A bought slot is only space once a bag is put in it, which bots do not do yet, so the
    /// bank-nearly-full gate keeps the purchase rare.
    /// </summary>
    public bool BuyBankSlots { get; set; } = true;

    /// <summary>
    /// Buy the reagents of the recipes the bot wants to craft that it carries fewer of than one craft uses at the faction auction house during a
    /// visit (on by default): at most 2 auctions a visit, within the visit budget. Guards: a reagent a vendor stocks is never bought; an auction
    /// holds at most twice the shortfall; a buyout per unit is at most <see cref="ReagentSellMultiple"/> times the item's vendor sell price
    /// plus <see cref="ReagentAllowanceCopper"/>. Off: no reagent is searched or bought. Reagents a wanted recipe uses are never listed either way.
    /// </summary>
    public bool BuyReagents { get; set; } = true;

    /// <summary>
    /// The most a bot pays per unit for a reagent at the house, as a multiple of the item's vendor sell price (1..20), plus
    /// <see cref="ReagentAllowanceCopper"/>; an item without a vendor sell price is never bought.
    /// </summary>
    public int ReagentSellMultiple { get; set; } = 4;

    /// <summary>Copper per unit (0..10000) added to <see cref="ReagentSellMultiple"/> times the vendor sell price in the reagent buy cap.</summary>
    public int ReagentAllowanceCopper { get; set; } = 10;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Economy";
        if (ListChancePerVisit is < 0 or > 100) throw new InvalidOperationException($"{section}: ListChancePerVisit must be 0..100.");
        if (MaxListingsPerBot is < 0 or > MaxListingsCeiling) throw new InvalidOperationException($"{section}: MaxListingsPerBot must be 0..{MaxListingsCeiling}.");
        if (!float.IsFinite(BuyUpgradeBudgetFraction) || BuyUpgradeBudgetFraction is < 0f or > 1f)
            throw new InvalidOperationException($"{section}: BuyUpgradeBudgetFraction must be 0..1.");
        if (VisitIntervalMinutes is < 1 or > MaxVisitIntervalMinutes)
            throw new InvalidOperationException($"{section}: VisitIntervalMinutes must be 1..{MaxVisitIntervalMinutes}.");
        if (ReagentSellMultiple is < 1 or > 20) throw new InvalidOperationException($"{section}: ReagentSellMultiple must be 1..20.");
        if (ReagentAllowanceCopper is < 0 or > 10000) throw new InvalidOperationException($"{section}: ReagentAllowanceCopper must be 0..10000.");
    }
}

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
    /// holds at most twice the shortfall; a buyout per unit is at most <see cref="BuyBelowAskPercent"/> percent of the bot floor of the item
    /// (an item with no vendor sell price is never bought). Off: no reagent is searched or bought. Reagents a wanted recipe uses are never listed either way.
    /// </summary>
    public bool BuyReagents { get; set; } = true;

    /// <summary>The bot floor of a common (white) item as a percent of its vendor sell price (never below the reference price or the vendor floor after the cut).</summary>
    public int ListMarkupPercentCommon { get; set; } = 500;

    /// <summary>The bot floor of an uncommon (green) item as a percent of its vendor sell price (never below the reference price or the vendor floor after the cut).</summary>
    public int ListMarkupPercentUncommon { get; set; } = 300;

    /// <summary>The bot floor of a rare (blue) item as a percent of its vendor sell price (never below the reference price or the vendor floor after the cut).</summary>
    public int ListMarkupPercentRare { get; set; } = 800;

    /// <summary>The bot floor of an epic or better item as a percent of its vendor sell price (never below the reference price or the vendor floor after the cut).</summary>
    public int ListMarkupPercentEpic { get; set; } = 10000;

    /// <summary>The most a bot pays per unit at the house (reagents and gear) as a percent of the bot floor of that item, so no bot ever pays what a bot asks.</summary>
    public int BuyBelowAskPercent { get; set; } = 50;

    /// <summary>The markup percent for an item of <paramref name="quality"/>: poor and common use the common one, then uncommon, rare, and epic or above.</summary>
    public int ListMarkupPercentFor(uint quality) => quality switch
    {
        <= 1 => ListMarkupPercentCommon,
        2 => ListMarkupPercentUncommon,
        3 => ListMarkupPercentRare,
        _ => ListMarkupPercentEpic,
    };

    /// <summary>
    /// A bot short of money lists every visit and gets 3 more listing slots (on by default). Short means its budget reserves more than it
    /// has, or its money is below MoneyGoalCopperPerLevel times its level. It only raises listing, and never when
    /// ListChancePerVisit or MaxListingsPerBot is 0.
    /// </summary>
    public bool MoneyGoal { get; set; } = true;

    /// <summary>The money floor per character level of the money goal, in copper (0..100000); 0: only what the budget reserves counts.</summary>
    public int MoneyGoalCopperPerLevel { get; set; } = 500;

    /// <summary>The most auctions one bot buys out, reagents and upgrades together, in any 60 minutes of world time (0..100); 0 buys nothing.</summary>
    public int MaxBuyoutsPerHour { get; set; } = 6;

    /// <summary>The most auctions one bot lists in any 60 minutes of world time (0..100); 0 lists nothing.</summary>
    public int MaxListingsPerHour { get; set; } = 10;

    /// <summary>
    /// The most auctions all bots together buy out at one auction house in any 60 minutes of world time (0..100000); 0 lets no bot buy there. A soft limit: a buyout counts when the house confirms it, so bots with buyouts in flight together can overshoot it by their number.
    /// </summary>
    public int HouseBuyoutsPerHour { get; set; } = 200;

    /// <summary>
    /// The most auctions all bots together list at one auction house in any 60 minutes of world time (0..100000); 0 lets no bot list there. A soft limit: a listing counts when the house confirms it, so bots with listings in flight together can overshoot it by their number.
    /// </summary>
    public int HouseListingsPerHour { get; set; } = 400;

    /// <summary>List stacks of consumables (food, drink, potions, bandages, elixirs, scrolls) beyond the keep count (on by default).</summary>
    public bool ListSurplusConsumables { get; set; } = true;

    /// <summary>Per consumable entry, the count (1..200) a bot always keeps before a stack of it is surplus.</summary>
    public int ConsumableKeepCount { get; set; } = 20;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Economy";
        if (ListChancePerVisit is < 0 or > 100) throw new InvalidOperationException($"{section}: ListChancePerVisit must be 0..100.");
        if (MaxListingsPerBot is < 0 or > MaxListingsCeiling) throw new InvalidOperationException($"{section}: MaxListingsPerBot must be 0..{MaxListingsCeiling}.");
        if (!float.IsFinite(BuyUpgradeBudgetFraction) || BuyUpgradeBudgetFraction is < 0f or > 1f)
            throw new InvalidOperationException($"{section}: BuyUpgradeBudgetFraction must be 0..1.");
        if (VisitIntervalMinutes is < 1 or > MaxVisitIntervalMinutes)
            throw new InvalidOperationException($"{section}: VisitIntervalMinutes must be 1..{MaxVisitIntervalMinutes}.");
        if (MoneyGoalCopperPerLevel is < 0 or > 100000) throw new InvalidOperationException($"{section}: MoneyGoalCopperPerLevel must be 0..100000.");
        if (MaxBuyoutsPerHour is < 0 or > 100) throw new InvalidOperationException($"{section}: MaxBuyoutsPerHour must be 0..100.");
        if (MaxListingsPerHour is < 0 or > 100) throw new InvalidOperationException($"{section}: MaxListingsPerHour must be 0..100.");
        if (HouseBuyoutsPerHour is < 0 or > 100000) throw new InvalidOperationException($"{section}: HouseBuyoutsPerHour must be 0..100000.");
        if (HouseListingsPerHour is < 0 or > 100000) throw new InvalidOperationException($"{section}: HouseListingsPerHour must be 0..100000.");
        if (ConsumableKeepCount is < 1 or > 200) throw new InvalidOperationException($"{section}: ConsumableKeepCount must be 1..200.");
        foreach ((string name, int value) in new[]
        {
            (nameof(ListMarkupPercentCommon), ListMarkupPercentCommon), (nameof(ListMarkupPercentUncommon), ListMarkupPercentUncommon),
            (nameof(ListMarkupPercentRare), ListMarkupPercentRare), (nameof(ListMarkupPercentEpic), ListMarkupPercentEpic),
        })
            if (value is < 100 or > 100000) throw new InvalidOperationException($"{section}: {name} must be 100..100000.");
        if (BuyBelowAskPercent is < 1 or > 99) throw new InvalidOperationException($"{section}: BuyBelowAskPercent must be 1..99.");
    }
}

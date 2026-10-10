using System.Globalization;

namespace ArcaneCore.Game.Economy.AuctionBot;

/// <summary>
/// The auction house bot (section <c>AuctionHouseBot</c>), after cMaNGOS AuctionHouseBot (src/game/AuctionHouseBot/AuctionHouseBot.cpp,
/// ahbot.conf.dist.in) with the custody limits of MaNGOS Zero (src/game/AuctionHouseBot/CustodyLedger.h). Off by default: no
/// listing is created and no auction is bought until <see cref="Enabled"/> is true.
/// </summary>
public sealed class AuctionBotOptions
{
    public const string SectionName = "AuctionHouseBot";

    /// <summary>The number of item classes a value list covers (cMaNGOS MAX_ITEM_CLASS for 1.12: 0 consumable .. 16).</summary>
    public const int ItemClassCount = 17;

    /// <summary>The number of item qualities (cMaNGOS MAX_ITEM_QUALITY: poor .. artifact).</summary>
    public const int QualityCount = 7;

    /// <summary>Run the bot (default false). With false nothing is listed, bought or minted.</summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds between bot actions. Each action handles one house, alternating the sell then the buy pass over the houses (cMaNGOS AuctionHouseBot::Update m_houseAction).</summary>
    public uint UpdateIntervalSeconds { get; set; } = 20;

    /// <summary>The auction houses the bot serves, comma-separated AuctionHouse.dbc ids (2 Alliance, 6 Horde, 7 neutral; cMaNGOS serves all three).</summary>
    public string Houses { get; set; } = "2,6,7";

    /// <summary>Item entries the bot never lists or buys, comma-separated (cMaNGOS ahbot_items rows with value 0).</summary>
    public string Blacklist { get; set; } = string.Empty;

    /// <summary>The parsed <see cref="Houses"/> (unreadable or zero entries dropped, duplicates removed).</summary>
    public uint[] HouseIds() => [.. ParseIds(Houses)];

    /// <summary>The parsed <see cref="Blacklist"/>.</summary>
    public uint[] BlacklistIds() => [.. ParseIds(Blacklist)];

    private static IEnumerable<uint> ParseIds(string? text)
        => (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => uint.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out uint v) ? v : 0).Where(v => v != 0).Distinct();

    /// <summary>Percent chance a sell action lists anything (cMaNGOS AuctionHouseBot.Chance.Sell, 0-100, default 10).</summary>
    public uint SellChance { get; set; } = 10;

    /// <summary>Percent chance a buy action buys anything (cMaNGOS AuctionHouseBot.Chance.Buy, 0-100, default 10).</summary>
    public uint BuyChance { get; set; } = 10;

    /// <summary>Fewest item templates drawn per sell action (cMaNGOS AuctionHouseBot.Items.Profession first value: 80).</summary>
    public int TemplatesPerSellMin { get; set; } = 80;

    /// <summary>Most item templates drawn per sell action (cMaNGOS AuctionHouseBot.Items.Profession second value: 90).</summary>
    public int TemplatesPerSellMax { get; set; } = 90;

    /// <summary>Smallest stack as a percent of the item's stack size (cMaNGOS Items.Profession third value: 0; at least one item).</summary>
    public uint StackPercentMin { get; set; }

    /// <summary>Largest stack as a percent of the item's stack size (cMaNGOS Items.Profession fourth value: 50).</summary>
    public uint StackPercentMax { get; set; } = 50;

    /// <summary>Most bot auctions open in one house at once; a sell action never lists past it (ArcaneCore guard, 0 = no listing).</summary>
    public uint MaxAuctionsPerHouse { get; set; } = 2000;

    /// <summary>Highest required level of a listed item (cMaNGOS AuctionHouseBot.Level.MaxRequired, 1-255, default 60). Below 60 the item level is capped at this + 5.</summary>
    public uint MaxRequiredLevel { get; set; } = 60;

    /// <summary>Price of poor items per item class as a percent of the vendor price, 17 comma-separated values (cMaNGOS AuctionHouseBot.Value.Poor; 0 = never sold or bought).</summary>
    public string ValuePoor { get; set; } = "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0";

    /// <summary>Price of common (white) items per item class (cMaNGOS AuctionHouseBot.Value.Normal).</summary>
    public string ValueNormal { get; set; } = "100,100,0,100,0,100,100,100,0,100,0,100,100,100,0,100,100";

    /// <summary>Price of uncommon (green) items per item class (cMaNGOS AuctionHouseBot.Value.Uncommon).</summary>
    public string ValueUncommon { get; set; } = "200,200,200,200,200,200,200,200,0,200,0,200,200,200,0,200,200";

    /// <summary>Price of rare (blue) items per item class (cMaNGOS AuctionHouseBot.Value.Rare).</summary>
    public string ValueRare { get; set; } = "400,400,400,400,400,400,400,400,0,400,0,400,400,400,0,400,400";

    /// <summary>Price of epic items per item class (cMaNGOS AuctionHouseBot.Value.Epic).</summary>
    public string ValueEpic { get; set; } = "800,800,800,800,800,800,800,800,0,800,0,800,800,800,0,800,800";

    /// <summary>Price of legendary items per item class (cMaNGOS AuctionHouseBot.Value.Legendary; all 0 by default).</summary>
    public string ValueLegendary { get; set; } = "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0";

    /// <summary>Price of artifact items per item class (cMaNGOS AuctionHouseBot.Value.Artifact; all 0 by default).</summary>
    public string ValueArtifact { get; set; } = "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0";

    /// <summary>Price an item that a vendor sells at 100 % of the vendor price, so it cannot be bought and resold for profit (cMaNGOS AuctionHouseBot.Value.Vendor, default true).</summary>
    public bool VendorValue { get; set; } = true;

    /// <summary>Random price spread in percent (cMaNGOS AuctionHouseBot.Value.Variance, 0-100, default 10).</summary>
    public uint ValueVariance { get; set; } = 10;

    /// <summary>Lowest starting bid as a percent of the buyout (cMaNGOS AuctionHouseBot.Bid.Min, 0-100, default 75).</summary>
    public uint BidMinPercent { get; set; } = 75;

    /// <summary>Highest starting bid as a percent of the buyout (cMaNGOS AuctionHouseBot.Bid.Max, 0-100, default 90).</summary>
    public uint BidMaxPercent { get; set; } = 90;

    /// <summary>Shortest listing in hours (cMaNGOS AuctionHouseBot.Time.Min, 1-72, default 2).</summary>
    public uint TimeMinHours { get; set; } = 2;

    /// <summary>Longest listing in hours (cMaNGOS AuctionHouseBot.Time.Max, 1-72, default 24).</summary>
    public uint TimeMaxHours { get; set; } = 24;

    /// <summary>The most the buyer pays, as a percent of the bot's own value of the item (cMaNGOS AuctionHouseBot.Buy.Value, 0-200, default 80).</summary>
    public uint BuyValuePercent { get; set; } = 80;

    /// <summary>Custody limit: most items the bot may create into the houses per UTC day, all houses together (MaNGOS Zero custody; 0 = none).</summary>
    public uint DailyItemBudget { get; set; } = 5000;

    /// <summary>Custody limit: most copper the buyer may pay out per UTC day, all houses together (MaNGOS Zero custody; 0 = the buyer never buys).</summary>
    public uint DailyBuyBudgetCopper { get; set; } = 10_000_000;

    /// <summary>The value lists by quality, parsed (missing values are 0, extra values ignored, as cMaNGOS ParseItemValueConfig).</summary>
    public uint[][] ParseValues() =>
    [
        ParseValueList(ValuePoor), ParseValueList(ValueNormal), ParseValueList(ValueUncommon), ParseValueList(ValueRare),
        ParseValueList(ValueEpic), ParseValueList(ValueLegendary), ParseValueList(ValueArtifact),
    ];

    /// <summary>One comma-separated list of percents; an unreadable entry is 0 (cMaNGOS atoi).</summary>
    public static uint[] ParseValueList(string? text)
    {
        var values = new uint[ItemClassCount];
        string[] parts = (text ?? string.Empty).Split(',');
        for (int i = 0; i < parts.Length && i < ItemClassCount; i++)
        {
            values[i] = uint.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint v) ? v : 0;
        }

        return values;
    }

    /// <summary>
    /// Clamp the options as cMaNGOS GetMinMaxConfig does (an out-of-range value takes its default) and order the min/max pairs
    /// (Bid.Min and Time.Min fall to the max). Returns one line per correction.
    /// </summary>
    public IReadOnlyList<string> Normalize()
    {
        var fixes = new List<string>();
        var defaults = new AuctionBotOptions();
        uint Range(string name, uint value, uint min, uint max, uint fallback)
        {
            if (value >= min && value <= max)
            {
                return value;
            }

            fixes.Add($"{SectionName}:{name} must be between {min} and {max}; using {fallback}.");
            return fallback;
        }

        SellChance = Range(nameof(SellChance), SellChance, 0, 100, defaults.SellChance);
        BuyChance = Range(nameof(BuyChance), BuyChance, 0, 100, defaults.BuyChance);
        ValueVariance = Range(nameof(ValueVariance), ValueVariance, 0, 100, defaults.ValueVariance);
        BidMinPercent = Range(nameof(BidMinPercent), BidMinPercent, 0, 100, defaults.BidMinPercent);
        BidMaxPercent = Range(nameof(BidMaxPercent), BidMaxPercent, 0, 100, defaults.BidMaxPercent);
        TimeMinHours = Range(nameof(TimeMinHours), TimeMinHours, 1, 72, defaults.TimeMinHours);
        TimeMaxHours = Range(nameof(TimeMaxHours), TimeMaxHours, 1, 72, defaults.TimeMaxHours);
        BuyValuePercent = Range(nameof(BuyValuePercent), BuyValuePercent, 0, 200, defaults.BuyValuePercent);
        MaxRequiredLevel = Range(nameof(MaxRequiredLevel), MaxRequiredLevel, 1, 255, defaults.MaxRequiredLevel);
        StackPercentMin = Range(nameof(StackPercentMin), StackPercentMin, 0, 100, defaults.StackPercentMin);
        StackPercentMax = Range(nameof(StackPercentMax), StackPercentMax, 0, 100, defaults.StackPercentMax);
        if (UpdateIntervalSeconds == 0)
        {
            fixes.Add($"{SectionName}:{nameof(UpdateIntervalSeconds)} must be at least 1; using {defaults.UpdateIntervalSeconds}.");
            UpdateIntervalSeconds = defaults.UpdateIntervalSeconds;
        }

        if (BidMinPercent > BidMaxPercent)
        {
            fixes.Add($"{SectionName}:{nameof(BidMinPercent)} must be at most {nameof(BidMaxPercent)}; using {BidMaxPercent}.");
            BidMinPercent = BidMaxPercent;
        }

        if (TimeMinHours > TimeMaxHours)
        {
            fixes.Add($"{SectionName}:{nameof(TimeMinHours)} must be at most {nameof(TimeMaxHours)}; using {TimeMaxHours}.");
            TimeMinHours = TimeMaxHours;
        }

        if (StackPercentMin > StackPercentMax)
        {
            fixes.Add($"{SectionName}:{nameof(StackPercentMin)} must be at most {nameof(StackPercentMax)}; using {StackPercentMax}.");
            StackPercentMin = StackPercentMax;
        }

        if (TemplatesPerSellMin > TemplatesPerSellMax)
        {
            fixes.Add($"{SectionName}:{nameof(TemplatesPerSellMin)} must be at most {nameof(TemplatesPerSellMax)}; using {TemplatesPerSellMax}.");
            TemplatesPerSellMin = TemplatesPerSellMax;
        }

        if (TemplatesPerSellMax < 0)
        {
            fixes.Add($"{SectionName}:{nameof(TemplatesPerSellMax)} must not be negative; using 0.");
            TemplatesPerSellMax = 0;
            TemplatesPerSellMin = Math.Min(TemplatesPerSellMin, 0);
        }

        return fixes;
    }
}

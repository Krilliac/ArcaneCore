namespace ArcaneCore.World.Playerbots;

public sealed partial class ManagedPlayerbotFeature
{
    private BotSocial.PlayerbotTradeDesk? _tradeDesk;
    private BotSocial.PlayerbotTradeOwners? _tradeOwners;
    private BotSocial.PlayerbotPriceBookDealPrices? _dealPrices;

    /// <summary>
    /// The price source the trade desk, the WTS/WTB posts and the whisper quotes share: the auction price book (what the bots saw listed, filled by the economy visits)
    /// with the reference price as fallback. Created on first use with the feature's clock and the live options; one per feature. Any thread (the book has its own lock).
    /// </summary>
    internal BotSocial.PlayerbotPriceBookDealPrices DealPrices => _dealPrices ?? CreateDealPrices();

    private BotSocial.PlayerbotPriceBookDealPrices CreateDealPrices()
    {
        var created = new BotSocial.PlayerbotPriceBookDealPrices(
            new BotSocial.PlayerbotPriceBook(() => _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds()), _options);
        return Interlocked.CompareExchange(ref _dealPrices, created, null) ?? created;
    }

    /// <summary>
    /// Who owns each bot's one trade slot (the desk, a tip, a fee, the party AI's gifts); created on first use with the feature's clock (Unix milliseconds); one per
    /// feature. Any thread (the registry has its own lock).
    /// </summary>
    internal BotSocial.PlayerbotTradeOwners TradeOwners => _tradeOwners ?? CreateTradeOwners();

    private BotSocial.PlayerbotTradeOwners CreateTradeOwners()
    {
        var created = new BotSocial.PlayerbotTradeOwners { Clock = () => _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds() };
        return Interlocked.CompareExchange(ref _tradeOwners, created, null) ?? created;
    }

    /// <summary>
    /// The world's trade desk (docs/areas/playbots-social.md, Trading): the bots' fixed-price deals through the trade window. Created on first use from the live
    /// <c>World:Playerbots</c> options, the social recorder, the price-book source, the feature's clock (Unix milliseconds) and the trade-slot registry; one per
    /// feature. Any thread.
    /// </summary>
    internal BotSocial.PlayerbotTradeDesk TradeDesk => _tradeDesk ?? CreateTradeDesk();

    // Two threads may race to the first use: the loser's empty desk is dropped and both see the winner's.
    private BotSocial.PlayerbotTradeDesk CreateTradeDesk()
    {
        var created = new BotSocial.PlayerbotTradeDesk(_options, Social, DealPrices,
            () => _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds(), TradeOwners);
        return Interlocked.CompareExchange(ref _tradeDesk, created, null) ?? created;
    }
}

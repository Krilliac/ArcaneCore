namespace ArcaneCore.World.Playerbots;

public sealed partial class ManagedPlayerbotFeature
{
    private BotSocial.PlayerbotTradeDesk? _tradeDesk;

    /// <summary>
    /// The world's trade desk (docs/areas/playbots-social.md, Trading): the bots' fixed-price deals through the trade window. Created on first use from the live
    /// <c>World:Playerbots</c> options, the social recorder, the reference price source and the feature's clock (Unix milliseconds); one per feature. Any thread.
    /// </summary>
    internal BotSocial.PlayerbotTradeDesk TradeDesk => _tradeDesk ?? CreateTradeDesk();

    // Two threads may race to the first use: the loser's empty desk is dropped and both see the winner's.
    private BotSocial.PlayerbotTradeDesk CreateTradeDesk()
    {
        var created = new BotSocial.PlayerbotTradeDesk(_options, Social, BotSocial.PlayerbotReferenceDealPrices.Instance,
            () => _setAsideTime.GetUtcNow().ToUnixTimeMilliseconds());
        return Interlocked.CompareExchange(ref _tradeDesk, created, null) ?? created;
    }
}

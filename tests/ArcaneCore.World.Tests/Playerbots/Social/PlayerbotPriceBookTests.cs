using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The trade price book (<see cref="PlayerbotPriceBook"/>, <see cref="PlayerbotPriceBookDealPrices"/>, docs/areas/playbots-social.md, Trading): the median of what distinct sellers
/// asked per unit within the window, the reference price below the seller minimum, and the WTS post asking what the desk quotes. The expected values are worked by hand from
/// the contract (one reading per seller, the mean of the two middle readings for an even count, the 141 copper reference of a green item with a 100 copper vendor price).
/// </summary>
public sealed class PlayerbotPriceBookTests
{
    private const uint Entry = 5000;
    private const long HourMs = 3_600_000;

    private static ItemTemplate Item(uint sell = 100, uint quality = 2)
        => new() { Entry = Entry, Name = "Test item", Class = 4, Quality = quality, SellPrice = sell };

    private static PlayerbotAuctionRow Row(uint buyout, ulong seller, uint count = 1, uint entry = Entry)
        => new(1, entry, 0, count, seller, 1, 1, buyout, 3_600_000, 0, 0);

    private sealed class Clock
    {
        public long Ms = 10 * HourMs;

        public long Read() => Ms;
    }

    /// <summary>
    /// The median is over distinct sellers, not listings. Seller 1 lists three stacks (100, 500 and 900 per unit) and counts once at its lowest, 100; with sellers 2 (200) and 3
    /// (300) the median is 200. Broken (per listing): the five readings 100, 200, 300, 500, 900 give 300.
    /// </summary>
    [Fact]
    public void Median_IsTakenOverDistinctSellers()
    {
        var clock = new Clock();
        var book = new PlayerbotPriceBook(clock.Read);
        book.Observe([Row(500, 1), Row(900, 1), Row(100, 1), Row(200, 2), Row(300, 3)]);
        Assert.Equal(200u, book.Median(Entry, 24, 3));

        // A later list from seller 1 replaces its reading (one reading per seller), so the three readings are now 200, 300 and 700: median 300.
        book.Observe([Row(700, 1)]);
        Assert.Equal(300u, book.Median(Entry, 24, 3));
    }

    /// <summary>A stack's reading is per unit (buyout divided by count); an even number of sellers gives the mean of the middle two; bid-only rows and other items carry no price.</summary>
    [Fact]
    public void Median_ReadsPerUnitAndSkipsRowsWithoutAPrice()
    {
        var clock = new Clock();
        var book = new PlayerbotPriceBook(clock.Read);
        book.Observe([Row(1000, 1, count: 10), Row(300, 2), Row(0, 3), Row(9999, 4, entry: Entry + 1)]);
        Assert.Equal(200u, book.Median(Entry, 24, 2)); // sellers 1 (100 each) and 2 (300): mean 200
        Assert.Null(book.Median(Entry, 24, 3)); // the bid-only seller is not a seller
        Assert.Null(book.Median(Entry + 1, 24, 2)); // one seller of the other item
        Assert.Equal(9999u, book.Median(Entry + 1, 24, 1));
    }

    /// <summary>Readings older than the window drop out: the same sellers seen 25 hours ago no longer make a quorum within 24 hours.</summary>
    [Fact]
    public void Median_IgnoresReadingsOlderThanTheWindow()
    {
        var clock = new Clock();
        var book = new PlayerbotPriceBook(clock.Read);
        book.Observe([Row(100, 1), Row(200, 2), Row(300, 3)]);
        Assert.Equal(200u, book.Median(Entry, 24, 3));
        clock.Ms += 25 * HourMs;
        Assert.Null(book.Median(Entry, 24, 3));
        Assert.Equal(0, book.ItemCount); // the stale item was dropped, not kept forever
    }

    /// <summary>The book stays bounded: more than 64 sellers of one item keep 64, and the oldest goes first.</summary>
    [Fact]
    public void Book_KeepsAtMostMaxSellersPerItem()
    {
        var clock = new Clock();
        var book = new PlayerbotPriceBook(clock.Read);
        for (uint seller = 1; seller <= PlayerbotPriceBook.MaxSellersPerItem + 1; seller++)
        {
            book.Observe([Row(seller * 10, seller)]);
            clock.Ms += 1000;
        }

        // Seller 1 (10 copper) was evicted: the readings are 20..650, so 64 sellers; the lowest middle pair is 330 and 340 -> 335.
        Assert.Equal(335u, book.Median(Entry, 24, PlayerbotPriceBook.MaxSellersPerItem));
        Assert.Null(book.Median(Entry, 24, PlayerbotPriceBook.MaxSellersPerItem + 1));
    }

    /// <summary>
    /// Below <c>PriceBookMinSellers</c> distinct sellers the source returns the reference price (141 for this item); at the minimum it returns the median. An item with no
    /// vendor price has no price at all, book or not.
    /// </summary>
    [Fact]
    public void DealPrices_FallBackToTheReferenceBelowTheSellerMinimum()
    {
        var clock = new Clock();
        var book = new PlayerbotPriceBook(clock.Read);
        var options = new PlayerbotOptions();
        options.Social.Trading.Deals.PriceBookMinSellers = 3;
        options.Social.Trading.Deals.PriceBookHours = 24;
        var source = new PlayerbotPriceBookDealPrices(book, options);
        ItemTemplate item = Item();
        uint reference = PlayerbotAuctionPricing.ReferencePerUnit(item);
        Assert.Equal(141u, reference);

        book.Observe([Row(5000, 1), Row(6000, 2)]);
        Assert.Equal(reference, source.ReferencePerUnit(item)); // two sellers: not enough

        book.Observe([Row(9000, 3)]);
        Assert.Equal(6000u, source.ReferencePerUnit(item));

        options.Social.Trading.Deals.PriceBookMinSellers = 4; // read live
        Assert.Equal(reference, source.ReferencePerUnit(item));

        options.Social.Trading.Deals.PriceBookMinSellers = 1;
        ItemTemplate worthless = Item(sell: 0);
        book.Observe([Row(500, 1)]);
        Assert.Equal(0u, source.ReferencePerUnit(worthless));
    }

    /// <summary>
    /// The post and the desk quote are equal. For one unit and a neutral partner the desk asks
    /// <c>RoundUpToQuote(max(SellerAsk(price, 1, 0, ...), vendor value, 1))</c> of the source's price; the WTS post text is the same amount. Checked with the reference price
    /// (below the seller minimum) and with the book's median, for a cheap item (the vendor floor binds) and an expensive one (a 10 silver rounding step binds). Broken (the
    /// post still asking four times the vendor price): 400 copper against the desk's 141 or 6000.
    /// </summary>
    [Theory]
    [InlineData(0, 100u)]
    [InlineData(3, 100u)]
    [InlineData(3, 40_000u)]
    public void Post_AndDeskQuote_AreEqual(int sellers, uint sellPrice)
    {
        var clock = new Clock();
        var book = new PlayerbotPriceBook(clock.Read);
        var options = new PlayerbotOptions();
        var source = new PlayerbotPriceBookDealPrices(book, options);
        for (uint s = 1; s <= sellers; s++) book.Observe([Row(s * 3000 + 123_457, s)]);
        ItemTemplate item = Item(sellPrice);
        uint perUnit = source.ReferencePerUnit(item);

        long deskQuote = PlayerbotDealRules.RoundUpToQuote(Math.Max(Math.Max(PlayerbotDealRules.SellerAsk(perUnit, 1, 0,
            options.Social.Trading.Deals.FriendDiscountPercent, options.Social.Trading.Deals.DislikeMarkupPercent), sellPrice), 1));
        long post = PlayerbotVoiceFacts.DealAsk(perUnit, item.SellPrice, item.Quality, options.Chat.Voice.PriceMultiplier);

        Assert.Equal(deskQuote, post);
        Assert.Equal(PlayerbotVoiceFacts.Price(deskQuote), PlayerbotVoiceFacts.Price(post));
        if (sellers == 0) Assert.Equal(PlayerbotDealRules.RoundUpToQuote(Math.Max(PlayerbotAuctionPricing.ReferencePerUnit(item), sellPrice)), post);
        else Assert.True(post > 4L * sellPrice || sellPrice > 10_000); // the median (about 129000 and up) drives the post, not the vendor multiple
    }

    /// <summary>With no price evidence (a reference of 0) the post keeps the vendor multiple, so an unpriced item still gets a sane post.</summary>
    [Fact]
    public void DealAsk_WithNoReference_KeepsTheVendorMultiple()
        => Assert.Equal(PlayerbotVoiceFacts.AskingPrice(50, 2, 4), PlayerbotVoiceFacts.DealAsk(0, 50, 2, 4));
}

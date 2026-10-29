using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Lane T3 (docs/areas/playbots-social.md, Trading; design bot-capital-trade-design section 4, T3): the pure rules of a bot's fixed-price deal. Every expected value here is
/// worked out by hand from the rules' own definitions (the 90% tolerance, the 0.67 buyer bound, the quarter cap, the 10/100/1000 copper chat rounding); nothing is read
/// back from the code under test. Each test's summary says what a broken rule would look like.
/// </summary>
public sealed class PlayerbotDealRulesTests
{
    private const uint Wanted = 100, Other = 200;

    private static PlayerbotDealGoods G(uint entry, uint count, bool soulbound = false) => new(entry, count, soulbound);

    private static PlayerbotDealTerms Sell(long price = 1000, uint count = 2, long floor = 400, long reference = 1000) => new(PlayerbotDealRole.Seller, Wanted, count, price, floor, reference);

    private static PlayerbotDealTerms Buy(long price = 1000, uint count = 2, long reference = 1000) => new(PlayerbotDealRole.Buyer, Wanted, count, price, 0, reference);

    private static PlayerbotDealWindow Win(PlayerbotDealGoods[]? bot = null, long botGold = 0, PlayerbotDealGoods[]? partner = null, long partnerGold = 0)
        => new(bot ?? [], botGold, partner ?? [], partnerGold);

    // --- Judge: seller -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A seller bot gives the goods only against the whole price. Broken: it would accept 999 of 1000, an overpayment-only check, a window in which it also gives something
    /// extra or the wrong item or gold, or one in which the partner offers an item instead of (or besides) the gold.
    /// </summary>
    [Fact]
    public void DealRules_Seller_AcceptsOnlyFullPayment()
    {
        PlayerbotDealTerms t = Sell();
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2)], partnerGold: 999)));
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2)], partnerGold: 1000)));
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2)], partnerGold: 1001))); // an overpayment only adds to what the bot gets
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(t, Win([G(Wanted, 1), G(Wanted, 1)], partnerGold: 1000))); // two stacks summing to the count

        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 3)], partnerGold: 1000))); // one more than agreed
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 1)], partnerGold: 1000))); // one fewer
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2), G(Other, 1)], partnerGold: 1000))); // an extra item of the bot's
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Other, 2)], partnerGold: 1000))); // wrong entry
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2, soulbound: true)], partnerGold: 1000)));
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2)], botGold: 1, partnerGold: 1000))); // the bot is also giving gold
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2)], partner: [G(Other, 1)], partnerGold: 1000))); // the partner adds an item
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([G(Wanted, 2)], partner: [G(Wanted, 1)], partnerGold: 1000)));
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win([], partnerGold: 1000))); // the bot has not put the goods in yet: nothing to accept
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win())); // an empty window is not a deal
    }

    /// <summary>
    /// A buyer bot pays only for the agreed goods and never more than the price. Broken: it would pay for a swapped item, a short count or a soulbound item, accept with
    /// no gold of its own on the table (a free gift read as a deal), or accept a bot gold above the agreed price.
    /// </summary>
    [Fact]
    public void DealRules_Buyer_PaysOnlyForAgreedGoods()
    {
        PlayerbotDealTerms t = Buy();
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Wanted, 2)])));
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(t, Win(botGold: 1, partner: [G(Wanted, 2)]))); // paying less than the price is fine
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Wanted, 1), G(Wanted, 1)])));
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Wanted, 2)], partnerGold: 50))); // the partner's gold only adds to what the bot receives

        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 1001, partner: [G(Wanted, 2)]))); // above the price
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 0, partner: [G(Wanted, 2)]))); // no gold of the bot's: not yet (it puts it in first)
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Other, 2)]))); // swapped item
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Wanted, 1)]))); // short count
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Wanted, 3)]))); // over count: not the agreed goods
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Wanted, 2, soulbound: true)])));
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [G(Wanted, 1), G(Other, 1)]))); // half swapped
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(botGold: 1000, partner: [])));
        Assert.Equal(PlayerbotDealVerdict.NotYet, PlayerbotDealRules.Judge(t, Win(bot: [G(Wanted, 1)], botGold: 1000, partner: [G(Wanted, 2)]))); // a buyer never gives items
    }

    /// <summary>
    /// Terms that break an anti-scam rule are Unsafe whatever the window holds. Boundaries are the integer ones: a buyer price p is safe while reference*100 &gt;= p*67
    /// (1492*67 = 99964 &lt;= 100000, 1493*67 = 100031 &gt; 100000); a seller price is safe from the vendor floor up.
    /// </summary>
    [Fact]
    public void DealRules_UnsafeTerms_AreNeverAccepted_AtTheirBoundaries()
    {
        PlayerbotDealWindow sellWindow = Win([G(Wanted, 2)], partnerGold: 100_000);
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Sell(price: 399, floor: 400), sellWindow)); // below the vendor floor
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(Sell(price: 400, floor: 400), sellWindow));
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Sell(reference: 0), sellWindow)); // no reference value
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Sell(count: 0), sellWindow));
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Sell(price: PlayerbotDealRules.MaxPrice + 1), Win([G(Wanted, 2)], partnerGold: long.MaxValue)));
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Sell() with { Entry = 0 }, sellWindow));

        PlayerbotDealWindow buyWindow = Win(botGold: 1000, partner: [G(Wanted, 2)]);
        Assert.Equal(PlayerbotDealVerdict.Accept, PlayerbotDealRules.Judge(Buy(price: 1492), buyWindow));
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Buy(price: 1493), buyWindow));
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Buy(reference: 0), buyWindow));
        Assert.True(PlayerbotDealRules.TermsAreSafe(Buy(price: 1492)));
        Assert.False(PlayerbotDealRules.TermsAreSafe(Buy(price: 1493)));
    }

    /// <summary>A price of 0 (or less) never accepts, for either role, even against a window that would otherwise match perfectly. Broken: a free-gift "deal".</summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void DealRules_PriceZero_NeverAccepts(long price)
    {
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Sell(price: price), Win([G(Wanted, 2)], partnerGold: 0)));
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Buy(price: price), Win(botGold: 0, partner: [G(Wanted, 2)])));
        Assert.Equal(PlayerbotDealVerdict.Unsafe, PlayerbotDealRules.Judge(Buy(price: price), Win(botGold: 1, partner: [G(Wanted, 2)])));
    }

    /// <summary>
    /// The never-a-zero-value property over a seeded random: for several thousand random terms and windows (half built to match, then half of those mutated), whenever
    /// Judge accepts, the bot's side is worth no more than it receives: a seller has partner gold &gt;= price &gt;= its vendor floor &gt; 0 and gives exactly the goods; a
    /// buyer pays gold in (0, price] with reference*100 &gt;= gold*67 for goods exactly matching. Also proves the generator reaches both Accept outcomes.
    /// </summary>
    [Fact]
    public void DealRules_NeverZeroValue()
    {
        var random = new Random(20261028);
        int sellerAccepts = 0, buyerAccepts = 0, unsafeCount = 0;
        for (int i = 0; i < 6000; i++)
        {
            var role = random.Next(2) == 0 ? PlayerbotDealRole.Seller : PlayerbotDealRole.Buyer;
            uint entry = (uint)random.Next(1, 4);
            uint count = (uint)random.Next(1, 5);
            long price = random.Next(-5, 3000);
            long floor = random.Next(1, 1500);
            long reference = random.Next(-1, 3000);
            var terms = new PlayerbotDealTerms(role, entry, count, price, floor, reference);

            // A window built to match the terms, then (half the time) one random mutation.
            var goods = new List<PlayerbotDealGoods>();
            uint remaining = count;
            while (remaining > 0)
            {
                uint part = (uint)random.Next(1, (int)remaining + 1);
                goods.Add(new PlayerbotDealGoods(entry, part, false));
                remaining -= part;
            }

            var botGoods = new List<PlayerbotDealGoods>();
            var partnerGoods = new List<PlayerbotDealGoods>();
            long botGold = 0, partnerGold = 0;
            if (role == PlayerbotDealRole.Seller)
            {
                botGoods.AddRange(goods);
                partnerGold = Math.Max(0, price) + random.Next(0, 3) - 1;
            }
            else
            {
                partnerGoods.AddRange(goods);
                botGold = Math.Max(1, price - random.Next(0, 3));
            }

            if (random.Next(2) == 0)
            {
                switch (random.Next(8))
                {
                    case 0: partnerGold = random.Next(0, 3500); break;
                    case 1: botGold = random.Next(0, 3500); break;
                    case 2: botGoods.Add(new PlayerbotDealGoods((uint)random.Next(1, 4), (uint)random.Next(1, 4), random.Next(4) == 0)); break;
                    case 3: partnerGoods.Add(new PlayerbotDealGoods((uint)random.Next(1, 4), (uint)random.Next(1, 4), random.Next(4) == 0)); break;
                    case 4 when botGoods.Count > 0: botGoods[0] = botGoods[0] with { Entry = (uint)random.Next(1, 4) }; break;
                    case 5 when partnerGoods.Count > 0: partnerGoods[0] = partnerGoods[0] with { Entry = (uint)random.Next(1, 4) }; break;
                    case 6 when botGoods.Count > 0: botGoods[0] = botGoods[0] with { Soulbound = true }; break;
                    default: if (partnerGoods.Count > 0) partnerGoods[0] = partnerGoods[0] with { Count = (uint)random.Next(1, 6) }; break;
                }
            }

            var window = new PlayerbotDealWindow(botGoods, botGold, partnerGoods, partnerGold);
            PlayerbotDealVerdict verdict = PlayerbotDealRules.Judge(terms, window);
            if (verdict == PlayerbotDealVerdict.Unsafe) { unsafeCount++; continue; }
            if (verdict != PlayerbotDealVerdict.Accept) continue;

            Assert.True(price > 0, $"accepted a price of {price}");
            if (role == PlayerbotDealRole.Seller)
            {
                sellerAccepts++;
                Assert.True(partnerGold >= price && price >= floor && floor > 0, $"seller accept: gold {partnerGold} price {price} floor {floor}");
                Assert.Equal(0, botGold);
                Assert.Empty(partnerGoods);
                Assert.Equal((long)count, botGoods.Sum(g => (long)g.Count));
                Assert.All(botGoods, g => Assert.True(g.Entry == entry && !g.Soulbound));
            }
            else
            {
                buyerAccepts++;
                Assert.True(botGold > 0 && botGold <= price, $"buyer accept: gold {botGold} price {price}");
                Assert.True(reference * 100 >= botGold * 67, $"buyer accept: gold {botGold} reference {reference}");
                Assert.Empty(botGoods);
                Assert.Equal((long)count, partnerGoods.Sum(g => (long)g.Count));
                Assert.All(partnerGoods, g => Assert.True(g.Entry == entry && !g.Soulbound));
            }
        }

        Assert.True(sellerAccepts > 100, $"the generator reached only {sellerAccepts} seller accepts");
        Assert.True(buyerAccepts > 100, $"the generator reached only {buyerAccepts} buyer accepts");
        Assert.True(unsafeCount > 100, $"the generator reached only {unsafeCount} unsafe terms");
    }

    // --- ParsePrice / IsYes ---------------------------------------------------------------------------------------------------------

    /// <summary>A price needs a unit; the units sum; the unit is a whole word; case does not matter. Broken: a bare "50" read as 50 copper or "silverware" as silver.</summary>
    [Theory]
    [InlineData("1g50s", 15_000L)]
    [InlineData("1g 50s", 15_000L)]
    [InlineData("150s", 15_000L)]
    [InlineData("2 gold", 20_000L)]
    [InlineData("1g 2s 3c", 10_203L)]
    [InlineData("5 copper", 5L)]
    [InlineData("10 SILVER", 1_000L)]
    [InlineData("1 G", 10_000L)]
    [InlineData("2g, 5s", 20_500L)]
    [InlineData("ill pay 3s", 300L)]
    [InlineData("4294967295c", 4_294_967_295L)]
    [InlineData("429496g 7295c", 4_294_967_295L)]
    public void ParsePrice_ReadsAmountsWithAUnit(string text, long copper) => Assert.Equal(copper, PlayerbotDealRules.ParsePrice(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("50")]
    [InlineData("deal")]
    [InlineData("i have 3 of them")]
    [InlineData("1.5g")]
    [InlineData("1,5g")]
    [InlineData("AQ40 gold")]
    [InlineData("50 silverware")]
    [InlineData("2 gx")]
    [InlineData("4294967296c")]
    [InlineData("430000g")]
    [InlineData("99999999999999999999c")]
    [InlineData("429496g 7296c")]
    public void ParsePrice_BareNumbersAmbiguousAndOversizedAmountsAreNoPrice(string text) => Assert.Null(PlayerbotDealRules.ParsePrice(text));

    [Theory]
    [InlineData("ok")]
    [InlineData("OK!")]
    [InlineData("okay")]
    [InlineData("k")]
    [InlineData("deal")]
    [InlineData("Yes.")]
    [InlineData("yep")]
    [InlineData("yea")]
    [InlineData("yeah")]
    [InlineData("sure")]
    [InlineData("buy")]
    [InlineData("sold")]
    [InlineData("ill take it")]
    [InlineData("I'll take it")]
    [InlineData("I’ll take it!")]
    [InlineData("sure thanks")]
    [InlineData("yeah pls")]
    public void IsYes_AcceptsTheAgreementWords(string text) => Assert.True(PlayerbotDealRules.IsYes(text));

    /// <summary>A line with a price is a counter-offer, never a yes; neither is a question, a refusal or a long sentence that merely contains "ok".</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    [InlineData("ok 5g")]
    [InlineData("deal for 5g")]
    [InlineData("ok?")]
    [InlineData("deal or no deal")]
    [InlineData("take it")]
    [InlineData("not ok")]
    [InlineData("ok lets go now please thanks then yes yes")]
    public void IsYes_RejectsCountersQuestionsAndSentences(string text) => Assert.False(PlayerbotDealRules.IsYes(text));

    // --- AcceptCounter ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A seller takes a counter of at least quote less the tolerance, rounded UP (1000 at 10% -> 900; 1001 at 10% -> 90090/100 = 900.9 -> 901), never under its limit
    /// (the vendor floor), never above the quote, and only one counter per deal. Broken: 899 accepted, the floor ignored, a second counter taken, or a price above the quote.
    /// </summary>
    [Fact]
    public void AcceptCounter_Seller_ToleranceBoundaryExactlyAtNinetyPercent_OneCounterOnly()
    {
        const PlayerbotDealRole S = PlayerbotDealRole.Seller;
        Assert.Equal(900L, PlayerbotDealRules.AcceptCounter(S, 1000, 400, 900, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(S, 1000, 400, 899, 10, false));
        Assert.Equal(1000L, PlayerbotDealRules.AcceptCounter(S, 1000, 400, 1000, 10, false));
        Assert.Equal(1000L, PlayerbotDealRules.AcceptCounter(S, 1000, 400, 1500, 10, false)); // a counter above the quote agrees at the quote
        Assert.Equal(901L, PlayerbotDealRules.AcceptCounter(S, 1001, 400, 901, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(S, 1001, 400, 900, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(S, 1000, 950, 920, 10, false)); // within tolerance but under the vendor floor
        Assert.Equal(950L, PlayerbotDealRules.AcceptCounter(S, 1000, 950, 950, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(S, 1000, 400, 999, 0, false)); // tolerance 0: only the full quote
        Assert.Equal(1000L, PlayerbotDealRules.AcceptCounter(S, 1000, 400, 1000, 0, false));
        Assert.Equal(500L, PlayerbotDealRules.AcceptCounter(S, 1000, 400, 500, 50, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(S, 1000, 400, 950, 10, counterUsed: true)); // one counter, then it holds
        Assert.Null(PlayerbotDealRules.AcceptCounter(S, 1000, 400, 0, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(S, 1000, 400, -5, 10, false));
    }

    /// <summary>A buyer takes a counter (what the partner asks) up to its ceiling and no higher; one counter per deal; never zero.</summary>
    [Fact]
    public void AcceptCounter_Buyer_UpToTheCeilingOnly_OneCounterOnly()
    {
        const PlayerbotDealRole B = PlayerbotDealRole.Buyer;
        Assert.Equal(1500L, PlayerbotDealRules.AcceptCounter(B, 1000, 1500, 1500, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(B, 1000, 1500, 1501, 10, false));
        Assert.Equal(1L, PlayerbotDealRules.AcceptCounter(B, 1000, 1500, 1, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(B, 1000, 1500, 0, 10, false));
        Assert.Null(PlayerbotDealRules.AcceptCounter(B, 1000, 1500, 1200, 10, counterUsed: true));
    }

    // --- prices and limits ----------------------------------------------------------------------------------------------------------

    /// <summary>The seller's ask: the reference, less the friend discount scaled by affinity, or plus the dislike markup, integer math. Hand-worked from 3 x 1000.</summary>
    [Fact]
    public void SellerAsk_ScalesTheDiscountAndMarkupByAffinity()
    {
        Assert.Equal(3000L, PlayerbotDealRules.SellerAsk(1000, 3, 0, 20, 25));
        Assert.Equal(2400L, PlayerbotDealRules.SellerAsk(1000, 3, 100, 20, 25)); // 3000 - 3000*20*100/10000 = 3000 - 600
        Assert.Equal(2700L, PlayerbotDealRules.SellerAsk(1000, 3, 50, 20, 25)); // 3000 - 300
        Assert.Equal(3750L, PlayerbotDealRules.SellerAsk(1000, 3, -100, 20, 25)); // 3000 + 750
        Assert.Equal(3375L, PlayerbotDealRules.SellerAsk(1000, 3, -50, 20, 25)); // 3000 + 375
        Assert.Equal(2400L, PlayerbotDealRules.SellerAsk(1000, 3, 500, 20, 25)); // affinity is clamped to 100
        Assert.Equal(3750L, PlayerbotDealRules.SellerAsk(1000, 3, -500, 20, 25));
        Assert.Equal(3000L, PlayerbotDealRules.SellerAsk(1000, 3, 100, 0, 0));
        Assert.Equal(3000L, PlayerbotDealRules.BuyerBid(1000, 3));
    }

    /// <summary>
    /// The buyer ceiling is the reference plus the premium, never above the reference divided by 0.67 (100000/67 = 1492 for a 1000 reference), and always a price
    /// <see cref="PlayerbotDealRules.TermsAreSafe"/> allows (swept over references, counts and premiums).
    /// </summary>
    [Fact]
    public void BuyerCeiling_PremiumCappedAtTheReferenceOverZeroPointSixSeven()
    {
        Assert.Equal(1000L, PlayerbotDealRules.BuyerCeiling(1000, 1, 0));
        Assert.Equal(1200L, PlayerbotDealRules.BuyerCeiling(1000, 1, 20));
        Assert.Equal(1492L, PlayerbotDealRules.BuyerCeiling(1000, 1, 50)); // 1500 would break the bound
        Assert.Equal(1492L, PlayerbotDealRules.BuyerCeiling(1000, 1, 100));
        Assert.Equal(1492L, PlayerbotDealRules.BuyerCeiling(1000, 1, 500)); // the premium is clamped to 100
        Assert.Equal(3000L, PlayerbotDealRules.BuyerCeiling(1000, 3, 0));
        for (uint reference = 1; reference <= 4000; reference += 7)
            for (uint count = 1; count <= 5; count++)
                for (int premium = 0; premium <= 100; premium += 10)
                {
                    long ceiling = PlayerbotDealRules.BuyerCeiling(reference, count, premium);
                    Assert.True(PlayerbotDealRules.TermsAreSafe(new PlayerbotDealTerms(PlayerbotDealRole.Buyer, Wanted, count, ceiling, 0, (long)reference * count)),
                        $"ceiling {ceiling} for reference {reference} x{count} premium {premium}");
                }
    }

    /// <summary>The quarter cap applies strictly below the low-trust line (trust 349 vs 350), integer division.</summary>
    [Fact]
    public void DealCap_LowTrustGetsAQuarter_AtTheBoundary()
    {
        Assert.Equal(500_000L, PlayerbotDealRules.DealCap(500_000, 350, 350));
        Assert.Equal(125_000L, PlayerbotDealRules.DealCap(500_000, 349, 350));
        Assert.Equal(125_000L, PlayerbotDealRules.DealCap(500_000, 0, 350));
        Assert.Equal(500_000L, PlayerbotDealRules.DealCap(500_000, 1000, 350));
        Assert.Equal(1L, PlayerbotDealRules.DealCap(7, 0, 350));
        Assert.Equal(0L, PlayerbotDealRules.DealCap(3, 0, 350));
    }

    /// <summary>The outflow is what the bot gives away below (seller) or above (buyer) the reference value, never negative.</summary>
    [Fact]
    public void Outflow_IsTheDiscountTheBotGives_NeverNegative()
    {
        Assert.Equal(200L, PlayerbotDealRules.Outflow(Sell(price: 800, reference: 1000)));
        Assert.Equal(0L, PlayerbotDealRules.Outflow(Sell(price: 1000, reference: 1000)));
        Assert.Equal(0L, PlayerbotDealRules.Outflow(Sell(price: 1200, reference: 1000)));
        Assert.Equal(200L, PlayerbotDealRules.Outflow(Buy(price: 1200, reference: 1000)));
        Assert.Equal(0L, PlayerbotDealRules.Outflow(Buy(price: 1000, reference: 1000)));
        Assert.Equal(0L, PlayerbotDealRules.Outflow(Buy(price: 900, reference: 1000)));
    }

    /// <summary>
    /// The price a bot quotes is one that chat writes exactly: rounded up for an ask and down for a bid to 1 copper below 10s, 1 silver below 10g, 10 silver above
    /// (boundaries worked by hand), and what <see cref="PlayerbotVoiceFacts.Price"/> writes reads back as the same amount.
    /// </summary>
    [Fact]
    public void QuoteRounding_MatchesWhatChatWrites()
    {
        Assert.Equal(1L, PlayerbotDealRules.RoundUpToQuote(0));
        Assert.Equal(999L, PlayerbotDealRules.RoundUpToQuote(999));
        Assert.Equal(1000L, PlayerbotDealRules.RoundUpToQuote(1000));
        Assert.Equal(1100L, PlayerbotDealRules.RoundUpToQuote(1001));
        Assert.Equal(100_000L, PlayerbotDealRules.RoundUpToQuote(99_901));
        Assert.Equal(101_000L, PlayerbotDealRules.RoundUpToQuote(100_001));
        Assert.Equal(1L, PlayerbotDealRules.RoundDownToQuote(0));
        Assert.Equal(999L, PlayerbotDealRules.RoundDownToQuote(999));
        Assert.Equal(1000L, PlayerbotDealRules.RoundDownToQuote(1099));
        Assert.Equal(99_900L, PlayerbotDealRules.RoundDownToQuote(99_999));
        Assert.Equal(100_000L, PlayerbotDealRules.RoundDownToQuote(100_999));
        for (long copper = 1; copper <= 400_000; copper += 7)
        {
            long up = PlayerbotDealRules.RoundUpToQuote(copper), down = PlayerbotDealRules.RoundDownToQuote(copper);
            Assert.True(up >= copper && down <= copper || down == 1, $"{copper}: up {up} down {down}");
            Assert.Equal(up, PlayerbotDealRules.ParsePrice(PlayerbotVoiceFacts.Price(up)));
            Assert.Equal(down, PlayerbotDealRules.ParsePrice(PlayerbotVoiceFacts.Price(down)));
        }
    }

    // --- fingerprints ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The fingerprint tells a real change of a window side from a bounce: equal input gives an equal value (stable across calls), and a different gold, entry, count,
    /// soulbound flag, slot count or item guid gives a different one. Broken: the bot could not tell the partner swapped an item for another stack of the same entry.
    /// </summary>
    [Fact]
    public void Fingerprint_IsStable_AndSeesEveryRealChange()
    {
        PlayerbotDealGoods[] goods = [G(Wanted, 2)];
        ulong[] guids = [11UL];
        long baseline = PlayerbotDealRules.Fingerprint(goods, 1000, guids);
        Assert.Equal(baseline, PlayerbotDealRules.Fingerprint([G(Wanted, 2)], 1000, new ulong[] { 11UL }));
        Assert.NotEqual(baseline, PlayerbotDealRules.Fingerprint(goods, 999, guids));
        Assert.NotEqual(baseline, PlayerbotDealRules.Fingerprint([G(Other, 2)], 1000, guids));
        Assert.NotEqual(baseline, PlayerbotDealRules.Fingerprint([G(Wanted, 3)], 1000, guids));
        Assert.NotEqual(baseline, PlayerbotDealRules.Fingerprint([G(Wanted, 2, soulbound: true)], 1000, guids));
        Assert.NotEqual(baseline, PlayerbotDealRules.Fingerprint([G(Wanted, 2), G(Wanted, 1)], 1000, new ulong[] { 11UL, 12UL }));
        Assert.NotEqual(baseline, PlayerbotDealRules.Fingerprint(goods, 1000, new ulong[] { 12UL })); // same entry and count, another stack
        Assert.NotEqual(PlayerbotDealRules.Combine(1, 2), PlayerbotDealRules.Combine(2, 1));
        Assert.Equal(PlayerbotDealRules.Combine(1, 2), PlayerbotDealRules.Combine(1, 2));
    }
}

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>The bot's side of a deal: <see cref="Seller"/> gives items for gold, <see cref="Buyer"/> gives gold for items.</summary>
internal enum PlayerbotDealRole : byte
{
    Seller,
    Buyer,
}

/// <summary>One traded slot: the item's entry, its stack count and whether it is soulbound.</summary>
internal readonly record struct PlayerbotDealGoods(uint Entry, uint Count, bool Soulbound);

/// <summary>
/// The terms of one deal, fixed when it was agreed. <see cref="Price"/> is the total copper agreed; <see cref="VendorFloor"/> is the
/// vendor sell price times the count; <see cref="ReferenceValue"/> is the reference price per unit times the count.
/// </summary>
internal sealed record PlayerbotDealTerms(PlayerbotDealRole Role, uint Entry, uint Count, long Price, long VendorFloor, long ReferenceValue);

/// <summary>
/// What a live trade window holds, from the bot's point of view: the goods and gold on the bot's own side and on the partner's side.
/// </summary>
internal readonly record struct PlayerbotDealWindow(IReadOnlyList<PlayerbotDealGoods> BotGoods, long BotGold, IReadOnlyList<PlayerbotDealGoods> PartnerGoods, long PartnerGold);

/// <summary>What <see cref="PlayerbotDealRules.Judge"/> decides about a window.</summary>
internal enum PlayerbotDealVerdict : byte
{
    /// <summary>The window is exactly the agreed deal: the bot may accept.</summary>
    Accept,

    /// <summary>The window is not (yet) the agreed deal; the bot waits or completes its own side.</summary>
    NotYet,

    /// <summary>The terms themselves break an anti-scam rule: the bot never accepts them.</summary>
    Unsafe,
}

/// <summary>
/// The pure rules of a bot's fixed-price trade deal (docs/areas/playbots-social.md, Trading; design section 2.3): which terms are safe, when
/// a live window is the agreed deal, how a price is read out of a line, how one counter-offer is weighed, and how an ask, a bid and a
/// ceiling are worked out. No world access; every input is handed in. Integer math throughout (no float rounding), so a price said in chat
/// is the price the rules compare against.
/// </summary>
internal static class PlayerbotDealRules
{
    /// <summary>The most copper a deal may involve: a character's gold cap is far above it, and the window's gold field is a 32-bit copper amount.</summary>
    internal const long MaxPrice = int.MaxValue;

    /// <summary>The share (percent) of the goods' reference value a buyer's price may be at most 100/67 of: the buyer never pays more than 1/0.67 of what the goods are worth.</summary>
    internal const int BuyerValuePercent = 67;

    /// <summary>
    /// Whether the terms themselves are safe to deal on. A count and an entry are needed; the price is positive and at most
    /// <see cref="MaxPrice"/>; the goods have a reference value. A seller never sells under the vendor floor. A buyer never pays more than
    /// the reference value divided by 0.67.
    /// </summary>
    internal static bool TermsAreSafe(PlayerbotDealTerms t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Count == 0 || t.Entry == 0 || t.Price <= 0 || t.Price > MaxPrice || t.ReferenceValue <= 0 || t.VendorFloor < 0) return false;
        if (t.Role == PlayerbotDealRole.Seller) return t.Price >= t.VendorFloor;
        return (Int128)t.ReferenceValue * 100 >= (Int128)t.Price * BuyerValuePercent;
    }

    /// <summary>
    /// Whether the window is the agreed deal. Seller: the bot side is exactly <see cref="PlayerbotDealTerms.Count"/> unbound items of the entry and no gold,
    /// the partner side is gold of at least the price and nothing else. Buyer: the partner side is exactly the count of the entry, none soulbound,
    /// and the bot side is only gold, more than 0 and at most the price (what the partner adds on its side in gold only adds to what the bot receives, and is ignored).
    /// </summary>
    internal static PlayerbotDealVerdict Judge(PlayerbotDealTerms t, PlayerbotDealWindow w)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!TermsAreSafe(t)) return PlayerbotDealVerdict.Unsafe;
        if (t.Role == PlayerbotDealRole.Seller)
        {
            bool match = w.BotGold == 0 && IsEmpty(w.PartnerGoods) && w.PartnerGold >= t.Price && Matches(w.BotGoods, t);
            return match ? PlayerbotDealVerdict.Accept : PlayerbotDealVerdict.NotYet;
        }

        bool buyMatch = IsEmpty(w.BotGoods) && w.BotGold > 0 && w.BotGold <= t.Price && Matches(w.PartnerGoods, t);
        return buyMatch ? PlayerbotDealVerdict.Accept : PlayerbotDealVerdict.NotYet;
    }

    /// <summary>Whether <paramref name="goods"/> is exactly the deal's goods: not empty, every slot the entry, none soulbound, the counts summing to the deal's count.</summary>
    internal static bool Matches(IReadOnlyList<PlayerbotDealGoods>? goods, PlayerbotDealTerms t)
    {
        if (goods is null || goods.Count == 0) return false;
        long sum = 0;
        foreach (PlayerbotDealGoods slot in goods)
        {
            if (slot.Entry != t.Entry || slot.Soulbound || slot.Count == 0) return false;
            sum += slot.Count;
        }

        return sum == t.Count;
    }

    private static bool IsEmpty(IReadOnlyList<PlayerbotDealGoods>? goods) => goods is null || goods.Count == 0;

    // --- reading a line ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The copper a line names: every <c>&lt;digits&gt;&lt;optional spaces&gt;&lt;unit&gt;</c> summed, the unit being g, gold, s, silver, c or copper (case-insensitive,
    /// the whole word). "1g50s", "1g 50s", "150s" and "2 gold" work. Null when the line names no such amount, when a number is glued to letters before it
    /// ("AQ40 gold"), when a number has a decimal part ("1.5g": ambiguous, never guessed), or when the sum is above <see cref="uint.MaxValue"/> copper.
    /// A bare number without a unit is not a price.
    /// </summary>
    internal static long? ParsePrice(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        long total = 0;
        bool any = false;
        int i = 0;
        int unitEnd = -1; // where the last unit token this parser consumed ended: "1g50s" reads 50s right after it
        while (i < text.Length)
        {
            if (!IsAsciiDigit(text[i])) { i++; continue; }
            int start = i;
            long value = 0;
            bool overflow = false;
            while (i < text.Length && IsAsciiDigit(text[i]))
            {
                if (value <= uint.MaxValue) value = (value * 10) + (text[i] - '0');
                else overflow = true;
                i++;
            }

            if (value > uint.MaxValue) overflow = true;
            // A decimal part ("1.5g", "1,5g") is ambiguous: no price at all rather than a wrong one.
            if (i + 1 < text.Length && (text[i] == '.' || text[i] == ',') && IsAsciiDigit(text[i + 1])) return null;
            if (start > 0 && start != unitEnd && (char.IsLetter(text[start - 1]) || text[start - 1] == '.' || text[start - 1] == ',')) continue; // glued to a word ("AQ40"), or the tail of a decimal
            int j = i;
            while (j < text.Length && text[j] == ' ') j++;
            int unitStart = j;
            while (j < text.Length && char.IsLetter(text[j])) j++;
            if (j == unitStart) continue;
            long multiplier = UnitMultiplier(text.AsSpan(unitStart, j - unitStart));
            if (multiplier == 0) continue;
            if (overflow) return null;
            total += value * multiplier;
            if (total > uint.MaxValue) return null;
            any = true;
            i = j;
            unitEnd = j;
        }

        return any ? total : null;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static long UnitMultiplier(ReadOnlySpan<char> word)
    {
        if (word.Equals("g", StringComparison.OrdinalIgnoreCase) || word.Equals("gold", StringComparison.OrdinalIgnoreCase)) return 10_000;
        if (word.Equals("s", StringComparison.OrdinalIgnoreCase) || word.Equals("silver", StringComparison.OrdinalIgnoreCase)) return 100;
        if (word.Equals("c", StringComparison.OrdinalIgnoreCase) || word.Equals("copper", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static readonly HashSet<string> s_yesWords = new(StringComparer.Ordinal)
    {
        "ok", "okay", "k", "kk", "deal", "sure", "yes", "yep", "yea", "yeah", "buy", "sold",
    };

    private static readonly HashSet<string> s_fillerWords = new(StringComparer.Ordinal)
    {
        "pls", "plz", "please", "ty", "thx", "thanks", "then", "ok", "okay", "k", "deal", "sure", "yes", "yep", "yea", "yeah", "buy", "sold",
    };

    /// <summary>
    /// Whether a line agrees to the quoted deal: after lower-casing and stripping punctuation it is made only of agreement words
    /// (ok, okay, k, deal, sure, yes, yep, yea, yeah, buy, sold, "ill take it", "i'll take it", with "pls"/"thanks" and the like allowed beside them).
    /// A line with a price in it is a counter-offer, never a yes, and neither is a question.
    /// </summary>
    internal static bool IsYes(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains('?') || ParsePrice(text) is not null) return false;
        var words = new List<string>(4);
        var word = new System.Text.StringBuilder(12);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) word.Append(char.ToLowerInvariant(c));
            else if (c == '\'' || c == '’') continue; // i'll -> ill
            else if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); }

            if (words.Count > 6) return false;
        }

        if (word.Length > 0) words.Add(word.ToString());
        if (words.Count == 0 || words.Count > 6) return false;
        if (words.Count == 3 && words[0] == "ill" && words[1] == "take" && words[2] == "it") return true;
        bool yes = false;
        foreach (string w in words)
        {
            if (s_yesWords.Contains(w)) yes = true;
            else if (!s_fillerWords.Contains(w)) return false;
        }

        return yes;
    }

    // --- counter-offers ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The price the bot agrees to for one counter-offer, or null (it holds its price). One counter per deal: after <paramref name="counterUsed"/> the answer is null.
    /// Seller: the counter is accepted when it is at least the quote less <paramref name="tolerancePercent"/> (rounded up) and at least <paramref name="limit"/>
    /// (the vendor floor); the price is the counter, never above the quote. Buyer: the counter (what the partner asks) is accepted when it is above 0 and at most
    /// <paramref name="limit"/> (the bot's ceiling); the price is the counter.
    /// </summary>
    internal static long? AcceptCounter(PlayerbotDealRole role, long quoted, long limit, long counter, int tolerancePercent, bool counterUsed)
    {
        if (counterUsed || counter <= 0 || quoted <= 0) return null;
        if (role == PlayerbotDealRole.Seller)
        {
            long tolerance = Math.Clamp(tolerancePercent, 0, 100);
            long lowest = (quoted * (100 - tolerance) + 99) / 100;
            if (counter < lowest || counter < limit) return null;
            return Math.Min(counter, quoted);
        }

        return counter <= limit ? counter : null;
    }

    // --- prices ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A seller's total ask for <paramref name="count"/> units of <paramref name="referencePerUnit"/>: the reference, less up to
    /// <paramref name="friendDiscountPercent"/> at full positive <paramref name="affinity"/> (-100..100), or more by up to <paramref name="dislikeMarkupPercent"/>
    /// at full negative affinity. The caller raises it to at least the vendor floor.
    /// </summary>
    internal static long SellerAsk(uint referencePerUnit, uint count, int affinity, int friendDiscountPercent, int dislikeMarkupPercent)
    {
        long basis = (long)referencePerUnit * count;
        int a = Math.Clamp(affinity, -100, 100);
        if (a > 0) return basis - (basis * Math.Clamp(friendDiscountPercent, 0, 100) * a / 10_000);
        if (a < 0) return basis + (basis * Math.Clamp(dislikeMarkupPercent, 0, 200) * -a / 10_000);
        return basis;
    }

    /// <summary>A buyer's opening bid: the reference value of the goods.</summary>
    internal static long BuyerBid(uint referencePerUnit, uint count) => (long)referencePerUnit * count;

    /// <summary>
    /// The most a buyer pays: the reference value plus <paramref name="premiumPercent"/>, and never more than the reference value divided by 0.67
    /// (the anti-scam bound of <see cref="TermsAreSafe"/>).
    /// </summary>
    internal static long BuyerCeiling(uint referencePerUnit, uint count, int premiumPercent)
    {
        long basis = (long)referencePerUnit * count;
        long premium = basis * (100 + Math.Clamp(premiumPercent, 0, 100)) / 100;
        long bound = basis * 100 / BuyerValuePercent;
        return Math.Min(premium, bound);
    }

    // A price in chat is written by PlayerbotVoiceFacts.Price: rounded to the 10 silver from 10 gold, to the silver from 1 gold, else exact. A price the
    // bot quotes is made one of those amounts, so the number in the whisper is the number the rules compare against.
    private static long Granularity(long copper) => copper >= 100_000 ? 1000 : copper >= 1000 ? 100 : 1;

    /// <summary>The smallest amount at or above <paramref name="copper"/> that a chat price shows exactly (a seller's ask).</summary>
    internal static long RoundUpToQuote(long copper)
    {
        copper = Math.Max(1, copper);
        long step = Granularity(copper);
        return (copper + step - 1) / step * step;
    }

    /// <summary>The largest amount at or below <paramref name="copper"/> that a chat price shows exactly, at least 1 (a buyer's bid).</summary>
    internal static long RoundDownToQuote(long copper)
    {
        copper = Math.Max(1, copper);
        long step = Granularity(copper);
        return Math.Max(1, copper / step * step);
    }

    // --- limits ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The copper the bot gives away at these terms, counted against the daily outflow caps: a seller below the reference value gives the
    /// difference, a buyer above it gives the difference; never negative.
    /// </summary>
    internal static long Outflow(PlayerbotDealTerms t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return t.Role == PlayerbotDealRole.Seller ? Math.Max(0, t.ReferenceValue - t.Price) : Math.Max(0, t.Price - t.ReferenceValue);
    }

    /// <summary>The most one deal may be worth: <paramref name="maxDealCopper"/>, a quarter of it for a partner whose trust is below <paramref name="lowTrustPermille"/>.</summary>
    internal static long DealCap(int maxDealCopper, int trustPermille, int lowTrustPermille)
        => trustPermille < lowTrustPermille ? maxDealCopper / 4 : maxDealCopper;

    // --- fingerprints ----------------------------------------------------------------------------------------------------

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// A stable (FNV-1a, not randomised per process) hash of one side of a window: every slot's entry, count and soulbound flag, the gold, and the item guids
    /// (<paramref name="itemGuids"/>, in slot order, so the same entry swapped for another stack differs). Tells a partner's real change from a bounce.
    /// </summary>
    internal static long Fingerprint(IReadOnlyList<PlayerbotDealGoods> goods, long gold, ReadOnlySpan<ulong> itemGuids)
    {
        ulong h = FnvOffset;
        h = Mix(h, (ulong)gold);
        h = Mix(h, (ulong)(goods?.Count ?? 0));
        if (goods is not null)
            foreach (PlayerbotDealGoods slot in goods)
            {
                h = Mix(h, slot.Entry);
                h = Mix(h, slot.Count);
                h = Mix(h, slot.Soulbound ? 1UL : 0UL);
            }

        foreach (ulong guid in itemGuids) h = Mix(h, guid);
        return unchecked((long)h);
    }

    /// <summary>Combines two fingerprints (order matters).</summary>
    internal static long Combine(long a, long b) => unchecked((long)Mix(Mix(FnvOffset, (ulong)a), (ulong)b));

    private static ulong Mix(ulong h, ulong value)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
            {
                h ^= (value >> shift) & 0xFF;
                h *= FnvPrime;
            }
        }

        return h;
    }
}

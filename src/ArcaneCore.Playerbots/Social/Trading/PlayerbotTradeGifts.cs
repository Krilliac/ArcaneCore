using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.World.Playerbots.Chat;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What a bot does with a trade request from someone it has no desk deal with (<see cref="PlayerbotTradeGifts.Answer"/>).</summary>
internal enum PlayerbotTradeAnswer : byte
{
    /// <summary>Open the window (CMSG_BEGIN_TRADE): a partner the bot trusts, as a gift window.</summary>
    Begin,

    /// <summary>Decline with "busy" (CMSG_BUSY_TRADE): a request nobody expected.</summary>
    Busy,

    /// <summary>Do nothing: <c>Social:Trading:AnswerTrades</c> is off.</summary>
    Ignore,
}

/// <summary>Whether a bot takes a gift window's items (<see cref="PlayerbotTradeGifts.Judge"/>).</summary>
internal enum PlayerbotGiftVerdict : byte
{
    Accept,

    /// <summary><c>Social:Trading:AcceptGifts</c> is off.</summary>
    Disabled,

    /// <summary>The partner already gave the bot <c>Social:Trading:MaxGiftsReceivedPerHour</c> gifts within the last hour.</summary>
    HourlyLimit,
}

/// <summary>One CMSG_SPLIT_ITEM the trade desk asks for: <see cref="Amount"/> units from the stack at (<see cref="SrcBag"/>, <see cref="SrcSlot"/>) into the empty slot (<see cref="DstBag"/>, <see cref="DstSlot"/>).</summary>
internal readonly record struct PlayerbotStackSplit(byte SrcBag, byte SrcSlot, byte DstBag, byte DstSlot, byte Amount);

/// <summary>
/// The playerbot's side of the "gift" trade windows and the desk's stack splitting (docs/areas/playbots-social.md, Trading). One instance per bot party AI.
/// <list type="bullet">
/// <item><see cref="Answer"/>: <c>Social:Trading:AnswerTrades</c>. Off: a request is left alone. On: a trusted partner (master, groupmate, an allowed trader) is
/// answered with BeginTrade and the window is a gift window; anyone else gets "busy".</item>
/// <item><see cref="Judge"/>: <c>Social:Trading:AcceptGifts</c> and <c>Social:Trading:MaxGiftsReceivedPerHour</c> (gifts accepted from one partner in a sliding hour; 0
/// accepts none). <see cref="NoteReceived"/> counts a gift when its window completed.</item>
/// <item><see cref="Counts"/>: <c>Social:Trading:GiftMinValueCopper</c>, the vendor value from which a received gift counts as a trade for the bot's memory.</item>
/// </list>
/// The hourly counts live in memory (they reset with the bot's session); persisting them needs a characters-schema table (the persisted-counters step is not in this type).
/// Options are read live from <see cref="PlayerbotOptions"/> on every call. Thread affinity: the world thread of the bot (the instance takes a lock anyway, so a test may read it from anywhere).
/// Memory is bounded: at most <see cref="MapCap"/> partners and <see cref="PerPartnerCap"/> stamps each.
/// </summary>
internal sealed class PlayerbotTradeGifts(PlayerbotOptions options, Func<long> clockUnixMs)
{
    /// <summary>Partners the hourly counter remembers before the one written longest ago is dropped.</summary>
    internal const int MapCap = 256;

    /// <summary>Stamps kept per partner (the hourly limit's maximum is 120).</summary>
    internal const int PerPartnerCap = 128;

    /// <summary>Stacks the split planner may hand to one trade (the window has six traded slots).</summary>
    internal const int TradedSlots = 6;

    private const long HourMs = 3_600_000;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Queue<long>> _received = [];
    private readonly Func<long> _clock = clockUnixMs ?? throw new ArgumentNullException(nameof(clockUnixMs));

    private PlayerbotSocialTradingOptions Trading => options.Social.Trading;

    /// <summary>The answer to a trade request from a partner with no desk deal (see the type summary).</summary>
    internal PlayerbotTradeAnswer Answer(bool trusted)
    {
        if (!Trading.AnswerTrades) return PlayerbotTradeAnswer.Ignore;
        return trusted ? PlayerbotTradeAnswer.Begin : PlayerbotTradeAnswer.Busy;
    }

    /// <summary>Whether the bot takes a gift from <paramref name="partner"/> now.</summary>
    internal PlayerbotGiftVerdict Judge(ulong partner)
    {
        PlayerbotSocialTradingOptions trading = Trading;
        if (!trading.AcceptGifts) return PlayerbotGiftVerdict.Disabled;
        return ReceivedWithinHour(partner) >= trading.MaxGiftsReceivedPerHour ? PlayerbotGiftVerdict.HourlyLimit : PlayerbotGiftVerdict.Accept;
    }

    /// <summary>
    /// Whether a window counts as a trade for the bot's memory: the bot gave something, or it received at least <c>GiftMinValueCopper</c> of item vendor value
    /// (<paramref name="receivedValue"/>, summed over the stacks; gold the partner puts in is not counted).
    /// </summary>
    internal bool Counts(bool gave, long receivedValue) => gave || receivedValue >= Trading.GiftMinValueCopper;

    /// <summary>The gifts <paramref name="partner"/> gave the bot inside the last hour.</summary>
    internal int ReceivedWithinHour(ulong partner)
    {
        long now = _clock();
        lock (_gate)
        {
            if (!_received.TryGetValue(partner, out Queue<long>? stamps)) return 0;
            while (stamps.Count > 0 && now - stamps.Peek() >= HourMs) stamps.Dequeue();
            if (stamps.Count != 0) return stamps.Count;
            _received.Remove(partner);
            return 0;
        }
    }

    /// <summary>One more gift from <paramref name="partner"/> (its window completed with items on the partner's side and none on the bot's).</summary>
    internal void NoteReceived(ulong partner)
    {
        long now = _clock();
        lock (_gate)
        {
            if (!_received.TryGetValue(partner, out Queue<long>? stamps))
            {
                if (_received.Count >= MapCap) DropStalest(now);
                _received[partner] = stamps = new Queue<long>();
            }

            stamps.Enqueue(now);
            while (stamps.Count > PerPartnerCap) stamps.Dequeue();
        }
    }

    // The partner whose newest stamp is the oldest goes (an expired one first); the others keep their counts.
    private void DropStalest(long now)
    {
        ulong victim = 0;
        long victimAt = long.MaxValue;
        bool found = false;
        foreach ((ulong partner, Queue<long> stamps) in _received)
        {
            long newest = stamps.Count == 0 ? long.MinValue : stamps.Last();
            if (now - newest >= HourMs) newest = long.MinValue;
            if (newest >= victimAt && found) continue;
            victim = partner;
            victimAt = newest;
            found = true;
        }

        if (found) _received.Remove(victim);
    }

    // --- stack splitting -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The split that lets a seller bot make exactly <paramref name="count"/> from <paramref name="stackCounts"/> (the sizes of its tradeable stacks of the entry), or null
    /// when no split helps. The same greedy plan as <c>PlayerbotTradeDesk.PlanStacks</c> (largest stacks first, a stack larger than what remains is skipped) runs first; what
    /// it cannot fill (<c>r</c>, 1..255) is split off the smallest stack it skipped (every skipped stack is larger than <c>r</c>), provided the plan plus the new stack
    /// fits the six traded slots. Returns the index into <paramref name="stackCounts"/> and the amount to split; null when the count is already exact, cannot be made
    /// (fewer units than the count, or the plan has no slot left) or needs more than a byte.
    /// </summary>
    internal static (int Index, uint Amount)? ChooseSplit(IReadOnlyList<uint> stackCounts, uint count)
    {
        ArgumentNullException.ThrowIfNull(stackCounts);
        if (count == 0 || stackCounts.Count == 0) return null;
        int[] order = new int[stackCounts.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int byCount = stackCounts[b].CompareTo(stackCounts[a]);
            return byCount != 0 ? byCount : a.CompareTo(b);
        });
        long remaining = count;
        int planned = 0;
        bool[] used = new bool[order.Length];
        for (int k = 0; k < order.Length; k++)
        {
            uint size = stackCounts[order[k]];
            if (size == 0 || remaining == 0 || planned >= TradedSlots) break;
            if (size > remaining) continue;
            used[k] = true;
            planned++;
            remaining -= size;
        }

        if (remaining == 0 || remaining > byte.MaxValue || planned + 1 > TradedSlots) return null;
        for (int k = order.Length - 1; k >= 0; k--)
            if (!used[k] && stackCounts[order[k]] > remaining) return (order[k], (uint)remaining);
        return null;
    }

    /// <summary>
    /// The split the desk asks of <paramref name="bot"/> to make <paramref name="count"/> of <paramref name="entry"/> (<see cref="ChooseSplit"/>): the source stack, an
    /// explicit empty slot in the backpack or a general-purpose bag (an automatic destination could merge the new stack straight back into the source), and the amount.
    /// Null when no split helps or the bags have no empty slot. Only tradeable bag items are considered (not soulbound, equipped or banked).
    /// </summary>
    internal static PlayerbotStackSplit? PlanSplit(Player bot, uint entry, uint count)
    {
        ArgumentNullException.ThrowIfNull(bot);
        var stacks = new List<Item>();
        var counts = new List<uint>();
        foreach (Item item in bot.Inventory.AllItems)
        {
            if (item.Entry != entry || item.Count == 0 || item.IsBag || !InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)
                || InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot) || bot.Inventory.CanBeTraded(item) != InventoryResult.Ok) continue;
            stacks.Add(item);
            counts.Add(item.Count);
        }

        if (ChooseSplit(counts, count) is not { } choice || EmptySlot(bot) is not { } empty) return null;
        Item source = stacks[choice.Index];
        return new PlayerbotStackSplit(source.BagSlot, source.Slot, empty.Bag, empty.Slot, (byte)choice.Amount);
    }

    private static (byte Bag, byte Slot)? EmptySlot(Player bot)
    {
        PlayerInventory inventory = bot.Inventory;
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
            if (inventory.GetItem(InventorySlots.Bag0, slot) is null) return (InventorySlots.Bag0, slot);
        for (byte bagSlot = InventorySlots.BagStart; bagSlot < InventorySlots.BagEnd; bagSlot++)
        {
            if (inventory.GetItem(InventorySlots.Bag0, bagSlot) is not Container container || container.Template.BagFamily != 0) continue;
            for (byte s = 0; s < container.Size; s++)
                if (container[s] is null) return (bagSlot, s);
        }

        return null;
    }

    // --- recipe reserve --------------------------------------------------------------------------------------------------

    /// <summary>
    /// <paramref name="surplus"/> with the reagents the bot's wanted recipes need kept back: <paramref name="reserve"/> is the largest count one such craft uses per
    /// item (<c>PlayerbotVendorSupplyNeeds.Reagents</c>). An item held only up to its reserve is dropped; one held above it is offered with the count above the reserve.
    /// A new list; <paramref name="surplus"/> is untouched.
    /// </summary>
    internal static List<BotChatItem> WithoutReserved(IReadOnlyList<BotChatItem> surplus, IReadOnlyDictionary<uint, uint> reserve)
    {
        ArgumentNullException.ThrowIfNull(surplus);
        ArgumentNullException.ThrowIfNull(reserve);
        var spare = new List<BotChatItem>(surplus.Count);
        foreach (BotChatItem item in surplus)
        {
            if (!reserve.TryGetValue(item.Entry, out uint kept) || kept == 0)
            {
                spare.Add(item);
                continue;
            }

            long over = (long)item.Count - kept;
            if (over > 0) spare.Add(item with { Count = (int)over });
        }

        return spare;
    }

    /// <summary>
    /// What a bot offers for a player's WTS line: the first of <paramref name="shortfalls"/> (reagents a wanted recipe lacks, with how many) whose name
    /// <paramref name="line"/> names (<see cref="PlayerbotWorldChatter.Wanted"/>'s whole-word match). Null when none, so a bot never opens a deal for goods it does not need.
    /// </summary>
    internal static BotChatItem? NeededFromLine(string line, IReadOnlyList<BotChatItem> shortfalls)
        => PlayerbotWorldChatter.Wanted(line, shortfalls);
}

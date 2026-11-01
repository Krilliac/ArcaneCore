using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bots.Progression;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Net;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Party;

// The group gift flow (World:Playerbots:Social:Trading:Gifts, BotSocial.PlayerbotGroupGifts): the world work around the state machine. World thread.
internal sealed partial class PlayerbotPartyAI
{
    private BotSocial.PlayerbotGroupGifts? _groupGifts;

    /// <summary>The bot's group gift flow (<c>Social:Trading:Gifts</c>); a test may replace it.</summary>
    internal BotSocial.PlayerbotGroupGifts GroupGifts
    {
        get => _groupGifts ??= new BotSocial.PlayerbotGroupGifts(_options);
        set => _groupGifts = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Whether a gift flow is under way (the tick then runs for a bot the idle throttle would skip).</summary>
    private bool GroupGiftActive => _groupGifts is { Phase: not BotSocial.PlayerbotGiftPhase.Idle };

    /// <summary>
    /// One tick of the group gift flow. Idle: after a fight, offer the most valuable piece of equipment the bot has no use for (and a groupmate could) in group chat.
    /// Offered: wait <c>GiftClaimSeconds</c> for a claim, then hand it to a groupmate bot it is an upgrade for, if any. Claimed: open the trade with the claimer;
    /// the window's packets are answered by the trade status handlers. Every failure ends the flow with its cooldown.
    /// </summary>
    private void TickGroupGifts(Player player)
    {
        BotSocial.PlayerbotGroupGifts gifts = GroupGifts;
        long now = Now;
        Group? group = gifts.Enabled ? Services.Social?.Groups.GetGroup(player.Guid) : null;
        if (group is null || group.Members.Count < 2 || !player.IsAlive)
        {
            if (gifts.Phase != BotSocial.PlayerbotGiftPhase.Idle) AbandonGroupGift(player, now);
            gifts.Reset();
            return;
        }

        switch (gifts.Phase)
        {
            case BotSocial.PlayerbotGiftPhase.Idle:
                if (gifts.OfferDue(player.Combat.IsInCombat, now) && LiveTrade(player) is null && TradeDesk?.HasDeal(_botId) != true) OfferGroupGift(player, group, gifts, now);
                return;
            case BotSocial.PlayerbotGiftPhase.Offered:
                if (!gifts.ClaimWindowOver(now)) return;
                if (player.Inventory.GetItemByGuid(gifts.Item) is { } offered && BestBotHeir(player, group, offered) is { IsEmpty: false } heir) gifts.Award(heir, now);
                else gifts.End(now);
                return;
        }

        // Claimed.
        Player? recipient = _session.World.FindOnlinePlayer(gifts.Recipient);
        if (gifts.TradeTimedOut(now) || player.Combat.IsInCombat || recipient is null || !ReferenceEquals(recipient.Map, player.Map) || !group.Members.Any(slot => slot.Guid == recipient.Guid)
            || player.Inventory.GetItemByGuid(gifts.Item) is null)
        {
            AbandonGroupGift(player, now);
            return;
        }

        // A window is already open (the gift's own, or another driver's): the status handlers answer it; the time bound ends a window that never goes.
        if (LiveTrade(player) is not null || Distance(recipient, player) > TradeReach) return;
        BotSocial.PlayerbotTradeOwners? owners = Owners;
        if (owners?.TryClaim(player, BotSocial.PlayerbotTradeOwner.Gift, recipient.Guid, null) == false) return;
        if (Act(WorldOpcode.CmsgInitiateTrade, PlayerbotNavigation.GuidPayload(recipient.Guid.Value), budgeted: false))
        {
            _tradePartner = recipient.Guid;
            _tradeCounts = false; _tradeGift = false;
            owners?.Of(player, LiveTrade(player)); // bind the claim to the window this initiate just opened
        }
        else
        {
            owners?.Release(player, BotSocial.PlayerbotTradeOwner.Gift);
            gifts.End(now);
        }
    }

    // Say the "anyone need" line for the best giftable item, or end the flow (cooldown) when there is none: the scan runs once per fight, never per tick.
    private void OfferGroupGift(Player player, Group group, BotSocial.PlayerbotGroupGifts gifts, long now)
    {
        List<BotSocial.PlayerbotGiftCandidate> candidates = [];
        foreach (Item item in player.Inventory.AllItems)
        {
            var template = item.Template;
            if (item.Count == 0 || item.IsBag || !InventorySlots.IsInventoryPos(item.BagSlot, item.Slot) || InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot)
                || (ItemClass)template.Class is not (ItemClass.Weapon or ItemClass.Armor) || template.Quality < 2 || template.SellPrice == 0
                || player.Inventory.CanBeTraded(item) != InventoryResult.Ok) continue;
            candidates.Add(new BotSocial.PlayerbotGiftCandidate(item.Guid, item.Entry, item.BagSlot, item.Slot, (long)template.SellPrice * item.Count));
        }

        BotSocial.PlayerbotGiftCandidate? pick = gifts.Choose(candidates, player.Level, candidate =>
            player.Inventory.GetItemByGuid(candidate.Item) is { } item
            // An upgrade for the bot itself is worn, not given away; a gift is for an item some groupmate would wear.
            && !PlayerbotItemScore.BestUpgradeSlot(player, item.Template, out _, out _)
            && OnlineMembers(player, group).Any(member => PlayerbotItemScore.BestUpgradeSlot(member, item.Template, out _, out _)));
        if (pick is not { } chosen || player.Inventory.GetItemByGuid(chosen.Item) is not { } chosenItem)
        {
            gifts.End(now);
            return;
        }

        string name = chosenItem.Count > 1 ? $"[{chosenItem.Template.Name}] x{chosenItem.Count}" : $"[{chosenItem.Template.Name}]";
        Language language = player.Team == Team.Horde ? Language.Orcish : Language.Common;
        if (Act(WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(PlayerbotChatCommands.GroupChannel(group.IsRaid), language, $"anyone need {name}?"), budgeted: false))
            gifts.Begin(chosen.Item, now);
        else gifts.End(now);
    }

    private IEnumerable<Player> OnlineMembers(Player player, Group group)
    {
        foreach (GroupMemberSlot slot in group.Members)
            if (slot.Guid != player.Guid && _session.World.FindOnlinePlayer(slot.Guid) is { IsAlive: true } member && ReferenceEquals(member.Map, player.Map) && member.Map is not null)
                yield return member;
    }

    // The groupmate bot with the largest upgrade from the item (empty when none gains): the offer went unclaimed, so a bot that would wear it gets it.
    private ObjectGuid BestBotHeir(Player player, Group group, Item item)
    {
        ObjectGuid best = ObjectGuid.Empty;
        float bestGain = 0f;
        foreach (Player member in OnlineMembers(player, group))
            if (member.Session is WorldSession { IsManaged: true } && Distance(member, player) <= TradeReach
                && PlayerbotItemScore.BestUpgradeSlot(member, item.Template, out _, out float gain) && gain > bestGain)
                (best, bestGain) = (member.Guid, gain);
        return best;
    }

    /// <summary>A claim for the offered item: a short "me" or "need" in group chat from a groupmate while the bot listens. True when the line was the claim (the chat service then never hears it).</summary>
    private bool TryClaimGroupGift(Player player, PlayerbotChatLine line, Player? sender)
    {
        BotSocial.PlayerbotGroupGifts gifts = GroupGifts;
        if (gifts.Phase != BotSocial.PlayerbotGiftPhase.Offered || sender is null || sender.Guid == player.Guid
            || line.Type is not (ChatType.Party or ChatType.Raid or ChatType.RaidLeader) || !BotSocial.PlayerbotGroupGifts.IsClaim(line.Text)
            || Services.Social?.Groups.AreInSameGroup(player.Guid, sender.Guid) != true) return false;
        return gifts.Claim(sender.Guid, Now);
    }

    // The window of the gift this bot is handing out: the claimed recipient, opened, the bot's own claim of the slot.
    private bool GiftOutWindow(Player player, out TradeSession trade)
    {
        trade = null!;
        if (_groupGifts is not { Phase: BotSocial.PlayerbotGiftPhase.Claimed } gifts || _tradePartner.IsEmpty || _tradePartner != gifts.Recipient
            || LiveTrade(player) is not { Opened: true, Settling: false } live || Owners?.Is(player, BotSocial.PlayerbotTradeOwner.Gift, _tradePartner, live) == false) return false;
        trade = live;
        return true;
    }

    /// <summary>The window opened or changed: put the offered item in the first free trade slot (nothing else is ever put in). False when it is not the gift's window.</summary>
    private bool AdvanceGiftOut(Player player)
    {
        if (!GiftOutWindow(player, out TradeSession trade)) return false;
        BotSocial.PlayerbotGroupGifts gifts = GroupGifts;
        TradeSide mine = trade.SideOf(player);
        int free = -1;
        for (int slot = 0; slot < TradeRules.TradedSlotCount; slot++)
        {
            if (mine[slot] == gifts.Item) return true; // already in
            if (free < 0 && mine[slot].IsEmpty) free = slot;
        }

        if (free < 0 || player.Inventory.GetItemByGuid(gifts.Item) is not { } item || player.Inventory.CanBeTraded(item) != InventoryResult.Ok) AbandonGroupGift(player, Now);
        else Act(WorldOpcode.CmsgSetTradeItem, [(byte)free, item.BagSlot, item.Slot], budgeted: false);
        return true;
    }

    /// <summary>The partner accepted: accept too when the bot's side is exactly the offered item. False when it is not the gift's window.</summary>
    private bool AnswerGiftOut(Player player)
    {
        if (!GiftOutWindow(player, out TradeSession trade)) return false;
        TradeSide mine = trade.SideOf(player);
        if (!trade.OtherSide(player).Accepted || mine.Gold != 0) return true;
        int items = 0;
        bool onlyOffered = true;
        foreach (ObjectGuid guid in mine.TradedItems)
        {
            items++;
            onlyOffered &= guid == GroupGifts.Item;
        }

        if (items != 1 || !onlyOffered) return true;
        _tradeCounts = true; // the bot gave something: the window counts as a trade for its memory
        Act(WorldOpcode.CmsgAcceptTrade, BitConverter.GetBytes(1u), budgeted: false);
        return true;
    }

    /// <summary>The window closed: when it was the gift's, the flow ends (called before the partner is forgotten). The item went, or the window failed; either way the cooldown runs.</summary>
    private void FinishGroupGift()
    {
        if (_groupGifts is { Phase: BotSocial.PlayerbotGiftPhase.Claimed } gifts && !_tradePartner.IsEmpty && _tradePartner == gifts.Recipient) gifts.End(Now);
    }

    // Give the flow up: cancel the gift's own window when it is open, free the slot claim and start the cooldown.
    private void AbandonGroupGift(Player player, long now)
    {
        BotSocial.PlayerbotGroupGifts gifts = GroupGifts;
        if (gifts.Phase == BotSocial.PlayerbotGiftPhase.Claimed && !_tradePartner.IsEmpty && _tradePartner == gifts.Recipient)
        {
            if (LiveTrade(player) is not null) Act(WorldOpcode.CmsgCancelTrade, [], budgeted: false);
            Owners?.Release(player, BotSocial.PlayerbotTradeOwner.Gift);
            _tradePartner = ObjectGuid.Empty;
            _tradeCounts = false; _tradeGift = false;
        }

        gifts.End(now);
    }
}

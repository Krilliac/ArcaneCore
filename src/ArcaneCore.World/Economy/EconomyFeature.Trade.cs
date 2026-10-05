using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Economy;

public sealed partial class EconomyFeature
{
    private readonly Dictionary<Player, TradeSession> _trades = new(ReferenceEqualityComparer.Instance);

    /// <summary>The open trade of a player (tests and GM tools).</summary>
    public TradeSession? TradeOf(Player player) => _trades.GetValueOrDefault(player);

    /// <summary>CMSG_INITIATE_TRADE (vmangos HandleInitiateTradeOpcode).</summary>
    public void InitiateTrade(WorldSession session, Player player, ObjectGuid targetGuid)
    {
        if (_trades.ContainsKey(player))
        {
            return;
        }

        void Status(TradeStatus status) => session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(status));

        if (!player.IsAlive)
        {
            Status(TradeStatus.YouDead);
            return;
        }

        if (player.IsLoggingOut)
        {
            Status(TradeStatus.YouLogout);
            return;
        }

        if (!Enabled || player.Map is null || !Settlements.CanAct(session, player))
        {
            Status(TradeStatus.TargetTooFar);
            return;
        }

        Player? other = _world?.FindOnlinePlayer(targetGuid);
        if (other is null || !ReferenceEquals(other.Map, player.Map))
        {
            Status(TradeStatus.NoTarget);
            return;
        }

        if (ReferenceEquals(other, player) || _trades.ContainsKey(other))
        {
            Status(TradeStatus.Busy);
            return;
        }

        if (!other.IsAlive)
        {
            Status(TradeStatus.TargetDead);
            return;
        }

        if (other.IsLoggingOut)
        {
            Status(TradeStatus.TargetLogout);
            return;
        }

        if (!Options.AllowCrossTeamTrade && other.Team != player.Team)
        {
            Status(TradeStatus.WrongFaction);
            return;
        }

        if (!TradeSession.InRange(player, other) || other.IsQuestSettlementPending)
        {
            Status(TradeStatus.TargetTooFar);
            return;
        }

        var trade = new TradeSession(player, other);
        _trades[player] = trade;
        _trades[other] = trade;
        other.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.BeginTrade, player.Guid.Value));
    }

    /// <summary>CMSG_BEGIN_TRADE: the invited player opens the window for both.</summary>
    public void BeginTrade(Player player)
    {
        if (_trades.GetValueOrDefault(player) is not { Opened: false } trade || !ReferenceEquals(trade.Target.Player, player))
        {
            return;
        }

        trade.Opened = true;
        SendBoth(trade, TradeStatus.OpenWindow);
    }

    /// <summary>CMSG_BUSY_TRADE / CMSG_IGNORE_TRADE: the invitation is declined.</summary>
    public void DeclineTrade(Player player, TradeStatus status)
    {
        if (_trades.GetValueOrDefault(player) is { Opened: false } trade && ReferenceEquals(trade.Target.Player, player))
        {
            CloseTrade(trade);
            trade.Initiator.Player.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(status));
        }
    }

    /// <summary>CMSG_SET_TRADE_ITEM: offer the item at (bag, slot) in a trade slot.</summary>
    public void SetTradeItem(Player player, byte tradeSlot, byte bag, byte slot)
    {
        if (OpenTradeOf(player) is not { } trade)
        {
            return;
        }

        if (tradeSlot >= TradeRules.SlotCount)
        {
            CancelTrade(player, TradeStatus.TradeCanceled);
            return;
        }

        Item? item = player.Inventory.GetItem(bag, slot);
        TradeSide mine = trade.SideOf(player);
        if (item is null || mine.SlotOf(item.Guid) >= 0
            || (tradeSlot != TradeRules.NonTradedSlot && player.Inventory.CanBeTraded(item) != InventoryResult.Ok))
        {
            CancelTrade(player, TradeStatus.TradeCanceled);
            return;
        }

        bool changed = mine[tradeSlot] != item.Guid;
        mine[tradeSlot] = item.Guid;
        TradeNegotiated(trade, mine, changed);
        InvalidatePendingItemChange(trade, mine, tradeSlot, changed);
    }

    /// <summary>CMSG_CLEAR_TRADE_ITEM.</summary>
    public void ClearTradeItem(Player player, byte tradeSlot)
    {
        if (OpenTradeOf(player) is not { } trade || tradeSlot >= TradeRules.SlotCount)
        {
            return;
        }

        TradeSide mine = trade.SideOf(player);
        bool changed = !mine[tradeSlot].IsEmpty;
        mine[tradeSlot] = ObjectGuid.Empty;
        TradeNegotiated(trade, mine, changed);
        InvalidatePendingItemChange(trade, mine, tradeSlot, changed);
    }

    /// <summary>CMSG_SET_TRADE_GOLD: more than the player owns is ignored.</summary>
    public void SetTradeGold(Player player, uint gold)
    {
        if (OpenTradeOf(player) is not { } trade || gold > player.Money)
        {
            return;
        }

        TradeSide mine = trade.SideOf(player);
        bool changed = mine.Gold != gold;
        mine.Gold = gold;
        TradeNegotiated(trade, mine, changed);
    }

    /// <summary>CMSG_UNACCEPT_TRADE.</summary>
    public void UnacceptTrade(Player player)
    {
        if (OpenTradeOf(player) is { } trade)
        {
            trade.SideOf(player).Accepted = false;
            trade.OtherSide(player).Player.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.BackToTrade));
        }
    }

    /// <summary>CMSG_CANCEL_TRADE (also sent by the client after logout).</summary>
    public void CancelTradeRequest(Player player) => CancelTrade(player, TradeStatus.TradeCanceled);

    /// <summary>
    /// CMSG_ACCEPT_TRADE (vmangos HandleAcceptTradeOpcode). When both have accepted, the exchange
    /// is validated again and settled as one two-character transaction; the window closes with
    /// TRADE_COMPLETE only after the commit, or the trade is cancelled with nothing moved.
    /// </summary>
    public void AcceptTrade(WorldSession session, Player player)
    {
        if (OpenTradeOf(player) is not { } trade)
        {
            return;
        }

        TradeSide mine = trade.SideOf(player);
        TradeSide theirs = trade.OtherSide(player);
        Player other = theirs.Player;

        // vmangos TradeHandler.cpp:251-257: an accept right after a modification is bounced with BACK_TO_TRADE.
        long nowMs = NowMs;
        if (TradeRules.ScamPrevented(mine.LastModifiedMs, nowMs, Options.TradeScamPreventionMs, Options.TradeScamPreventionWholeSeconds))
        {
            session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.BackToTrade));
            return;
        }

        mine.LastModifiedMs = nowMs;
        mine.Accepted = true;
        if (!TradeSession.InRange(player, other))
        {
            mine.Accepted = false;
            session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.TargetTooFar));
            return;
        }

        // vmangos TradeHandler.cpp:274-290: the short player is told "not enough gold" and the PARTNER gets BACK_TO_TRADE
        // (SetAccepted(false, crosssend)).
        if (mine.Gold > player.Money)
        {
            mine.Accepted = false;
            RefuseForGold(trade, player, other);
            return;
        }

        if (theirs.Gold > other.Money)
        {
            theirs.Accepted = false;
            RefuseForGold(trade, other, player);
            return;
        }

        if ((long)player.Money - mine.Gold + theirs.Gold > EconomyOptions.MaxMoney
            || (long)other.Money - theirs.Gold + mine.Gold > EconomyOptions.MaxMoney
            || !OffersStillValid(mine) || !OffersStillValid(theirs))
        {
            CancelTrade(player, TradeStatus.TradeCanceled);
            return;
        }

        if (!theirs.Accepted)
        {
            other.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.TradeAccept));
            return;
        }

        other.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.TradeAccept));
        CompleteTrade(trade, player);
    }

    /// <summary>The player short of gold is notified (801); the partner receives BACK_TO_TRADE. With the option off both get BACK_TO_TRADE.</summary>
    private void RefuseForGold(TradeSession trade, Player shortPlayer, Player partner)
    {
        if (!Options.TradeSpaceNotifications)
        {
            SendBackToTrade(trade);
            return;
        }

        Notify(shortPlayer, "You do not have enough gold");
        partner.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.BackToTrade));
    }

    /// <summary>SMSG_NOTIFICATION (vmangos WorldSession::SendNotification); the texts are mangos_string 801-803 of classic-db.</summary>
    private static void Notify(Player player, string text)
        => player.Session.Send(WorldOpcode.SmsgNotification, ArcaneCore.World.Packets.ChatPackets.BuildNotification(text));

    private void CompleteTrade(TradeSession trade, Player accepter)
    {
        TradeSide a = trade.Initiator;
        TradeSide b = trade.Target;
        if (!TryPlanPendingEnchantment(trade, a, out TradeEnchantmentPlan? aEnchant)
            || !TryPlanPendingEnchantment(trade, b, out TradeEnchantmentPlan? bEnchant))
        {
            trade.ClearAccepted();
            SendBackToTrade(trade);
            return;
        }
        List<ItemInstanceData> aGives = [.. a.TradedItems.Select(g => a.Player.Inventory.GetItemByGuid(g)!.ToData())];
        List<ItemInstanceData> bGives = [.. b.TradedItems.Select(g => b.Player.Inventory.GetItemByGuid(g)!.ToData())];
        InventoryResult aResult = a.Player.Inventory.TryStageEconomyTransfer([.. a.TradedItems], bGives, out EconomyInventoryStage? aStage,
            trade: true, replacements: bEnchant is null ? [] : [bEnchant.UpdatedRecipientItem], consume: aEnchant?.Reagents);
        InventoryResult bResult = b.Player.Inventory.TryStageEconomyTransfer([.. b.TradedItems], aGives, out EconomyInventoryStage? bStage,
            trade: true, replacements: aEnchant is null ? [] : [aEnchant.UpdatedRecipientItem], consume: bEnchant?.Reagents);
        if ((aResult != InventoryResult.Ok || bResult != InventoryResult.Ok) && Options.TradeSpaceNotifications)
        {
            // vmangos TradeHandler.cpp:420-455: the trade stays open; the player who cannot receive is told (802) and the
            // partner is told it is the partner's bags (803); both acceptances clear, each with BACK_TO_TRADE.
            bool accepterShort = (ReferenceEquals(accepter, a.Player) ? aResult : bResult) != InventoryResult.Ok;
            Player partner = trade.OtherSide(accepter).Player;
            Notify(accepter, accepterShort ? "You do not have enough free slots" : "Your partner does not have enough free bag slots");
            Notify(partner, accepterShort ? "Your partner does not have enough free bag slots" : "You do not have enough free slots");
            trade.ClearAccepted();
            trade.OtherSide(accepter).LastModifiedMs = NowMs;
            SendBackToTrade(trade);
            return;
        }

        if (aResult != InventoryResult.Ok || bResult != InventoryResult.Ok)
        {
            // Not enough room on one side (vmangos LANG_NOT_FREE_TRADE_SLOTS): nothing moves; the window
            // closes with the inventory error, flagged as the partner's problem on the other client.
            trade.ClearAccepted();
            a.Player.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.CloseWindow,
                inventoryResult: aResult != InventoryResult.Ok ? aResult : bResult, targetError: aResult == InventoryResult.Ok));
            b.Player.Session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.CloseWindow,
                inventoryResult: bResult != InventoryResult.Ok ? bResult : aResult, targetError: bResult == InventoryResult.Ok));
            CloseTrade(trade);
            return;
        }

        WorldSession aSession = (WorldSession)a.Player.Session;
        WorldSession bSession = (WorldSession)b.Player.Session;
        EconomyActor? aActor = Settlements.CreateActor(aSession, a.Player, aStage!, a.Player.Money - a.Gold + b.Gold, aEnchant?.CasterLifeAfter);
        EconomyActor? bActor = Settlements.CreateActor(bSession, b.Player, bStage!, b.Player.Money - b.Gold + a.Gold, bEnchant?.CasterLifeAfter);
        Guid publicationId = Guid.NewGuid();
        trade.Settling = true;
        if (aActor is null || bActor is null || !Start([aActor, bActor], [], outcome =>
            {
                CloseTrade(trade);
                if (outcome == EconomyOutcome.After)
                {
                    Publish(a, b, aEnchant);
                    Publish(b, a, bEnchant);
                    SendBoth(trade, TradeStatus.TradeComplete);
                }
                else if (outcome != EconomyOutcome.Unknown)
                {
                    SendBoth(trade, TradeStatus.TradeCanceled);
                }
            }))
        {
            trade.Settling = false;
            CancelTrade(a.Player, TradeStatus.TradeCanceled);
        }

        void Publish(TradeSide caster, TradeSide recipient, TradeEnchantmentPlan? plan)
        {
            if (plan is null || recipient.Player.Inventory.GetItemByGuid(ObjectGuid.Item(plan.UpdatedRecipientItem.Guid)) is not { } item)
                return;
            _services.GetRequiredService<SpellFeature>().System.PublishCommittedTradeEnchantment(publicationId, caster.Player, item, plan);
        }
    }

    /// <summary>Cancel the player's trade (both sides receive <paramref name="status"/>); a settling trade cannot be cancelled.</summary>
    private void CancelTrade(Player player, TradeStatus status)
    {
        if (_trades.GetValueOrDefault(player) is not { Settling: false } trade)
        {
            return;
        }

        CloseTrade(trade);
        SendBoth(trade, status);
    }

    private void CloseTrade(TradeSession trade)
    {
        trade.ClearPendingEnchantments();
        if (ReferenceEquals(_trades.GetValueOrDefault(trade.Initiator.Player), trade))
        {
            _trades.Remove(trade.Initiator.Player);
        }

        if (ReferenceEquals(_trades.GetValueOrDefault(trade.Target.Player), trade))
        {
            _trades.Remove(trade.Target.Player);
        }
    }

    private TradeSession? OpenTradeOf(Player player)
        => _trades.GetValueOrDefault(player) is { Opened: true, Settling: false } trade ? trade : null;

    /// <summary>Every offered item is still carried, unmoved in identity and tradable (slot 6 only needs to exist).</summary>
    private static bool OffersStillValid(TradeSide side)
    {
        for (int slot = 0; slot < TradeRules.SlotCount; slot++)
        {
            ObjectGuid guid = side[slot];
            if (guid.IsEmpty)
            {
                continue;
            }

            if (side.Player.Inventory.GetItemByGuid(guid) is not { } item
                || (slot != TradeRules.NonTradedSlot && side.Player.Inventory.CanBeTraded(item) != InventoryResult.Ok))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// vmangos HandleSetTradeGold/Item/ClearTradeItem (TradeHandler.cpp:675-746) with TradeData::SetItem/SetMoney
    /// (TradeData.cpp:57-120). Every call clears the PARTNER's acceptance (BACK_TO_TRADE to the partner) and stamps both
    /// modification times; only a real change then clears the owner's acceptance too (another BACK_TO_TRADE each) and
    /// refreshes the trader's view of the changed side. Setting the value that is already set changes nothing more.
    /// </summary>
    private void TradeNegotiated(TradeSession trade, TradeSide changedSide, bool changed)
    {
        TradeSide partnerSide = trade.OtherSide(changedSide.Player);
        Player owner = changedSide.Player;
        Player partner = partnerSide.Player;
        byte[] backToTrade = EconomyPackets.TradeStatus(TradeStatus.BackToTrade);
        partnerSide.Accepted = false;
        partner.Session.Send(WorldOpcode.SmsgTradeStatus, backToTrade);
        partnerSide.LastModifiedMs = changedSide.LastModifiedMs = NowMs;
        if (!changed)
        {
            return;
        }

        changedSide.Accepted = false;
        owner.Session.Send(WorldOpcode.SmsgTradeStatus, backToTrade);
        partner.Session.Send(WorldOpcode.SmsgTradeStatus, backToTrade);
        List<ItemInstanceData?> slots = [.. changedSide.Items.Select(g => g.IsEmpty ? null : owner.Inventory.GetItemByGuid(g)?.ToData())];
        partner.Session.Send(WorldOpcode.SmsgTradeStatusExtended, EconomyPackets.TradeStatusExtended(true, changedSide.Gold, slots, Templates.Find,
            changedSide.PendingEnchantment?.SpellId ?? 0));
    }

    private SpellCastResult DeferTradeEnchantment(Player player, uint spellId, SpellCastTargets targets)
    {
        if (!targets.IsRawNonTradedTradeTarget || OpenTradeOf(player) is not { } trade
            || !TradeSession.InRange(player, trade.OtherSide(player).Player))
            return SpellCastResult.ItemNotReady;
        TradeSide mine = trade.SideOf(player);
        TradeSide partner = trade.OtherSide(player);
        Item? target = partner.Player.Inventory.GetItemByGuid(partner[TradeRules.NonTradedSlot]);
        if (target is null) return SpellCastResult.ItemNotReady;
        SpellSystem spells = _services.GetRequiredService<SpellFeature>().System;
        SpellCastResult result = spells.TryPlanTradeEnchantment(player, partner.Player, target, spellId, ObjectGuid.Empty, out _);
        if (result != SpellCastResult.CastOk) return result;
        var pending = new PendingTradeEnchantment(spellId, ObjectGuid.Empty);
        if (mine.PendingEnchantment != pending)
        {
            mine.PendingEnchantment = pending;
            trade.ClearAccepted();
            SendBackToTrade(trade);
            SendTradeView(mine, partner.Player, traderWindow: true);
            SendTradeView(mine, player, traderWindow: false);
        }
        return SpellCastResult.DontReport;
    }

    private bool TryPlanPendingEnchantment(TradeSession trade, TradeSide casterSide, out TradeEnchantmentPlan? plan)
    {
        plan = null;
        if (casterSide.PendingEnchantment is not { } pending) return true;
        TradeSide recipientSide = trade.OtherSide(casterSide.Player);
        Item? item = recipientSide.Player.Inventory.GetItemByGuid(recipientSide[TradeRules.NonTradedSlot]);
        SpellSystem spells = _services.GetRequiredService<SpellFeature>().System;
        SpellCastResult result = item is null ? SpellCastResult.ItemNotReady : spells.TryPlanTradeEnchantment(
            casterSide.Player, recipientSide.Player, item, pending.SpellId, pending.CastItemGuid, out plan, acceptance: true);
        if (result == SpellCastResult.CastOk) return true;
        casterSide.Player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(pending.SpellId, result));
        return false;
    }

    private void SendTradeView(TradeSide side, Player viewer, bool traderWindow)
    {
        List<ItemInstanceData?> slots = [.. side.Items.Select(g => g.IsEmpty ? null : side.Player.Inventory.GetItemByGuid(g)?.ToData())];
        viewer.Session.Send(WorldOpcode.SmsgTradeStatusExtended, EconomyPackets.TradeStatusExtended(traderWindow,
            side.Gold, slots, Templates.Find, side.PendingEnchantment?.SpellId ?? 0));
    }

    private void InvalidatePendingItemChange(TradeSession trade, TradeSide changedSide, byte slot, bool changed)
    {
        if (!changed) return;
        ClearPending(changedSide);
        if (slot == TradeRules.NonTradedSlot) ClearPending(trade.OtherSide(changedSide.Player));

        void ClearPending(TradeSide side)
        {
            if (side.PendingEnchantment is null) return;
            side.PendingEnchantment = null;
            trade.ClearAccepted();
            SendBackToTrade(trade);
            SendTradeView(side, trade.OtherSide(side.Player).Player, traderWindow: true);
            SendTradeView(side, side.Player, traderWindow: false);
        }
    }

    private static void SendBackToTrade(TradeSession trade) => SendBoth(trade, TradeStatus.BackToTrade);

    private static void SendBoth(TradeSession trade, TradeStatus status)
    {
        byte[] packet = EconomyPackets.TradeStatus(status);
        trade.Initiator.Player.Session.Send(WorldOpcode.SmsgTradeStatus, packet);
        trade.Target.Player.Session.Send(WorldOpcode.SmsgTradeStatus, packet);
    }

    /// <summary>Map tick: trades whose partners died, separated or left the map are cancelled (vmangos Player::Update).</summary>
    internal void CheckTrades(Map map)
    {
        if (_trades.Count == 0)
        {
            return;
        }

        foreach (TradeSession trade in _trades.Values.Distinct().Where(t => !t.Settling && ReferenceEquals(t.Initiator.Player.Map, map)).ToList())
        {
            Player a = trade.Initiator.Player;
            Player b = trade.Target.Player;
            if (!a.IsAlive || !b.IsAlive || !ReferenceEquals(a.Map, b.Map) || !TradeSession.InRange(a, b))
            {
                CancelTrade(a, TradeStatus.TradeCanceled);
            }
        }
    }

    internal void OnPlayerLeftMap(Player player)
    {
        if (_trades.GetValueOrDefault(player) is { Settling: false })
        {
            CancelTrade(player, TradeStatus.TradeCanceled);
        }
    }
}

/// <summary>Per-map trade upkeep (distance, death, map change).</summary>
public sealed class TradeMapUpdater(EconomyFeature feature) : IMapUpdater
{
    public void Update(Map map, uint diffMs) => feature.CheckTrades(map);

    public void OnPlayerRemoved(Map map, Player player) => feature.OnPlayerLeftMap(player);
}

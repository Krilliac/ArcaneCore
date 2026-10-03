using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
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

        Item? item = player.Inventory.GetItem(bag, slot);
        TradeSide mine = trade.SideOf(player);
        if (tradeSlot >= TradeRules.SlotCount || item is null || mine.SlotOf(item.Guid) >= 0
            || (tradeSlot != TradeRules.NonTradedSlot && player.Inventory.CanBeTraded(item) != InventoryResult.Ok))
        {
            CancelTrade(player, TradeStatus.TradeCanceled);
            return;
        }

        mine[tradeSlot] = item.Guid;
        TradeChanged(trade, mine);
    }

    /// <summary>CMSG_CLEAR_TRADE_ITEM.</summary>
    public void ClearTradeItem(Player player, byte tradeSlot)
    {
        if (OpenTradeOf(player) is not { } trade || tradeSlot >= TradeRules.SlotCount)
        {
            return;
        }

        TradeSide mine = trade.SideOf(player);
        mine[tradeSlot] = ObjectGuid.Empty;
        TradeChanged(trade, mine);
    }

    /// <summary>CMSG_SET_TRADE_GOLD: more than the player owns is ignored.</summary>
    public void SetTradeGold(Player player, uint gold)
    {
        if (OpenTradeOf(player) is not { } trade || gold > player.Money)
        {
            return;
        }

        TradeSide mine = trade.SideOf(player);
        mine.Gold = gold;
        TradeChanged(trade, mine);
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
        mine.Accepted = true;
        Player other = theirs.Player;
        if (!TradeSession.InRange(player, other))
        {
            mine.Accepted = false;
            session.Send(WorldOpcode.SmsgTradeStatus, EconomyPackets.TradeStatus(TradeStatus.TargetTooFar));
            return;
        }

        if (mine.Gold > player.Money)
        {
            mine.Accepted = false;
            SendBackToTrade(trade);
            return;
        }

        if (theirs.Gold > other.Money)
        {
            theirs.Accepted = false;
            SendBackToTrade(trade);
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
        CompleteTrade(trade);
    }

    private void CompleteTrade(TradeSession trade)
    {
        TradeSide a = trade.Initiator;
        TradeSide b = trade.Target;
        List<ItemInstanceData> aGives = [.. a.TradedItems.Select(g => a.Player.Inventory.GetItemByGuid(g)!.ToData())];
        List<ItemInstanceData> bGives = [.. b.TradedItems.Select(g => b.Player.Inventory.GetItemByGuid(g)!.ToData())];
        InventoryResult aResult = a.Player.Inventory.TryStageEconomyTransfer([.. a.TradedItems], bGives, out EconomyInventoryStage? aStage, trade: true);
        InventoryResult bResult = b.Player.Inventory.TryStageEconomyTransfer([.. b.TradedItems], aGives, out EconomyInventoryStage? bStage, trade: true);
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
        EconomyActor? aActor = Settlements.CreateActor(aSession, a.Player, aStage!, a.Player.Money - a.Gold + b.Gold);
        EconomyActor? bActor = Settlements.CreateActor(bSession, b.Player, bStage!, b.Player.Money - b.Gold + a.Gold);
        trade.Settling = true;
        if (aActor is null || bActor is null || !Start([aActor, bActor], [], outcome =>
            {
                CloseTrade(trade);
                if (outcome == EconomyOutcome.After)
                {
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

    /// <summary>An offer changed: both acceptances clear and both windows show the changed side.</summary>
    private void TradeChanged(TradeSession trade, TradeSide changed)
    {
        bool wasAccepted = trade.Initiator.Accepted || trade.Target.Accepted;
        trade.ClearAccepted();
        if (wasAccepted)
        {
            SendBackToTrade(trade);
        }

        Player owner = changed.Player;
        Player other = trade.OtherSide(owner).Player;
        List<ItemInstanceData?> slots = [.. changed.Items.Select(g => g.IsEmpty ? null : owner.Inventory.GetItemByGuid(g)?.ToData())];
        owner.Session.Send(WorldOpcode.SmsgTradeStatusExtended, EconomyPackets.TradeStatusExtended(false, changed.Gold, slots, Templates.Find));
        other.Session.Send(WorldOpcode.SmsgTradeStatusExtended, EconomyPackets.TradeStatusExtended(true, changed.Gold, slots, Templates.Find));
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

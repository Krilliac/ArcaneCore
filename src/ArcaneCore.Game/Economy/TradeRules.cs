using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Economy;

/// <summary>SMSG_TRADE_STATUS values (vmangos TradeStatus; wow_messages 1.12).</summary>
public enum TradeStatus : uint
{
    Busy = 0,
    BeginTrade = 1,
    OpenWindow = 2,
    TradeCanceled = 3,
    TradeAccept = 4,
    Busy2 = 5,
    NoTarget = 6,
    BackToTrade = 7,
    TradeComplete = 8,
    TradeRejected = 9,
    TargetTooFar = 10,
    WrongFaction = 11,
    CloseWindow = 12,
    IgnoreYou = 14,
    YouStunned = 15,
    TargetStunned = 16,
    YouDead = 17,
    TargetDead = 18,
    YouLogout = 19,
    TargetLogout = 20,
    TrialAccount = 21,
    OnlyConjured = 22,
}

/// <summary>Trade constants (vmangos TradeData / TradeHandler).</summary>
public static class TradeRules
{
    /// <summary>TRADE_SLOT_COUNT: six traded slots and the "will not be traded" slot.</summary>
    public const int SlotCount = 7;

    /// <summary>TRADE_SLOT_TRADED_COUNT.</summary>
    public const int TradedSlotCount = 6;

    /// <summary>TRADE_SLOT_NONTRADED: shown to the partner (enchanting target), never moved.</summary>
    public const int NonTradedSlot = 6;

    /// <summary>TRADE_DISTANCE in yards (vmangos: 11.11).</summary>
    public const float MaxDistance = 11.11f;
}

/// <summary>One side of an open trade: its offered item GUIDs per slot, gold and acceptance.</summary>
public sealed class TradeSide(Player player)
{
    private readonly ObjectGuid[] _items = new ObjectGuid[TradeRules.SlotCount];

    public Player Player { get; } = player;

    public uint Gold { get; set; }

    public bool Accepted { get; set; }

    public IReadOnlyList<ObjectGuid> Items => _items;

    public ObjectGuid this[int slot]
    {
        get => _items[slot];
        set => _items[slot] = value;
    }

    /// <summary>The slot already holding <paramref name="item"/>, if any.</summary>
    public int SlotOf(ObjectGuid item) => Array.IndexOf(_items, item);

    /// <summary>The items that change hands (the six traded slots).</summary>
    public IEnumerable<ObjectGuid> TradedItems => _items.Take(TradeRules.TradedSlotCount).Where(g => !g.IsEmpty);
}

/// <summary>
/// An open trade between two players (vmangos TradeData pair). Created by CMSG_INITIATE_TRADE;
/// the window opens on CMSG_BEGIN_TRADE. Any change of items or gold clears both acceptances.
/// World thread only.
/// </summary>
public sealed class TradeSession(Player initiator, Player target)
{
    public TradeSide Initiator { get; } = new(initiator);

    public TradeSide Target { get; } = new(target);

    /// <summary>Whether the target answered with CMSG_BEGIN_TRADE.</summary>
    public bool Opened { get; set; }

    /// <summary>Set while the two-character settlement runs; the trade accepts no changes.</summary>
    public bool Settling { get; set; }

    public TradeSide SideOf(Player player) => ReferenceEquals(player, Initiator.Player) ? Initiator : Target;

    public TradeSide OtherSide(Player player) => ReferenceEquals(player, Initiator.Player) ? Target : Initiator;

    public bool Involves(Player player) => ReferenceEquals(player, Initiator.Player) || ReferenceEquals(player, Target.Player);

    public void ClearAccepted()
    {
        Initiator.Accepted = false;
        Target.Accepted = false;
    }

    /// <summary>Whether two players stand close enough to trade (3D, as vmangos GetDistance3dToCenter).</summary>
    public static bool InRange(Player a, Player b)
    {
        if (a.MapId != b.MapId)
        {
            return false;
        }

        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= TradeRules.MaxDistance * TradeRules.MaxDistance;
    }
}

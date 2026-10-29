using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Items;

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

    /// <summary>TRADE_DISTANCE in yards (vmangos: 11.11). Initiate and accept use this.</summary>
    public const float MaxDistance = 11.11f;

    /// <summary>
    /// INTERACTION_DISTANCE (vmangos ObjectDefines.h). <c>Player::SetPosition</c> cancels an open trade at this range
    /// (Player.cpp:5977-5979, <c>IsWithinDistInMap</c>), not at <see cref="MaxDistance"/>.
    /// </summary>
    public const float InteractionDistance = 5.0f;

    /// <summary>
    /// Whether a position change ends the trade (vmangos Player::SetPosition, Player.cpp:5977-5979).
    /// <c>IsWithinDistInMap(trader, INTERACTION_DISTANCE)</c> adds both bounding radii and uses a strict
    /// less-than, so a distance equal to that reach already cancels. Standing still does not: a trade
    /// opened between this and <see cref="MaxDistance"/> stays open until somebody moves.
    /// </summary>
    public static bool MovementEndsTrade(bool positionChanged, float distanceSquared, float combinedBoundingRadius)
    {
        if (!positionChanged || !float.IsFinite(distanceSquared))
        {
            return false;
        }

        float radii = float.IsFinite(combinedBoundingRadius) && combinedBoundingRadius > 0f ? combinedBoundingRadius : 0f;
        float reach = InteractionDistance + radii;
        return distanceSquared >= reach * reach;
    }

    /// <summary>
    /// Whether an accept arrives too soon after the last modification of the trade (vmangos HandleAcceptTradeOpcode,
    /// TradeHandler.cpp:251-257: difftime(now, last) * 1000 &lt; delay). vmangos measures with whole-second time(), so its
    /// effective delay is "not within the same second"; <paramref name="wholeSeconds"/> false uses real milliseconds.
    /// A trade never modified (<paramref name="lastModifiedMs"/> 0) is not delayed.
    /// </summary>
    public static bool ScamPrevented(long lastModifiedMs, long nowMs, uint delayMs, bool wholeSeconds)
    {
        if (delayMs == 0 || lastModifiedMs == 0)
        {
            return false;
        }

        long elapsedMs = wholeSeconds ? ((nowMs / 1000) - (lastModifiedMs / 1000)) * 1000 : nowMs - lastModifiedMs;
        return elapsedMs < delayMs;
    }
}

/// <summary>Spell and optional cast-item identity deferred until both trade sides accept.</summary>
public readonly record struct PendingTradeEnchantment(uint SpellId, ObjectGuid CastItemGuid,
    byte ClientSpellIndex = 0, uint CastItemEntry = 0, ItemSpell? ItemSpellSnapshot = null);

/// <summary>One side of an open trade: its offered item GUIDs per slot, gold and acceptance.</summary>
public sealed class TradeSide(Player player)
{
    private readonly ObjectGuid[] _items = new ObjectGuid[TradeRules.SlotCount];

    public Player Player { get; } = player;

    public uint Gold { get; set; }

    public bool Accepted { get; set; }

    /// <summary>Deferred enchantment requested for this side's partner non-traded item.</summary>
    public PendingTradeEnchantment? PendingEnchantment { get; set; }

    /// <summary>Unix milliseconds of the last modification or accept attempt (vmangos TradeData::m_lastModificationTime); 0 = never.</summary>
    public long LastModifiedMs { get; set; }

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
    private (float X, float Y, float Z) _anchorA = (initiator.X, initiator.Y, initiator.Z);
    private (float X, float Y, float Z) _anchorB = (target.X, target.Y, target.Z);

    public TradeSide Initiator { get; } = new(initiator);

    public TradeSide Target { get; } = new(target);

    /// <summary>Whether the target answered with CMSG_BEGIN_TRADE.</summary>
    public bool Opened { get; set; }

    /// <summary>Set while the two-character settlement runs; the trade accepts no changes.</summary>
    public bool Settling { get; set; }

    /// <summary>Set when the settlement committed (the trade ended with TradeComplete): a closed session that never reached it was cancelled or failed. Read by drivers that must tell the two apart.</summary>
    public bool Completed { get; set; }

    public TradeSide SideOf(Player player) => ReferenceEquals(player, Initiator.Player) ? Initiator : Target;

    public TradeSide OtherSide(Player player) => ReferenceEquals(player, Initiator.Player) ? Target : Initiator;

    public bool Involves(Player player) => ReferenceEquals(player, Initiator.Player) || ReferenceEquals(player, Target.Player);

    public void ClearAccepted()
    {
        Initiator.Accepted = false;
        Target.Accepted = false;
    }

    /// <summary>Clear both deferred trade enchant requests when trade contents or settlement state changes.</summary>
    public void ClearPendingEnchantments()
    {
        Initiator.PendingEnchantment = null;
        Target.PendingEnchantment = null;
    }

    /// <summary>Whether two players stand close enough to trade (3D, as vmangos GetDistance3dToCenter).</summary>
    public static bool InRange(Player a, Player b)
    {
        return WorldGeometry.IsWithinDist(a, b, TradeRules.MaxDistance, inclusive: true, addRadii: false, MapCheck.Id);
    }

    /// <summary>
    /// vmangos <c>Player::SetPosition</c> (Player.cpp:5977-5979): once either player actually moves, the trade ends
    /// when the two are no longer within <see cref="TradeRules.InteractionDistance"/> in 3D, including both bounding
    /// radii. The positions at construction are the baseline, so standing still — even past interaction distance but
    /// inside <see cref="TradeRules.MaxDistance"/> — does not end it. Turning in place does not count as a move.
    /// </summary>
    public bool SeparatedByMovement()
    {
        Player a = Initiator.Player;
        Player b = Target.Player;
        bool moved = a.X != _anchorA.X || a.Y != _anchorA.Y || a.Z != _anchorA.Z
            || b.X != _anchorB.X || b.Y != _anchorB.Y || b.Z != _anchorB.Z;
        _anchorA = (a.X, a.Y, a.Z);
        _anchorB = (b.X, b.Y, b.Z);
        return TradeRules.MovementEndsTrade(moved, WorldGeometry.DistanceSq3D(a, b), a.BoundingRadius + b.BoundingRadius);
    }
}

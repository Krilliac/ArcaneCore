namespace ArcaneCore.Game.Items;

/// <summary>
/// Item-mechanics configuration, bound from the <c>Items</c> section. Every default is the retail
/// (vmangos) value; a different value is a deliberate, operator-chosen deviation.
/// </summary>
public sealed class ItemMechanicsOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Items";

    /// <summary>
    /// vmangos <c>DurabilityLoss.Enable</c> (mangosd.conf.dist.in:2847, World.cpp:553; read first in
    /// Player::DurabilityPointsLoss, Player.cpp:4866). False: no item ever loses durability.
    /// </summary>
    public bool DurabilityLossEnable { get; set; } = true;

    /// <summary>
    /// vmangos <c>DurabilityLossChance.Damage</c> (mangosd.conf.dist.in:2848, World.cpp:554): the
    /// percent chance (0.5 means 0.5 percent) that damage dealt or taken costs one random worn
    /// item a durability point (Unit.cpp:880-895). Consumed by the combat triggers; the inventory
    /// only exposes the value.
    /// </summary>
    public double DurabilityLossChanceDamage { get; set; } = 0.5;

    /// <summary>
    /// How often (ms) the per-map item maintenance runs: timed-item ticks and map/area-limited item
    /// checks. vmangos reacts to the zone change itself (Player::UpdateZone, Player.cpp:6643-6656)
    /// and ticks durations once a second (Player.cpp:1155); here Player.ZoneId has no change
    /// event, so the zone is polled at this interval (a deliberate, documented deviation: the result
    /// is the same within one interval).
    /// </summary>
    public int ZoneLimitCheckMs { get; set; } = 1000;
}

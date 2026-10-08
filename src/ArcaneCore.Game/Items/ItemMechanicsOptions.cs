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
    /// percent chance (0.5 means 0.5 percent) that damage dealt or taken costs a worn item a
    /// durability point (Unit.cpp:1093-1108). Rolled per damage event by MapCombat (MapCombat.Durability.cs):
    /// a player victim that survives loses a point on a uniformly random equipment slot, and a
    /// player's connecting melee swing wears the weapon of the swinging hand. Zero or less never
    /// rolls; <see cref="DurabilityLossEnable"/> false overrides it.
    /// </summary>
    public double DurabilityLossChanceDamage { get; set; } = 0.5;

    /// <summary>
    /// mangos <c>DurabilityLossChance.Parry</c> (mangos-classic World.cpp:461, mangoszero WorldConfig.cpp:234; mangos default 0.05): the percent
    /// chance that a player who parries a melee swing loses a durability point on the main-hand weapon (MapCombat.Durability.cs). Default 0 (off):
    /// vmangos, the fidelity reference, has no parry wear (it reads only <c>DurabilityLossChance.Damage</c>, World.cpp:554); 0.05 restores the
    /// mangos setting. Zero or less never rolls; <see cref="DurabilityLossEnable"/> false overrides it.
    /// </summary>
    public double DurabilityLossChanceParry { get; set; }

    /// <summary>
    /// mangos <c>DurabilityLossChance.Block</c> (mangos-classic World.cpp:462, mangoszero WorldConfig.cpp:235; mangos default 0.05): the percent
    /// chance that a player who blocks a melee swing loses a durability point on the off-hand item (the shield). Default 0 (off), as vmangos has
    /// no block wear; 0.05 restores the mangos setting. Zero or less never rolls.
    /// </summary>
    public double DurabilityLossChanceBlock { get; set; }

    /// <summary>
    /// mangos <c>DurabilityLossChance.Absorb</c> (mangos-classic World.cpp:460, mangoszero WorldConfig.cpp:233; mangos default 0.5): the percent
    /// chance that a player whose absorb effects take part of a melee swing loses a durability point on one worn armor piece (the hit-taken
    /// pool). Default 0 (off), as vmangos has no absorb wear; 0.5 restores the mangos setting. Zero or less never rolls.
    /// </summary>
    public double DurabilityLossChanceAbsorb { get; set; }

    /// <summary>
    /// How often (ms) the per-map item maintenance runs: timed-item ticks and map/area-limited item
    /// checks. vmangos reacts to the zone change itself (Player::UpdateZone, Player.cpp:6643-6656)
    /// and ticks durations once a second (Player.cpp:1155); here Player.ZoneId has no change
    /// event, so the zone is polled at this interval (a deliberate, documented deviation: the result
    /// is the same within one interval).
    /// </summary>
    public int ZoneLimitCheckMs { get; set; } = 1000;
}

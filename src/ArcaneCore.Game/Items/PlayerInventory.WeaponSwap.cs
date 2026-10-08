namespace ArcaneCore.Game.Items;

public sealed partial class PlayerInventory
{
    /// <summary>
    /// Whether the combat weapon change timer runs (vmangos Player::m_weaponChangeTimer, started by a weapon put on in combat; the spell system
    /// owns it, <c>SpellSystem.IsWeaponChangeLocked</c>, and the equip binding sets this at login). Null: never locked. While it answers true a
    /// weapon cannot be equipped in combat: <see cref="InventoryResult.CantDoRightNow"/> (Player::CanEquipItem, Player.cpp:9710-9711).
    /// </summary>
    public Func<bool>? WeaponChangeLocked { get; set; }
}

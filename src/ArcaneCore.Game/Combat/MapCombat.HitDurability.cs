using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// vmangos Unit.cpp:879-895 hit wear. Called by the positive, non-lethal damage path;
    /// death wear is a separate Kill concern. Each player side rolls independently, including
    /// player-versus-player and self damage. The chance is a percent (0.5 = 0.5 percent).
    /// </summary>
    internal void ApplyHitDurability(Unit attacker, Unit victim)
    {
        if (victim is Player victimPlayer)
        {
            RollHitWear(victimPlayer);
        }

        if (attacker is Player attackerPlayer)
        {
            RollHitWear(attackerPlayer);
        }
    }

    private void RollHitWear(Player player)
    {
        double chance = player.Inventory.Options.DurabilityLossChanceDamage;
        if (chance <= 0 || (chance < 100 && Random.NextFloat(0, 100) >= chance))
        {
            return;
        }

        byte slot = (byte)Random.Next(0, InventorySlots.EquipmentEnd - 1);
        player.Inventory.DurabilityPointLossForEquipSlot(slot);
    }
}

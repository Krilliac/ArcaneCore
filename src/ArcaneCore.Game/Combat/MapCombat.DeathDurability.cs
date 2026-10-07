using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>vmangos Unit::Kill: eligible PvE deaths lose 10% equipment durability (Unit.cpp:1191-1202).</summary>
    private static void ApplyDeathDurability(Player victim, bool durabilityLoss, Player? playerTap, SpellInfo? spell)
    {
        if (!durabilityLoss || playerTap is not null || victim.Map?.Template is { IsBattleground: true }
            || ((spell?.AttributesEx3 ?? 0) & 0x20) != 0)
        {
            return;
        }

        victim.Inventory.DurabilityLossAll(0.10, inventory: false);
        // DurabilityLossEnable is checked by the inventory primitive. The source still
        // sends this empty victim-only notification when item mutation is disabled.
        victim.Session.Send(WorldOpcode.SmsgDurabilityDamageDeath, []);
    }
}

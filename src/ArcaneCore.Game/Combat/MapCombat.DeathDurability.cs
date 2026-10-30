using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>
    /// vmangos Unit::Kill: eligible PvE deaths lose 10% equipment durability (Unit.cpp:1191-1202). A PvP death costs none, except
    /// with War Mode on (<c>World:WarMode:Enabled</c>, not retail): then an open-world PvP death costs
    /// <c>World:WarMode:PvpDeathDurabilityLoss</c> of every equipped item. A battleground death costs none either way.
    /// </summary>
    private void ApplyDeathDurability(Player victim, bool durabilityLoss, Player? playerTap, SpellInfo? spell)
    {
        if (!durabilityLoss || victim.Map?.Template is { IsBattleground: true } || ((spell?.AttributesEx3 ?? 0) & 0x20) != 0)
        {
            return;
        }

        double percent = 0.10;
        if (playerTap is not null)
        {
            WorldState.WarModeOptions war = WarMode;
            if (!war.Enabled || war.PvpDeathDurabilityLoss <= 0f)
            {
                return;
            }

            percent = Math.Round((double)war.PvpDeathDurabilityLoss, 6); // 0.1f is 0.10000000149...; the operator wrote 0.1
        }

        victim.Inventory.DurabilityLossAll(percent, inventory: false);
        // DurabilityLossEnable is checked by the inventory primitive. The source still
        // sends this empty victim-only notification when item mutation is disabled.
        victim.Session.Send(WorldOpcode.SmsgDurabilityDamageDeath, []);
    }
}

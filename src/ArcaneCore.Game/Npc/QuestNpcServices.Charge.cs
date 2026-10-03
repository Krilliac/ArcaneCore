using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Npc;

public sealed partial class QuestNpcServices
{
    /// <summary>
    /// Take <paramref name="copper"/> from the player's purse through the one money-mutation path of
    /// the NPC services (vmangos Player::ModifyMoney: MoneyChanged update and character save), for
    /// fixed-price services that are not vendor sales: guild charters (PetitionsHandler.cpp:105-110,
    /// 1000 copper) and the guild tabard emblem (GuildHandler.cpp:709-715, 10 gold). False, with
    /// nothing changed, when the player cannot pay or is not tracked. World thread.
    /// </summary>
    /// <remarks>
    /// Callers follow the vendor order (Vendor.cs): store the goods first, charge second, so a
    /// failed store never costs money.
    /// </remarks>
    public bool TryCharge(Player player, uint copper)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Ready(player) is not { } state || player.Money < copper)
        {
            return false;
        }

        player.EnsureQuestSettlementMutationAllowed();
        ModifyMoney(state, -(long)copper);
        Flush(state);
        return true;
    }
}

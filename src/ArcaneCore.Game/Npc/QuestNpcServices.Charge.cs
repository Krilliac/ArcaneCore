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

    /// <summary>
    /// A flight leg's fare at its path transition (vmangos FlightPathMovementGenerator::Update, WaypointMovementGenerator.cpp:421-423:
    /// <c>ModifyMoney(-cost)</c>), which clamps at zero and never refuses (Player.h:1025-1031): a character that no longer holds the fare
    /// pays what it has and the flight goes on. The up-front check of <c>ActivateTaxiPathTo</c> is the only gate. Used by a route resumed at
    /// login, whose launch-time check was made in an earlier session. Always true for a tracked player, false when it is not tracked. World thread.
    /// </summary>
    public bool ChargeFlightLeg(Player player, uint copper)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (Ready(player) is not { } state)
        {
            return false;
        }

        if (copper != 0)
        {
            player.EnsureQuestSettlementMutationAllowed();
            ModifyMoney(state, -(long)copper);
            Flush(state);
        }

        return true;
    }
}

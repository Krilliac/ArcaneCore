using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Npc;

/// <summary>Bankers and spirit healers (vmangos HandleBankerActivateOpcode, HandleBuyBankSlotOpcode, HandleSpiritHealerActivateOpcode).</summary>
public sealed partial class QuestNpcServices
{
    /// <summary>Bank bag slots a character can buy (vmangos BANK_SLOT_BAG_END − BANK_SLOT_BAG_START).</summary>
    public const byte MaxBankBagSlots = 6;

    /// <summary>
    /// CMSG_BANKER_ACTIVATE (vmangos HandleBankerActivateOpcode → SendShowBank): an interactable
    /// banker opens the bank window (SMSG_SHOW_BANK: u64 banker). Bank moves are allowed while
    /// that banker stays interactable (vmangos Player::CanUseBank re-checks the banker).
    /// </summary>
    public void BankerActivate(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } s)
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.Banker) is not { } npc)
        {
            LogMissing("BankerActivate", guid);
            return;
        }

        ShowBank(s, npc);
    }

    /// <summary>
    /// CMSG_BUY_BANK_SLOT (vmangos HandleBuyBankSlotOpcode): an interactable banker, a next slot
    /// with a BankBagSlotPrices price, and enough money; vmangos ItemHandler.cpp:872-909 sends
    /// SMSG_BUY_BANK_SLOT_RESULT for a failed purchase and no reply for success. The count is
    /// written to PLAYER_BYTES_2 and saved with the money change in the character snapshot.
    /// </summary>
    public void BuyBankSlot(Player player, ObjectGuid guid)
    {
        if (Ready(player) is not { } s)
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.Banker) is null)
        {
            LogMissing("BuyBankSlot", guid);
            Send(player, WorldOpcode.SmsgBuyBankSlotResult, NpcPackets.BuyBankSlotResult(BankSlotResult.NotBanker));
            return;
        }

        if (Deps.Items is not { } items)
        {
            return;
        }

        uint next = (uint)player.Inventory.BankBagSlotCount + 1;
        if (next > MaxBankBagSlots || items.BankBagSlotPrice(next) is not { } price)
        {
            Send(player, WorldOpcode.SmsgBuyBankSlotResult, NpcPackets.BuyBankSlotResult(BankSlotResult.TooMany));
            return;
        }

        if (player.Money < price)
        {
            Send(player, WorldOpcode.SmsgBuyBankSlotResult, NpcPackets.BuyBankSlotResult(BankSlotResult.InsufficientFunds));
            return;
        }

        if (!items.SetBankBagSlotCount(player, (byte)next))
        {
            return;
        }

        ModifyMoney(s, -(long)price);
        Flush(s);
    }

    /// <summary>
    /// CMSG_SPIRIT_HEALER_ACTIVATE (vmangos HandleSpiritHealerActivateOpcode → SendSpiritResurrect):
    /// a dead player at an interactable spirit healer is resurrected by the resurrection owner.
    /// </summary>
    public void SpiritHealerActivate(Player player, ObjectGuid guid)
    {
        if (Ready(player) is null || player.IsAlive)
        {
            return;
        }

        if (InteractableNpc(player, guid, NpcFlags.SpiritHealer, out string? refusal) is null)
        {
            _logger.LogDebug("SpiritHealerActivate: {Npc} refused: {Refusal}", guid, refusal);
            return;
        }

        InteractingCancels?.Invoke(player); // vmangos HandleSpiritHealerActivateOpcode, NPCHandler.cpp:425-426
        Deps.Resurrection?.ResurrectAtSpiritHealer(player);
    }

    /// <summary>
    /// Why CMSG_SPIRIT_HEALER_ACTIVATE for <paramref name="guid"/> would leave <paramref name="player"/> dead now, or null when it would
    /// resurrect it: the checks of <see cref="SpiritHealerActivate"/> (the player's NPC state loaded, dead; the healer interactable,
    /// <see cref="InteractableNpc(Player, ObjectGuid, NpcFlags, out string?)"/>) and of the resurrection itself
    /// (<see cref="Combat.MapCombat.ResurrectAtSpiritHealer"/>: a ghost, no quest settlement pending). For a caller that reports a
    /// refusal (a managed bot's recovery); a client's refused request stays silent, as in vmangos.
    /// </summary>
    public string? SpiritHealerRefusal(Player player, ObjectGuid guid)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.IsAlive) return "the player is alive";
        if (Ready(player) is null) return "the player's NPC state is not ready (not loaded, or a quest settlement pending)";
        if (InteractableNpc(player, guid, NpcFlags.SpiritHealer, out string? refusal) is null) return refusal ?? "not interactable";
        if ((player.Flags & PlayerFlags.Ghost) == 0) return "the player is not a ghost (its body was not released)";
        if (player.IsQuestSettlementPending) return "a quest settlement is pending";
        return Deps.Resurrection is null ? "no resurrection service" : null;
    }

    /// <summary>vmangos WorldSession::SendShowBank.</summary>
    internal void ShowBank(PlayerNpcState s, NpcInfo npc)
    {
        Player player = s.Quests.Player;
        ObjectGuid banker = npc.Guid;
        Deps.Items?.OpenBank(player, () => InteractableNpc(player, banker, NpcFlags.Banker) is not null);
        Send(player, WorldOpcode.SmsgShowBank, NpcPackets.Guid(banker));
    }

    /// <summary>
    /// The gossip spirit healer option (vmangos OnGossipSelect casts 17251 on the healer, whose
    /// effect asks the client to confirm): SMSG_SPIRIT_HEALER_CONFIRM (u64 healer) to a dead player.
    /// </summary>
    internal static void SendSpiritHealerConfirm(Player player, NpcInfo npc)
    {
        if (!player.IsAlive)
        {
            CloseGossip(player);
            Send(player, WorldOpcode.SmsgSpiritHealerConfirm, NpcPackets.Guid(npc.Guid));
        }
    }
}

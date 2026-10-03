using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

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

        if (InteractableNpc(player, guid, NpcFlags.SpiritHealer) is null)
        {
            LogMissing("SpiritHealerActivate", guid);
            return;
        }

        Deps.Resurrection?.ResurrectAtSpiritHealer(player);
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

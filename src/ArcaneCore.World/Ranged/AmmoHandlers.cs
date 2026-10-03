using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ranged;

/// <summary>The ammunition opcode (vmangos ItemHandler.cpp HandleSetAmmoOpcode).</summary>
public sealed class AmmoHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgSetAmmo, HandleSetAmmo);

    /// <summary>
    /// CMSG_SET_AMMO: u32 item (gtker cmsg_set_ammo.wowm). A dead player gets YOU_ARE_DEAD; an item
    /// the player does not hold gets ITEM_NOT_FOUND; item 0 removes the ammo; anything else goes
    /// through <see cref="PlayerAmmo.SetAmmo"/> (vmangos ItemHandler.cpp:988-1008).
    /// </summary>
    private static void HandleSetAmmo(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint item = reader.ReadUInt32();
        if (!player.IsAlive)
        {
            player.Inventory.SendEquipError(InventoryResult.YouAreDead, null, null);
            return;
        }

        AmmoFeature feature = session.Services.GetRequiredService<AmmoFeature>();
        if (item == 0)
        {
            PlayerAmmo.RemoveAmmo(player);
            feature.Save(player);
            return;
        }

        if (player.Inventory.GetItemCount(item) == 0)
        {
            player.Inventory.SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        if (PlayerAmmo.SetAmmo(player, item))
        {
            feature.Save(player);
        }
    }
}

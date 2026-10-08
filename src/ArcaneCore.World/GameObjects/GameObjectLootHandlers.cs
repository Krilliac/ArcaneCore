using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// Game object and loot opcodes (vmangos QueryHandler HandleGameObjectQueryOpcode,
/// MiscHandler HandleGameObjectUseOpcode, LootHandler.cpp, ItemHandler HandleOpenItemOpcode;
/// layouts cross-checked with gtker/wow_messages). Malformed (short) payloads are ignored.
/// </summary>
public sealed class GameObjectLootHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgGameobjectQuery, SessionStates.LoggedIn, HandleQueryAsync);
        table.OnWorld(WorldOpcode.CmsgGameobjUse, Use);
        table.OnWorld(WorldOpcode.CmsgLoot, Loot);
        table.OnWorld(WorldOpcode.CmsgAutostoreLootItem, AutostoreLootItem);
        table.OnWorld(WorldOpcode.CmsgLootMoney, LootMoney);
        table.OnWorld(WorldOpcode.CmsgLootRelease, LootRelease);
        table.OnWorld(WorldOpcode.CmsgOpenItem, OpenItem);
        table.OnWorld(WorldOpcode.CmsgLootRoll, LootRoll);
        table.OnWorld(WorldOpcode.CmsgLootMasterGive, LootMasterGive);
    }

    private static GameObjectLootFeature? Feature(WorldSession session) => session.Services.GetService<GameObjectLootFeature>();

    private static LootService? LootOf(WorldSession session, Player player)
        => player.Map is { } map ? Feature(session)?.FindSystem(map)?.Loot : null;

    /// <summary>CMSG_GAMEOBJECT_QUERY: u32 entry, u64 guid. Answered on the session task (content is immutable).</summary>
    private static Task HandleQueryAsync(WorldSession session, byte[] payload)
    {
        if (payload.Length < 4)
        {
            return Task.CompletedTask;
        }

        var reader = new PacketReader(payload);
        uint entry = reader.ReadUInt32();
        session.Send(WorldOpcode.SmsgGameobjectQueryResponse,
            Feature(session)?.Content.FindTemplate(entry) is { } template
                ? GameObjectPackets.QueryResponse(template)
                : GameObjectPackets.QueryUnknown(entry));
        return Task.CompletedTask;
    }

    /// <summary>CMSG_GAMEOBJ_USE: u64 guid.</summary>
    private static void Use(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 8 || player.Map is not { } map || Feature(session)?.FindSystem(map) is not { } system)
        {
            return;
        }

        var reader = new PacketReader(payload);
        var guid = new ObjectGuid(reader.ReadUInt64());
        if (system.Use(player, guid) == GameObjectUseResult.TooFar && system.Find(guid) is { } gameObject)
        {
            // Refused already; far beyond any interaction distance it is a remote-use attempt (docs/areas/anticheat.md).
            session.Services.GetService<AntiCheat.AntiCheatFeature>()?.OnGameObjectTooFar(session, player, gameObject);
        }
    }

    /// <summary>CMSG_LOOT: u64 corpse guid.</summary>
    private static void Loot(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 8 || LootOf(session, player) is not { } loot)
        {
            return;
        }

        var reader = new PacketReader(payload);
        ObjectGuid source = new(reader.ReadUInt64());
        LootResult result = loot.Open(player, source);
        if (result != LootResult.Ok && session.Logger.IsEnabled(LogLevel.Debug))
        {
            string diagnostic = JsonSerializer.Serialize(loot.DescribeOpen(player, source, result));
            session.Logger.LogDebug("Loot open refused: {Diagnostic}", diagnostic);
        }
    }

    /// <summary>CMSG_AUTOSTORE_LOOT_ITEM: u8 loot slot.</summary>
    private static void AutostoreLootItem(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 1 || LootOf(session, player) is not { } loot)
        {
            return;
        }

        loot.TakeItem(player, payload[0]);
    }

    /// <summary>CMSG_LOOT_MONEY: empty.</summary>
    private static void LootMoney(WorldSession session, Player player, byte[] payload)
        => LootOf(session, player)?.TakeMoney(player);

    /// <summary>CMSG_LOOT_RELEASE: u64 guid.</summary>
    private static void LootRelease(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 8 || LootOf(session, player) is not { } loot)
        {
            return;
        }

        var reader = new PacketReader(payload);
        loot.Release(player, new ObjectGuid(reader.ReadUInt64()));
    }

    /// <summary>CMSG_LOOT_ROLL: u64 corpse, u32 slot, u8 vote (0 pass, 1 need, 2 greed; larger votes are dropped).</summary>
    private static void LootRoll(WorldSession session, Player player, byte[] payload)
    {
        if (LootOf(session, player) is { } loot && GroupLootPackets.TryParseLootRoll(payload, out ObjectGuid corpse, out uint slot, out RollVote vote))
        {
            loot.Rolls.Vote(player, corpse, slot, vote);
        }
    }

    /// <summary>CMSG_LOOT_MASTER_GIVE: u64 loot guid, u8 slot, u64 target player.</summary>
    private static void LootMasterGive(WorldSession session, Player player, byte[] payload)
    {
        if (LootOf(session, player) is { } loot && GroupLootPackets.TryParseMasterGive(payload, out ObjectGuid guid, out byte slot, out ObjectGuid target))
        {
            loot.GiveMasterLoot(player, guid, slot, target);
        }
    }

    /// <summary>CMSG_OPEN_ITEM: u8 bag, u8 slot.</summary>
    private static void OpenItem(WorldSession session, Player player, byte[] payload)
    {
        // A wrapped gift opens into its contents (vmangos HandleOpenItemOpcode, SpellHandler.cpp:200-227), loot service or not.
        if (payload.Length >= 2 && player.Inventory.GetItem(payload[0], payload[1]) is { } wrapped
            && (wrapped.DynamicFlags & ItemDynFlags.Wrapped) != 0)
        {
            OpenGift(player, wrapped);
            return;
        }

        if (payload.Length < 2 || LootOf(session, player) is not { } loot)
        {
            return;
        }

        if (player.Inventory.GetItem(payload[0], payload[1]) is not { } item)
        {
            player.Inventory.SendEquipError(InventoryResult.ItemNotFound, null, null);
            return;
        }

        loot.OpenItem(player, item);
    }

    /// <summary>The refusals before the wrapped branch, in vmangos order (SpellHandler.cpp:163-174): on a taxi, then dead.</summary>
    private static void OpenGift(Player player, Item gift)
    {
        if ((player.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            player.Inventory.SendEquipError(InventoryResult.CantDoRightNow, gift, null);
            return;
        }

        if (!player.IsAlive)
        {
            player.Inventory.SendEquipError(InventoryResult.YouAreDead, gift, null);
            return;
        }

        player.Inventory.OpenGift(gift);
    }
}

using System.Buffers.Binary;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.ClientErrors;

/// <summary>Which kind of "no" a server packet tells the client.</summary>
public enum ClientRejectionKind : byte
{
    CastResult,
    PetCastFailed,
    InventoryChange,
    LootError,
    BuyFailed,
    SellFailed,
    QuestInvalid,
    QuestFailed,
    QuestUpdateFailed,
    TradeStatus,
    TrainerBuyFailed,
    PartyCommand,
    AuctionCommand,
    MailResult,
    PetTameFailure,
    ReadItemFailed,
}

/// <summary>
/// One decoded refusal: the kind, the reason code, the target guid (vendor, loot source, item) and the spell, item or quest id it
/// concerns, and one extra number (the second item guid's low part, the equip-level, the trade or party operation).
/// </summary>
public readonly record struct ClientRejection(ClientRejectionKind Kind, uint Reason, ulong Target, uint Entry, uint Arg)
{
    /// <summary>The reason as its enum name, or the number when the code is not a defined name. Allocates only for an unnamed code; call it after the rate limit.</summary>
    public string ReasonName => Kind switch
    {
        ClientRejectionKind.CastResult or ClientRejectionKind.PetCastFailed => Name((SpellCastResult)(byte)Reason),
        ClientRejectionKind.InventoryChange => Name((Items.InventoryResult)(byte)Reason),
        ClientRejectionKind.LootError => Name((LootError)(byte)Reason),
        ClientRejectionKind.BuyFailed => Name((BuyResult)(byte)Reason),
        ClientRejectionKind.SellFailed => Name((SellResult)(byte)Reason),
        ClientRejectionKind.QuestInvalid or ClientRejectionKind.QuestFailed => Name((QuestInvalidReason)Reason),
        ClientRejectionKind.TradeStatus => Name((TradeStatus)Reason),
        ClientRejectionKind.PartyCommand => Name((PartyResult)Reason),
        ClientRejectionKind.AuctionCommand => Name((AuctionError)Reason),
        ClientRejectionKind.MailResult => Name((MailResult)Reason),
        ClientRejectionKind.PetTameFailure => Name((PetTameFailureReason)(byte)Reason),
        ClientRejectionKind.TrainerBuyFailed => Reason switch { 0 => "Unavailable", 1 => "NotEnoughMoney", 2 => "NotEnoughSkill", _ => Reason.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        _ => Reason.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Name<T>(T value) where T : struct, Enum
        => Enum.IsDefined(value) ? Enum.GetName(value)! : Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Recognises the server packets that tell a client "no" and reads their reason out of the payload (docs/ops/observability.md). Pure and
/// allocation free: <see cref="IsCandidate"/> is one table load, <see cref="TryDecode"/> reads a few integers. The layouts are the ones the
/// packet builders write (SpellPackets.BuildCastResult, ItemPackets.InventoryChangeFailure, GroupLootPackets.LootErrorResponse, NpcPackets,
/// QuestPackets, EconomyPackets).
/// </summary>
public static class ClientRejectionDecoder
{
    private static readonly bool[] Candidates = BuildCandidates();

    private static bool[] BuildCandidates()
    {
        var table = new bool[1024];
        WorldOpcode[] all =
        [
            WorldOpcode.SmsgCastResult, WorldOpcode.SmsgPetCastFailed, WorldOpcode.SmsgInventoryChangeFailure, WorldOpcode.SmsgLootResponse,
            WorldOpcode.SmsgBuyFailed, WorldOpcode.SmsgSellItem, WorldOpcode.SmsgQuestgiverQuestInvalid, WorldOpcode.SmsgQuestgiverQuestFailed,
            WorldOpcode.SmsgQuestupdateFailed, WorldOpcode.SmsgQuestupdateFailedtimer, WorldOpcode.SmsgTradeStatus, WorldOpcode.SmsgTrainerBuyFailed,
            WorldOpcode.SmsgPartyCommandResult, WorldOpcode.SmsgAuctionCommandResult, WorldOpcode.SmsgSendMailResult, WorldOpcode.SmsgPetTameFailure,
            WorldOpcode.SmsgReadItemFailed,
        ];
        foreach (WorldOpcode opcode in all)
        {
            table[(ushort)opcode] = true;
        }

        return table;
    }

    /// <summary>True when a packet of this opcode can be a refusal (the payload decides).</summary>
    public static bool IsCandidate(WorldOpcode opcode) => (uint)(ushort)opcode < (uint)Candidates.Length && Candidates[(ushort)opcode];

    private static readonly bool[] ServerErrorReasons = BuildServerErrorReasons();

    private static bool[] BuildServerErrorReasons()
    {
        var table = new bool[(Enum.GetValues<ClientRejectionKind>().Length) << 8];
        Mark(table, ClientRejectionKind.MailResult, (uint)MailResult.InternalError);
        Mark(table, ClientRejectionKind.AuctionCommand, (uint)AuctionError.Database);
        Mark(table, ClientRejectionKind.CastResult, (uint)SpellCastResult.Unknown);
        Mark(table, ClientRejectionKind.PetCastFailed, (uint)SpellCastResult.Unknown);
        Mark(table, ClientRejectionKind.InventoryChange, (uint)Items.InventoryResult.ItemNotFound);
        Mark(table, ClientRejectionKind.BuyFailed, (uint)BuyResult.CantFindItem);
        Mark(table, ClientRejectionKind.SellFailed, (uint)SellResult.CantFindVendor);
        return table;
    }

    private static void Mark(bool[] table, ClientRejectionKind kind, uint reason) => table[((int)kind << 8) | (int)reason] = true;

    /// <summary>
    /// True when the refusal says the server itself failed (a mail or auction internal error, an unknown cast failure, an item or vendor the server
    /// cannot find) rather than that the player asked for something not allowed. One bounds check and one table load; no allocation.
    /// </summary>
    public static bool IsServerError(in ClientRejection rejection)
    {
        int index = ((int)rejection.Kind << 8) | (int)(rejection.Reason & 0xFF);
        return rejection.Reason <= 0xFF && (uint)index < (uint)ServerErrorReasons.Length && ServerErrorReasons[index];
    }

    private const uint TradeStatusCloseWindow = (uint)TradeStatus.CloseWindow;

    /// <summary>The trade statuses that mean the trade cannot go on (not the normal flow: open, accept, complete, close, cancel).</summary>
    private static bool IsTradeFailure(uint status) => status is 0 or 5 or 6 or 10 or 11 or (>= 14 and <= 22);

    /// <summary>Decode <paramref name="payload"/> as a refusal; false for a success reply, a normal reply or a payload that is too short to say.</summary>
    public static bool TryDecode(WorldOpcode opcode, ReadOnlySpan<byte> payload, out ClientRejection rejection)
    {
        rejection = default;
        switch (opcode)
        {
            case WorldOpcode.SmsgCastResult:
            case WorldOpcode.SmsgPetCastFailed:
                if (payload.Length < 6 || payload[4] != (byte)SpellCastResultStatus.Failure)
                {
                    return false;
                }

                rejection = new ClientRejection(opcode == WorldOpcode.SmsgCastResult ? ClientRejectionKind.CastResult : ClientRejectionKind.PetCastFailed,
                    payload[5], 0, U32(payload, 0), payload.Length >= 10 ? U32(payload, 6) : 0);
                return true;

            case WorldOpcode.SmsgInventoryChangeFailure:
                if (payload.Length < 1 || payload[0] == 0)
                {
                    return false;
                }

                int guidAt = payload[0] == (byte)Items.InventoryResult.CantEquipLevelI ? 5 : 1;
                rejection = new ClientRejection(ClientRejectionKind.InventoryChange, payload[0],
                    payload.Length >= guidAt + 8 ? U64(payload, guidAt) : 0, 0, guidAt == 5 && payload.Length >= 5 ? U32(payload, 1) : 0);
                return true;

            case WorldOpcode.SmsgLootResponse:
                // The error form is guid, type 0, error byte (GroupLootPackets.LootErrorResponse); a real loot window has a non-zero type.
                if (payload.Length != 10 || payload[8] != 0)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.LootError, payload[9], U64(payload, 0), 0, 0);
                return true;

            case WorldOpcode.SmsgBuyFailed:
                if (payload.Length < 13)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.BuyFailed, payload[12], U64(payload, 0), U32(payload, 8), 0);
                return true;

            case WorldOpcode.SmsgSellItem:
                if (payload.Length < 17)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.SellFailed, payload[16], U64(payload, 0), 0, (uint)(U64(payload, 8) & 0xFFFFFFFF));
                return true;

            case WorldOpcode.SmsgQuestgiverQuestInvalid:
                if (payload.Length < 4)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.QuestInvalid, U32(payload, 0), 0, 0, 0);
                return true;

            case WorldOpcode.SmsgQuestgiverQuestFailed:
                if (payload.Length < 8)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.QuestFailed, U32(payload, 4), 0, U32(payload, 0), 0);
                return true;

            case WorldOpcode.SmsgQuestupdateFailed:
            case WorldOpcode.SmsgQuestupdateFailedtimer:
                if (payload.Length < 4)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.QuestUpdateFailed, opcode == WorldOpcode.SmsgQuestupdateFailedtimer ? 1u : 0u, 0, U32(payload, 0), 0);
                return true;

            case WorldOpcode.SmsgTradeStatus:
                if (payload.Length < 4 || !IsTradeFailure(U32(payload, 0)))
                {
                    // CLOSE_WINDOW is the normal close unless it carries an inventory error (a side without room): u32 status, u32 inventory result, u8 target flag, u32 0.
                    if (payload.Length >= 8 && U32(payload, 0) == TradeStatusCloseWindow && U32(payload, 4) != 0)
                    {
                        rejection = new ClientRejection(ClientRejectionKind.TradeStatus, TradeStatusCloseWindow, 0, 0, U32(payload, 4));
                        return true;
                    }

                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.TradeStatus, U32(payload, 0), 0, 0, 0);
                return true;

            case WorldOpcode.SmsgTrainerBuyFailed:
                if (payload.Length < 16)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.TrainerBuyFailed, U32(payload, 12), U64(payload, 0), U32(payload, 8), 0);
                return true;

            case WorldOpcode.SmsgPartyCommandResult:
            {
                // u32 operation, CString name, u32 result.
                if (payload.Length < 9)
                {
                    return false;
                }

                int nul = payload[4..].IndexOf((byte)0);
                if (nul < 0 || payload.Length < 4 + nul + 1 + 4)
                {
                    return false;
                }

                uint result = U32(payload, 4 + nul + 1);
                if (result == 0)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.PartyCommand, result, 0, 0, U32(payload, 0));
                return true;
            }

            case WorldOpcode.SmsgAuctionCommandResult:
                if (payload.Length < 12 || U32(payload, 8) == 0)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.AuctionCommand, U32(payload, 8), 0, U32(payload, 0), U32(payload, 4));
                return true;

            case WorldOpcode.SmsgSendMailResult:
                if (payload.Length < 12 || U32(payload, 8) == 0)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.MailResult, U32(payload, 8), 0, U32(payload, 0), U32(payload, 4));
                return true;

            case WorldOpcode.SmsgPetTameFailure:
                if (payload.Length < 1)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.PetTameFailure, payload[0], 0, 0, 0);
                return true;

            case WorldOpcode.SmsgReadItemFailed:
                if (payload.Length < 8)
                {
                    return false;
                }

                rejection = new ClientRejection(ClientRejectionKind.ReadItemFailed, 0, U64(payload, 0), 0, 0);
                return true;

            default:
                return false;
        }
    }

    private static uint U32(ReadOnlySpan<byte> payload, int at) => BinaryPrimitives.ReadUInt32LittleEndian(payload[at..]);

    private static ulong U64(ReadOnlySpan<byte> payload, int at) => BinaryPrimitives.ReadUInt64LittleEndian(payload[at..]);
}

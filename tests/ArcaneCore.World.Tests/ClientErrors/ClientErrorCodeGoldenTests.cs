using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.ClientErrors;

/// <summary>
/// Golden values from the 1.12.1 client binary, recorded in WoW-RE-Research protocol/client-error-codes.md
/// (summary: docs/areas/client-errors.md). The tables below are the client's own code-to-string-key mappings: the
/// cast-result switch at 0x6E23E0 and the glue response-code table at 0x85CAE8. A failing test means an enum drifted
/// from what the client displays; the binary wins over emulator headers.
/// </summary>
public sealed class ClientErrorCodeGoldenTests
{
    /// <summary>SPELL_FAILED_* keys for cast results 0x00-0x90, in code order (binary; values above 0x90 show SPELL_FAILED_UNKNOWN).</summary>
    private static readonly string[] SpellFailedKeys =
    [
        "AFFECTING_COMBAT",
        "ALREADY_AT_FULL_HEALTH",
        "ALREADY_AT_FULL_POWER",
        "ALREADY_BEING_TAMED",
        "ALREADY_HAVE_CHARM",
        "ALREADY_HAVE_SUMMON",
        "ALREADY_OPEN",
        "AURA_BOUNCED",
        "AUTOTRACK_INTERRUPTED",
        "BAD_IMPLICIT_TARGETS",
        "BAD_TARGETS",
        "CANT_BE_CHARMED",
        "CANT_BE_DISENCHANTED",
        "CANT_BE_PROSPECTED",
        "CANT_CAST_ON_TAPPED",
        "CANT_DUEL_WHILE_INVISIBLE",
        "CANT_DUEL_WHILE_STEALTHED",
        "CANT_STEALTH",
        "CASTER_AURASTATE",
        "CASTER_DEAD",
        "CHARMED",
        "CHEST_IN_USE",
        "CONFUSED",
        "DONT_REPORT",
        "EQUIPPED_ITEM",
        "EQUIPPED_ITEM_CLASS",
        "EQUIPPED_ITEM_CLASS_MAINHAND",
        "EQUIPPED_ITEM_CLASS_OFFHAND",
        "ERROR",
        "FIZZLE",
        "FLEEING",
        "FOOD_LOWLEVEL",
        "HIGHLEVEL",
        "HUNGER_SATIATED",
        "IMMUNE",
        "INTERRUPTED",
        "INTERRUPTED_COMBAT",
        "ITEM_ALREADY_ENCHANTED",
        "ITEM_GONE",
        "ITEM_NOT_FOUND",
        "ITEM_NOT_READY",
        "LEVEL_REQUIREMENT",
        "LINE_OF_SIGHT",
        "LOWLEVEL",
        "LOW_CASTLEVEL",
        "MAINHAND_EMPTY",
        "MOVING",
        "NEED_AMMO",
        "NEED_AMMO_POUCH",
        "NEED_EXOTIC_AMMO",
        "NOPATH",
        "NOT_BEHIND",
        "NOT_FISHABLE",
        "NOT_HERE",
        "NOT_INFRONT",
        "NOT_IN_CONTROL",
        "NOT_KNOWN",
        "NOT_MOUNTED",
        "NOT_ON_TAXI",
        "NOT_ON_TRANSPORT",
        "NOT_READY",
        "NOT_SHAPESHIFT",
        "NOT_STANDING",
        "NOT_TRADEABLE",
        "NOT_TRADING",
        "NOT_UNSHEATHED",
        "NOT_WHILE_GHOST",
        "NO_AMMO",
        "NO_CHARGES_REMAIN",
        "NO_CHAMPION",
        "NO_COMBO_POINTS",
        "NO_DUELING",
        "NO_ENDURANCE",
        "NO_FISH",
        "NO_ITEMS_WHILE_SHAPESHIFTED",
        "NO_MOUNTS_ALLOWED",
        "NO_PET",
        "NO_POWER",
        "NOTHING_TO_DISPEL",
        "NOTHING_TO_STEAL",
        "ONLY_ABOVEWATER",
        "ONLY_DAYTIME",
        "ONLY_INDOORS",
        "ONLY_MOUNTED",
        "ONLY_NIGHTTIME",
        "ONLY_OUTDOORS",
        "ONLY_SHAPESHIFT",
        "ONLY_STEALTHED",
        "ONLY_UNDERWATER",
        "OUT_OF_RANGE",
        "PACIFIED",
        "POSSESSED",
        "REAGENTS",
        "REQUIRES_AREA",
        "REQUIRES_SPELL_FOCUS",
        "ROOTED",
        "SILENCED",
        "SPELL_IN_PROGRESS",
        "SPELL_LEARNED",
        "SPELL_UNAVAILABLE",
        "STUNNED",
        "TARGETS_DEAD",
        "TARGET_AFFECTING_COMBAT",
        "TARGET_AURASTATE",
        "TARGET_DUELING",
        "TARGET_ENEMY",
        "TARGET_ENRAGED",
        "TARGET_FRIENDLY",
        "TARGET_IN_COMBAT",
        "TARGET_IS_PLAYER",
        "TARGET_NOT_DEAD",
        "TARGET_NOT_IN_PARTY",
        "TARGET_NOT_LOOTED",
        "TARGET_NOT_PLAYER",
        "TARGET_NO_POCKETS",
        "TARGET_NO_WEAPONS",
        "TARGET_UNSKINNABLE",
        "THIRST_SATIATED",
        "TOO_CLOSE",
        "TOO_MANY_OF_ITEM",
        "TOTEMS",
        "TRAINING_POINTS",
        "TRY_AGAIN",
        "UNIT_NOT_BEHIND",
        "UNIT_NOT_INFRONT",
        "WRONG_PET_FOOD",
        "NOT_WHILE_FATIGUED",
        "TARGET_NOT_IN_INSTANCE",
        "NOT_WHILE_TRADING",
        "TARGET_NOT_IN_RAID",
        "DISENCHANT_WHILE_LOOTING",
        "PROSPECT_WHILE_LOOTING",
        "PROSPECT_NEED_MORE",
        "TARGET_FREEFORALL",
        "NO_EDIBLE_CORPSES",
        "ONLY_BATTLEGROUNDS",
        "TARGET_NOT_GHOST",
        "TOO_MANY_SKILLS",
        "TRANSFORM_UNUSABLE",
        "WRONG_WEATHER",
        "DAMAGE_IMMUNE",
        "PREVENTED_BY_MECHANIC",
        "PLAY_TIME",
        "REPUTATION",
        "MIN_SKILL"
    ];

    /// <summary>The character-screen part of the client's response-code table (binary).</summary>
    private static readonly Dictionary<int, string> ResponseKeys = new()
    {
        [0x2E] = "CHAR_CREATE_SUCCESS",
        [0x2F] = "CHAR_CREATE_ERROR",
        [0x30] = "CHAR_CREATE_FAILED",
        [0x31] = "CHAR_CREATE_NAME_IN_USE",
        [0x32] = "CHAR_CREATE_DISABLED",
        [0x33] = "CHAR_CREATE_PVP_TEAMS_VIOLATION",
        [0x34] = "CHAR_CREATE_SERVER_LIMIT",
        [0x35] = "CHAR_CREATE_ACCOUNT_LIMIT",
        [0x36] = "CHAR_CREATE_SERVER_QUEUE",
        [0x37] = "CHAR_CREATE_ONLY_EXISTING",
        [0x38] = "CHAR_DELETE_IN_PROGRESS",
        [0x39] = "CHAR_DELETE_SUCCESS",
        [0x3A] = "CHAR_DELETE_FAILED",
        [0x3B] = "CHAR_DELETE_FAILED_LOCKED_FOR_TRANSFER",
        [0x3C] = "CHAR_LOGIN_IN_PROGRESS",
        [0x3D] = "CHAR_LOGIN_SUCCESS",
        [0x3E] = "CHAR_LOGIN_NO_WORLD",
        [0x3F] = "CHAR_LOGIN_DUPLICATE_CHARACTER",
        [0x40] = "CHAR_LOGIN_NO_INSTANCES",
        [0x41] = "CHAR_LOGIN_FAILED",
        [0x42] = "CHAR_LOGIN_DISABLED",
        [0x43] = "CHAR_LOGIN_NO_CHARACTER",
        [0x44] = "CHAR_LOGIN_LOCKED_FOR_TRANSFER",
        [0x45] = "CHAR_NAME_NO_NAME",
        [0x46] = "CHAR_NAME_TOO_SHORT",
        [0x47] = "CHAR_NAME_TOO_LONG",
        [0x48] = "CHAR_NAME_INVALID_CHARACTER",
        [0x49] = "CHAR_NAME_MIXED_LANGUAGES",
        [0x4A] = "CHAR_NAME_PROFANE",
        [0x4B] = "CHAR_NAME_RESERVED",
        [0x4C] = "CHAR_NAME_INVALID_APOSTROPHE",
        [0x4D] = "CHAR_NAME_MULTIPLE_APOSTROPHES",
        [0x4E] = "CHAR_NAME_THREE_CONSECUTIVE",
        [0x4F] = "CHAR_NAME_INVALID_SPACE",
        [0x50] = "CHAR_NAME_CONSECUTIVE_SPACES",
        [0x51] = "CHAR_NAME_FAILURE",
        [0x52] = "CHAR_NAME_SUCCESS",
    };

    [Fact]
    public void TheInternalErrorCodes_AreTheOnesTheClientShowsAsInternalError()
    {
        Assert.Equal(0x1C, (byte)SpellCastResult.Error);
        Assert.Equal("ERROR", SpellFailedKeys[0x1C]); // SPELL_FAILED_ERROR = "Internal error"
        Assert.Equal(40, (byte)InventoryResult.IntBagError); // ERR_INTERNAL_BAG_ERROR = "Internal Bag Error"
        Assert.Equal(6u, (uint)MailResult.InternalError); // ERR_MAIL_DATABASE_ERROR = "Internal mail database error."
        Assert.Equal(0x2F, (byte)CharResult.CharCreateError);
        Assert.Equal(0x3A, (byte)CharResult.CharDeleteFailed);
        Assert.Equal(0x41, (byte)CharResult.CharLoginFailed);
    }

    [Fact]
    public void EverySpellCastResultBelowTheUnknownArm_NamesTheClientKeyAtItsValue()
    {
        Assert.Equal(0x91, SpellFailedKeys.Length);
        var mismatches = new List<string>();
        foreach (SpellCastResult result in Enum.GetValues<SpellCastResult>())
        {
            int code = (byte)result;
            if (code >= SpellFailedKeys.Length)
            {
                continue; // Unknown, ClientMax, NotFound, CastOk: server-side conveniences the client has no arm for
            }

            string key = SpellFailedKeys[code];
            if (code == 0x84)
            {
                Assert.Equal("PROSPECT_NEED_MORE", key); // enum name NeedMoreItems, same meaning
                continue;
            }

            if (!string.Equals(key.Replace("_", string.Empty, StringComparison.Ordinal), result.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add($"0x{code:X2}: enum {result}, client SPELL_FAILED_{key}");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void EveryCastResultTheClientHasAnArmFor_IsInTheEnum()
    {
        int[] missing = [.. Enumerable.Range(0, SpellFailedKeys.Length).Where(code => !Enum.IsDefined((SpellCastResult)code))];
        Assert.Empty(missing);
        Assert.Equal(0x08, (byte)SpellCastResult.AutotrackInterrupted);
    }

    [Fact]
    public void EveryCharResult_IsTheClientKeyAtItsValue()
    {
        foreach (CharResult result in Enum.GetValues<CharResult>())
        {
            Assert.True(ResponseKeys.TryGetValue((byte)result, out string? key), $"{result} = 0x{(byte)result:X2} has no client key");
            Assert.Equal(key, UpperSnake(result.ToString()));
        }
    }

    [Fact]
    public void CharNameTail_FollowsTheBinary_NotGtker()
    {
        Assert.Equal(0x50, (byte)CharResult.CharNameConsecutiveSpaces);
        Assert.Equal(0x51, (byte)CharResult.CharNameFailure);
        Assert.Equal(0x52, (byte)CharResult.CharNameSuccess);
    }

    [Fact]
    public void InventoryResults_MatchTheClientsTranslation()
    {
        // The client's 67-arm switch (0x622630): these pairs read the same key the enum name says.
        Assert.Equal(1, (byte)InventoryResult.CantEquipLevelI); // ERR_CANT_EQUIP_LEVEL_I, carries the u32 level
        Assert.Equal(4, (byte)InventoryResult.BagFull); // ERR_BAG_FULL (also the default arm)
        Assert.Equal(23, (byte)InventoryResult.ItemNotFound); // ERR_ITEM_NOT_FOUND
        Assert.Equal(50, (byte)InventoryResult.InventoryFull); // ERR_INV_FULL
        Assert.Equal(51, (byte)InventoryResult.BankFull); // ERR_BANK_FULL
        Assert.Equal(58, (byte)InventoryResult.ObjectIsBusy); // ERR_OBJECT_IS_BUSY
        Assert.Equal(66, (byte)InventoryResult.LootCantLootThatNow); // ERR_LOOT_CANT_LOOT_THAT_NOW, the last arm
    }

    [Fact]
    public void MailAndTransferCodes_MatchTheClientsArms()
    {
        // SMSG_SEND_MAIL_RESULT arms (0x4AD050): 2 to self, 3 money, 4 recipient, 5 faction, 14 trial, 15 cap; the rest is internal.
        Assert.Equal(2u, (uint)MailResult.CannotSendToSelf);
        Assert.Equal(3u, (uint)MailResult.NotEnoughMoney);
        Assert.Equal(4u, (uint)MailResult.RecipientNotFound);
        Assert.Equal(5u, (uint)MailResult.NotYourTeam);
        Assert.Equal(14u, (uint)MailResult.DisabledForTrialAccount);
        Assert.Equal(15u, (uint)MailResult.RecipientCapReached);

        // SMSG_TRANSFER_ABORTED (0x4019A0): 1-3 and 5 have a key, 4 is silent.
        Assert.Equal(1, (byte)TransferAbortReason.MaxPlayers);
        Assert.Equal(2, (byte)TransferAbortReason.NotFound);
        Assert.Equal(3, (byte)TransferAbortReason.TooManyInstances);
        Assert.Equal(4, (byte)TransferAbortReason.Silently);
        Assert.Equal(5, (byte)TransferAbortReason.ZoneInCombat);
    }

    private static string UpperSnake(string pascal)
    {
        var builder = new System.Text.StringBuilder(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(pascal[i]));
        }

        return builder.ToString();
    }
}

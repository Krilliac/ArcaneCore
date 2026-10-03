namespace ArcaneCore.Game.Quests;

/// <summary>vmangos QuestDef.h QuestStatus (also the character_queststatus.status column).</summary>
public enum QuestStatus : byte
{
    None = 0,
    Complete = 1,
    Unavailable = 2,
    Incomplete = 3,
    Available = 4,
    Failed = 5,
}

/// <summary>vmangos QuestDef.h __QuestGiverStatus (SMSG_QUESTGIVER_STATUS byte, quest menu icons).</summary>
public enum DialogStatus : byte
{
    None = 0,
    Unavailable = 1,
    Chat = 2,
    Incomplete = 3,
    RewardRep = 4,
    Available = 5,
    RewardOld = 6,
    Reward2 = 7,
}

/// <summary>vmangos QuestDef.h QuestFlags (quest_template.QuestFlags).</summary>
[Flags]
public enum QuestFlags : uint
{
    None = 0x000,
    StayAlive = 0x001,
    PartyAccept = 0x002,
    Exploration = 0x004,
    Sharable = 0x008,
    Epic = 0x020,
    Raid = 0x040,
    Unk2 = 0x100,
    HiddenRewards = 0x200,
    AutoRewarded = 0x400,
}

/// <summary>vmangos QuestDef.h QuestSpecialFlags (quest_template.SpecialFlags 1|2 plus load-time bits).</summary>
[Flags]
public enum QuestSpecialFlags : uint
{
    None = 0x00,
    Repeatable = 0x01,
    ExplorationOrEvent = 0x02,
    DbAllowed = Repeatable | ExplorationOrEvent,
    Deliver = 0x08,
    SpeakTo = 0x10,
    KillOrCast = 0x20,
    Timed = 0x40,
}

/// <summary>vmangos QuestDef.h INVALIDREASON_* (SMSG_QUESTGIVER_QUEST_INVALID / _FAILED).</summary>
public enum QuestInvalidReason : uint
{
    DontHaveReq = 0,
    LowLevel = 1,
    Reqs = 2,
    InventoryFull = 4,
    WrongRace = 6,
    OnlyOneTimed = 12,
    AlreadyOn = 13,
    DuplicateItem = 17,
    MissingItems = 20,
    NotEnoughMoney = 22,
}

/// <summary>Quest log and template sizes (vmangos QuestDef.h, Player.h).</summary>
public static class QuestConstants
{
    /// <summary>MAX_QUEST_LOG_SIZE.</summary>
    public const int MaxQuestLogSize = 20;

    /// <summary>QUEST_OBJECTIVES_COUNT = QUEST_ITEM_OBJECTIVES_COUNT = QUEST_SOURCE_ITEM_IDS_COUNT.</summary>
    public const int ObjectivesCount = 4;

    /// <summary>QUEST_REWARD_CHOICES_COUNT.</summary>
    public const int RewardChoicesCount = 6;

    /// <summary>QUEST_REWARDS_COUNT.</summary>
    public const int RewardsCount = 4;

    /// <summary>QUEST_EMOTE_COUNT.</summary>
    public const int EmoteCount = 4;

    /// <summary>Update fields per log slot (MAX_QUEST_OFFSET): id, count/state, timer.</summary>
    public const int FieldsPerSlot = 3;

    /// <summary>QUEST_STATE_COMPLETE in byte 3 of the count/state field.</summary>
    public const byte SlotStateComplete = 0x01;

    /// <summary>QUEST_STATE_FAIL in byte 3 of the count/state field.</summary>
    public const byte SlotStateFail = 0x02;

    /// <summary>QUEST_METHOD_DISABLED bit of quest_template.Method (vmangos Quest ctor).</summary>
    public const byte MethodDisabled = 1;

    /// <summary>Item bonding BIND_QUEST_ITEM / BIND_QUEST_ITEM1 (vmangos ItemPrototype.h).</summary>
    public const uint BindQuestItem = 4;

    public const uint BindQuestItem1 = 5;

    /// <summary>BIND_WHEN_PICKED_UP.</summary>
    public const uint BindWhenPickedUp = 1;
}

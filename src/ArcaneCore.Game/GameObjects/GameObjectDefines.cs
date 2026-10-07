namespace ArcaneCore.Game.GameObjects;

// Re-implemented from the 1.12 client enums as documented by vmangos/cmangos SharedDefines.h
// and GameObject.h (GameobjectTypes, GOState, GameObjectFlags, GameObjectDynamicLowFlags,
// LootState) and gtker/wow_messages; values are protocol facts, no code was copied.

/// <summary>GAMEOBJECT_TYPE_* (1.12.1 has 0–25; 26+ are later expansions).</summary>
public enum GameObjectType : uint
{
    Door = 0,
    Button = 1,
    QuestGiver = 2,
    Chest = 3,
    Binder = 4,
    Generic = 5,
    Trap = 6,
    Chair = 7,
    SpellFocus = 8,
    Text = 9,
    Goober = 10,
    Transport = 11,
    AreaDamage = 12,
    Camera = 13,
    MapObject = 14,
    MoTransport = 15,
    DuelArbiter = 16,
    FishingNode = 17,
    SummoningRitual = 18,
    Mailbox = 19,
    AuctionHouse = 20,
    GuardPost = 21,
    SpellCaster = 22,
    MeetingStone = 23,
    FlagStand = 24,
    FishingHole = 25,
}

/// <summary>GOState: GAMEOBJECT_STATE values.</summary>
public enum GameObjectState : uint
{
    /// <summary>GO_STATE_ACTIVE: a door is open, a button pressed.</summary>
    Active = 0,

    /// <summary>GO_STATE_READY: a door is closed, a chest closed.</summary>
    Ready = 1,

    /// <summary>GO_STATE_ACTIVE_ALTERNATIVE.</summary>
    ActiveAlternative = 2,
}

/// <summary>GameObjectFlags: GAMEOBJECT_FLAGS bits.</summary>
[Flags]
public enum GameObjectFlags : uint
{
    None = 0,

    /// <summary>GO_FLAG_IN_USE: being used; cannot be used again until released.</summary>
    InUse = 0x01,

    /// <summary>GO_FLAG_LOCKED: needs a key or spell to open.</summary>
    Locked = 0x02,

    /// <summary>GO_FLAG_INTERACT_COND: usable only under a condition (quest objects).</summary>
    InteractCond = 0x04,

    /// <summary>GO_FLAG_TRANSPORT.</summary>
    Transport = 0x08,

    /// <summary>GO_FLAG_NO_INTERACT: cannot be interacted with at all.</summary>
    NoInteract = 0x10,

    /// <summary>GO_FLAG_NODESPAWN: never despawns (doors, buttons).</summary>
    NoDespawn = 0x20,

    /// <summary>GO_FLAG_TRIGGERED.</summary>
    Triggered = 0x40,
}

/// <summary>GameObjectDynamicLowFlags: GAMEOBJECT_DYN_FLAGS bits, set per viewer.</summary>
[Flags]
public enum GameObjectDynFlags : uint
{
    None = 0,

    /// <summary>GO_DYNFLAG_LO_ACTIVATE: the object can be clicked by this viewer.</summary>
    Activate = 0x01,

    /// <summary>GO_DYNFLAG_LO_ANIMATE.</summary>
    Animate = 0x02,

    /// <summary>GO_DYNFLAG_LO_NO_INTERACT.</summary>
    NoInteract = 0x04,

    /// <summary>GO_DYNFLAG_LO_SPARKLE: quest sparkle for this viewer.</summary>
    Sparkle = 0x08,
}

/// <summary>vmangos LootState: the use/respawn state machine of one object.</summary>
public enum GameObjectLootState
{
    /// <summary>GO_NOT_READY: spawned but not usable yet (traps arming).</summary>
    NotReady,

    /// <summary>GO_READY: usable.</summary>
    Ready,

    /// <summary>GO_ACTIVATED: in use (door open, chest being looted).</summary>
    Activated,

    /// <summary>GO_JUST_DEACTIVATED: used up; despawns at the next update and respawns later.</summary>
    JustDeactivated,
}

/// <summary>Why a use request was refused (logged/tested; the client gets no reply for most).</summary>
public enum GameObjectUseResult
{
    Ok,
    NotFound,
    TooFar,
    NotUsable,
    InUse,
    Locked,
    MissingKey,
    SkillTooLow,
    NeedsQuest,
    OnCooldown,
    Dead,
    Unsupported,

    /// <summary>The object needs line of sight to the user and has none (chairs).</summary>
    LineOfSight,

    /// <summary>The user carries UNIT_FLAG_IMMUNE and the object cannot be used under immunity (vmangos CannotBeUsedUnderImmunity).</summary>
    Immune,
}

/// <summary>LockKeyType (Lock.dbc Type[i]).</summary>
public enum LockKeyType : uint
{
    None = 0,
    Item = 1,
    Skill = 2,
}

/// <summary>LockType (Lock.dbc Index[i] when Type[i] is a skill) — the 1.12 values used by gathering.</summary>
public enum LockType : uint
{
    PickLock = 1,
    Herbalism = 2,
    Mining = 3,
    DisarmTrap = 4,
    Open = 5,
    Treasure = 6,
    CalcifiedElvenGems = 7,
    Close = 8,
    ArmTrap = 9,
    QuickOpen = 10,
    QuickClose = 11,
    OpenTinkering = 12,
    OpenKneeling = 13,
    OpenAttacking = 14,
    Gahzridian = 15,
    Blasting = 16,
    SlowOpen = 17,
    SlowClose = 18,
    Fishing = 19,
}

/// <summary>SkillLine.dbc ids used by locks (vmangos SkillLineType).</summary>
public static class LockSkills
{
    public const uint Lockpicking = 633;
    public const uint Herbalism = 182;
    public const uint Mining = 186;
    public const uint Fishing = 356;

    /// <summary>vmangos SkillByLockType: the profession that opens a skill lock of <paramref name="type"/> (0 = none).</summary>
    public static uint ForLockType(LockType type) => type switch
    {
        LockType.PickLock => Lockpicking,
        LockType.Herbalism => Herbalism,
        LockType.Mining => Mining,
        LockType.Fishing => Fishing,
        _ => 0,
    };
}

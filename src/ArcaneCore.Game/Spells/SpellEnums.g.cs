// Generated from the cited reference headers by a one-off script (values verbatim; names PascalCased). Do not edit values.
namespace ArcaneCore.Game.Spells;

/// <summary>Cast result codes for build 5875 (cmangos-classic SpellDefines.h enum SpellCastResult; identical to gtker/wow_messages SpellCastResult for 1.12). <c>CastOk</c> (0xFF) and <c>NotFound</c> (0xFE) are server-internal and never sent.</summary>
public enum SpellCastResult : byte
{
    /// <summary>You are in combat</summary>
    AffectingCombat = 0x00,
    /// <summary>You are already at full Health.</summary>
    AlreadyAtFullHealth = 0x01,
    /// <summary>You are already at full %s.</summary>
    AlreadyAtFullPower = 0x02,
    /// <summary>That creature is already being tamed</summary>
    AlreadyBeingTamed = 0x03,
    /// <summary>You already control a charmed creature</summary>
    AlreadyHaveCharm = 0x04,
    /// <summary>You already control a summoned creature</summary>
    AlreadyHaveSummon = 0x05,
    /// <summary>Already open</summary>
    AlreadyOpen = 0x06,
    /// <summary>A more powerful spell is already active</summary>
    AuraBounced = 0x07,
    /// <summary>You have no target.</summary>
    BadImplicitTargets = 0x09,
    /// <summary>Invalid target</summary>
    BadTargets = 0x0A,
    /// <summary>Target can't be charmed</summary>
    CantBeCharmed = 0x0B,
    /// <summary>Item cannot be disenchanted</summary>
    CantBeDisenchanted = 0x0C,
    /// <summary>There are no gems in this</summary>
    CantBeProspected = 0x0D,
    /// <summary>Target is tapped</summary>
    CantCastOnTapped = 0x0E,
    /// <summary>You can't start a duel while invisible</summary>
    CantDuelWhileInvisible = 0x0F,
    /// <summary>You can't start a duel while stealthed</summary>
    CantDuelWhileStealthed = 0x10,
    /// <summary>You are too close to enemies</summary>
    CantStealth = 0x11,
    /// <summary>You can't do that yet</summary>
    CasterAurastate = 0x12,
    /// <summary>You are dead</summary>
    CasterDead = 0x13,
    /// <summary>Can't do that while charmed</summary>
    Charmed = 0x14,
    /// <summary>That is already being used</summary>
    ChestInUse = 0x15,
    /// <summary>Can't do that while confused</summary>
    Confused = 0x16,
    /// <summary>Message is hidden/unused</summary>
    DontReport = 0x17,
    /// <summary>Must have the proper item equipped</summary>
    EquippedItem = 0x18,
    /// <summary>Must have a %s equipped</summary>
    EquippedItemClass = 0x19,
    /// <summary>Must have a %s equipped in the main hand</summary>
    EquippedItemClassMainhand = 0x1A,
    /// <summary>Must have a %s equipped in the offhand</summary>
    EquippedItemClassOffhand = 0x1B,
    /// <summary>Internal error</summary>
    Error = 0x1C,
    /// <summary>Fizzled</summary>
    Fizzle = 0x1D,
    /// <summary>Can't do that while fleeing</summary>
    Fleeing = 0x1E,
    /// <summary>That food's level is not high enough for your pet</summary>
    FoodLowlevel = 0x1F,
    /// <summary>Target is too high level</summary>
    Highlevel = 0x20,
    /// <summary>Message is hidden/unused</summary>
    HungerSatiated = 0x21,
    /// <summary>Immune</summary>
    Immune = 0x22,
    /// <summary>Interrupted</summary>
    Interrupted = 0x23,
    /// <summary>Interrupted</summary>
    InterruptedCombat = 0x24,
    /// <summary>Item is already enchanted</summary>
    ItemAlreadyEnchanted = 0x25,
    /// <summary>Item is gone</summary>
    ItemGone = 0x26,
    /// <summary>Tried to enchant an item that didn't exist</summary>
    ItemNotFound = 0x27,
    /// <summary>Item is not ready yet.</summary>
    ItemNotReady = 0x28,
    /// <summary>You are not high enough level</summary>
    LevelRequirement = 0x29,
    /// <summary>Target not in line of sight</summary>
    LineOfSight = 0x2A,
    /// <summary>Target is too low level</summary>
    Lowlevel = 0x2B,
    /// <summary>Skill not high enough</summary>
    LowCastlevel = 0x2C,
    /// <summary>Your weapon hand is empty</summary>
    MainhandEmpty = 0x2D,
    /// <summary>Can't do that while moving</summary>
    Moving = 0x2E,
    /// <summary>Ammo needs to be in the paper doll ammo slot before it can be fired</summary>
    NeedAmmo = 0x2F,
    /// <summary>Requires: %s</summary>
    NeedAmmoPouch = 0x30,
    /// <summary>Requires exotic ammo: %s</summary>
    NeedExoticAmmo = 0x31,
    /// <summary>No path available</summary>
    Nopath = 0x32,
    /// <summary>You must be behind your target</summary>
    NotBehind = 0x33,
    /// <summary>Your cast didn't land in fishable water</summary>
    NotFishable = 0x34,
    /// <summary>You can't use that here</summary>
    NotHere = 0x35,
    /// <summary>You must be in front of your target</summary>
    NotInfront = 0x36,
    /// <summary>You are not in control of your actions</summary>
    NotInControl = 0x37,
    /// <summary>Spell not learned</summary>
    NotKnown = 0x38,
    /// <summary>You are mounted</summary>
    NotMounted = 0x39,
    /// <summary>You are in flight</summary>
    NotOnTaxi = 0x3A,
    /// <summary>You are on a transport</summary>
    NotOnTransport = 0x3B,
    /// <summary>Spell is not ready yet.</summary>
    NotReady = 0x3C,
    /// <summary>You are in shapeshift form</summary>
    NotShapeshift = 0x3D,
    /// <summary>You must be standing to do that</summary>
    NotStanding = 0x3E,
    /// <summary>You can only use this on an object you own</summary>
    NotTradeable = 0x3F,
    /// <summary>Tried to enchant a trade item, but not trading</summary>
    NotTrading = 0x40,
    /// <summary>You have to be unsheathed to do that!</summary>
    NotUnsheathed = 0x41,
    /// <summary>Can't cast as ghost</summary>
    NotWhileGhost = 0x42,
    /// <summary>Out of ammo</summary>
    NoAmmo = 0x43,
    /// <summary>No charges remain</summary>
    NoChargesRemain = 0x44,
    /// <summary>You haven't selected a champion</summary>
    NoChampion = 0x45,
    /// <summary>That ability requires combo points</summary>
    NoComboPoints = 0x46,
    /// <summary>Dueling isn't allowed here</summary>
    NoDueling = 0x47,
    /// <summary>Not enough endurance</summary>
    NoEndurance = 0x48,
    /// <summary>There aren't any fish here</summary>
    NoFish = 0x49,
    /// <summary>Can't use items while shapeshifted</summary>
    NoItemsWhileShapeshifted = 0x4A,
    /// <summary>You can't mount here</summary>
    NoMountsAllowed = 0x4B,
    /// <summary>You do not have a pet</summary>
    NoPet = 0x4C,
    /// <summary>Dynamic pre-defined messages, no args: Not enough mana, Not enough rage, etc</summary>
    NoPower = 0x4D,
    /// <summary>Nothing to dispel</summary>
    NothingToDispel = 0x4E,
    /// <summary>Nothing to steal</summary>
    NothingToSteal = 0x4F,
    /// <summary>Cannot use while swimming</summary>
    OnlyAbovewater = 0x50,
    /// <summary>Can only use during the day</summary>
    OnlyDaytime = 0x51,
    /// <summary>Can only use indoors</summary>
    OnlyIndoors = 0x52,
    /// <summary>Can only use while mounted</summary>
    OnlyMounted = 0x53,
    /// <summary>Can only use during the night</summary>
    OnlyNighttime = 0x54,
    /// <summary>Can only use outside</summary>
    OnlyOutdoors = 0x55,
    /// <summary>Must be in %s</summary>
    OnlyShapeshift = 0x56,
    /// <summary>You must be in stealth mode</summary>
    OnlyStealthed = 0x57,
    /// <summary>Can only use while swimming</summary>
    OnlyUnderwater = 0x58,
    /// <summary>Out of range.</summary>
    OutOfRange = 0x59,
    /// <summary>Can't use that ability while pacified</summary>
    Pacified = 0x5A,
    /// <summary>You are possessed</summary>
    Possessed = 0x5B,
    Reagents = 0x5C,
    /// <summary>You need to be in %s</summary>
    RequiresArea = 0x5D,
    /// <summary>Requires %s</summary>
    RequiresSpellFocus = 0x5E,
    /// <summary>You are unable to move</summary>
    Rooted = 0x5F,
    /// <summary>Can't do that while silenced</summary>
    Silenced = 0x60,
    /// <summary>Another action is in progress</summary>
    SpellInProgress = 0x61,
    /// <summary>You have already learned the spell</summary>
    SpellLearned = 0x62,
    /// <summary>The spell is not available to you</summary>
    SpellUnavailable = 0x63,
    /// <summary>Can't do that while stunned</summary>
    Stunned = 0x64,
    /// <summary>Your target is dead</summary>
    TargetsDead = 0x65,
    /// <summary>Target is in combat</summary>
    TargetAffectingCombat = 0x66,
    /// <summary>You can't do that yet</summary>
    TargetAurastate = 0x67,
    /// <summary>Target is currently dueling</summary>
    TargetDueling = 0x68,
    /// <summary>Target is hostile</summary>
    TargetEnemy = 0x69,
    /// <summary>Target is too enraged to be charmed</summary>
    TargetEnraged = 0x6A,
    /// <summary>Target is friendly</summary>
    TargetFriendly = 0x6B,
    /// <summary>The target can't be in combat</summary>
    TargetInCombat = 0x6C,
    /// <summary>Can't target players</summary>
    TargetIsPlayer = 0x6D,
    /// <summary>Target is alive</summary>
    TargetNotDead = 0x6E,
    /// <summary>Target is not in your party</summary>
    TargetNotInParty = 0x6F,
    /// <summary>Creature must be looted first</summary>
    TargetNotLooted = 0x70,
    /// <summary>Target is not a player</summary>
    TargetNotPlayer = 0x71,
    /// <summary>No pockets to pick</summary>
    TargetNoPockets = 0x72,
    /// <summary>Target has no weapons equipped</summary>
    TargetNoWeapons = 0x73,
    /// <summary>Creature is not skinnable</summary>
    TargetUnskinnable = 0x74,
    /// <summary>Message is hidden/unused</summary>
    ThirstSatiated = 0x75,
    /// <summary>Target too close</summary>
    TooClose = 0x76,
    /// <summary>You have too many of that item already</summary>
    TooManyOfItem = 0x77,
    Totems = 0x78,
    /// <summary>Not enough training points</summary>
    TrainingPoints = 0x79,
    /// <summary>Failed attempt</summary>
    TryAgain = 0x7A,
    /// <summary>Target needs to be behind you</summary>
    UnitNotBehind = 0x7B,
    /// <summary>Target needs to be in front of you</summary>
    UnitNotInfront = 0x7C,
    /// <summary>Your pet doesn't like that food</summary>
    WrongPetFood = 0x7D,
    /// <summary>Can't cast while fatigued</summary>
    NotWhileFatigued = 0x7E,
    /// <summary>Target must be in this instance</summary>
    TargetNotInInstance = 0x7F,
    /// <summary>Can't cast while trading</summary>
    NotWhileTrading = 0x80,
    /// <summary>Target is not in your party or raid group</summary>
    TargetNotInRaid = 0x81,
    /// <summary>Cannot disenchant while looting</summary>
    DisenchantWhileLooting = 0x82,
    /// <summary>Cannot prospect while looting</summary>
    ProspectWhileLooting = 0x83,
    NeedMoreItems = 0x84,
    /// <summary>Target is currently in free-for-all PvP combat</summary>
    TargetFreeforall = 0x85,
    /// <summary>There are no nearby corpses to eat</summary>
    NoEdibleCorpses = 0x86,
    /// <summary>Can only use in battlegrounds</summary>
    OnlyBattlegrounds = 0x87,
    /// <summary>Target is not a ghost</summary>
    TargetNotGhost = 0x88,
    /// <summary>Your pet can't learn any more skills</summary>
    TooManySkills = 0x89,
    /// <summary>You can't use the new item</summary>
    TransformUnusable = 0x8A,
    /// <summary>The weather isn't right for that</summary>
    WrongWeather = 0x8B,
    /// <summary>You can't do that while you are immune</summary>
    DamageImmune = 0x8C,
    /// <summary>Can't do that while %s</summary>
    PreventedByMechanic = 0x8D,
    /// <summary>Maximum play time exceeded</summary>
    PlayTime = 0x8E,
    /// <summary>Your reputation isn't high enough</summary>
    Reputation = 0x8F,
    /// <summary>Your skill is not high enough.  Requires %s (%d).</summary>
    MinSkill = 0x90,
    /// <summary>Generic out of bounds response:  Unknown reason</summary>
    Unknown = 0x91,
    ClientMax = 0x92,
    NotFound = 0xFE,
    /// <summary>custom value, don't must be send to client</summary>
    CastOk = 0xFF,
}

/// <summary>Spell.dbc Effect values (vmangos SpellDefines.h enum SpellEffects; cmangos-classic SpellEffectDefines.h agrees for 0-129).</summary>
public enum SpellEffectName : byte
{
    None = 0x00,
    Instakill = 0x01,
    SchoolDamage = 0x02,
    Dummy = 0x03,
    PortalTeleport = 0x04,
    TeleportUnits = 0x05,
    ApplyAura = 0x06,
    EnvironmentalDamage = 0x07,
    PowerDrain = 0x08,
    HealthLeech = 0x09,
    Heal = 0x0A,
    Bind = 0x0B,
    Portal = 0x0C,
    RitualBase = 0x0D,
    RitualSpecialize = 0x0E,
    RitualActivatePortal = 0x0F,
    QuestComplete = 0x10,
    WeaponDamageNoschool = 0x11,
    Resurrect = 0x12,
    AddExtraAttacks = 0x13,
    Dodge = 0x14,
    Evade = 0x15,
    Parry = 0x16,
    Block = 0x17,
    CreateItem = 0x18,
    Weapon = 0x19,
    Defense = 0x1A,
    PersistentAreaAura = 0x1B,
    Summon = 0x1C,
    Leap = 0x1D,
    Energize = 0x1E,
    WeaponPercentDamage = 0x1F,
    TriggerMissile = 0x20,
    OpenLock = 0x21,
    SummonChangeItem = 0x22,
    ApplyAreaAuraParty = 0x23,
    LearnSpell = 0x24,
    SpellDefense = 0x25,
    Dispel = 0x26,
    Language = 0x27,
    DualWield = 0x28,
    SummonWild = 0x29,
    SummonGuardian = 0x2A,
    TeleportUnitsFaceCaster = 0x2B,
    SkillStep = 0x2C,
    AddHonor = 0x2D,
    Spawn = 0x2E,
    TradeSkill = 0x2F,
    Stealth = 0x30,
    Detect = 0x31,
    TransDoor = 0x32,
    ForceCriticalHit = 0x33,
    GuaranteeHit = 0x34,
    EnchantItem = 0x35,
    EnchantItemTemporary = 0x36,
    Tamecreature = 0x37,
    SummonPet = 0x38,
    LearnPetSpell = 0x39,
    WeaponDamage = 0x3A,
    OpenLockItem = 0x3B,
    Proficiency = 0x3C,
    SendEvent = 0x3D,
    PowerBurn = 0x3E,
    Threat = 0x3F,
    TriggerSpell = 0x40,
    HealthFunnel = 0x41,
    PowerFunnel = 0x42,
    HealMaxHealth = 0x43,
    InterruptCast = 0x44,
    Distract = 0x45,
    Pull = 0x46,
    Pickpocket = 0x47,
    AddFarsight = 0x48,
    SummonPossessed = 0x49,
    SummonTotem = 0x4A,
    HealMechanical = 0x4B,
    SummonObjectWild = 0x4C,
    ScriptEffect = 0x4D,
    Attack = 0x4E,
    Sanctuary = 0x4F,
    AddComboPoints = 0x50,
    CreateHouse = 0x51,
    BindSight = 0x52,
    Duel = 0x53,
    Stuck = 0x54,
    SummonPlayer = 0x55,
    ActivateObject = 0x56,
    SummonTotemSlot1 = 0x57,
    SummonTotemSlot2 = 0x58,
    SummonTotemSlot3 = 0x59,
    SummonTotemSlot4 = 0x5A,
    ThreatAll = 0x5B,
    EnchantHeldItem = 0x5C,
    SummonPhantasm = 0x5D,
    SelfResurrect = 0x5E,
    Skinning = 0x5F,
    Charge = 0x60,
    SummonCritter = 0x61,
    KnockBack = 0x62,
    Disenchant = 0x63,
    Inebriate = 0x64,
    FeedPet = 0x65,
    DismissPet = 0x66,
    Reputation = 0x67,
    SummonObjectSlot1 = 0x68,
    SummonObjectSlot2 = 0x69,
    SummonObjectSlot3 = 0x6A,
    SummonObjectSlot4 = 0x6B,
    DispelMechanic = 0x6C,
    SummonDeadPet = 0x6D,
    DestroyAllTotems = 0x6E,
    DurabilityDamage = 0x6F,
    SummonDemon = 0x70,
    ResurrectNew = 0x71,
    AttackMe = 0x72,
    DurabilityDamagePct = 0x73,
    SkinPlayerCorpse = 0x74,
    SpiritHeal = 0x75,
    Skill = 0x76,
    ApplyAreaAuraPet = 0x77,
    TeleportGraveyard = 0x78,
    NormalizedWeaponDmg = 0x79,
    Unk122 = 0x7A,
    SendTaxi = 0x7B,
    PlayerPull = 0x7C,
    ModifyThreatPercent = 0x7D,
    Unk126 = 0x7E,
    Unk127 = 0x7F,
    ApplyAreaAuraFriend = 0x80,
    ApplyAreaAuraEnemy = 0x81,
    DespawnObject = 0x82,
    Nostalrius = 0x83,
    ApplyAreaAuraRaid = 0x84,
    ApplyAreaAuraOwner = 0x85,
}

/// <summary>Spell.dbc EffectApplyAuraName values (vmangos SpellAuraDefines.h enum AuraType).</summary>
public enum AuraType : byte
{
    None = 0x00,
    BindSight = 0x01,
    ModPossess = 0x02,
    PeriodicDamage = 0x03,
    Dummy = 0x04,
    ModConfuse = 0x05,
    ModCharm = 0x06,
    ModFear = 0x07,
    PeriodicHeal = 0x08,
    ModAttackspeed = 0x09,
    ModThreat = 0x0A,
    ModTaunt = 0x0B,
    ModStun = 0x0C,
    ModDamageDone = 0x0D,
    ModDamageTaken = 0x0E,
    DamageShield = 0x0F,
    ModStealth = 0x10,
    ModStealthDetect = 0x11,
    ModInvisibility = 0x12,
    ModInvisibilityDetection = 0x13,
    /// <summary>20,21 unofficial</summary>
    ObsModHealth = 0x14,
    ObsModMana = 0x15,
    ModResistance = 0x16,
    PeriodicTriggerSpell = 0x17,
    PeriodicEnergize = 0x18,
    ModPacify = 0x19,
    ModRoot = 0x1A,
    ModSilence = 0x1B,
    ReflectSpells = 0x1C,
    ModStat = 0x1D,
    ModSkill = 0x1E,
    ModIncreaseSpeed = 0x1F,
    ModIncreaseMountedSpeed = 0x20,
    ModDecreaseSpeed = 0x21,
    ModIncreaseHealth = 0x22,
    ModIncreaseEnergy = 0x23,
    ModShapeshift = 0x24,
    EffectImmunity = 0x25,
    StateImmunity = 0x26,
    SchoolImmunity = 0x27,
    DamageImmunity = 0x28,
    DispelImmunity = 0x29,
    ProcTriggerSpell = 0x2A,
    ProcTriggerDamage = 0x2B,
    TrackCreatures = 0x2C,
    TrackResources = 0x2D,
    ModParrySkill = 0x2E,
    ModParryPercent = 0x2F,
    ModDodgeSkill = 0x30,
    ModDodgePercent = 0x31,
    ModBlockSkill = 0x32,
    ModBlockPercent = 0x33,
    ModCritPercent = 0x34,
    PeriodicLeech = 0x35,
    ModHitChance = 0x36,
    ModSpellHitChance = 0x37,
    Transform = 0x38,
    ModSpellCritChance = 0x39,
    ModIncreaseSwimSpeed = 0x3A,
    ModDamageDoneCreature = 0x3B,
    ModPacifySilence = 0x3C,
    ModScale = 0x3D,
    PeriodicHealthFunnel = 0x3E,
    PeriodicManaFunnel = 0x3F,
    PeriodicManaLeech = 0x40,
    ModCastingSpeedNotStack = 0x41,
    FeignDeath = 0x42,
    ModDisarm = 0x43,
    ModStalked = 0x44,
    SchoolAbsorb = 0x45,
    ExtraAttacks = 0x46,
    ModSpellCritChanceSchool = 0x47,
    ModPowerCostSchoolPct = 0x48,
    ModPowerCostSchool = 0x49,
    ReflectSpellsSchool = 0x4A,
    ModLanguage = 0x4B,
    FarSight = 0x4C,
    MechanicImmunity = 0x4D,
    Mounted = 0x4E,
    ModDamagePercentDone = 0x4F,
    ModPercentStat = 0x50,
    SplitDamagePct = 0x51,
    WaterBreathing = 0x52,
    ModBaseResistance = 0x53,
    ModRegen = 0x54,
    ModPowerRegen = 0x55,
    ChannelDeathItem = 0x56,
    ModDamagePercentTaken = 0x57,
    ModHealthRegenPercent = 0x58,
    PeriodicDamagePercent = 0x59,
    ModResistChance = 0x5A,
    ModDetectRange = 0x5B,
    PreventsFleeing = 0x5C,
    ModUnattackable = 0x5D,
    InterruptRegen = 0x5E,
    Ghost = 0x5F,
    SpellMagnet = 0x60,
    ManaShield = 0x61,
    ModSkillTalent = 0x62,
    ModAttackPower = 0x63,
    AurasVisible = 0x64,
    ModResistancePct = 0x65,
    ModMeleeAttackPowerVersus = 0x66,
    ModTotalThreat = 0x67,
    WaterWalk = 0x68,
    FeatherFall = 0x69,
    Hover = 0x6A,
    AddFlatModifier = 0x6B,
    AddPctModifier = 0x6C,
    AddTargetTrigger = 0x6D,
    ModPowerRegenPercent = 0x6E,
    AddCasterHitTrigger = 0x6F,
    OverrideClassScripts = 0x70,
    ModRangedDamageTaken = 0x71,
    ModRangedDamageTakenPct = 0x72,
    ModHealing = 0x73,
    ModRegenDuringCombat = 0x74,
    ModMechanicResistance = 0x75,
    ModHealingPct = 0x76,
    SharePetTracking = 0x77,
    Untrackable = 0x78,
    Empathy = 0x79,
    ModOffhandDamagePct = 0x7A,
    ModTargetResistance = 0x7B,
    ModRangedAttackPower = 0x7C,
    ModMeleeDamageTaken = 0x7D,
    ModMeleeDamageTakenPct = 0x7E,
    RangedAttackPowerAttackerBonus = 0x7F,
    ModPossessPet = 0x80,
    ModSpeedAlways = 0x81,
    ModMountedSpeedAlways = 0x82,
    ModRangedAttackPowerVersus = 0x83,
    ModIncreaseEnergyPercent = 0x84,
    ModIncreaseHealthPercent = 0x85,
    ModManaRegenInterrupt = 0x86,
    ModHealingDone = 0x87,
    ModHealingDonePercent = 0x88,
    ModTotalStatPercentage = 0x89,
    ModMeleeHaste = 0x8A,
    ForceReaction = 0x8B,
    ModRangedHaste = 0x8C,
    ModRangedAmmoHaste = 0x8D,
    ModBaseResistancePct = 0x8E,
    ModResistanceExclusive = 0x8F,
    SafeFall = 0x90,
    Charisma = 0x91,
    Persuaded = 0x92,
    MechanicImmunityMask = 0x93,
    RetainComboPoints = 0x94,
    /// <summary>Resist Pushback</summary>
    ResistPushback = 0x95,
    ModShieldBlockvaluePct = 0x96,
    /// <summary>Track Stealthed</summary>
    TrackStealthed = 0x97,
    /// <summary>Mod Detected Range</summary>
    ModDetectedRange = 0x98,
    /// <summary>Split Damage Flat</summary>
    SplitDamageFlat = 0x99,
    /// <summary>Stealth Level Modifier</summary>
    ModStealthLevel = 0x9A,
    /// <summary>Mod Water Breathing</summary>
    ModWaterBreathing = 0x9B,
    /// <summary>Mod Reputation Gain</summary>
    ModReputationGain = 0x9C,
    /// <summary>Mod Pet Damage</summary>
    PetDamageMulti = 0x9D,
    ModShieldBlockvalue = 0x9E,
    NoPvpCredit = 0x9F,
    ModAoeAvoidance = 0xA0,
    ModHealthRegenInCombat = 0xA1,
    PowerBurnMana = 0xA2,
    ModCritDamageBonus = 0xA3,
    Unk164 = 0xA4,
    MeleeAttackPowerAttackerBonus = 0xA5,
    ModAttackPowerPct = 0xA6,
    ModRangedAttackPowerPct = 0xA7,
    ModDamageDoneVersus = 0xA8,
    ModCritPercentVersus = 0xA9,
    DetectAmore = 0xAA,
    ModSpeedNotStack = 0xAB,
    ModMountedSpeedNotStack = 0xAC,
    AllowChampionSpells = 0xAD,
    /// <summary>in 1.12.1 only dependent spirit case</summary>
    ModSpellDamageOfStatPercent = 0xAE,
    ModSpellHealingOfStatPercent = 0xAF,
    SpiritOfRedemption = 0xB0,
    AoeCharm = 0xB1,
    ModDebuffResistance = 0xB2,
    ModAttackerSpellCritChance = 0xB3,
    ModFlatSpellDamageVersus = 0xB4,
    /// <summary>unused - possible flat spell crit damage versus</summary>
    ModFlatSpellCritDamageVersus = 0xB5,
    ModResistanceOfStatPercent = 0xB6,
    ModCriticalThreat = 0xB7,
    ModAttackerMeleeHitChance = 0xB8,
    ModAttackerRangedHitChance = 0xB9,
    ModAttackerSpellHitChance = 0xBA,
    ModAttackerMeleeCritChance = 0xBB,
    ModAttackerRangedCritChance = 0xBC,
    ModRating = 0xBD,
    ModFactionReputationGain = 0xBE,
    UseNormalMovementSpeed = 0xBF,
    /// <summary>Ajoute les auras d'un sort tant que cet aura est actif.</summary>
    AuraSpell = 0xC0,
}

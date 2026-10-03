namespace ArcaneCore.Kernel.Skills;

/// <summary>
/// SkillLine.dbc ids of build 5875. Every constant is vmangos src/game/SharedDefines.h:941-1069
/// (<c>enum SkillType</c>, commented "Data from SpellLine.dbc (1.12.1 checked)"); names follow the
/// reference with the obvious typos fixed (SKILL_SWIMING is <see cref="Swimming"/>).
/// </summary>
public static class SkillIds
{
    public const uint None = 0;
    public const uint Frost = 6;
    public const uint Fire = 8;
    public const uint Arms = 26;
    public const uint Combat = 38;
    public const uint Subtlety = 39;
    public const uint Poisons = 40;
    public const uint Swords = 43;
    public const uint Axes = 44;
    public const uint Bows = 45;
    public const uint Guns = 46;
    public const uint BeastMastery = 50;
    public const uint Survival = 51;
    public const uint Maces = 54;
    public const uint TwoHandedSwords = 55;
    public const uint Holy = 56;
    public const uint Shadow = 78;
    public const uint Defense = 95;
    public const uint LanguageCommon = 98;
    public const uint RacialDwarven = 101;
    public const uint LanguageOrcish = 109;
    public const uint LanguageDwarven = 111;
    public const uint LanguageDarnassian = 113;
    public const uint LanguageTaurahe = 115;
    public const uint DualWield = 118;
    public const uint RacialTauren = 124;
    public const uint RacialOrc = 125;
    public const uint RacialNightElf = 126;
    public const uint FirstAid = 129;
    public const uint FeralCombat = 134;
    public const uint Staves = 136;
    public const uint LanguageThalassian = 137;
    public const uint LanguageDraconic = 138;
    public const uint LanguageDemonTongue = 139;
    public const uint LanguageTitan = 140;
    public const uint LanguageOldTongue = 141;
    public const uint Survival2 = 142;
    public const uint RidingHorse = 148;
    public const uint RidingWolf = 149;
    public const uint RidingTiger = 150;
    public const uint RidingRam = 152;
    public const uint Swimming = 155;
    public const uint TwoHandedMaces = 160;
    public const uint Unarmed = 162;
    public const uint Marksmanship = 163;
    public const uint Blacksmithing = 164;
    public const uint Leatherworking = 165;
    public const uint Alchemy = 171;
    public const uint TwoHandedAxes = 172;
    public const uint Daggers = 173;
    public const uint Thrown = 176;
    public const uint Herbalism = 182;
    public const uint GenericDnd = 183;
    public const uint Retribution = 184;
    public const uint Cooking = 185;
    public const uint Mining = 186;
    public const uint PetImp = 188;
    public const uint PetFelhunter = 189;
    public const uint Tailoring = 197;
    public const uint Engineering = 202;
    public const uint PetSpider = 203;
    public const uint PetVoidwalker = 204;
    public const uint PetSuccubus = 205;
    public const uint PetInfernal = 206;
    public const uint PetDoomguard = 207;
    public const uint PetWolf = 208;
    public const uint PetCat = 209;
    public const uint PetBear = 210;
    public const uint PetBoar = 211;
    public const uint PetCrocolisk = 212;
    public const uint PetCarrionBird = 213;
    public const uint PetCrab = 214;
    public const uint PetGorilla = 215;
    public const uint PetRaptor = 217;
    public const uint PetTallstrider = 218;
    public const uint RacialUndead = 220;
    public const uint Crossbows = 226;
    public const uint Wands = 228;
    public const uint Polearms = 229;
    public const uint PetScorpid = 236;
    public const uint Arcane = 237;
    public const uint PetTurtle = 251;
    public const uint Assassination = 253;
    public const uint Fury = 256;
    public const uint Protection = 257;
    public const uint BeastTraining = 261;
    public const uint Protection2 = 267;
    public const uint PetTalents = 270;
    public const uint PlateMail = 293;
    public const uint LanguageGnomish = 313;
    public const uint LanguageTroll = 315;
    public const uint Enchanting = 333;
    public const uint Demonology = 354;
    public const uint Affliction = 355;
    public const uint Fishing = 356;
    public const uint Enhancement = 373;
    public const uint Restoration = 374;
    public const uint ElementalCombat = 375;
    public const uint Skinning = 393;
    public const uint Mail = 413;
    public const uint Leather = 414;
    public const uint Cloth = 415;
    public const uint Shield = 433;
    public const uint FistWeapons = 473;
    public const uint RidingRaptor = 533;
    public const uint RidingMechanostrider = 553;
    public const uint RidingUndeadHorse = 554;
    public const uint Restoration2 = 573;
    public const uint Balance = 574;
    public const uint Destruction = 593;
    public const uint Holy2 = 594;
    public const uint Discipline = 613;
    public const uint Lockpicking = 633;
    public const uint PetBat = 653;
    public const uint PetHyena = 654;
    public const uint PetOwl = 655;
    public const uint PetWindSerpent = 656;
    public const uint LanguageGutterspeak = 673;
    public const uint RidingKodo = 713;
    public const uint RacialTroll = 733;
    public const uint RacialGnome = 753;
    public const uint RacialHuman = 754;
    public const uint PetEventRc = 758;
    public const uint Riding = 762;

    /// <summary>One past the highest 1.12.1 skill id (vmangos SharedDefines.h:1070 <c>MAX_SKILL_TYPE 763</c>).</summary>
    public const uint MaxSkillType = 763;
}

/// <summary>SkillLine.dbc categoryId values (vmangos SharedDefines.h:1102-1112 <c>enum SkillCategory</c>).</summary>
public static class SkillCategories
{
    public const int Attributes = 5;
    public const int Weapon = 6;
    public const int Class = 7;
    public const int Armor = 8;

    /// <summary>Secondary professions.</summary>
    public const int Secondary = 9;
    public const int Languages = 10;

    /// <summary>Primary professions.</summary>
    public const int Profession = 11;
    public const int Generic = 12;
}

/// <summary>
/// SkillRaceClassInfo.dbc flag bits (vmangos Database/DBCEnums.h:172-181
/// <c>enum SkillRaceClassInfoFlags</c>).
/// </summary>
public static class SkillRaceClassFlags
{
    public const uint NoSkillUpMessage = 0x2;
    public const uint AlwaysMaxValue = 0x10;

    /// <summary>The player may unlearn the skill (CMSG_UNLEARN_SKILL, vmangos SkillHandler.cpp:59-70).</summary>
    public const uint Unlearnable = 0x20;
    public const uint IncludeInSort = 0x80;

    /// <summary>The skill cannot be taught by a trainer (vmangos Objects/Player.cpp:19548 tests it as <c>ABILITY_SKILL_NONTRAINABLE</c>, DBCEnums.h:159).</summary>
    public const uint NotTrainable = 0x100;

    /// <summary>The client displays the skill with value 1 (a clientside flag; the real value can differ).</summary>
    public const uint MonoValue = 0x400;
}

/// <summary>
/// How a skill's maximum is derived for a race and class (vmangos ObjectMgr.h:391-397
/// <c>enum SkillRangeType</c>).
/// </summary>
public enum SkillRangeType
{
    /// <summary>300..300.</summary>
    Language = 0,

    /// <summary>1..max skill for the player's level.</summary>
    Level = 1,

    /// <summary>1..1, the grey "monolith" bar.</summary>
    Mono = 2,

    /// <summary>1..the skill of the known rank (SkillTiers.dbc).</summary>
    Rank = 3,

    /// <summary>0..0, always.</summary>
    None = 4,
}

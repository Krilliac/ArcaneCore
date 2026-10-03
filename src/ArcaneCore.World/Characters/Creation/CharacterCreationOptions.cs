namespace ArcaneCore.World.Characters.Creation;

/// <summary>
/// Whether the retail character-creation rules apply. <see cref="Legacy"/> keeps the permissive
/// pre-lane behaviour (any race/class pair that has a start row, no team or disable checks, no
/// start level or money) for development hosts; <see cref="Retail"/> (the default) follows vmangos.
/// </summary>
public enum CharacterCreationMode
{
    Retail,
    Legacy,
}

/// <summary>vmangos RealmType (World.h:629-634), the GameType option.</summary>
public enum RealmGameType
{
    Normal = 0,
    PvP = 1,
    Normal2 = 4,
    Rp = 6,
    RpPvP = 8,
    FfaPvP = 16,
}

/// <summary>
/// Character-creation options (configuration section <see cref="SectionName"/>), named after the
/// mangosd.conf keys of vmangos (mangosd.conf.dist.in:909-1066, 1362-1366, 2487) with retail
/// defaults. Every value is read through the clamping accessors that mirror World.cpp:597-679.
/// </summary>
public sealed class CharacterCreationOptions
{
    public const string SectionName = "CharacterCreation";

    /// <summary>Retail (default) or Legacy (the old permissive behaviour).</summary>
    public CharacterCreationMode Mode { get; set; } = CharacterCreationMode.Retail;

    /// <summary>
    /// vmangos CharactersCreatingDisabled (World.cpp:629): bit 0 stops Alliance, bit 1 stops Horde
    /// creations for ordinary players (CharacterHandler.cpp:193-216). Default 0.
    /// </summary>
    public uint CharactersCreatingDisabled { get; set; }

    /// <summary>vmangos GameType (World.cpp:600): PvP, RP-PvP and FFA realms are "PvP realms" (World.h:802).</summary>
    public RealmGameType GameType { get; set; } = RealmGameType.Normal;

    /// <summary>vmangos AllowTwoSide.Accounts (default off): one account may hold both factions on a PvP realm.</summary>
    public bool AllowTwoSideAccounts { get; set; }

    /// <summary>vmangos RealmZone (RealmZone.h:23-61), selects the name alphabet with StrictPlayerNames bit 2. The vmangos default is 1 (development).</summary>
    public int RealmZone { get; set; } = 1;

    /// <summary>vmangos StrictPlayerNames (0 any one script, bit 1 basic Latin, bit 2 realm zone script).</summary>
    public uint StrictPlayerNames { get; set; }

    /// <summary>vmangos MinPlayerName (default 2), clamped to 2..12 (World.cpp:621).</summary>
    public int MinPlayerName { get; set; } = CharacterNames.MinLength;

    /// <summary>vmangos StartPlayerLevel (default 1), clamped to 1..MaxPlayerLevel (World.cpp:673).</summary>
    public int StartPlayerLevel { get; set; } = 1;

    /// <summary>vmangos GM.StartLevel (default 1), clamped to StartPlayerLevel..MaxPlayerLevel; used for accounts above player security (World.cpp:679, Player.cpp:16217).</summary>
    public int GmStartLevel { get; set; } = 1;

    /// <summary>
    /// Give new characters their <c>playercreateinfo_action</c> bar (vmangos MasterPlayer::Create). False leaves it empty.
    /// </summary>
    public bool StartActions { get; set; } = true;

    /// <summary>vmangos StartPlayerMoney in copper (default 0), clamped to 0..MAX_MONEY_AMOUNT (World.cpp:674).</summary>
    public long StartPlayerMoney { get; set; }

    /// <summary>The effective MinPlayerName (World.cpp:621: min 2, max MAX_PLAYER_NAME).</summary>
    public int EffectiveMinPlayerName => Math.Clamp(MinPlayerName, CharacterNames.MinLength, CharacterNames.MaxLength);

    /// <summary>vmangos IsPvPRealm (World.h:802).</summary>
    public bool IsPvPRealm => GameType is RealmGameType.PvP or RealmGameType.RpPvP or RealmGameType.FfaPvP;

    /// <summary>The effective CharactersPerRealm: clamped to 1..10 (World.cpp:633).</summary>
    public static int EffectiveCharactersPerRealm(int configured) => Math.Clamp(configured, 1, 10);

    /// <summary>vmangos PLAYER_STRONG_MAX_LEVEL (DBCEnums.h:38), the ceiling of GM.StartLevel.</summary>
    public const int MaxLevel = 255;

    /// <summary>The start level for an account of the given security (World.cpp:673-679).</summary>
    public int StartLevelFor(bool staff, int maxPlayerLevel)
    {
        int start = Math.Clamp(StartPlayerLevel, 1, Math.Max(1, maxPlayerLevel));
        return staff ? Math.Clamp(GmStartLevel, start, MaxLevel) : start;
    }

    /// <summary>The start money (vmangos MAX_MONEY_AMOUNT = 0x7FFFFFFF - 1, Player.h:656).</summary>
    public uint StartMoney => (uint)Math.Clamp(StartPlayerMoney, 0, MaxMoneyAmount);

    /// <summary>vmangos MAX_MONEY_AMOUNT (Player.h:656).</summary>
    public const long MaxMoneyAmount = 0x7FFFFFFF - 1;
}

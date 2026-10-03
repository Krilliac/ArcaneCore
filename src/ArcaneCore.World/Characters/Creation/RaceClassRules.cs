using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Characters.Creation;

/// <summary>
/// Which races and classes exist and may be created, from the vanilla masks of vmangos
/// SharedDefines.h:60-110 (the answer a ChrRaces/ChrClasses DBC gives for a stock 1.12 client; no
/// such DBC is read here). Race 9 (goblin) is a DBC row flagged NOT_PLAYABLE; classes 6 and 10 do
/// not exist.
/// </summary>
public static class RaceClassRules
{
    /// <summary>RACEMASK_ALL_PLAYABLE (SharedDefines.h:72).</summary>
    private const uint PlayableRaces = 0xFF;

    /// <summary>RACEMASK_ALLIANCE: human, dwarf, night elf, gnome (SharedDefines.h:79).</summary>
    private const uint AllianceRaces = (1u << 0) | (1u << 2) | (1u << 3) | (1u << 6);

    /// <summary>CLASSMASK_ALL_PLAYABLE (SharedDefines.h:101).</summary>
    private const uint PlayableClasses = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 6) | (1u << 7) | (1u << 8) | (1u << 10);

    /// <summary>RACE_GOBLIN, the only race row besides the eight playable ones.</summary>
    private const byte Goblin = 9;

    /// <summary>A ChrRaces row exists (vmangos sChrRacesStore.LookupEntry).</summary>
    public static bool RaceExists(byte race) => IsPlayableRace(race) || race == Goblin;

    /// <summary>The race row is not flagged CHRRACES_FLAGS_NOT_PLAYABLE.</summary>
    public static bool IsPlayableRace(byte race) => race is >= 1 and <= 8 && (PlayableRaces & (1u << (race - 1))) != 0;

    /// <summary>A ChrClasses row exists (the playable classes are exactly the stored ones).</summary>
    public static bool ClassExists(byte cls) => cls is >= 1 and <= 11 && (PlayableClasses & (1u << (cls - 1))) != 0;

    /// <summary>vmangos Player::TeamForRace for the playable races.</summary>
    public static Team TeamForRace(byte race) => race is >= 1 and <= 8 && (AllianceRaces & (1u << (race - 1))) != 0 ? Team.Alliance : Team.Horde;
}

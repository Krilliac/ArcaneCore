namespace ArcaneCore.Game.Entities;

/// <summary>The faction rules every layer shares: which team a race fights for and the array index of a team.</summary>
public static class TeamRules
{
    /// <summary>vmangos Player::TeamForRace: human, dwarf, night elf and gnome are Alliance; every other value is Horde.</summary>
    public static Team TeamOf(Race race) => race is Race.Human or Race.Dwarf or Race.NightElf or Race.Gnome ? Team.Alliance : Team.Horde;

    /// <summary>The team index used by per-team arrays (vmangos <c>BG_TEAM_ALLIANCE</c> 0, <c>BG_TEAM_HORDE</c> 1).</summary>
    public static int Index(this Team team) => team == Team.Alliance ? 0 : 1;
}

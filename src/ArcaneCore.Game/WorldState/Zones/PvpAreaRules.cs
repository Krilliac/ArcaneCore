using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.WorldState.Zones;

/// <summary>
/// What kind of PvP realm this is (vmangos <c>sWorld.IsPvPRealm()</c> / <c>IsFFAPvPRealm()</c>,
/// World.h:802-803). The realm list's own type is the realm daemon's business; the world daemon
/// has no such setting, so it is configured here (<c>World:Zones:PvpRealmMode</c>).
/// </summary>
public enum PvpRealmMode
{
    /// <summary>A normal (PvE) realm.</summary>
    Normal,

    /// <summary>A PvP realm (vmangos REALM_TYPE_PVP / RPPVP).</summary>
    Pvp,

    /// <summary>A free-for-all PvP realm (REALM_TYPE_FFA_PVP): a PvP realm where PvP means FFA.</summary>
    FfaPvp,
}

/// <summary>The "in a PvP-enforced area" flag per player (vmangos <c>pvpInfo.inPvPEnforcedArea</c>).</summary>
public static class PvpAreaState
{
    private sealed class Box
    {
        public bool Value;
        public bool WarModeExempt;
        public bool WarModeKnown;
    }

    private static readonly ConditionalWeakTable<Player, Box> s_states = new();

    /// <summary>Whether the player is in an area where PvP is enforced; read by the PvP flag timer.</summary>
    public static bool IsInEnforcedArea(Player player) => s_states.TryGetValue(player, out Box? box) && box.Value;

    internal static void Set(Player player, bool value) => s_states.GetValue(player, _ => new Box()).Value = value;

    /// <summary>
    /// Whether War Mode (<see cref="WarModeOptions"/>) leaves the player alone here: the player stands in an exempt area
    /// (<see cref="WarModeAreas.IsExempt"/>, kept by <see cref="PvpAreaTracker"/> on every zone and area change) or on a battleground map,
    /// where nothing changes. World thread.
    /// </summary>
    public static bool IsWarModeExempt(Player player)
        => player.Map?.Template?.IsBattleground == true || (s_states.TryGetValue(player, out Box? box) && box.WarModeExempt);

    /// <summary>
    /// Whether <see cref="PvpAreaTracker"/> has placed the player yet. War Mode forces no flag before that: a player who logs in inside a capital
    /// would otherwise be flagged by the first combat tick, which can run before the first zone update, and keep the flag for five minutes.
    /// </summary>
    public static bool IsWarModeKnown(Player player) => s_states.TryGetValue(player, out Box? box) && box.WarModeKnown;

    internal static void SetWarModeExempt(Player player, bool value)
    {
        Box box = s_states.GetValue(player, _ => new Box());
        box.WarModeExempt = value;
        box.WarModeKnown = true;
    }
}

/// <summary>
/// The areas War Mode never force-flags. An area is exempt when its flags (or those of its zone) include Capital or City and its owning team is
/// the player's own team or no team (a neutral town such as Booty Bay); an enemy-faction capital is not exempt. An arena is exempt too, so
/// arenas keep their own rules. The sanctuary area flag is not used: it does not exist in 1.12.1 (vmangos <c>Database/DBCEnums.h:55-64</c> lists
/// AreaFlags only up to 0x200, mangoszero <c>Server/DBCEnums.h:51-60</c> the same), so there is no value to verify it against.
/// </summary>
public static class WarModeAreas
{
    /// <summary>
    /// Whether a player of <paramref name="playerTeam"/> in <paramref name="area"/> (inside <paramref name="zone"/>, which may be the same entry) is in an exempt area.
    /// An entry's own team wins; one without a team takes its zone's.
    /// </summary>
    public static bool IsExempt(AreaTemplate? area, AreaTemplate? zone, Team playerTeam)
        => IsExemptEntry(area, zone, playerTeam) || (!ReferenceEquals(area, zone) && IsExemptEntry(zone, zone, playerTeam));

    private static bool IsExemptEntry(AreaTemplate? entry, AreaTemplate? zone, Team playerTeam)
    {
        if (entry is null)
        {
            return false;
        }

        var flags = (AreaFlags)entry.Flags;
        if ((flags & AreaFlags.Arena) != 0)
        {
            return true;
        }

        if ((flags & (AreaFlags.Capital | AreaFlags.City)) == 0)
        {
            return false;
        }

        uint team = entry.Team != AreaTeams.None ? entry.Team : zone?.Team ?? AreaTeams.None;
        return team == AreaTeams.None || team == (playerTeam == Team.Alliance ? AreaTeams.Ally : AreaTeams.Horde);
    }
}

/// <summary>vmangos <c>Player::UpdateZone</c>' PvP part (Player.cpp:6612-6636) and the PvP part of <c>UpdateArea</c> (:6566-6580).</summary>
public static class PvpAreaRules
{
    /// <summary>
    /// The zone-entry truth table (Player.cpp:6612-6629): a zone owned by the Alliance or Horde enforces PvP on
    /// players of the other team on a PvP realm, or in its capital; an unowned zone enforces it on a PvP realm or
    /// in a battleground; any other team value never does.
    /// </summary>
    public static bool IsEnforced(uint zoneTeam, AreaFlags zoneFlags, Team playerTeam, PvpRealmMode realm, bool inBattleground)
    {
        bool pvpRealm = realm != PvpRealmMode.Normal;
        bool capital = (zoneFlags & AreaFlags.Capital) != 0;
        return zoneTeam switch
        {
            AreaTeams.Ally => playerTeam != Team.Alliance && (pvpRealm || capital),
            AreaTeams.Horde => playerTeam != Team.Horde && (pvpRealm || capital),
            AreaTeams.None => pvpRealm || inBattleground,
            _ => false, // 6 in fact
        };
    }
}

/// <summary>
/// Applies the zone's PvP rules when a player enters a zone or an area (vmangos Player.cpp:6566-6580,
/// :6612-6636): the enforced-area flag, <c>UpdatePvP(true)</c> on entering a hostile area (the flag
/// timer then stays frozen while inside, <see cref="MapCombat"/>), FFA on FFA realms and in arenas.
/// A battleground is a player on a battleground map (vmangos <c>InBattleGround</c>: the map is open only to its match's players).
/// Not delivered: taxi flights (no taxi system), capture points
/// and flag carriers (the other two terms of the vmangos timer freeze), and the capital rest type, which the
/// rest lane takes from <see cref="PvpAreaState.IsInEnforcedArea"/> (<c>CAPITAL &amp;&amp; !enforced</c>, :6639).
/// </summary>
public sealed class PvpAreaTracker(WorldStateHooks hooks) : IPlayerLocationListener
{
    public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
    {
        if (zoneEntry is null)
        {
            PvpAreaState.SetWarModeExempt(player, false); // a place the zone table cannot name is no exempt area
            return; // vmangos returned before this point for an unknown zone
        }

        PvpRealmMode realm = hooks.Zones.PvpRealmMode;
        bool enforced = PvpAreaRules.IsEnforced(zoneEntry.Team, (AreaFlags)zoneEntry.Flags, player.Team, realm, inBattleground: player.Map?.Template?.IsBattleground == true);
        PvpAreaState.Set(player, enforced);
        PvpAreaState.SetWarModeExempt(player, WarModeAreas.IsExempt(hooks.Locator.Find(newArea) ?? zoneEntry, zoneEntry, player.Team));

        if (enforced)
        {
            MapCombat.UpdatePvp(player, true); // in a hostile area (no taxi flights yet)
        }

        // on a FFA realm, FFA is toggled together with the PvP flag
        if (realm == PvpRealmMode.FfaPvp)
        {
            bool pvp = (player.UnitFlags & UnitFlags.Pvp) != 0;
            SetFfaPvp(player, pvp && !player.IsGameMaster && (player.Flags & PlayerFlags.Resting) == 0);
        }
    }

    public void OnAreaChanged(Player player, uint oldArea, uint newArea)
    {
        // FFA flags are area and not zone dependent (Player::UpdateArea)
        AreaTemplate? area = hooks.Locator.Find(newArea);
        AreaTemplate? zone = area is null ? null : area.IsZone ? area : hooks.Locator.Find(area.ZoneId);
        PvpAreaState.SetWarModeExempt(player, WarModeAreas.IsExempt(area, zone, player.Team));
        if (area is not null && (area.Flags & (uint)AreaFlags.Arena) != 0)
        {
            if (!player.IsGameMaster)
            {
                SetFfaPvp(player, true);
            }
        }
        else if ((player.Flags & PlayerFlags.FfaPvp) != 0 && hooks.Zones.PvpRealmMode != PvpRealmMode.FfaPvp)
        {
            // remove the FFA flag only if this is not an FFA realm
            SetFfaPvp(player, false);
        }
    }

    private static void SetFfaPvp(Player player, bool state)
        => player.Flags = state ? player.Flags | PlayerFlags.FfaPvp : player.Flags & ~PlayerFlags.FfaPvp;
}

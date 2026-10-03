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
    }

    private static readonly ConditionalWeakTable<Player, Box> s_states = new();

    /// <summary>Whether the player is in an area where PvP is enforced; read by the PvP flag timer.</summary>
    public static bool IsInEnforcedArea(Player player) => s_states.TryGetValue(player, out Box? box) && box.Value;

    internal static void Set(Player player, bool value) => s_states.GetValue(player, _ => new Box()).Value = value;
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
/// Not delivered: battlegrounds (no <c>InBattleGround</c>), taxi flights (no taxi system), capture points
/// and flag carriers (the other two terms of the vmangos timer freeze), and the capital rest type, which the
/// rest lane takes from <see cref="PvpAreaState.IsInEnforcedArea"/> (<c>CAPITAL &amp;&amp; !enforced</c>, :6639).
/// </summary>
public sealed class PvpAreaTracker(WorldStateHooks hooks) : IPlayerLocationListener
{
    public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
    {
        if (zoneEntry is null)
        {
            return; // vmangos returned before this point for an unknown zone
        }

        PvpRealmMode realm = hooks.Zones.PvpRealmMode;
        bool enforced = PvpAreaRules.IsEnforced(zoneEntry.Team, (AreaFlags)zoneEntry.Flags, player.Team, realm, inBattleground: false);
        PvpAreaState.Set(player, enforced);

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

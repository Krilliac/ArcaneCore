using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.States;

namespace ArcaneCore.Game.OutdoorPvP;

/// <summary>
/// An outdoor PvP zone script (vmangos <c>ZoneScript</c> + <c>OutdoorPvP</c>, Maps/ZoneScript.cpp): the zones it owns, the players
/// in them by team, its capture points and its world states. The world feature routes zone changes, area triggers, flag drops and the
/// update tick here. World thread.
/// </summary>
public abstract class OutdoorPvPZoneScript
{
    private readonly HashSet<ObjectGuid>[] _players = [[], []];
    private readonly List<CapturePoint> _capturePoints = [];
    private uint _updateTimer;

    protected OutdoorPvPZoneScript(IOutdoorPvPHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Host = host;
    }

    public IOutdoorPvPHost Host { get; }

    /// <summary>The map the script runs on (vmangos <c>ZoneScript_Script::GetMapId</c>).</summary>
    public abstract uint MapId { get; }

    /// <summary>The zones registered by <c>SetupZoneScript</c> (vmangos <c>RegisterZone</c>).</summary>
    public abstract IReadOnlyList<uint> Zones { get; }

    public IReadOnlyList<CapturePoint> CapturePoints => _capturePoints;

    /// <summary>True when the last update changed an objective's state (vmangos <c>m_objective_changed</c>).</summary>
    protected bool ObjectiveChanged { get; private set; }

    public IReadOnlyCollection<ObjectGuid> Players(Team team) => _players[CapturePoint.TeamIndex(team)];

    public bool HasPlayer(ObjectGuid guid) => _players[0].Contains(guid) || _players[1].Contains(guid);

    /// <summary>vmangos <c>SetupZoneScript</c>: create the capture points and the zone's objects.</summary>
    public abstract void Setup();

    protected void AddCapturePoint(CapturePoint point) => _capturePoints.Add(point);

    /// <summary>vmangos <c>OutdoorPvP::IsInsideObjective</c>.</summary>
    public bool IsInsideObjective(ObjectGuid guid) => _capturePoints.Any(p => p.IsInside(guid));

    /// <summary>vmangos <c>ZoneScript::OnPlayerEnter</c>.</summary>
    public virtual void OnPlayerEnter(OutdoorPvPPlayer player) => _players[CapturePoint.TeamIndex(player.Team)].Add(player.Guid);

    /// <summary>
    /// vmangos <c>OutdoorPvP::OnPlayerLeave</c> + <c>ZoneScript::OnPlayerLeave</c> (ZoneScript.cpp:278-284, 593-600): the capture
    /// points let go, then the zone's world states are cleared unless the player is logging out.
    /// </summary>
    public virtual void OnPlayerLeave(OutdoorPvPPlayer player, bool loggingOut)
    {
        foreach (CapturePoint point in _capturePoints)
        {
            if (point.IsInside(player))
            {
                point.HandlePlayerLeave(player);
            }
        }

        if (!loggingOut)
        {
            SendRemoveWorldStates(player.Guid);
        }

        _players[CapturePoint.TeamIndex(player.Team)].Remove(player.Guid);
    }

    /// <summary>
    /// vmangos <c>ZoneScriptMgr::Update</c> (ZoneScriptMgr.cpp:116-127) and <c>OutdoorPvP::Update</c> (ZoneScript.cpp:291-299): the
    /// capture points run once the timer passes the one-second interval, with the whole elapsed time.
    /// </summary>
    public void Tick(uint diffMs)
    {
        _updateTimer += diffMs;
        if (_updateTimer <= CapturePoint.UpdateIntervalMs)
        {
            return;
        }

        uint elapsed = _updateTimer;
        _updateTimer = 0;
        Update(elapsed);
    }

    /// <summary>vmangos <c>OutdoorPvP::Update</c>; scripts override and call the base first when they need <see cref="ObjectiveChanged"/>.</summary>
    public virtual void Update(uint diffMs)
    {
        ObjectiveChanged = false;
        foreach (CapturePoint point in _capturePoints)
        {
            if (point.Update(diffMs))
            {
                ObjectiveChanged = true;
            }
        }
    }

    public abstract void FillInitialWorldStates(List<WorldStatePair> states);

    public abstract void SendRemoveWorldStates(ObjectGuid player);

    /// <summary>vmangos <c>HandleAreaTrigger</c>: true when the script consumed the trigger.</summary>
    public virtual bool HandleAreaTrigger(OutdoorPvPPlayer player, uint triggerId) => false;

    /// <summary>vmangos <c>HandleDropFlag</c>: true when the script handled the drop.</summary>
    public virtual bool HandleDropFlag(OutdoorPvPPlayer player, uint spellId) => false;

    /// <summary>vmangos <c>ZoneScript::SendUpdateWorldState</c>: the state to every player of the script's zones.</summary>
    public void SendUpdateWorldState(uint state, uint value)
    {
        foreach (HashSet<ObjectGuid> team in _players)
        {
            foreach (ObjectGuid guid in team)
            {
                Host.SendWorldState(guid, state, value);
            }
        }
    }

    /// <summary>vmangos <c>ZoneScript::TeamApplyBuff</c> (ZoneScript.cpp:629-649): cast on one team, remove from the other.</summary>
    public void TeamApplyBuff(Team team, uint spellId)
    {
        foreach (ObjectGuid guid in Players(team).ToArray())
        {
            Host.CastOnSelf(guid, spellId);
        }

        foreach (ObjectGuid guid in Players(team == Team.Alliance ? Team.Horde : Team.Alliance).ToArray())
        {
            Host.RemoveAura(guid, spellId);
        }
    }

    protected static void Fill(List<WorldStatePair> states, uint state, uint value) => states.Add(new WorldStatePair(state, (int)value));

    protected static void Fill(List<WorldStatePair> states, uint state, bool value) => states.Add(new WorldStatePair(state, value ? 1 : 0));
}

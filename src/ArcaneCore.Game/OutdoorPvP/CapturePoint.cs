using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.OutdoorPvP;

/// <summary>The slider states of a capture point (vmangos ZoneScript.h:41-50 <c>ObjectiveStates</c>).</summary>
public enum ObjectiveState
{
    Neutral = 0,
    AllianceContested = 1,
    HordeContested = 2,
    AllianceProgressing = 3,
    HordeProgressing = 4,
    Alliance = 5,
    Horde = 6,
}

/// <summary>Who holds a capture point's slider (vmangos <c>TeamId</c>: TEAM_ALLIANCE, TEAM_HORDE, TEAM_NEUTRAL).</summary>
public enum CaptureTeam
{
    Neutral,
    Alliance,
    Horde,
}

/// <summary>
/// One capture point (vmangos <c>OPvPCapturePoint</c>, ZoneScript.cpp:32-454): the players inside its radius and the slider they move.
/// <para>The slider runs from +max (Alliance, blue) to -max (Horde, red); the grey band is |value| &lt; min where
/// min = max * neutralPercent / 100. Each update moves it by (alliance - horde) * diff / 1000, capped at max / minTime per ms.</para>
/// </summary>
public abstract class CapturePoint
{
    /// <summary>vmangos OUTDOORPVP_OBJECTIVE_UPDATE_INTERVAL (ZoneScriptMgr.h:21).</summary>
    public const uint UpdateIntervalMs = 1000;

    private readonly HashSet<ObjectGuid>[] _active = [[], []];

    protected CapturePoint(OutdoorPvPZoneScript zone, OutdoorPvPSpawn point, CapturePointTemplate template)
    {
        ArgumentNullException.ThrowIfNull(zone);
        Zone = zone;
        Point = point;
        Template = template;
        MaxValue = template.MaxTime;
        MaxSpeed = MaxValue / (template.MinTime != 0 ? template.MinTime : 60);
        NeutralValuePct = template.NeutralPercent;
        MinValue = MaxValue * NeutralValuePct / 100f;
    }

    public OutdoorPvPZoneScript Zone { get; }

    protected IOutdoorPvPHost Host => Zone.Host;

    public OutdoorPvPSpawn Point { get; }

    public CapturePointTemplate Template { get; }

    /// <summary>The capture point object itself (vmangos <c>m_capturePoint</c>); null until <see cref="Spawn"/> succeeded.</summary>
    public ObjectGuid? Object { get; private set; }

    public float MaxValue { get; }

    public float MinValue { get; }

    public float MaxSpeed { get; }

    public uint NeutralValuePct { get; }

    public float Value { get; protected set; }

    public ObjectiveState State { get; protected set; } = ObjectiveState.Neutral;

    public ObjectiveState OldState { get; protected set; } = ObjectiveState.Neutral;

    public CaptureTeam Owner { get; private set; } = CaptureTeam.Neutral;

    public uint ValuePct { get; private set; }

    public int FactDiff { get; private set; }

    public IReadOnlyCollection<ObjectGuid> ActiveAlliance => _active[0];

    public IReadOnlyCollection<ObjectGuid> ActiveHorde => _active[1];

    /// <summary>vmangos <c>SetCapturePointData</c>: summon the capture point object.</summary>
    public bool Spawn()
    {
        Object = Host.SummonObject(Point);
        return Object is not null;
    }

    public bool IsInside(OutdoorPvPPlayer player) => _active[TeamIndex(player.Team)].Contains(player.Guid);

    public bool IsInside(ObjectGuid guid) => _active[0].Contains(guid) || _active[1].Contains(guid);

    /// <summary>vmangos <c>OPvPCapturePoint::HandlePlayerEnter</c> (ZoneScript.cpp:44-72): show the slider, position last.</summary>
    public virtual bool HandlePlayerEnter(OutdoorPvPPlayer player)
    {
        Host.SendWorldState(player.Guid, Template.WorldStateDisplay, 1);
        Host.SendWorldState(player.Guid, Template.WorldStateNeutral, NeutralValuePct);
        Host.SendWorldState(player.Guid, Template.WorldStatePosition, ValuePct);
        return _active[TeamIndex(player.Team)].Add(player.Guid);
    }

    /// <summary>vmangos <c>OPvPCapturePoint::HandlePlayerLeave</c>: hide the slider.</summary>
    public virtual void HandlePlayerLeave(OutdoorPvPPlayer player)
    {
        Host.SendWorldState(player.Guid, Template.WorldStateDisplay, 0);
        _active[TeamIndex(player.Team)].Remove(player.Guid);
    }

    /// <summary>vmangos <c>OPvPCapturePoint::SendChangePhase</c>: the slider position to everyone inside.</summary>
    protected virtual void SendChangePhase() => SendToActive(Template.WorldStatePosition, ValuePct);

    /// <summary>vmangos <c>ChangeState</c>: the script's reaction to a new slider state.</summary>
    protected abstract void ChangeState();

    /// <summary>vmangos <c>ChangeTeam</c>: nothing by default.</summary>
    protected virtual void ChangeTeam(CaptureTeam oldTeam)
    {
    }

    public abstract void FillInitialWorldStates(List<WorldState.States.WorldStatePair> states);

    protected void SendToActive(uint state, uint value)
    {
        foreach (HashSet<ObjectGuid> team in _active)
        {
            foreach (ObjectGuid guid in team)
            {
                Host.SendWorldState(guid, state, value);
            }
        }
    }

    /// <summary>
    /// vmangos <c>OPvPCapturePoint::Update</c> (ZoneScript.cpp:301-433). True when the slider state changed (the zone then
    /// recounts its objectives).
    /// </summary>
    public bool Update(uint diffMs)
    {
        if (Object is null)
        {
            return false;
        }

        var near = Host.ActivePlayersNear(Point.MapId, Point.X, Point.Y, Point.Z, Template.Radius).ToList();
        var nearGuids = near.Select(p => p.Guid).ToHashSet();

        // Players who left the radius, died or stopped being PvP-active lose the slider.
        foreach (HashSet<ObjectGuid> team in _active)
        {
            foreach (ObjectGuid guid in team.Where(g => !nearGuids.Contains(g)).ToArray())
            {
                Host.SendWorldState(guid, Template.WorldStateDisplay, 0);
                team.Remove(guid);
            }
        }

        foreach (OutdoorPvPPlayer player in near)
        {
            // "!IsInsideObjective(player)": a player counts for one objective of the zone at a time.
            if (!IsInside(player) && !Zone.IsInsideObjective(player.Guid))
            {
                HandlePlayerEnter(player);
            }
        }

        return Advance(diffMs);
    }

    /// <summary>The slider step of <see cref="Update"/>, after the player sets are current.</summary>
    internal bool Advance(uint diffMs)
    {
        float factDiff = ((float)_active[0].Count - _active[1].Count) * diffMs / UpdateIntervalMs;
        float maxDiff = MaxSpeed * diffMs;
        Team? challenger;
        if (factDiff < 0f)
        {
            if (Value <= -MaxValue)
            {
                return false;
            }

            factDiff = Math.Max(factDiff, -maxDiff);
            challenger = Entities.Team.Horde;
        }
        else if (factDiff > 0f)
        {
            if (Value >= MaxValue)
            {
                return false;
            }

            factDiff = Math.Min(factDiff, maxDiff);
            challenger = Entities.Team.Alliance;
        }
        else
        {
            challenger = null;
        }

        uint oldValuePct = ValuePct;
        int oldFactDiff = FactDiff;
        CaptureTeam oldTeam = Owner;
        OldState = State;
        Value += factDiff;

        if (Value <= -MinValue)
        {
            if (Value <= -MaxValue)
            {
                Value = -MaxValue;
                State = ObjectiveState.Horde;
            }
            else
            {
                State = ObjectiveState.HordeProgressing;
            }

            Owner = CaptureTeam.Horde;
        }
        else if (Value >= MinValue)
        {
            if (Value >= MaxValue)
            {
                Value = MaxValue;
                State = ObjectiveState.Alliance;
            }
            else
            {
                State = ObjectiveState.AllianceProgressing;
            }

            Owner = CaptureTeam.Alliance;
        }
        else
        {
            State = challenger switch
            {
                Entities.Team.Alliance => ObjectiveState.AllianceContested,
                Entities.Team.Horde => ObjectiveState.HordeContested,
                _ => ObjectiveState.Neutral,
            };
            Owner = CaptureTeam.Neutral;
        }

        ValuePct = (uint)Math.Ceiling((Value + MaxValue) / (2 * MaxValue) * 100f);
        FactDiff = _active[0].Count - _active[1].Count;

        if (OldState != State)
        {
            if (oldTeam != Owner)
            {
                ChangeTeam(oldTeam);
            }

            ChangeState();
            return true;
        }

        // Only send when the slider moved, or the difference just became zero (the client then drops the direction arrow).
        if (ValuePct != oldValuePct || (oldFactDiff != FactDiff && FactDiff == 0))
        {
            SendChangePhase();
        }

        return false;
    }

    /// <summary>Run <see cref="ChangeState"/> once for the initial neutral state (the vmangos constructors do).</summary>
    protected void InitializeState()
    {
        ValuePct = (uint)Math.Ceiling((Value + MaxValue) / (2 * MaxValue) * 100f);
        ChangeState();
    }

    /// <summary>Forget every player (the zone script is being torn down).</summary>
    internal void ClearPlayers()
    {
        _active[0].Clear();
        _active[1].Clear();
    }

    internal static int TeamIndex(Team team) => team == Entities.Team.Alliance ? 0 : 1;
}

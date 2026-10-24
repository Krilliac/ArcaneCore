using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Transports;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.GameObjects;

/// <summary>One stationary run of an elevator animation: the node it starts at and how long it lasts.</summary>
/// <param name="Number">1-based, in order of <paramref name="StartMs"/>.</param>
/// <param name="StartMs">Progress (ms into the cycle) at which the car reaches the run's first node.</param>
/// <param name="DurationMs">How long the car stays; the run that wraps the end of the cycle is merged with the one at its start.</param>
public readonly record struct ElevatorStop(int Number, uint StartMs, uint DurationMs, float X, float Y, float Z);

/// <summary>What a GM override does to an elevator's schedule (see <see cref="GameObjectMapSystem.GetElevatorControl"/>).</summary>
/// <param name="Overridden">False: the lift runs the default schedule (the time since it was created).</param>
/// <param name="Scale">Cycle speed multiplier; 1 by default, 0 while frozen.</param>
/// <param name="Frozen">The cycle is held; <paramref name="ResumeScale"/> is the speed <c>Resume</c> restores.</param>
/// <param name="ResumeScale">The speed a frozen lift continues at.</param>
public readonly record struct ElevatorControlInfo(bool Overridden, double Scale, bool Frozen, double ResumeScale);

public sealed partial class GameObjectMapSystem
{
    /// <summary>Scale granularity: 1000 = 1.0. Integer thousandths keep the progress an exact integer function of the clock (no float drift).</summary>
    internal const int ScaleUnit = 1000;

    /// <summary>Smallest accepted <see cref="SetElevatorScale"/> factor.</summary>
    public const double MinElevatorScale = 0.1;

    /// <summary>Largest accepted <see cref="SetElevatorScale"/> factor.</summary>
    public const double MaxElevatorScale = 10.0;

    /// <summary>
    /// A GM override of one lift's schedule: progress = (<see cref="PhaseBaseMs"/> + (clock - <see cref="AnchorMs"/>) * <see cref="ScaleMilli"/> / 1000)
    /// mod cycle. Every change re-anchors at the current progress, so the lift never jumps unless a phase was asked for.
    /// </summary>
    private readonly record struct ElevatorControl(long AnchorMs, long PhaseBaseMs, int ScaleMilli, int ResumeScaleMilli);

    /// <summary>
    /// Overrides by object guid. Empty (and so allocation free per tick) unless a GM changed a lift; an entry is dropped with its object
    /// (grid unload, removal), after which the lift is created again on the default schedule. A map-wide car (Deeprun Tram) is in no grid
    /// list: its entry ends with a reset, a removal, or when the map has no players left (<c>UpdateElevators</c>), else with the map.
    /// </summary>
    private readonly Dictionary<ObjectGuid, ElevatorControl> _elevatorControls = [];

    /// <summary>
    /// The one place elevator progress is derived from the clock: <paramref name="atClockMs"/> on this map's update clock (a later clock for
    /// look-ahead). Without an override this is <c>(clock - CreatedAtMs) % total</c>, exactly as before.
    /// </summary>
    private uint ElevatorProgressAtClock(GameObject go, long atClockMs, uint total)
    {
        if (_elevatorControls.Count == 0 || !_elevatorControls.TryGetValue(go.Guid, out ElevatorControl control))
        {
            return (uint)((atClockMs - go.CreatedAtMs) % total);
        }

        long advanced = (atClockMs - control.AnchorMs) * control.ScaleMilli / ScaleUnit;
        return (uint)((control.PhaseBaseMs + advanced) % total);
    }

    /// <summary>The override state of <paramref name="go"/>.</summary>
    public ElevatorControlInfo GetElevatorControl(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        return _elevatorControls.TryGetValue(go.Guid, out ElevatorControl c)
            ? new ElevatorControlInfo(true, (double)c.ScaleMilli / ScaleUnit, c.ScaleMilli == 0, (double)c.ResumeScaleMilli / ScaleUnit)
            : new ElevatorControlInfo(false, 1.0, false, 1.0);
    }

    /// <summary>Jump <paramref name="go"/> to <paramref name="phaseMs"/> into its cycle (taken modulo the cycle); the speed and a freeze stay. Resends it to its viewers.</summary>
    public bool SetElevatorPhase(GameObject go, uint phaseMs)
        => Control(go, out uint total, out ElevatorControl c) && Apply(go, c with { AnchorMs = _clockMs, PhaseBaseMs = phaseMs % total }, resend: true);

    /// <summary>Hold <paramref name="go"/> where it is. Viewers are not resent: their clients keep animating until a resync or reset.</summary>
    public bool FreezeElevator(GameObject go)
        => Control(go, out _, out ElevatorControl c) && Apply(go, Reanchor(go, c) with { ScaleMilli = 0 }, resend: false);

    /// <summary>Continue <paramref name="go"/> from where it stands at the speed it had before <see cref="FreezeElevator"/>. Resends it.</summary>
    public bool ResumeElevator(GameObject go)
        => Control(go, out _, out ElevatorControl c) && Apply(go, Reanchor(go, c) with { ScaleMilli = c.ResumeScaleMilli }, resend: true);

    /// <summary>
    /// Run <paramref name="go"/> at <paramref name="scale"/> times its speed (clamped to <see cref="MinElevatorScale"/>..<see cref="MaxElevatorScale"/>,
    /// rounded to a thousandth). A frozen lift stays frozen and resumes at the new speed. Resends it.
    /// </summary>
    public bool SetElevatorScale(GameObject go, double scale)
    {
        if (double.IsNaN(scale) || !Control(go, out _, out ElevatorControl c))
        {
            return false;
        }

        int milli = (int)Math.Round(Math.Clamp(scale, MinElevatorScale, MaxElevatorScale) * ScaleUnit, MidpointRounding.AwayFromZero);
        ElevatorControl next = Reanchor(go, c) with { ResumeScaleMilli = milli };
        return Apply(go, c.ScaleMilli == 0 ? next : next with { ScaleMilli = milli }, resend: true);
    }

    /// <summary>Put <paramref name="go"/> back on the default schedule (the time since it was created). Resends it.</summary>
    public bool ResetElevator(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        if (go.Type != GameObjectType.Transport || !Tracks(go) || ElevatorAnimations.TotalTime(go.Entry) == 0)
        {
            return false;
        }

        _elevatorControls.Remove(go.Guid);
        Republish(go);
        return true;
    }

    /// <summary>Whether <paramref name="go"/> is a map-wide elevator (a Deeprun Tram car): it moves horizontally and is in no grid.</summary>
    public bool IsMapWideElevator(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        return _mapWideElevators.Contains(go);
    }

    /// <summary>The players whose clients have <paramref name="go"/> (a lift in view), in join order.</summary>
    public IReadOnlyList<Player> ElevatorViewers(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        return [.. Map.ObserversOf(go).Where(p => p.Session.WantsUpdateObjects).OrderBy(p => p.MapSequence)];
    }

    /// <summary>The stationary runs of <paramref name="go"/>'s animation in order of their start; empty without an animation.</summary>
    public IReadOnlyList<ElevatorStop> ElevatorStops(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        List<(TransportAnimationNode At, uint Duration)> runs = CollectStops(_elevatorAnimations.Nodes(go.Entry));
        runs.Sort((a, b) => a.At.TimeSeg.CompareTo(b.At.TimeSeg));
        var stops = new List<ElevatorStop>(runs.Count);
        foreach ((TransportAnimationNode at, uint duration) in runs)
        {
            stops.Add(new ElevatorStop(stops.Count + 1, at.TimeSeg, duration, at.X, at.Y, at.Z));
        }

        return stops;
    }

    /// <summary>
    /// Real-time ms until the car next reaches the start of a stop (the stop it is in does not count), at the lift's current speed;
    /// null without stops, or while frozen.
    /// </summary>
    public uint? MsToNextElevatorStop(GameObject go)
    {
        ArgumentNullException.ThrowIfNull(go);
        uint total = ElevatorAnimations.TotalTime(go.Entry);
        ElevatorControlInfo info = GetElevatorControl(go);
        IReadOnlyList<ElevatorStop> stops = ElevatorStops(go);
        if (total == 0 || stops.Count == 0 || info.Frozen)
        {
            return null;
        }

        uint now = ElevatorProgressAtClock(go, _clockMs, total);
        long ahead = -1;
        foreach (ElevatorStop stop in stops)
        {
            if (stop.StartMs > now)
            {
                ahead = stop.StartMs - now;
                break;
            }
        }

        if (ahead < 0)
        {
            ahead = (total - now) + stops[0].StartMs;
        }

        return (uint)Math.Ceiling(ahead / info.Scale);
    }

    private bool Control(GameObject go, out uint total, out ElevatorControl control)
    {
        ArgumentNullException.ThrowIfNull(go);
        total = ElevatorAnimations.TotalTime(go.Entry);
        control = default;
        if (total == 0 || go.Type != GameObjectType.Transport || !Tracks(go))
        {
            return false;
        }

        control = _elevatorControls.TryGetValue(go.Guid, out ElevatorControl existing)
            ? existing
            : new ElevatorControl(go.CreatedAtMs, 0, ScaleUnit, ScaleUnit); // equals the default schedule: progress = clock - CreatedAtMs
        return true;
    }

    /// <summary>The same schedule re-based at the current clock: anchor = now, phase base = the progress now.</summary>
    private ElevatorControl Reanchor(GameObject go, ElevatorControl c)
    {
        uint total = ElevatorAnimations.TotalTime(go.Entry);
        long advanced = (_clockMs - c.AnchorMs) * c.ScaleMilli / ScaleUnit;
        return c with { AnchorMs = _clockMs, PhaseBaseMs = (c.PhaseBaseMs + advanced) % total };
    }

    private bool Apply(GameObject go, ElevatorControl control, bool resend)
    {
        _elevatorControls[go.Guid] = control;
        if (resend)
        {
            Republish(go);
        }
        else
        {
            go.PathProgress = ElevatorProgressAtClock(go, _clockMs, ElevatorAnimations.TotalTime(go.Entry));
        }

        return true;
    }

    /// <summary>Recompute <see cref="GameObject.PathProgress"/> now and send the lift again to everyone who has it (out of range, then create).</summary>
    private void Republish(GameObject go)
    {
        go.PathProgress = ElevatorProgressAtClock(go, _clockMs, ElevatorAnimations.TotalTime(go.Entry));
        _ = Map.ResendToObservers(go);
    }
}

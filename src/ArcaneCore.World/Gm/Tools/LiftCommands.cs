using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Tools;

/// <summary>
/// <c>.debug lift</c>: look at and steer the schedule of an elevator (game object type 11). <c>lift</c> shows one (GameMaster); <c>phase</c>,
/// <c>stop</c>, <c>freeze</c>, <c>resume</c>, <c>speed</c> and <c>reset</c> change it (Administrator). A change moves the server's idea of the lift
/// (bots, anti-cheat and transport tracking read it) and, except for <c>freeze</c>, resends the lift to every player who has it, so those clients
/// jump to the new phase. Overrides are not saved and end when the lift's grid unloads or the lift is removed. A map-wide car (Deeprun Tram) is in no
/// grid: its override ends with <c>reset</c>, a removal, or when the last player leaves the map (else with the map), and <c>freeze</c> and <c>speed</c>
/// are refused for it (a tram moves horizontally, so a frozen or sped-up tram would make anti-cheat flag its real riders). A resend destroys and
/// recreates the lift on each viewer's client; what a client does with a player standing on it at that moment is unverified. No vmangos equivalent.
/// </summary>
public sealed class ToolsLiftCommands : ICommandExtension
{
    /// <summary>How far, in yards, the nearest lift is looked for without a guid (a shaft is deep; the car's spawn is at one end).</summary>
    public const float NearestLiftRadius = 100f;

    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("lift", AccountSecurity.GameMaster,
            "Syntax: .debug lift [#guid]\nShow an elevator (the nearest within 100 yards without #guid): cycle length, progress, the stops (stationary runs, numbered by start), time to the next stop, speed, frozen, and the players who have it in view.\nChange it with: .debug lift phase|stop|freeze|resume|speed|reset.",
            Info,
            [
                new("phase", AccountSecurity.Administrator,
                    "Syntax: .debug lift phase #ms [#guid]\nJump the elevator to #ms into its cycle (taken modulo the cycle) and resend it to the players who have it. Not saved; ends when its grid unloads (a map-wide tram car: with reset, or when the map has no players).\nA player standing on the lift during the resend gets the car destroyed and recreated under them; the client behaviour is unverified.",
                    Phase, IsWrite: true),
                new("stop", AccountSecurity.Administrator,
                    "Syntax: .debug lift stop #n [#guid]\nJump the elevator to the start of its Nth stationary run (see .debug lift) and resend it to the players who have it.\nA player standing on the lift during the resend gets the car destroyed and recreated under them; the client behaviour is unverified.",
                    Stop, IsWrite: true),
                new("freeze", AccountSecurity.Administrator,
                    "Syntax: .debug lift freeze [#guid]\nHold the elevator where it is on the server (bots, anti-cheat). Players who have it keep the normal animation on their clients and see it out of sync until .debug lift reset. Refused for map-wide trams (Deeprun Tram).",
                    Freeze, IsWrite: true),
                new("resume", AccountSecurity.Administrator,
                    "Syntax: .debug lift resume [#guid]\nContinue a frozen elevator from where it stands at its previous speed and resend it to the players who have it.\nA player standing on the lift during the resend gets the car destroyed and recreated under them; the client behaviour is unverified.",
                    Resume, IsWrite: true),
                new("speed", AccountSecurity.Administrator,
                    "Syntax: .debug lift speed #factor [#guid]\nRun the elevator at #factor times its speed (0.1 to 10; the reply says when it was clamped) on the server and resend it. Players who have it keep the normal speed on their clients (they see it out of sync) until .debug lift reset. Refused for map-wide trams (Deeprun Tram).",
                    Speed, IsWrite: true),
                new("reset", AccountSecurity.Administrator,
                    "Syntax: .debug lift reset [#guid]\nPut the elevator back on its normal schedule (the time since it was created) and resend it to the players who have it.\nA player standing on the lift during the resend gets the car destroyed and recreated under them; the client behaviour is unverified.",
                    Reset, IsWrite: true),
            ],
            RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static GameObject? FindLift(CommandContext context, uint counter, out GameObjectMapSystem? system)
        => GmGameObjectTarget.Find(context, counter, out system, GameObjectType.Transport, NearestLiftRadius);

    private static string Progress(GameObject go) => string.Create(C, $"{go.PathProgress}");

    private static void Audit(CommandContext context, GameObject go, string field, string oldValue, string newValue)
        => DebugWrite.Record(context, Name(go), field, oldValue, newValue, DebugWriteScope.Live);

    private static string Name(GameObject go) => string.Create(C, $"{go.Template.Name} (entry {go.Entry}, guid {go.Guid.Counter})");

    private static bool Info(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!GmGameObjectTarget.TryOptionalGuid(args, out uint counter))
        {
            return false;
        }

        if (FindLift(context, counter, out GameObjectMapSystem? system) is not { } go || system is null)
        {
            return true;
        }

        uint cycle = system.ElevatorCycle(go);
        if (cycle == 0)
        {
            context.Reply($"{Name(go)} has no animation in TransportAnimation.dbc, so it has no schedule.");
            return true;
        }

        ElevatorControlInfo control = system.GetElevatorControl(go);
        string speed = control.Frozen
            ? string.Create(C, $"frozen (resumes at x{control.ResumeScale:0.###})")
            : string.Create(C, $"x{control.Scale:0.###}");
        context.Reply(string.Create(C,
            $"{Name(go)}: cycle {cycle} ms, at {go.PathProgress} ms, speed {speed}, schedule {(control.Overridden ? "overridden" : "default")}."));
        IReadOnlyList<ElevatorStop> stops = system.ElevatorStops(go);
        foreach (ElevatorStop stop in stops)
        {
            context.Reply(string.Create(C, $"  stop {stop.Number}: from {stop.StartMs} ms for {stop.DurationMs} ms, offset z {stop.Z:0.#}"));
        }

        if (stops.Count == 0)
        {
            context.Reply("  no stationary runs in its animation");
        }

        context.Reply(system.MsToNextElevatorStop(go) is { } next
            ? string.Create(C, $"  next stop in {next} ms")
            : "  next stop: none (frozen or no stops)");
        IReadOnlyList<Player> viewers = system.ElevatorViewers(go);
        context.Reply(viewers.Count == 0
            ? "  in view of no player"
            : string.Create(C, $"  in view of {viewers.Count} player(s): {string.Join(", ", viewers.Select(p => p.Name))}"));
        return true;
    }

    private static bool Phase(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint ms) || !GmGameObjectTarget.TryOptionalGuid(args, out uint counter))
        {
            return false;
        }

        if (FindLift(context, counter, out GameObjectMapSystem? system) is not { } go || system is null)
        {
            return true;
        }

        string before = Progress(go);
        if (!system.SetElevatorPhase(go, ms))
        {
            return NoSchedule(context, go);
        }

        Audit(context, go, "phase ms", before, Progress(go));
        context.Reply(string.Create(C, $"{Name(go)} now at {go.PathProgress} ms of {system.ElevatorCycle(go)}; resent to {Resent(system, go, context)}."));
        return true;
    }

    private static bool Stop(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint number) || !GmGameObjectTarget.TryOptionalGuid(args, out uint counter))
        {
            return false;
        }

        if (FindLift(context, counter, out GameObjectMapSystem? system) is not { } go || system is null)
        {
            return true;
        }

        IReadOnlyList<ElevatorStop> stops = system.ElevatorStops(go);
        if (number == 0 || number > stops.Count)
        {
            context.Reply(stops.Count == 0
                ? $"{Name(go)} has no stops."
                : string.Create(C, $"{Name(go)} has stops 1 to {stops.Count}."));
            return true;
        }

        ElevatorStop stop = stops[(int)number - 1];
        string beforeStop = Progress(go);
        if (!system.SetElevatorPhase(go, stop.StartMs))
        {
            return NoSchedule(context, go);
        }

        Audit(context, go, "phase ms", beforeStop, Progress(go));
        context.Reply(string.Create(C, $"{Name(go)} at the start of stop {stop.Number} ({stop.StartMs} ms, stays {stop.DurationMs} ms); resent to {Resent(system, go, context)}."));
        return true;
    }

    private static bool Freeze(CommandContext context, string text)
        => WithLift(context, text, (system, go) =>
        {
            if (system.IsMapWideElevator(go))
            {
                return RefuseMapWide(context, go, "freeze");
            }

            bool wasFrozen = system.GetElevatorControl(go).Frozen;
            if (!system.FreezeElevator(go))
            {
                return NoSchedule(context, go);
            }

            Audit(context, go, "frozen", wasFrozen ? "true" : "false", "true");

            context.Reply(string.Create(C, $"{Name(go)} frozen at {go.PathProgress} ms on the server."));
            WarnOthers(context, system, go);
            return true;
        });

    private static bool Resume(CommandContext context, string text)
        => WithLift(context, text, (system, go) =>
        {
            bool wasFrozen = system.GetElevatorControl(go).Frozen;
            if (!system.ResumeElevator(go))
            {
                return NoSchedule(context, go);
            }

            Audit(context, go, "frozen", wasFrozen ? "true" : "false", "false");

            context.Reply(string.Create(C, $"{Name(go)} running again from {go.PathProgress} ms; resent to {Resent(system, go, context)}."));
            return true;
        });

    private static bool Speed(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out float factor) || !float.IsFinite(factor) || !GmGameObjectTarget.TryOptionalGuid(args, out uint counter))
        {
            return false;
        }

        if (FindLift(context, counter, out GameObjectMapSystem? system) is not { } go || system is null)
        {
            return true;
        }

        if (system.IsMapWideElevator(go))
        {
            return RefuseMapWide(context, go, "speed");
        }

        double oldScale = system.GetElevatorControl(go).ResumeScale;
        if (!system.SetElevatorScale(go, factor))
        {
            return NoSchedule(context, go);
        }

        ElevatorControlInfo control = system.GetElevatorControl(go);
        Audit(context, go, "speed scale", oldScale.ToString("0.###", C), control.ResumeScale.ToString("0.###", C));
        string clamped = factor is < (float)GameObjectMapSystem.MinElevatorScale or > (float)GameObjectMapSystem.MaxElevatorScale
            ? string.Create(C, $" (requested x{factor:0.###}, clamped to {GameObjectMapSystem.MinElevatorScale}-{GameObjectMapSystem.MaxElevatorScale})")
            : string.Empty;
        context.Reply(string.Create(C,
            $"{Name(go)} speed x{control.ResumeScale:0.###}{clamped}{(control.Frozen ? " (frozen, applies on resume)" : string.Empty)}; resent to {Resent(system, go, context)}."));
        WarnOthers(context, system, go);
        return true;
    }

    private static bool Reset(CommandContext context, string text)
        => WithLift(context, text, (system, go) =>
        {
            bool wasOverridden = system.GetElevatorControl(go).Overridden;
            if (!system.ResetElevator(go))
            {
                return NoSchedule(context, go);
            }

            Audit(context, go, "schedule overridden", wasOverridden ? "true" : "false", "false");

            context.Reply(string.Create(C, $"{Name(go)} back on its default schedule, at {go.PathProgress} ms; resent to {Resent(system, go, context)}."));
            return true;
        });

    /// <summary>The commands that take only an optional guid: false (syntax) on junk, true after the lookup replied when no lift was found.</summary>
    private static bool WithLift(CommandContext context, string text, Func<GameObjectMapSystem, GameObject, bool> action)
    {
        var args = new CommandArgs(text);
        if (!GmGameObjectTarget.TryOptionalGuid(args, out uint counter))
        {
            return false;
        }

        return FindLift(context, counter, out GameObjectMapSystem? system) is not { } go || system is null || action(system, go);
    }

    private static bool RefuseMapWide(CommandContext context, GameObject go, string what)
    {
        context.Reply($"{Name(go)} is a map-wide tram car: '{what}' is refused because a tram moves horizontally and anti-cheat would flag its real riders. Use phase, stop or reset.");
        return true;
    }

    private static bool NoSchedule(CommandContext context, GameObject go)
    {
        context.Reply($"{Name(go)} has no animation in TransportAnimation.dbc, so it has no schedule.");
        return true;
    }

    private static string Resent(GameObjectMapSystem system, GameObject go, CommandContext context)
    {
        IReadOnlyList<Player> viewers = system.ElevatorViewers(go);
        int others = viewers.Count(p => !ReferenceEquals(p, context.Player));
        return string.Create(C, $"{viewers.Count} player(s) in view ({others} besides you)");
    }

    private static void WarnOthers(CommandContext context, GameObjectMapSystem system, GameObject go)
    {
        string[] others = [.. system.ElevatorViewers(go).Where(p => !ReferenceEquals(p, context.Player)).Select(p => p.Name)];
        if (others.Length != 0)
        {
            context.Reply($"Warning: {string.Join(", ", others)} keep the normal animation on their clients and will see this lift out of sync until '.debug lift reset'.");
        }
    }
}

using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>What the supervisor does about a child that has exited.</summary>
public enum SupervisedExitDisposition
{
    StayDown = 0,
    RelaunchNow = 1,
    RelaunchWithBackoff = 2,
    Fault = 3,
}

/// <summary>Who ended a child's life, when the supervisor did.</summary>
public enum SupervisorKillKind
{
    None = 0,
    Hang = 1,
    StopGrace = 2,
}

/// <summary>
/// Decides what the supervisor does about an exited child. Order: a hang kill always relaunches with backoff; otherwise the exit code decides (0 stays
/// down, 2 relaunches now, 78 and 64 fault because a restart would fail the same way); a child the supervisor killed after it announced a stop falls
/// back to that announced intent; everything else is a crash and relaunches with backoff.
/// </summary>
public static class SupervisedExitClassifier
{
    public static SupervisedExitDisposition Classify(int exitCode, SupervisorKillKind killedBy, SupervisorStopIntent? announced)
    {
        if (killedBy == SupervisorKillKind.Hang)
            return SupervisedExitDisposition.RelaunchWithBackoff;

        switch (exitCode)
        {
            case ExitCodes.Success:
                return SupervisedExitDisposition.StayDown;
            case ExitCodes.Restart:
                return SupervisedExitDisposition.RelaunchNow;
            case ExitCodes.InvalidConfiguration:
            case ExitCodes.Usage:
                return SupervisedExitDisposition.Fault;
        }

        if (killedBy == SupervisorKillKind.StopGrace)
        {
            return announced switch
            {
                SupervisorStopIntent.Restart => SupervisedExitDisposition.RelaunchNow,
                SupervisorStopIntent.Shutdown => SupervisedExitDisposition.StayDown,
                _ => SupervisedExitDisposition.RelaunchWithBackoff,
            };
        }

        return SupervisedExitDisposition.RelaunchWithBackoff;
    }
}

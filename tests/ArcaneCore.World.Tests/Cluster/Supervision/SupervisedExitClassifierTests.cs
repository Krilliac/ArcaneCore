using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Supervision;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>What the supervisor does about a child that exited, decided from the exit code (the ExitCodes contract), who killed it and what it announced.</summary>
public sealed class SupervisedExitClassifierTests
{
    private static SupervisedExitDisposition Classify(int code, SupervisorKillKind kill = SupervisorKillKind.None, SupervisorStopIntent? announced = null)
        => SupervisedExitClassifier.Classify(code, kill, announced);

    [Fact]
    public void ExitClassifier_Success_StaysDown()
        => Assert.Equal(SupervisedExitDisposition.StayDown, Classify(ExitCodes.Success));

    [Fact]
    public void ExitClassifier_Restart_RelaunchesNow()
        => Assert.Equal(SupervisedExitDisposition.RelaunchNow, Classify(ExitCodes.Restart));

    [Theory]
    [InlineData(ExitCodes.InvalidConfiguration)]
    [InlineData(ExitCodes.Usage)]
    public void ExitClassifier_ConfigAndUsage_Fault(int code)
        => Assert.Equal(SupervisedExitDisposition.Fault, Classify(code));

    [Theory]
    [InlineData(1)]
    [InlineData(70)]
    [InlineData(-2146232797)] // 0x80131623, a FailFast on Windows
    [InlineData(134)]         // SIGABRT, a FailFast on Linux
    [InlineData(137)]         // SIGKILL
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(7)]
    public void ExitClassifier_CrashCodes_BackOff(int code)
        => Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Classify(code));

    [Theory]
    [InlineData(ExitCodes.Success)]
    [InlineData(ExitCodes.Failure)]
    [InlineData(ExitCodes.Restart)]
    [InlineData(ExitCodes.Usage)]
    [InlineData(ExitCodes.InvalidConfiguration)]
    [InlineData(137)]
    [InlineData(-1)]
    public void ExitClassifier_HangKill_BacksOffWhateverTheCode(int code)
    {
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Classify(code, SupervisorKillKind.Hang));
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Classify(code, SupervisorKillKind.Hang, SupervisorStopIntent.Restart));
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Classify(code, SupervisorKillKind.Hang, SupervisorStopIntent.Shutdown));
    }

    [Theory]
    [InlineData(137)]
    [InlineData(-1)]
    [InlineData(1)]
    public void ExitClassifier_KilledAfterAnnouncedRestart_Relaunches(int code)
        => Assert.Equal(SupervisedExitDisposition.RelaunchNow, Classify(code, SupervisorKillKind.StopGrace, SupervisorStopIntent.Restart));

    [Theory]
    [InlineData(137)]
    [InlineData(-1)]
    [InlineData(1)]
    public void ExitClassifier_KilledAfterAnnouncedShutdown_StaysDown(int code)
        => Assert.Equal(SupervisedExitDisposition.StayDown, Classify(code, SupervisorKillKind.StopGrace, SupervisorStopIntent.Shutdown));

    [Fact]
    public void ExitClassifier_KilledAfterAnnouncedFailureOrNothing_BacksOff()
    {
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Classify(137, SupervisorKillKind.StopGrace, SupervisorStopIntent.Failure));
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Classify(137, SupervisorKillKind.StopGrace, null));
    }

    [Fact]
    public void ExitClassifier_ExitCodeWinsOverAnnouncement()
    {
        // The child said "restart" but returned 0, or said "shutdown" but returned 2, or returned the config code: the code is what happened.
        Assert.Equal(SupervisedExitDisposition.StayDown, Classify(0, SupervisorKillKind.None, SupervisorStopIntent.Restart));
        Assert.Equal(SupervisedExitDisposition.RelaunchNow, Classify(2, SupervisorKillKind.None, SupervisorStopIntent.Shutdown));
        Assert.Equal(SupervisedExitDisposition.Fault, Classify(78, SupervisorKillKind.None, SupervisorStopIntent.Restart));
        Assert.Equal(SupervisedExitDisposition.StayDown, Classify(0, SupervisorKillKind.StopGrace, SupervisorStopIntent.Restart));
        Assert.Equal(SupervisedExitDisposition.RelaunchNow, Classify(2, SupervisorKillKind.StopGrace, SupervisorStopIntent.Shutdown));
        // An announcement alone never turns an ordinary crash into a restart: nobody killed it.
        Assert.Equal(SupervisedExitDisposition.RelaunchWithBackoff, Classify(1, SupervisorKillKind.None, SupervisorStopIntent.Restart));
    }
}

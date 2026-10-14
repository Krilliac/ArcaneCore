namespace ArcaneCore.Kernel.Cluster.Transfer;

/// <summary>
/// Six-stage transactional cross-worker player transfer state machine plus terminal abort state.
/// <see cref="CommitDecision"/> is the single durable point of no return.
/// </summary>
public enum PlayerTransferStage : byte
{
    None = 0,
    Reserve = 1,
    SuspendAndCapture = 2,
    InstallPassive = 3,
    StageWorldPort = 4,
    CommitDecision = 5,
    ActivateAndRetire = 6,
    Aborted = 7,
}

/// <summary>Durable commit status of a cross-worker transfer record.</summary>
public enum PlayerTransferDecisionState : byte
{
    PreCommit = 0,
    Committed = 1,
    Completed = 2,
    Aborted = 3,
}

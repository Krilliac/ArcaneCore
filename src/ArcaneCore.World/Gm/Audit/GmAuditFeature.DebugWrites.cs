using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Audit;

public sealed partial class GmAuditFeature
{
    /// <summary>The structured write audit of the debug commands (<see cref="DebugWrite.Record"/>).</summary>
    public DebugWriteLog DebugWrites { get; } = new(DebugWriteLog.DefaultCapacity, clock);

    /// <summary>The short confirm codes that destructive or broad debug commands answer with (<see cref="ConfirmTokenStore"/>).</summary>
    public ConfirmTokenStore ConfirmTokens { get; } = new(clock);

    /// <summary>
    /// The <c>AllowDangerousDebug</c> decision, taken once when the feature is built (the option is Restart-only) from the host's
    /// configuration and the live-server marker.
    /// </summary>
    public DangerousDebugGate DangerousDebug { get; } = DangerousDebugGate.Create(configuration, DangerousDebugGate.LiveServerProbe());
}

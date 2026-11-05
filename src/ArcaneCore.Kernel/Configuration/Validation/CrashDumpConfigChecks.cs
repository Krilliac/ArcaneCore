using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Kernel.Configuration.Validation;

/// <summary>
/// The <c>Diagnostics:CrashDump</c> section (docs/ops/invariants.md). Every daemon binds it (<c>UseArcaneDiagnostics</c>), so the check lives in the
/// Kernel: the world and the realm find it by discovery, the gateway lists it explicitly. <c>TimeoutSeconds</c> is limited to 1 to 3600 so the
/// chain's maximum (three times it) cannot overflow and a dump is never skipped for it; a value below 1 is an error here (the check refuses it at start-up; <c>CrashDumpService</c> clamps with <c>Math.Max(1, ..)</c> only as a defence for options built in code). Above 1180 the supervisor's advice to keep <c>Cluster:Supervisor:DumpGraceSeconds</c> above three times this plus a minute cannot be met, because that cap is itself limited to 3600 (3 x 1180 + 60 = 3600): a larger value is accepted, but the supervisor then stops waiting for the dump at 3600 seconds.
/// Stateless, pure over the configuration; called from the start-up thread.
/// </summary>
public sealed class CrashDumpConfigChecks : IConfigCheck
{
    /// <summary>The largest <c>CrashDump:TimeoutSeconds</c>: far above any real dump, and the announced maximum (three times it) stays a small number of seconds.</summary>
    public const int MaxTimeoutSeconds = 3600;

    private const string Prefix = DiagnosticsOptions.SectionName + ":CrashDump:";

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();

        ConfigChecks.EnumOr(issues, Prefix + nameof(CrashDumpOptions.Mode), configuration[Prefix + nameof(CrashDumpOptions.Mode)], CrashDumpMode.Full);
        ConfigChecks.ReadRange(issues, configuration, Prefix + nameof(CrashDumpOptions.MaxFiles), 0, int.MaxValue, "0 keeps every file; default 5");
        ConfigChecks.ReadRange(issues, configuration, Prefix + nameof(CrashDumpOptions.FullMinFreeGB), 0, int.MaxValue, "0 never falls back for space; default 16");
        ConfigChecks.ReadRange(issues, configuration, Prefix + nameof(CrashDumpOptions.TimeoutSeconds), 1, MaxTimeoutSeconds, "1 to 3600 seconds; the whole dump may take three times this, and a cluster supervisor waits at most 3600 s (Cluster:Supervisor:DumpGraceSeconds), so with that cap at its 3600 maximum a value above 1180 can cut the dump short (at the default cap of 420, above 120); default 120");

        return issues;
    }
}

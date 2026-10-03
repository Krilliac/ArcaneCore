using ArcaneCore.Kernel.Configuration;

namespace ArcaneCore.World.HotCode;

/// <summary>The server refused to start because of its hot-code configuration.</summary>
public sealed class HotCodeRefusedException(string message) : InvalidOperationException(message);

/// <summary>Outcome of <see cref="HotCodeGuard.Evaluate"/>.</summary>
/// <param name="Refusals">Why the start must be refused; empty when it is allowed.</param>
/// <param name="HotReloadActive">The runtime is set up to take code deltas (watch agent, debugger, env var).</param>
public sealed record HotCodeVerdict(IReadOnlyList<string> Refusals, bool HotReloadActive)
{
    public bool Allowed => Refusals.Count == 0;
}

/// <summary>
/// Fail-closed launch gate for code hot reload. The <c>dotnet watch</c> agent is injected before
/// our code runs and cannot be vetoed afterwards, so the only place to say no is at startup:
/// <list type="bullet">
/// <item>the runtime is set up for metadata updates but <c>World:HotCode:Enabled</c> is false;</item>
/// <item><c>World:HotCode:Enabled</c> is true outside the Development / Staging environment.</item>
/// </list>
/// With the default configuration on a normal launch nothing is refused and nothing changes.
/// </summary>
public static class HotCodeGuard
{
    public const string WatchVariable = "DOTNET_WATCH";
    public const string ModifiableAssembliesVariable = "DOTNET_MODIFIABLE_ASSEMBLIES";
    public const string StartupHooksVariable = "DOTNET_STARTUP_HOOKS";

    private const string DeltaApplierName = "DotNetDeltaApplier";

    public static HotCodeVerdict Evaluate(HotCodeOptions options, IRuntimeProbe probe)
    {
        var active = new List<string>();
        if (probe.MetadataUpdatesSupported)
        {
            active.Add("the runtime reports metadata updates are supported (MetadataUpdater.IsSupported)");
        }

        if (!string.IsNullOrWhiteSpace(probe.GetEnvironmentVariable(ModifiableAssembliesVariable)))
        {
            active.Add($"{ModifiableAssembliesVariable} is set");
        }

        if (probe.GetEnvironmentVariable(StartupHooksVariable)?.Contains(DeltaApplierName, StringComparison.OrdinalIgnoreCase) == true)
        {
            active.Add($"{StartupHooksVariable} names the {DeltaApplierName} hot reload agent");
        }

        if (probe.GetEnvironmentVariable(WatchVariable) == "1")
        {
            active.Add($"{WatchVariable}=1 (launched by dotnet watch)");
        }

        var refusals = new List<string>();
        if (!options.Enabled && active.Count > 0)
        {
            refusals.Add(
                $"Code hot reload is disabled ({HotCodeOptions.SectionName}:Enabled=false) but this process is set up for it: "
                + string.Join("; ", active)
                + $". Start through scripts/hot-runner.ps1, or set {HotCodeOptions.SectionName}:Enabled=true "
                + "(environment variable World__HotCode__Enabled=true) in a Development or Staging environment, "
                + "or remove the hot reload variables (a Visual Studio / Rider debug session sets them too).");
        }

        if (options.Enabled && !IsDevelopmentLike(probe.EnvironmentName))
        {
            refusals.Add(
                $"{HotCodeOptions.SectionName}:Enabled=true is only accepted when the host environment is Development or Staging, "
                + $"but it is '{probe.EnvironmentName}'. Set DOTNET_ENVIRONMENT=Development, or turn code hot reload off.");
        }

        return new HotCodeVerdict(refusals, active.Count > 0);
    }

    /// <summary>
    /// Evaluate and enforce: audit the decision and throw <see cref="HotCodeRefusedException"/>
    /// on a refusal. Call before the host is built and before any database is touched.
    /// </summary>
    public static HotCodeVerdict Enforce(HotCodeOptions options, IRuntimeProbe probe, HotCodeAudit audit)
    {
        HotCodeVerdict verdict = Evaluate(options, probe);
        if (!verdict.Allowed)
        {
            string message = string.Join(" ", verdict.Refusals);
            try
            {
                audit.Record("start-refused", message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The refusal itself is what matters; an unwritable audit file must not mask it.
            }

            throw new HotCodeRefusedException(message);
        }

        if (options.Enabled)
        {
            audit.Record("start-allowed", $"environment={probe.EnvironmentName}; hotReloadActive={verdict.HotReloadActive}");
        }

        return verdict;
    }

    private static bool IsDevelopmentLike(string environmentName)
        => environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase)
            || environmentName.Equals("Staging", StringComparison.OrdinalIgnoreCase);
}

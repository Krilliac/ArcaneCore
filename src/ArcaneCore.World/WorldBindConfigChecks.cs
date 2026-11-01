using System.Net;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World;

/// <summary>
/// Start-up check of the gateway worker listener's address (<c>Cluster:Worker:BindAddress</c>), in the style of the realm's
/// <c>AuthConfigChecks</c>: <c>GatewayWorkerEndpoint</c> runs <c>IPAddress.Parse</c> on it, so a bad value would otherwise pass
/// <c>check-config</c> and throw at start. Only checked while worker mode is on (<c>Cluster:Worker:Enabled</c>), the only time the
/// address is read. <c>World:BindAddress</c> is checked by <c>WorldConfigChecks</c>. Stateless; run once before the host is built.
/// </summary>
public sealed class WorldBindConfigChecks : IConfigCheck
{
    private const string BindKey = GatewayWorkerOptions.SectionName + ":BindAddress";

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();
        bool workerEnabled = bool.TryParse(configuration[$"{GatewayWorkerOptions.SectionName}:Enabled"], out bool enabled) && enabled;
        string? bind = configuration[BindKey];

        // Only a missing key falls back to the default; a present-but-empty value is bound as "" and IPAddress.Parse("") throws.
        if (workerEnabled && bind is not null && !IPAddress.TryParse(bind, out _))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, BindKey, $"'{bind}' is not an IP address.",
                "use an IPv4/IPv6 address such as 127.0.0.1 or ::1 (host names are not resolved)"));
        }

        return issues;
    }
}

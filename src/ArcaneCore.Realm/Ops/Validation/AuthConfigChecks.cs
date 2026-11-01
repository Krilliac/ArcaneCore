using System.Net;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.Realm.Ops.Validation;

/// <summary>
/// Start-up checks for the logon daemon's listener (<c>Auth:BindAddress</c>, <c>Auth:Port</c>). Without them a bad address passes
/// <c>check-config</c> and then throws from <c>IPAddress.Parse</c> inside the listener's start, so the process dies with an
/// unhandled-exception code that a supervisor relaunches, instead of exiting 78. Stateless; run once before the host is built.
/// </summary>
public sealed class AuthConfigChecks : IConfigCheck
{
    private const string BindKey = AuthOptions.SectionName + ":BindAddress";
    private const string PortKey = AuthOptions.SectionName + ":Port";

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var issues = new List<ConfigIssue>();

        string? bind = configuration[BindKey];
        // Only a missing key falls back to the default; a present-but-empty value is bound as "" and IPAddress.Parse("") throws in the listener.
        if (bind is not null && !IPAddress.TryParse(bind, out _))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, BindKey, $"'{bind}' is not an IP address.",
                "use an IPv4/IPv6 address such as 0.0.0.0 or 127.0.0.1 (host names are not resolved)"));
        }

        string? port = configuration[PortKey];
        // A present-but-empty port is an error too: ConfigurationBinder sends the empty value to the Int32 converter, which throws, and the
        // binder rethrows that as InvalidOperationException, so options binding at realm start would crash the daemon (pinned by
        // RealmStartupValidationTests.EmptyAuthPort_ThrowsWhenBoundAtStart). check-config reports it with exit 78 instead.
        if (port is not null
            && !(int.TryParse(port, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) && value is >= 0 and <= 65535))
        {
            issues.Add(new ConfigIssue(ConfigSeverity.Error, PortKey, $"'{port}' is not a valid TCP port.",
                "use a whole number from 0 to 65535 (the vanilla logon port is 3724; 0 lets the OS pick, for tests)"));
        }

        return issues;
    }
}

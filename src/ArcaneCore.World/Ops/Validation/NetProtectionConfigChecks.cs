using System.Globalization;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Ops.Validation;

/// <summary>
/// Startup checks for the <c>Net:Protection</c> section (docs/ops/netguard.md). A value that would
/// make a protection meaningless or the table unusable is an error (exit 78); a value that merely
/// weakens one is a warning. Parsed from the raw strings so every problem is listed at once.
/// </summary>
public sealed class NetProtectionConfigChecks : IConfigCheck
{
    private const string Root = NetProtectionOptions.SectionName;

    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        var defaults = new NetProtectionOptions();

        int? perIp = Int(issues, configuration, $"{Root}:MaxConnectionsPerIp", defaults.MaxConnectionsPerIp, 0, int.MaxValue, "0 to disable, or a positive connection count");
        int? connBurst = Int(issues, configuration, $"{Root}:ConnectionBurstPerIp", defaults.ConnectionBurstPerIp, 0, int.MaxValue, "0 to disable, or a positive connection count");
        int? connRate = Int(issues, configuration, $"{Root}:ConnectionsPerMinutePerIp", defaults.ConnectionsPerMinutePerIp, 0, int.MaxValue, "0 or a positive number of connections per minute");
        int? authBurst = Int(issues, configuration, $"{Root}:AuthFailureBurstPerIp", defaults.AuthFailureBurstPerIp, 0, int.MaxValue, "0 to disable, or a positive attempt count");
        int? authRate = Int(issues, configuration, $"{Root}:AuthFailuresPerMinutePerIp", defaults.AuthFailuresPerMinutePerIp, 0, int.MaxValue, "0 or a positive number of attempts per minute");
        Int(issues, configuration, $"{Root}:MaxTrackedAddresses", defaults.MaxTrackedAddresses, 1, 1 << 20, "1 to 1048576 slots (about 48 bytes each, allocated at start)");
        TimeSpan? idle = Time(issues, configuration, $"{Root}:AddressIdleEviction", defaults.AddressIdleEviction, TimeSpan.FromSeconds(1), "a duration of at least one second, for example 00:10:00");
        Time(issues, configuration, $"{Root}:FrameReadTimeout", defaults.FrameReadTimeout, TimeSpan.Zero, "00:00:00 to disable, or a duration such as 00:00:30");
        Time(issues, configuration, $"{Root}:LogonUnauthenticatedLifetime", defaults.LogonUnauthenticatedLifetime, TimeSpan.Zero, "00:00:00 to disable, or a duration such as 00:00:30");
        Time(issues, configuration, $"{Root}:LogInterval", defaults.LogInterval, TimeSpan.Zero, "00:00:00 to log every refusal, or an interval such as 00:00:10");
        int? opcodeBurst = Int(issues, configuration, $"{Root}:WorldOpcodeBurst", defaults.WorldOpcodeBurst, 0, int.MaxValue, "0 to disable, or a positive packet count");
        double? opcodeRefill = Double(issues, configuration, $"{Root}:WorldOpcodeRefillPerSecond", defaults.WorldOpcodeRefillPerSecond, 0, "0 or a positive number of packets per second");
        int? perSecond = Int(issues, configuration, $"{Root}:WorldPacketsPerSecond", defaults.WorldPacketsPerSecond, 0, int.MaxValue, "0 to disable, or a positive packet count");
        int? flood = Int(issues, configuration, $"{Root}:WorldFloodPacketsPerSecond", defaults.WorldFloodPacketsPerSecond, 0, int.MaxValue, "0 to disable, or a positive packet count");

        if (opcodeBurst is > 0 && opcodeRefill is 0)
        {
            issues.Add(Warn($"{Root}:WorldOpcodeRefillPerSecond", "the per-opcode buckets never refill: once a connection has sent WorldOpcodeBurst packets of one opcode, every later one is dropped.", "set a positive rate, or 0 for WorldOpcodeBurst to disable the per-opcode buckets"));
        }

        if (opcodeBurst is > 0 and < 100)
        {
            issues.Add(Warn($"{Root}:WorldOpcodeBurst", $"{opcodeBurst} is below what a retail client sends of one query opcode when its cache is empty; queries would be dropped and the client does not repeat them.", "use 0 (off) or at least a few hundred"));
        }

        if (flood is > 0 && perSecond is > 0 && flood <= perSecond)
        {
            issues.Add(Warn($"{Root}:WorldFloodPacketsPerSecond", "the flood cap is not above WorldPacketsPerSecond, so a burst disconnects instead of being dropped first.", "set it above WorldPacketsPerSecond"));
        }

        if (connBurst is > 0 && connRate is 0)
        {
            issues.Add(Warn($"{Root}:ConnectionsPerMinutePerIp", "the connection budget never refills: an address that uses its burst can never connect again until its slot idles out.", "set a positive rate, or 0 for ConnectionBurstPerIp to disable the rate limit"));
        }

        if (authBurst is > 0 && authRate is 0)
        {
            issues.Add(Warn($"{Root}:AuthFailuresPerMinutePerIp", "the failure budget never refills: an address that fails its burst stays refused until its slot idles out.", "set a positive rate, or 0 for AuthFailureBurstPerIp to disable the failure limit"));
        }

        if (idle is { } window && authBurst is > 0 && authRate is > 0 && window < TimeSpan.FromMinutes((double)authBurst.Value / authRate.Value))
        {
            issues.Add(Warn($"{Root}:AddressIdleEviction", "shorter than the time a spent failure budget takes to refill, so a limited address can be forgotten and start over.", $"use at least {TimeSpan.FromMinutes((double)authBurst.Value / authRate.Value):c}, or lower AuthFailureBurstPerIp"));
        }

        if (perIp is 0 && connBurst is 0)
        {
            issues.Add(Warn($"{Root}:MaxConnectionsPerIp", "both per-address connection limits are off; one address may hold and open any number of connections.", "set MaxConnectionsPerIp or ConnectionBurstPerIp (or the daemon's own MaxConnectionsPerIp) unless a proxy in front enforces this"));
        }

        return issues;
    }

    private static int? Int(List<ConfigIssue> issues, IConfiguration configuration, string key, int fallback, int min, int max, string expected)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            issues.Add(Error(key, $"'{text}' is not a whole number.", "use a whole number"));
            return null;
        }

        if (value < min || value > max)
        {
            issues.Add(Error(key, $"{value} is out of range.", $"use {expected}"));
            return null;
        }

        return value;
    }

    private static double? Double(List<ConfigIssue> issues, IConfiguration configuration, string key, double fallback, double min, string expected)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            issues.Add(Error(key, $"'{text}' is not a number.", "use a number such as 250"));
            return null;
        }

        if (value < min)
        {
            issues.Add(Error(key, $"{value.ToString(CultureInfo.InvariantCulture)} is out of range.", $"use {expected}"));
            return null;
        }

        return value;
    }

    private static TimeSpan? Time(List<ConfigIssue> issues, IConfiguration configuration, string key, TimeSpan fallback, TimeSpan min, string expected)
    {
        string? text = configuration[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan value))
        {
            issues.Add(Error(key, $"'{text}' is not a duration.", "use hh:mm:ss, for example 00:00:30"));
            return null;
        }

        if (value < min)
        {
            issues.Add(Error(key, $"{value:c} is out of range.", $"use {expected}"));
            return null;
        }

        return value;
    }

    private static ConfigIssue Error(string key, string problem, string fix) => new(ConfigSeverity.Error, key, problem, fix);

    private static ConfigIssue Warn(string key, string problem, string fix) => new(ConfigSeverity.Warning, key, problem, fix);
}

using System.Globalization;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Configuration.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>
/// The rules every <c>Ops:Watchdog</c> value must satisfy, applied twice: as an options validator
/// at host start (an invalid section stops the host before anything binds) and as an
/// <see cref="IConfigCheck"/> for <c>check-config</c> and the world daemon's start-up report (exit
/// 78 with key, problem and fix). Both fail closed: the daemon does not run with a watchdog
/// configuration it cannot honour.
/// </summary>
public sealed class WatchdogOptionsValidation : IValidateOptions<WatchdogOptions>, IConfigCheck
{
    private const string Root = WatchdogOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, WatchdogOptions options)
    {
        List<ConfigIssue> issues = Problems(options);
        return issues.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(issues.Select(i => i.ToString()));
    }

    /// <summary>The problems of a bound options object.</summary>
    public static List<ConfigIssue> Problems(WatchdogOptions o)
    {
        var issues = new List<ConfigIssue>();
        Range(issues, $"{Root}:CheckIntervalMs", o.CheckIntervalMs, 100, 60_000, "100-60000 milliseconds");
        Range(issues, $"{Root}:TickMonitor:RingCapacity", o.TickMonitor.RingCapacity, TickRing.MinCapacity, TickRing.MaxCapacity, $"{TickRing.MinCapacity}-{TickRing.MaxCapacity} ticks");
        NonNegative(issues, $"{Root}:TickMonitor:BudgetMs", o.TickMonitor.BudgetMs);
        NonNegative(issues, $"{Root}:TickMonitor:HangMs", o.TickMonitor.HangMs);
        Range(issues, $"{Root}:TickMonitor:WarnIntervalSeconds", o.TickMonitor.WarnIntervalSeconds, 1, 86_400, "1-86400 seconds");
        NonNegative(issues, $"{Root}:TickMonitor:SummaryIntervalSeconds", o.TickMonitor.SummaryIntervalSeconds);
        if (o.TickMonitor.HangMs > 0 && o.TickMonitor.BudgetMs > 0 && o.TickMonitor.HangMs <= o.TickMonitor.BudgetMs)
        {
            issues.Add(Error($"{Root}:TickMonitor:HangMs", $"{o.TickMonitor.HangMs} is not above BudgetMs ({o.TickMonitor.BudgetMs}).", "a hang must be longer than an overrun; use a larger HangMs or 0 to disable hang detection"));
        }

        Range(issues, $"{Root}:Memory:SampleIntervalSeconds", o.Memory.SampleIntervalSeconds, 1, 3600, "1-3600 seconds");
        NonNegative(issues, $"{Root}:Memory:GrowthLogBytes", o.Memory.GrowthLogBytes);
        NonNegative(issues, $"{Root}:Memory:WarnHeapBytes", o.Memory.WarnHeapBytes);
        Range(issues, $"{Root}:Memory:WarnLoadPercent", o.Memory.WarnLoadPercent, 0, 100, "0 to disable, or 1-100");
        NonNegative(issues, $"{Root}:Memory:ActionHeapBytes", o.Memory.ActionHeapBytes);
        Range(issues, $"{Root}:Memory:ActionCooldownSeconds", o.Memory.ActionCooldownSeconds, 1, 86_400, "1-86400 seconds");
        Range(issues, $"{Root}:Memory:WarnIntervalSeconds", o.Memory.WarnIntervalSeconds, 1, 86_400, "1-86400 seconds");
        if (!Enum.IsDefined(o.Memory.Action))
        {
            issues.Add(Error($"{Root}:Memory:Action", $"'{o.Memory.Action}' is not an action.", "use " + string.Join(", ", Enum.GetNames<MemoryPressureAction>())));
        }

        if (o.Memory.Action != MemoryPressureAction.None && o.Memory.ActionHeapBytes == 0)
        {
            issues.Add(Warn($"{Root}:Memory:ActionHeapBytes", $"Memory:Action is {o.Memory.Action} but ActionHeapBytes is 0, so the action never fires.", "set the heap size in bytes at which it fires, or set Action to None"));
        }

        Range(issues, $"{Root}:ThreadPool:ProbeIntervalSeconds", o.ThreadPool.ProbeIntervalSeconds, 1, 3600, "1-3600 seconds");
        NonNegative(issues, $"{Root}:ThreadPool:WarnDelayMs", o.ThreadPool.WarnDelayMs);
        NonNegative(issues, $"{Root}:ThreadPool:CriticalDelayMs", o.ThreadPool.CriticalDelayMs);
        Range(issues, $"{Root}:ThreadPool:WarnIntervalSeconds", o.ThreadPool.WarnIntervalSeconds, 1, 86_400, "1-86400 seconds");
        if (o.ThreadPool.CriticalDelayMs > 0 && o.ThreadPool.WarnDelayMs > 0 && o.ThreadPool.CriticalDelayMs <= o.ThreadPool.WarnDelayMs)
        {
            issues.Add(Error($"{Root}:ThreadPool:CriticalDelayMs", $"{o.ThreadPool.CriticalDelayMs} is not above WarnDelayMs ({o.ThreadPool.WarnDelayMs}).", "use a larger critical delay, or 0 to disable the critical line"));
        }

        if (!Enum.IsDefined(o.Heartbeat.Mode))
        {
            issues.Add(Error($"{Root}:Heartbeat:Mode", $"'{o.Heartbeat.Mode}' is not a heartbeat mode.", "use " + string.Join(", ", Enum.GetNames<HeartbeatMode>())));
        }

        NonNegative(issues, $"{Root}:Heartbeat:IntervalSeconds", o.Heartbeat.IntervalSeconds);
        Range(issues, $"{Root}:Heartbeat:WarnIntervalSeconds", o.Heartbeat.WarnIntervalSeconds, 1, 86_400, "1-86400 seconds");
        if (o.Heartbeat.Mode == HeartbeatMode.File && string.IsNullOrWhiteSpace(o.Heartbeat.FilePath))
        {
            issues.Add(Error($"{Root}:Heartbeat:FilePath", "Heartbeat:Mode is File but no FilePath is set.", "set the liveness file path, or choose another mode"));
        }

        NonNegative(issues, $"{Root}:Counters:DumpIntervalSeconds", o.Counters.DumpIntervalSeconds);
        return issues;
    }

    /// <summary>The <c>check-config</c> view: the raw strings are parsed one by one so every bad value is listed, not only the first the binder trips on.</summary>
    public IEnumerable<ConfigIssue> Check(IConfiguration configuration)
    {
        var issues = new List<ConfigIssue>();
        var options = new WatchdogOptions();
        IConfigurationSection section = configuration.GetSection(WatchdogOptions.SectionName);
        if (!section.Exists())
        {
            return issues;
        }

        options.Enabled = Bool(issues, section, "Enabled", options.Enabled);
        options.CheckIntervalMs = Int(issues, section, "CheckIntervalMs", options.CheckIntervalMs);
        options.TickMonitor.Enabled = Bool(issues, section, "TickMonitor:Enabled", options.TickMonitor.Enabled);
        options.TickMonitor.RingCapacity = Int(issues, section, "TickMonitor:RingCapacity", options.TickMonitor.RingCapacity);
        options.TickMonitor.BudgetMs = Int(issues, section, "TickMonitor:BudgetMs", options.TickMonitor.BudgetMs);
        options.TickMonitor.HangMs = Int(issues, section, "TickMonitor:HangMs", options.TickMonitor.HangMs);
        options.TickMonitor.WarnIntervalSeconds = Int(issues, section, "TickMonitor:WarnIntervalSeconds", options.TickMonitor.WarnIntervalSeconds);
        options.TickMonitor.SummaryIntervalSeconds = Int(issues, section, "TickMonitor:SummaryIntervalSeconds", options.TickMonitor.SummaryIntervalSeconds);
        options.TickMonitor.GatesHeartbeat = Bool(issues, section, "TickMonitor:GatesHeartbeat", options.TickMonitor.GatesHeartbeat);
        options.Memory.Enabled = Bool(issues, section, "Memory:Enabled", options.Memory.Enabled);
        options.Memory.SampleIntervalSeconds = Int(issues, section, "Memory:SampleIntervalSeconds", options.Memory.SampleIntervalSeconds);
        options.Memory.GrowthLogBytes = Long(issues, section, "Memory:GrowthLogBytes", options.Memory.GrowthLogBytes);
        options.Memory.WarnHeapBytes = Long(issues, section, "Memory:WarnHeapBytes", options.Memory.WarnHeapBytes);
        options.Memory.WarnLoadPercent = Int(issues, section, "Memory:WarnLoadPercent", options.Memory.WarnLoadPercent);
        options.Memory.Action = EnumValue(issues, section, "Memory:Action", options.Memory.Action);
        options.Memory.ActionHeapBytes = Long(issues, section, "Memory:ActionHeapBytes", options.Memory.ActionHeapBytes);
        options.Memory.ActionCooldownSeconds = Int(issues, section, "Memory:ActionCooldownSeconds", options.Memory.ActionCooldownSeconds);
        options.Memory.FullGcNotifications = Bool(issues, section, "Memory:FullGcNotifications", options.Memory.FullGcNotifications);
        options.Memory.WarnIntervalSeconds = Int(issues, section, "Memory:WarnIntervalSeconds", options.Memory.WarnIntervalSeconds);
        options.ThreadPool.Enabled = Bool(issues, section, "ThreadPool:Enabled", options.ThreadPool.Enabled);
        options.ThreadPool.ProbeIntervalSeconds = Int(issues, section, "ThreadPool:ProbeIntervalSeconds", options.ThreadPool.ProbeIntervalSeconds);
        options.ThreadPool.WarnDelayMs = Int(issues, section, "ThreadPool:WarnDelayMs", options.ThreadPool.WarnDelayMs);
        options.ThreadPool.CriticalDelayMs = Int(issues, section, "ThreadPool:CriticalDelayMs", options.ThreadPool.CriticalDelayMs);
        options.ThreadPool.WarnIntervalSeconds = Int(issues, section, "ThreadPool:WarnIntervalSeconds", options.ThreadPool.WarnIntervalSeconds);
        options.Heartbeat.Mode = EnumValue(issues, section, "Heartbeat:Mode", options.Heartbeat.Mode);
        options.Heartbeat.IntervalSeconds = Int(issues, section, "Heartbeat:IntervalSeconds", options.Heartbeat.IntervalSeconds);
        options.Heartbeat.FilePath = section["Heartbeat:FilePath"] ?? options.Heartbeat.FilePath;
        options.Heartbeat.WarnIntervalSeconds = Int(issues, section, "Heartbeat:WarnIntervalSeconds", options.Heartbeat.WarnIntervalSeconds);
        options.Counters.DumpIntervalSeconds = Int(issues, section, "Counters:DumpIntervalSeconds", options.Counters.DumpIntervalSeconds);
        options.Counters.ChangedOnly = Bool(issues, section, "Counters:ChangedOnly", options.Counters.ChangedOnly);
        if (issues.Count == 0)
        {
            issues.AddRange(Problems(options));
        }

        return issues;
    }

    private static int Int(List<ConfigIssue> issues, IConfigurationSection section, string key, int fallback)
    {
        string? text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        issues.Add(Error($"{Root}:{key}", $"'{text}' is not a whole number.", "use a whole number"));
        return fallback;
    }

    private static long Long(List<ConfigIssue> issues, IConfigurationSection section, string key, long fallback)
    {
        string? text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        issues.Add(Error($"{Root}:{key}", $"'{text}' is not a whole number.", "use a whole number of bytes"));
        return fallback;
    }

    private static bool Bool(List<ConfigIssue> issues, IConfigurationSection section, string key, bool fallback)
    {
        string? text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (bool.TryParse(text, out bool value))
        {
            return value;
        }

        issues.Add(Error($"{Root}:{key}", $"'{text}' is not true or false.", "use true or false"));
        return fallback;
    }

    private static T EnumValue<T>(List<ConfigIssue> issues, IConfigurationSection section, string key, T fallback) where T : struct, Enum
    {
        string? text = section[key];
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value))
        {
            return value;
        }

        issues.Add(Error($"{Root}:{key}", $"'{text}' is not one of the allowed values.", "use " + string.Join(", ", Enum.GetNames<T>())));
        return fallback;
    }

    private static void Range(List<ConfigIssue> issues, string key, long value, long min, long max, string expected)
    {
        if (value < min || value > max)
        {
            issues.Add(Error(key, $"{value} is out of range.", $"use {expected}"));
        }
    }

    private static void NonNegative(List<ConfigIssue> issues, string key, long value)
    {
        if (value < 0)
        {
            issues.Add(Error(key, $"{value} is negative.", "use 0 to disable, or a positive value"));
        }
    }

    private static ConfigIssue Error(string key, string problem, string fix) => new(ConfigSeverity.Error, key, problem, fix);

    private static ConfigIssue Warn(string key, string problem, string fix) => new(ConfigSeverity.Warning, key, problem, fix);
}

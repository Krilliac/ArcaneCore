using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>The file sink is restart-only; a reload mentions it only when the resolved <see cref="LogFileSinkPlan"/> differs.</summary>
public sealed class LogReloadPlanCompareTests
{
    private static ArcaneLoggingOptions Opts(Action<ArcaneLoggingOptions> configure)
    {
        var options = new ArcaneLoggingOptions();
        options.Console.Mode = ConsoleMode.Plain;
        configure(options);
        return options;
    }

    private static string Reload(ArcaneLoggingOptions initial, ArcaneLoggingOptions next)
    {
        var output = new StringWriter();
        var monitor = new Monitor(initial);
        LogSink[] sinks = [new TextLogSink("console", new TextWriterLineWriter(output), color: false)];
        using var provider = new ArcaneLoggerProvider(initial, colorAllowed: false, sinks, monitor);
        monitor.Change(next);
        return output.ToString();
    }

    [Fact]
    public void Reload_JsonPathChangedToTheSameEffectivePath_LogsNothing()
    {
        // File:Path wins over Json:Path when Format is set, so changing Json:Path does not change the plan.
        ArcaneLoggingOptions initial = Opts(o => { o.Format = LogFileFormat.Text; o.File.Enabled = true; o.File.Path = "logs/a.log"; o.Json.Path = "logs/x.jsonl"; });
        ArcaneLoggingOptions next = Opts(o => { o.Format = LogFileFormat.Text; o.File.Enabled = true; o.File.Path = "logs/a.log"; o.Json.Path = "logs/y.jsonl"; });

        Assert.DoesNotContain("can't be changed at reload", Reload(initial, next), StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_PathMovedFromJsonPathToFilePath_LogsNothing()
    {
        ArcaneLoggingOptions initial = Opts(o => { o.Format = LogFileFormat.Json; o.File.Enabled = true; o.Json.Path = "logs/a.jsonl"; });
        ArcaneLoggingOptions next = Opts(o => { o.Format = LogFileFormat.Json; o.File.Enabled = true; o.File.Path = "logs/a.jsonl"; });

        Assert.DoesNotContain("can't be changed at reload", Reload(initial, next), StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_RealPathChange_StillLogsKeepStartValue()
    {
        ArcaneLoggingOptions initial = Opts(o => { o.Format = LogFileFormat.Text; o.File.Enabled = true; o.File.Path = "logs/a.log"; });
        ArcaneLoggingOptions next = Opts(o => { o.Format = LogFileFormat.Text; o.File.Enabled = true; o.File.Path = "logs/b.log"; });

        string text = Reload(initial, next);

        Assert.Contains("Logging:ArcaneCore:File:Path option can't be changed at reload; still logs/a.log, restart to apply logs/b.log.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_FormatChangeWhileEnabled_StillLogs()
    {
        ArcaneLoggingOptions initial = Opts(o => { o.Format = LogFileFormat.Text; o.File.Enabled = true; o.File.Path = "logs/a.log"; });
        ArcaneLoggingOptions next = Opts(o => { o.Format = LogFileFormat.Json; o.File.Enabled = true; o.File.Path = "logs/a.log"; });

        Assert.Contains("Logging:ArcaneCore:Format option can't be changed at reload", Reload(initial, next), StringComparison.Ordinal);
    }

    [Fact]
    public void Reload_FileDisabledBothTimes_FormatNoise_LogsNothing()
    {
        // Disabled on both sides: the plan is idle, so the path and tuning keys are compared but Format is not.
        ArcaneLoggingOptions initial = Opts(o => { o.File.Path = "logs/a.log"; });
        ArcaneLoggingOptions next = Opts(o => { o.Format = LogFileFormat.Json; o.File.Path = "logs/a.log"; });

        Assert.DoesNotContain("can't be changed at reload", Reload(initial, next), StringComparison.Ordinal);
    }

    private sealed class Monitor(ArcaneLoggingOptions current) : IOptionsMonitor<ArcaneLoggingOptions>
    {
        private Action<ArcaneLoggingOptions, string?>? _listener;

        public ArcaneLoggingOptions CurrentValue { get; private set; } = current;

        public ArcaneLoggingOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ArcaneLoggingOptions, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void Change(ArcaneLoggingOptions next)
        {
            CurrentValue = next;
            _listener?.Invoke(next, Options.DefaultName);
        }
    }
}

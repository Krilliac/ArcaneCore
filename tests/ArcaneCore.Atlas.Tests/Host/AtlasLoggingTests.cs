using System.Collections.Concurrent;
using System.Net;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host.Cli;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// What <c>arcane-atlas</c> writes to its log: the CLI's own logger factory (<see cref="AtlasCli.CreateLoggers"/>) started under a real
/// loopback server, with a capturing provider next to the console one. Live, every poll of the web UI wrote "Request starting" and
/// "Request finished" lines at Information.
/// </summary>
public sealed class AtlasLoggingTests
{
    private sealed record Entry(string Category, LogLevel Level, string Message);

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, Entries);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
        }
    }

    private static void Boom(AtlasContext context)
        => context.Commands.Register(new CommandDefinition("test.boom", "Throws an exception the registry does not translate.", [],
            static (_, _) => throw new DivideByZeroException("boom")));

    private static async Task GetTilesAsync(HostRig rig, int count)
    {
        for (int i = 0; i < count; i++)
        {
            using HttpResponseMessage response = await rig.Http.GetAsync("api/tiles?map=0");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await response.Content.ReadAsStringAsync();
        }
    }

    [Fact]
    public async Task TenTileRequests_WriteNoInformationLinesFromAspNetCore()
    {
        var capture = new CapturingProvider();
        using ILoggerFactory loggers = AtlasCli.CreateLoggers(LogLevel.Information, configure: b => b.AddProvider(capture));
        await using HostRig rig = await HostRig.StartAsync(loggers: loggers);

        await GetTilesAsync(rig, 10);

        Assert.DoesNotContain(capture.Entries, e => e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && e.Level < LogLevel.Warning);
    }

    [Fact]
    public async Task WithoutTheFilter_TheSameRequestsDoLogAtInformation_SoTheCheckAboveIsNotVacuous()
    {
        var capture = new CapturingProvider();
        using ILoggerFactory loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(capture));
        await using HostRig rig = await HostRig.StartAsync(loggers: loggers);

        await GetTilesAsync(rig, 10);

        Assert.True(
            capture.Entries.Count(e => e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) && e.Level == LogLevel.Information) >= 10,
            "the control run must show the per-request lines this fix removes");
    }

    [Fact]
    public async Task ARequestThatEndsIn500_StillLogsAtWarningOrAbove()
    {
        var capture = new CapturingProvider();
        using ILoggerFactory loggers = AtlasCli.CreateLoggers(LogLevel.Information, configure: b => b.AddProvider(capture));
        await using HostRig rig = await HostRig.StartAsync(loggers: loggers, register: Boom);

        using var body = new StringContent("""{"name":"test.boom"}""", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await rig.Http.PostAsync("api/command", body);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(capture.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains("/api/command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheNowListeningLine_IsStillWritten()
    {
        var capture = new CapturingProvider();
        using ILoggerFactory loggers = AtlasCli.CreateLoggers(LogLevel.Information, configure: b => b.AddProvider(capture));
        await using HostRig rig = await HostRig.StartAsync(loggers: loggers);

        Entry line = Assert.Single(capture.Entries, e => e.Message.StartsWith("Now listening on", StringComparison.Ordinal));
        Assert.Equal("Microsoft.Hosting.Lifetime", line.Category);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains(rig.Server!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListeningLine_SurvivesAWarningLogLevel_ButRequestLinesStayOut()
    {
        var capture = new CapturingProvider();
        using ILoggerFactory loggers = AtlasCli.CreateLoggers(LogLevel.Warning, configure: b => b.AddProvider(capture));
        await using HostRig rig = await HostRig.StartAsync(loggers: loggers);

        await GetTilesAsync(rig, 3);

        Assert.Contains(capture.Entries, e => e.Message.StartsWith("Now listening on", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Entries, e => e.Level < LogLevel.Warning && e.Category != "Microsoft.Hosting.Lifetime");
    }

    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("INFO", LogLevel.Information)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("none", LogLevel.None)]
    public void LogLevel_NamesAreAcceptedInAnyCase(string text, LogLevel expected)
        => Assert.Equal(expected, AtlasCli.ParseLogLevel(text, LogLevel.Information));

    [Fact]
    public void LogLevel_DefaultsWhenAbsent_AndRejectsUnknownNames()
    {
        Assert.Equal(LogLevel.Warning, AtlasCli.ParseLogLevel(null, LogLevel.Warning));
        Assert.Throws<UsageException>(() => AtlasCli.ParseLogLevel("loud", LogLevel.Information));
        Assert.Throws<UsageException>(() => AtlasCli.ParseLogLevel("", LogLevel.Information));
    }

    [Theory]
    [InlineData("serve", "--log-level", "loud")]
    [InlineData("run", "--log-level", "loud", "status")]
    [InlineData("mcp", "--headless", "--log-level", "loud")]
    public async Task ABadLogLevel_IsAUsageError_OnTheVerbsThatLog(params string[] args)
    {
        using var rig = new CliRig();

        CliResult result = await rig.RunAsync(args);

        Assert.Equal(2, result.Exit);
        Assert.Contains("--log-level must be", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogFile_WritesUtf8Lines_WhileTheFactoryIsOpen_AndTheLogLevelFiltersIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "atlas-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "nested", "atlas.log");
        try
        {
            using (ILoggerFactory loggers = AtlasCli.CreateLoggers(LogLevel.Information, logFile: path))
            {
                ILogger log = loggers.CreateLogger("Test.Category");
                log.LogInformation("héllo wörld ☃");
                log.LogDebug("below the level");
                log.LogError(new InvalidOperationException("bad thing"), "it failed");
            }

            await Task.Yield();
            string text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            Assert.Contains("[INF] Test.Category: héllo wörld ☃", text, StringComparison.Ordinal);
            Assert.Contains("[ERR] Test.Category: it failed", text, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException: bad thing", text, StringComparison.Ordinal);
            Assert.DoesNotContain("below the level", text, StringComparison.Ordinal);
            Assert.DoesNotContain('﻿', text); // UTF-8 without a byte order mark
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LogFile_NeedsAPath()
    {
        using var rig = new CliRig();

        CliResult result = await rig.RunAsync("run", "--log-file=", "status");

        Assert.Equal(2, result.Exit);
        Assert.Contains("--log-file needs a path", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_DocumentsTheLoggingOptions()
    {
        Assert.Contains("--log-level", AtlasCli.Usage, StringComparison.Ordinal);
        Assert.Contains("--log-file", AtlasCli.Usage, StringComparison.Ordinal);
    }
}

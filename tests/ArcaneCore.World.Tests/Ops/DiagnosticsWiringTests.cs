using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.World.Ops.Cli;
using ArcaneCore.World.Ops.Diagnostics;
using ArcaneCore.World.Ops.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>The world daemon's share of docs/ops/invariants.md: the Diagnostics config check and the tick context of a crash report.</summary>
public sealed class DiagnosticsWiringTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void DiagnosticsCheck_AcceptsTheDefaultsAndEveryNamedValue_CaseInsensitively()
    {
        var check = new DiagnosticsConfigChecks();
        Assert.Empty(check.Check(Config()));
        Assert.Empty(check.Check(Config(
            ("Diagnostics:OnInvariant", "failfast"),
            ("Diagnostics:OnUnhandled", "Exit"),
            ("Diagnostics:OnUnobservedTask", "FailFast"),
            ("Diagnostics:BreakOnInvariant", "true"),
            ("Diagnostics:FirstChanceExceptions", "false"),
            ("Diagnostics:InvariantLogLimit", "0"))));
    }

    [Theory]
    [InlineData("Diagnostics:OnInvariant", "Ignore")]
    [InlineData("Diagnostics:OnUnhandled", "Bogus")]
    [InlineData("Diagnostics:OnUnhandled", "7")]
    [InlineData("Diagnostics:OnUnobservedTask", "Crash")]
    [InlineData("Diagnostics:BreakOnInvariant", "yes")]
    [InlineData("Diagnostics:InvariantLogLimit", "-1")]
    [InlineData("Diagnostics:InvariantLogLimit", "ten")]
    public void DiagnosticsCheck_RejectsAnUnknownValue_NamingTheKey_AndTheDaemonValidationSeesIt(string key, string value)
    {
        ConfigIssue issue = Assert.Single(new DiagnosticsConfigChecks().Check(Config((key, value))));
        Assert.Equal(ConfigSeverity.Error, issue.Severity);
        Assert.Equal(key, issue.Key);
        Assert.Contains(value, issue.Problem, StringComparison.Ordinal);

        // check-config and the start-up validation run the same list of checks.
        ConfigReport report = OpsCli.Validate(Config((key, value)));
        Assert.Contains(report.Issues, i => i.Key == key && i.Severity == ConfigSeverity.Error);
        Assert.True(report.IsInvalid);
    }

    [Fact]
    public void WorldCrashContext_CountsTicksFromTheWorldTickHook_AndDescribesTheWorld()
    {
        var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NoSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var context = new WorldCrashContext(world);
        Assert.Equal("world", context.Name);
        Assert.Equal(0, context.TickNumber);

        world.RunTick(50);
        world.RunTick(50);
        world.RunTick(7);

        Assert.Equal(3, context.TickNumber);
        var report = new StringBuilder();
        context.Describe(report);
        string text = report.ToString();
        Assert.Contains("tick number (begun): 3\n", text, StringComparison.Ordinal);
        Assert.Contains("last tick diff: 7 ms\n", text, StringComparison.Ordinal);
        Assert.Contains("online players: 0\n", text, StringComparison.Ordinal);
        Assert.Contains("crashing thread is the world thread: yes\n", text, StringComparison.Ordinal); // no world thread is running: every thread counts as it
        Assert.Contains("uptime: ", text, StringComparison.Ordinal);
    }

    private sealed class NoSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

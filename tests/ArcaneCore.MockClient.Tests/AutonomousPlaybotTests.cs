using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Playbots;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

public sealed class AutonomousPlaybotTests
{
    [Fact]
    public async Task ExplicitCombatEntryProducesNormalMeleePacketsAndStopsAfterObservedDeath()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(limit.Token);
        await server.AddAccountAsync("FIGHTBOT", "FIGHTBOTPASSWORD", limit.Token);
        var options = new PlaybotRunOptions(server.RealmEndpoint, "FIGHTBOT", "FIGHTBOTPASSWORD", "Botfighter",
            Steps: 12, Seconds: 25, AttackEntry: SyntheticArcaneServer.TargetEntry, Movement: false);
        PlaybotRunReport report = await AutonomousPlaybot.RunAsync(options, new DeterministicPlaybotSelector(), limit.Token);
        Assert.Equal("budget-complete", report.Outcome);
        Assert.True(report.LogoutComplete);
        Assert.Contains(report.Steps, step => step.Action == "Attack");
        Assert.Contains(report.Steps, step => step.Action == "StopAttack");
        Assert.True(report.Replies.GetValueOrDefault("SmsgAttackerstateupdate") > 0);
        Assert.DoesNotContain(report.Steps, step => step.Action == "Move");
    }

    [Fact]
    public async Task NormalPlayerAuthenticatesCreatesExploresQueriesAndLogsOutWithinStepBudget()
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using SyntheticArcaneServer server = await SyntheticArcaneServer.StartAsync(limit.Token);
        await server.AddAccountAsync("AUTOBOT", "AUTOBOTPASSWORD", limit.Token);
        var options = new PlaybotRunOptions(server.RealmEndpoint, "AUTOBOT", "AUTOBOTPASSWORD", "Botwalker", Steps: 6, Seconds: 20);
        PlaybotRunReport report = await AutonomousPlaybot.RunAsync(options, new DeterministicPlaybotSelector(), limit.Token);
        Assert.Equal("budget-complete", report.Outcome);
        Assert.Null(report.Failure);
        Assert.True(report.EnteredWorld);
        Assert.True(report.LogoutComplete);
        Assert.Equal(6, report.Steps.Count);
        Assert.Contains(report.Steps, step => step.Action == "QueryCreature");
        Assert.Contains(report.Steps, step => step.Action == "Move");
        Assert.True(report.Replies.GetValueOrDefault("SmsgCreatureQueryResponse") > 0);
        Assert.DoesNotContain(report.Steps, step => step.Action == "Attack");
        Assert.All(report.Steps, step => Assert.Equal("deterministic", step.Provider));
        string json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("AUTOBOTPASSWORD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AUTOBOT", json, StringComparison.Ordinal);
    }
}

using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Commands;
using Xunit;

namespace ArcaneCore.World.Tests.Commands;

public sealed class ServerAnomalyCommandsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Overview_WhenEmpty_PrintsZeroCountsAndEmptySubsystemsMessage()
    {
        var ledger = new AnomalyLedger();
        var lines = new List<string>();

        bool ok = ServerFaultCommands.RunAnomalies(string.Empty, AccountSecurity.GameMaster, ledger, T0, lines.Add);

        Assert.True(ok);
        Assert.Equal(2, lines.Count);
        Assert.Equal("anomalies: 0 (0 distinct)", lines[0]);
        Assert.Equal("Anomaly subsystems: 0 anomalies recorded.", lines[1]);
    }

    [Fact]
    public void Overview_SummarizesBySubsystem_AndListsTopFifteenKeysByCount()
    {
        var clock = new FixedClock(T0.AddMinutes(-2));
        var ledger = new AnomalyLedger(new AnomalyLedgerLimits(), clock);

        for (int i = 1; i <= 17; i++)
        {
            string code = $"code-{i:D2}";
            for (int c = 0; c < i; c++)
            {
                ledger.Report("bot-elevator", code, $"bot Thrall (guid {c + 1:X})", $"detail {i}", AnomalySeverity.Warning);
            }
        }

        ledger.Report("movement", "nan-position", "unit 99", "non-finite X", AnomalySeverity.Error);

        var lines = new List<string>();
        bool ok = ServerFaultCommands.RunAnomalies(string.Empty, AccountSecurity.GameMaster, ledger, T0, lines.Add);

        Assert.True(ok);
        Assert.Equal($"anomalies: {ledger.Total} (18 distinct)", lines[0]);
        Assert.Contains(lines, l => l.Contains("Anomaly subsystems (2):", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("bot-elevator: 153 total (17 distinct)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("movement: 1 total (1 distinct)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(" 1. bot-elevator/code-17 x17 (17 subjects, Warning)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("... 3 more;", StringComparison.Ordinal));
    }

    [Fact]
    public void SubsystemFilter_ListsAllKeysForSubsystem_AndHandlesUnknownSubsystem()
    {
        var clock = new FixedClock(T0.AddSeconds(-30));
        var ledger = new AnomalyLedger(new AnomalyLedgerLimits(), clock);
        ledger.Report("bot-quest", "giver-missing", "bot Jaina (guid 10)", "quest 42", AnomalySeverity.Warning);
        ledger.Report("bot-quest", "giver-missing", "bot Uther (guid 11)", "quest 42", AnomalySeverity.Warning);
        ledger.Report("bot-quest", "objective-unreachable", "bot Jaina (guid 10)", "quest 99", AnomalySeverity.Info);
        ledger.Report("bot-elevator", "board-missed", "bot Thrall (guid 12)", "lift 180000", AnomalySeverity.Warning);

        var lines = new List<string>();
        Assert.True(ServerFaultCommands.RunAnomalies("bot-quest", AccountSecurity.GameMaster, ledger, T0, lines.Add));
        Assert.Equal("Subsystem 'bot-quest': 3 total (2 distinct):", lines[0]);
        Assert.Contains(lines, l => l.Contains("bot-quest/giver-missing x2 (2 subjects, Warning)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("bot-quest/objective-unreachable x1 (1 subjects, Info)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("bot-elevator", StringComparison.Ordinal));

        var missingLines = new List<string>();
        Assert.True(ServerFaultCommands.RunAnomalies("nonexistent-sub", AccountSecurity.GameMaster, ledger, T0, missingLines.Add));
        Assert.Equal("No anomalies recorded for subsystem 'nonexistent-sub'.", Assert.Single(missingLines));
    }

    [Fact]
    public void Show_PrintsFullSnapshot_AndRejectsInvalidOrMissingKey()
    {
        var clock = new FixedClock(T0.AddSeconds(-45));
        var ledger = new AnomalyLedger(new AnomalyLedgerLimits(), clock);
        ledger.Report("bot-elevator", "board-missed", "bot Thrall (guid 1A)", "waited 60s at lift", AnomalySeverity.Warning);
        clock.Advance(TimeSpan.FromSeconds(15));
        ledger.Report("bot-elevator", "board-missed", "bot Cairne (guid 1B)", "waited 62s at lift", AnomalySeverity.Error);

        var lines = new List<string>();
        Assert.True(ServerFaultCommands.RunAnomalies("show bot-elevator/board-missed", AccountSecurity.GameMaster, ledger, T0, lines.Add));
        Assert.Equal(4, lines.Count);
        Assert.Contains("Anomaly bot-elevator/board-missed (subsystem bot-elevator, code board-missed, severity Error)", lines[0], StringComparison.Ordinal);
        Assert.Contains("Count 2 (2 distinct subjects);", lines[1], StringComparison.Ordinal);
        Assert.Equal("Subject: bot Cairne (guid 1B)", lines[2]);
        Assert.Equal("Detail: waited 62s at lift", lines[3]);

        var missing = new List<string>();
        Assert.True(ServerFaultCommands.RunAnomalies("show bot-elevator/no-such-code", AccountSecurity.GameMaster, ledger, T0, missing.Add));
        Assert.Equal("No anomaly recorded for key 'bot-elevator/no-such-code'.", Assert.Single(missing));

        // Negative syntax cases: key without slash or extra arguments
        Assert.False(ServerFaultCommands.RunAnomalies("show invalidkeywithoutslash", AccountSecurity.GameMaster, ledger, T0, _ => { }));
        Assert.False(ServerFaultCommands.RunAnomalies("show a/b extra", AccountSecurity.GameMaster, ledger, T0, _ => { }));
        Assert.False(ServerFaultCommands.RunAnomalies("sub1 sub2", AccountSecurity.GameMaster, ledger, T0, _ => { }));
    }

    [Fact]
    public void Reset_RequiresAdministrator_AndClearsLedgerWhenAuthorized()
    {
        var ledger = new AnomalyLedger();
        ledger.Report("bot-dungeon", "portal-loop", "bot Rexxar (guid 5)", " bounced 4 times", AnomalySeverity.Warning);

        var gmReply = new List<string>();
        Assert.True(ServerFaultCommands.RunAnomalies("reset", AccountSecurity.GameMaster, ledger, T0, gmReply.Add));
        Assert.Equal("Only an administrator can reset the anomaly ledger.", Assert.Single(gmReply));
        Assert.Equal(1, ledger.Total);
        Assert.Equal(1, ledger.Distinct);

        var adminReply = new List<string>();
        Assert.True(ServerFaultCommands.RunAnomalies("reset", AccountSecurity.Administrator, ledger, T0, adminReply.Add));
        Assert.Equal("Anomaly ledger cleared (1 distinct, 1 total).", Assert.Single(adminReply));
        Assert.Equal(0, ledger.Total);
        Assert.Equal(0, ledger.Distinct);

        Assert.False(ServerFaultCommands.RunAnomalies("reset extra", AccountSecurity.Administrator, ledger, T0, _ => { }));
    }

    [Fact]
    public void InfoLine_IncludesBothFaultAndAnomalyCounts()
    {
        var faults = new FaultLedger();
        var anomalies = new AnomalyLedger();
        anomalies.Report("bot-loot", "corpse-missing", "bot Valeera (guid 9)", "corpse despawned", AnomalySeverity.Warning);
        anomalies.Report("bot-loot", "corpse-missing", "bot Valeera (guid 9)", "corpse despawned", AnomalySeverity.Warning);

        string combined = ServerFaultCommands.InfoLine(faults, anomalies, T0);
        Assert.Equal("faults: 0 (0 distinct, last never); anomalies: 2 (1 distinct)", combined);
    }

    private sealed class FixedClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Audit;

/// <summary>The structured debug write audit (plan 1.5, I3): the ring, the redaction and <see cref="DebugWrite.Record"/> against a real host.</summary>
public sealed class DebugWriteLogTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void Record_KeepsEveryField_AndStampsTheClock()
    {
        var log = new DebugWriteLog(10, new Clock());
        DebugWriteEntry entry = log.Record(7, "Gmone", "alter speed", "creature #5", "speed", "1", "2.5", DebugWriteScope.Live, "test", true);

        Assert.Equal(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), entry.Unix);
        Assert.Equal((7, "Gmone", "alter speed", "creature #5", "speed", "1", "2.5"),
            (entry.AccountId, entry.Character, entry.Command, entry.Target, entry.Field, entry.OldValue, entry.NewValue));
        Assert.Equal((DebugWriteScope.Live, "test", true), (entry.Scope, entry.Reason, entry.Confirmed));
        Assert.Same(entry, Assert.Single(log.Tail(5)));
    }

    [Fact]
    public void TheRing_DropsTheOldest_AndTotalCountsEverything()
    {
        var log = new DebugWriteLog(3, new Clock());
        for (int i = 0; i < 5; i++) log.Record(1, "G", "cmd", "t", "f", i.ToString(), (i + 1).ToString(), DebugWriteScope.UntilReload);

        Assert.Equal(5, log.Total);
        Assert.Equal(["2", "3", "4"], log.Tail(10).Select(e => e.OldValue));
        Assert.Equal(["4"], log.Tail(1).Select(e => e.OldValue));
        Assert.Empty(log.Tail(0));
        Assert.Empty(log.Tail(-3));
    }

    [Fact]
    public void ACapacityOfZero_KeepsNothing_ButStillCounts()
    {
        var log = new DebugWriteLog(0, new Clock());
        log.Record(1, "G", "cmd", "t", "f", null, null, DebugWriteScope.Persisted);

        Assert.Equal(1, log.Total);
        Assert.Empty(log.Tail(5));
    }

    [Fact]
    public void LongText_IsCut_AndNullBecomesEmpty()
    {
        var log = new DebugWriteLog(2, new Clock());
        DebugWriteEntry entry = log.Record(1, "G", "cmd", "t", "f", new string('x', 1000), null, DebugWriteScope.Live);

        Assert.Equal(DebugWriteLog.MaxValueLength, entry.OldValue.Length);
        Assert.EndsWith("...", entry.OldValue, StringComparison.Ordinal);
        Assert.Equal(string.Empty, entry.NewValue);
        Assert.Equal(string.Empty, entry.Reason);
        Assert.False(entry.Confirmed);
    }

    [Fact]
    public void Hash_IsStable_HidesTheValue_AndKeepsEmptyEmpty()
    {
        string a = DebugWrite.Hash("203.0.113.9:8085");

        Assert.Equal(a, DebugWrite.Hash("203.0.113.9:8085"));
        Assert.NotEqual(a, DebugWrite.Hash("203.0.113.10:8085"));
        Assert.StartsWith("sha256:", a, StringComparison.Ordinal);
        Assert.Equal("sha256:".Length + 8, a.Length);
        Assert.DoesNotContain("203", a.AsSpan("sha256:".Length).ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, DebugWrite.Hash(null));
        Assert.Equal(string.Empty, DebugWrite.Hash(string.Empty));
    }

    [Fact]
    public async Task Record_FilesTheEntryOnTheAuditFeature_ForAPlayerInvoker()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DWLPLAY", "Dwlplayer", AccountSecurity.Administrator);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWLPLAY"))!.Id)!;
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        bool recorded = false;

        await host.OnWorldAsync(() =>
        {
            CommandContext context = WriteTestCommands.Bind(new CommandContext(session, session.Player!, ChatCommands.CreateTable()), "alter speed");
            recorded = DebugWrite.Record(context, "self", "speed", "1", "2", DebugWriteScope.Live, "why", confirmed: true);
        });

        Assert.True(recorded);
        DebugWriteEntry entry = Assert.Single(audit.DebugWrites.Tail(5));
        Assert.Equal(session.AccountId, entry.AccountId);
        Assert.Equal("Dwlplayer", entry.Character);
        Assert.Equal(("alter speed", "self", "speed", "1", "2"), (entry.Command, entry.Target, entry.Field, entry.OldValue, entry.NewValue));
        Assert.Equal((DebugWriteScope.Live, "why", true), (entry.Scope, entry.Reason, entry.Confirmed));
    }

    [Fact]
    public async Task Record_ForTheConsole_UsesTheConsoleName_AndHashesPersonalValues()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DWLCON", "Dwlconsole", AccountSecurity.Administrator);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWLCON"))!.Id)!;
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();

        await host.OnWorldAsync(() =>
        {
            CommandContext context = WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "session kick");
            DebugWrite.Record(context, "session 4", "endpoint", "203.0.113.9:8085", "", DebugWriteScope.Live, sensitive: true);
        });

        DebugWriteEntry entry = Assert.Single(audit.DebugWrites.Tail(5));
        Assert.Equal(CommandContext.ConsoleName, entry.Character);
        Assert.Equal(0, entry.AccountId);
        Assert.Equal(DebugWrite.Hash("203.0.113.9:8085"), entry.OldValue);
        Assert.DoesNotContain("203.0.113.9", entry.OldValue, StringComparison.Ordinal);
        Assert.Equal(string.Empty, entry.NewValue);
    }

    [Fact]
    public async Task Record_ByAnUnflaggedCommand_ThrowsInStrictMode_AndRecordsNothing()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DWLBAD", "Dwlbad", AccountSecurity.Administrator);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWLBAD"))!.Id)!;
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        List<Exception?> thrown = [];

        await host.OnWorldAsync(() =>
        {
            CommandContext flagless = WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "alter speed", isWrite: false);
            thrown.Add(Record.Exception(() => DebugWrite.Record(flagless, "t", "f", "a", "b", DebugWriteScope.Live)));
            CommandContext unbound = CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { });
            thrown.Add(Record.Exception(() => DebugWrite.Record(unbound, "t", "f", "a", "b", DebugWriteScope.Live)));
        });

        Assert.True(audit.DebugWrites.Strict, "the test host runs the audit in strict mode");
        Assert.All(thrown, e => Assert.IsType<InvalidOperationException>(e));
        Assert.Contains("alter speed", thrown[0]!.Message, StringComparison.Ordinal);
        Assert.Equal(0, audit.DebugWrites.Total);
    }

    [Fact]
    public async Task Record_ByAFlaggedCommandRunThroughTheTable_CarriesTheResolvedPath()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("DWLTAB", "Dwltab", AccountSecurity.Administrator);
        WorldSession session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("DWLTAB"))!.Id)!;
        GmAuditFeature audit = host.WorldServices.GetRequiredService<GmAuditFeature>();
        CommandTable table = new([
            new ChatCommand("probe", AccountSecurity.Administrator, "g", null,
            [
                new ChatCommand("set", AccountSecurity.Administrator, "w", (context, args) =>
                {
                    DebugWrite.Record(context, "self", "probe", "1", args, DebugWriteScope.Action);
                    return true;
                }, IsWrite: true),
            ]),
        ]);

        await host.OnWorldAsync(() => table.Execute(CommandContext.ForConsole(session, table, _ => { }), "probe set 5"));

        DebugWriteEntry entry = Assert.Single(audit.DebugWrites.Tail(5));
        Assert.Equal(("probe set", "5", DebugWriteScope.Action), (entry.Command, entry.NewValue, entry.Scope));
    }
}

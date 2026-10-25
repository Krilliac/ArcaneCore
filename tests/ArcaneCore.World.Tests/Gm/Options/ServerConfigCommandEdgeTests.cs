using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Rates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.ConfigEditor;

/// <summary>
/// <c>.server config</c> and <c>.rates history|overrides</c>: the permission matrix for every verb, the syntax and failure replies, paging
/// bounds and the documented command tree.
/// </summary>
public sealed class ServerConfigCommandEdgeTests
{
    private const string NotAvailable = "This command is not available to you.";

    private static async Task<string> RunAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        // Wait for the first reply line (a loaded machine can take longer than any quiet window to answer at all), then collect the rest.
        var lines = new List<string> { (await client.ReadSystemChatAsync()).Text };
        var packets = await client.CollectAsync(TimeSpan.FromMilliseconds(600));
        lines.AddRange(packets.Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload)).Where(m => m.Type == ChatType.System).Select(m => m.Text));
        return string.Join('\n', lines);
    }

    [Fact]
    public void TheCommandTree_HasTheDocumentedVerbs_WithTheRightSecurity()
    {
        CommandTable table = ChatCommands.CreateTable();

        foreach (string read in new[] { "server config", "server config list", "server config get", "server config diff", "rates history", "rates overrides" })
        {
            Assert.Null(table.Resolve(read, AccountSecurity.Player));
            Assert.NotNull(table.Resolve(read, AccountSecurity.GameMaster));
        }

        foreach (string write in new[] { "server config set", "server config reset", "server config sources", "server config dump" })
        {
            Assert.Null(table.Resolve(write, AccountSecurity.Player));
            Assert.Null(table.Resolve(write, AccountSecurity.Moderator));
            Assert.Null(table.Resolve(write, AccountSecurity.GameMaster));
            Assert.NotNull(table.Resolve(write, AccountSecurity.Administrator));
        }
    }

    [Fact]
    public void TheOldSetVerbs_RemainRegistered_ForAdministrators()
    {
        CommandTable table = ChatCommands.CreateTable();

        foreach (string verb in new[] { "rates set", "anticheat set", "movement set", "packetcheck on", "packetcheck bots" })
        {
            Assert.NotNull(table.Resolve(verb, AccountSecurity.Administrator));
        }
    }

    [Fact]
    public async Task APlainPlayer_CannotRunAnyOfTheVerbs_AndNothingChanges()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("PLAYER", "Player", AccountSecurity.Player);
        await player.CollectAsync();
        int original = host.World.Options.AutosaveIntervalMs;

        foreach (string line in new[]
        {
            ".server config", ".server config list", ".server config get World:AutosaveIntervalMs", ".server config diff", ".server config set World:AutosaveIntervalMs 5",
            ".server config reset all", ".server config sources", ".server config dump World", ".rates history", ".rates overrides",
        })
        {
            Assert.Equal(NotAvailable, await RunAsync(player, line));
        }

        Assert.Equal(original, await host.OnWorldAsync(() => host.World.Options.AutosaveIntervalMs));
    }

    [Fact]
    public async Task AGameMaster_ReadsTheRatesViews_AndTheBareCommandListsTheOptions()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Assert.Equal("No rate has changed since start.", await RunAsync(gm, ".rates history"));
        Assert.Equal("No rate is overridden.", await RunAsync(gm, ".rates overrides"));
        Assert.StartsWith("No option is overridden", await RunAsync(gm, ".server config diff"), StringComparison.Ordinal);
        Assert.StartsWith("Server options (", await RunAsync(gm, ".server config"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownKeys_AndWrongArguments_AnswerWithAMessageOrTheSyntax()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        Assert.Contains("unknown option 'No:Such'", await RunAsync(admin, ".server config get No:Such"), StringComparison.Ordinal);
        Assert.Contains("unknown option 'No:Such'", await RunAsync(admin, ".server config reset No:Such"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config get", await RunAsync(admin, ".server config get"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config get", await RunAsync(admin, ".server config get a b"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config reset", await RunAsync(admin, ".server config reset a b"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config diff", await RunAsync(admin, ".server config diff now"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config set", await RunAsync(admin, ".server config set"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .rates history", await RunAsync(admin, ".rates history 0"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .rates history", await RunAsync(admin, ".rates history abc"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .rates history", await RunAsync(admin, ".rates history 1 2"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .rates overrides", await RunAsync(admin, ".rates overrides x"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_AcceptsAQuotedValue_KeepsAMultiWordReason_AndMarksNextLoadRates()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string set = await RunAsync(admin, ".server config set World:AutosaveIntervalMs \"31000\" load test for the weekend");
        Assert.Contains("-> 31000 (until .reload config or restart; not saved to any file).", set, StringComparison.Ordinal);
        Assert.Contains("set by Admin (load test for the weekend)", await RunAsync(admin, ".server config get World:AutosaveIntervalMs"), StringComparison.Ordinal);

        string nextLoad = await RunAsync(admin, ".server config set Rate.Creature.Normal.HP 2");
        Assert.EndsWith("Applies to what is created or loaded afterwards.", nextLoad, StringComparison.Ordinal);
        Assert.Contains("[next-load, OVERRIDDEN]", await RunAsync(admin, ".server config get Rate.Creature.Normal.HP"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ForARestartKey_SaysReadOnly_AndForARateShowsBoundsAndAliases()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        string restart = await RunAsync(gm, ".server config get World:TickIntervalMs");
        Assert.Contains("[restart, read-only]", restart, StringComparison.Ordinal);
        Assert.Contains("apply restart; read-only", restart, StringComparison.Ordinal);

        string rate = await RunAsync(gm, ".server config get xp");
        Assert.Contains("Rate.XP.Kill = 1 [live]", rate, StringComparison.Ordinal);
        Assert.Contains("type Number, ", rate, StringComparison.Ordinal);
        Assert.Contains("writable", rate, StringComparison.Ordinal);
        Assert.Contains("aliases: ", rate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ClampsAPageBeyondTheLast_AndRejectsPageZero()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        string last = await RunAsync(gm, ".server config list page 99999");
        string header = last.Split('\n')[0];
        Assert.Matches(@"\(page (\d+)/\1;", header);
        Assert.True(last.Split('\n').Length is >= 2 and <= 26);

        Assert.StartsWith("Syntax: .server config list", await RunAsync(gm, ".server config list page 0"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config list", await RunAsync(gm, ".server config list page -3"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RatesHistory_IsCappedAt25Rows_AndACountShowsThatMany()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await host.OnWorldAsync(() =>
        {
            var scope = new RateScope(host.WorldServices, host.World, NullLogger.Instance);
            ServerRates rates = ServerRates.For(host.World);
            RateDefinition rate = ServerRates.Find("xp")!;
            for (int i = 0; i < 30; i++)
            {
                Assert.Equal(RateSetResult.Ok, rates.TrySet(scope, rate, 1f + (i % 2), "Seeder", $"change {i}", out _));
            }
        });

        string capped = await RunAsync(gm, ".rates history 999");
        string[] rows = capped.Split('\n');
        Assert.StartsWith("Rate changes (newest first, 25 of 30)", rows[0], StringComparison.Ordinal);
        Assert.Equal(26, rows.Length);
        Assert.Contains("(change 29)", rows[1], StringComparison.Ordinal);
        Assert.Contains("(change 5)", rows[25], StringComparison.Ordinal);

        string one = await RunAsync(gm, ".rates history 1");
        Assert.StartsWith("Rate changes (newest first, 1 of 30)", one, StringComparison.Ordinal);
        Assert.Equal(2, one.Split('\n').Length);
    }

    [Fact]
    public async Task Reset_OfASingleRate_ThroughTheEditor_IsInTheHistory_AndLeavesOthers()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await RunAsync(admin, ".server config set xp 2");
        await RunAsync(admin, ".server config set money 4");
        Assert.Equal("Rate.XP.Kill is back to its baseline value 1.", await RunAsync(admin, ".server config reset xp"));

        string history = await RunAsync(admin, ".rates history");
        Assert.Contains("Reset Rate.XP.Kill: 2 -> 1 by Admin", history, StringComparison.Ordinal);
        string overrides = await RunAsync(admin, ".rates overrides");
        Assert.DoesNotContain("Rate.XP.Kill", overrides, StringComparison.Ordinal);
        Assert.Contains(".rates set Rate.Drop.Money 4", overrides, StringComparison.Ordinal);
    }
}

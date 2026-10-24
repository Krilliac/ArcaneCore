using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Rates;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.ConfigEditor;

/// <summary>
/// <c>.server config</c> and <c>.rates history|overrides</c> end to end over chat: the permission split (GameMaster reads, Administrator
/// writes), the replies, the alias commands showing up in <c>diff</c>, and that a restart-only key stays read-only.
/// </summary>
public sealed class ServerConfigCommandTests
{
    private const string NotAvailable = "This command is not available to you.";

    private static Task SayAsync(WorldTestClient client, string text) => client.SendChatAsync(ChatType.Say, Language.Common, text);

    private static async Task<string> RunAsync(WorldTestClient client, string line)
    {
        await SayAsync(client, line);
        // Wait for the first reply line (a loaded machine can take longer than any quiet window to answer at all), then collect the rest.
        var lines = new List<string> { (await client.ReadSystemChatAsync()).Text };
        var packets = await client.CollectAsync(TimeSpan.FromMilliseconds(600));
        lines.AddRange(packets.Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload)).Where(m => m.Type == ChatType.System).Select(m => m.Text));
        return string.Join('\n', lines);
    }

    [Fact]
    public async Task Set_Get_Diff_Reset_RoundTrip_ForAnAdministrator()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        int original = host.World.Options.AutosaveIntervalMs;

        string set = await RunAsync(admin, ".server config set World:AutosaveIntervalMs 4242 testing the editor");
        Assert.Equal($"Option World:AutosaveIntervalMs: {original} -> 4242 (until .reload config or restart; not saved to any file).", set);
        Assert.Equal(4242, await host.OnWorldAsync(() => host.World.Options.AutosaveIntervalMs));

        string get = await RunAsync(admin, ".server config get autosaveintervalms");
        Assert.StartsWith("World:AutosaveIntervalMs = 4242 [live, OVERRIDDEN]", get, StringComparison.Ordinal);
        Assert.Contains($"baseline {original}, set by Admin (testing the editor)", get, StringComparison.Ordinal);

        string diff = await RunAsync(admin, ".server config diff");
        Assert.Contains($"World:AutosaveIntervalMs: {original} -> 4242 [live] by Admin (testing the editor)", diff, StringComparison.Ordinal);

        string reset = await RunAsync(admin, ".server config reset World:AutosaveIntervalMs");
        Assert.Equal($"World:AutosaveIntervalMs is back to its baseline value {original}.", reset);
        Assert.Equal(original, await host.OnWorldAsync(() => host.World.Options.AutosaveIntervalMs));
        Assert.Equal("World:AutosaveIntervalMs is not overridden.", await RunAsync(admin, ".server config reset World:AutosaveIntervalMs"));
        Assert.StartsWith("No option is overridden", await RunAsync(admin, ".server config diff"), StringComparison.Ordinal);
        Assert.Equal("No option is overridden.", await RunAsync(admin, ".server config reset all"));
    }

    [Fact]
    public async Task Set_RefusesRestartOnlyKeys_BadValues_AndUnknownNames()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        Assert.Contains("read from the configuration at start", await RunAsync(admin, ".server config set World:TickIntervalMs 10"), StringComparison.Ordinal);
        Assert.Contains("is not a whole number", await RunAsync(admin, ".server config set World:AutosaveIntervalMs lots"), StringComparison.Ordinal);
        Assert.Contains("out of range", await RunAsync(admin, ".server config set Rate.XP.Kill 500"), StringComparison.Ordinal);
        Assert.Contains("unknown option 'No:Such'", await RunAsync(admin, ".server config set No:Such 1"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config set", await RunAsync(admin, ".server config set World:AutosaveIntervalMs"), StringComparison.Ordinal);
        Assert.Equal(0, await host.OnWorldAsync(() => ServerRates.For(host.World).OverrideCount));
    }

    [Fact]
    public async Task AGameMaster_CanReadButNotWrite_AndSourcesAndDumpAreAdministratorOnly()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GM", "Gm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Assert.StartsWith("World:AutosaveIntervalMs = ", await RunAsync(gm, ".server config get World:AutosaveIntervalMs"), StringComparison.Ordinal);
        Assert.StartsWith("Server options (", await RunAsync(gm, ".server config list maps"), StringComparison.Ordinal);
        Assert.Equal(NotAvailable, await RunAsync(gm, ".server config set World:AutosaveIntervalMs 5"));
        Assert.Equal(NotAvailable, await RunAsync(gm, ".server config reset all"));
        Assert.Equal(NotAvailable, await RunAsync(gm, ".server config sources"));
        Assert.Equal(NotAvailable, await RunAsync(gm, ".server config dump World:Maps"));
        Assert.True(host.World.Options.AutosaveIntervalMs != 5);
    }

    [Fact]
    public async Task List_Pages25Rows_AndFiltersByPathAliasAndDescription()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string first = await RunAsync(admin, ".server config list");
        string[] lines = first.Split('\n');
        Assert.Equal(1 + 25, lines.Length);
        Assert.Contains("(page 1/", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines.Skip(1), l => l.Contains("[restart, read-only]", StringComparison.Ordinal) || l.Contains("[live", StringComparison.Ordinal));

        string second = await RunAsync(admin, ".server config list page 2");
        Assert.Contains("(page 2/", second, StringComparison.Ordinal);
        Assert.NotEqual(lines[1], second.Split('\n')[1]);

        string rates = await RunAsync(admin, ".server config list xp.kill");
        Assert.Contains("Rate.XP.Kill = 1 [live]", rates, StringComparison.Ordinal);
        Assert.Contains("no option matches", await RunAsync(admin, ".server config list nomatchatall"), StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config list", await RunAsync(admin, ".server config list maps page zero"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rates_ThroughTheEditor_AreSeenByRatesHistoryAndOverrides()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        Assert.Equal("No rate has changed since start.", await RunAsync(admin, ".rates history"));
        Assert.Equal("No rate is overridden.", await RunAsync(admin, ".rates overrides"));

        Assert.StartsWith("Option Rate.XP.Kill: 1 -> 2 (", await RunAsync(admin, ".server config set xp 2 weekend event"), StringComparison.Ordinal);
        await RunAsync(admin, ".rates set money 3");

        string overrides = await RunAsync(admin, ".rates overrides");
        Assert.Contains(".rates set Rate.XP.Kill 2   (configured 1)", overrides, StringComparison.Ordinal);
        Assert.Contains(".rates set Rate.Drop.Money 3   (configured 1)", overrides, StringComparison.Ordinal);

        string history = await RunAsync(admin, ".rates history 5");
        string[] rows = history.Split('\n');
        Assert.StartsWith("Rate changes (newest first, 2 of 2)", rows[0], StringComparison.Ordinal);
        Assert.Contains("Set Rate.Drop.Money: 1 -> 3 by Admin", rows[1], StringComparison.Ordinal);
        Assert.Contains("Set Rate.XP.Kill: 1 -> 2 by Admin (weekend event)", rows[2], StringComparison.Ordinal);

        string diff = await RunAsync(admin, ".server config diff");
        Assert.StartsWith("2 overridden option(s)", diff, StringComparison.Ordinal);
        Assert.Equal("2 option(s) back to their baseline values.", await RunAsync(admin, ".server config reset all"));
        Assert.Equal("No rate is overridden.", await RunAsync(admin, ".rates overrides"));
    }

    [Fact]
    public async Task TheOldSetVerbs_StillWork_AndTheirChangesAreInDiff()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        Assert.StartsWith("AntiCheat: decay = 7", await RunAsync(admin, ".anticheat set decay 7"), StringComparison.Ordinal);
        Assert.StartsWith("Movement: run ", await RunAsync(admin, ".movement set run 1.5"), StringComparison.Ordinal);
        Assert.Contains("Option AntiCheat:DecayPerSecond", await RunAsync(admin, ".server config set anticheat.decay 8"), StringComparison.Ordinal);

        string diff = await RunAsync(admin, ".server config diff");
        Assert.Contains("AntiCheat:DecayPerSecond: 2 -> 8", diff, StringComparison.Ordinal);
        Assert.Contains("Locomotion:PlayerRunSpeedRate: 1 -> 1.5", diff, StringComparison.Ordinal);

        Assert.StartsWith("2 option(s) back", await RunAsync(admin, ".server config reset all"), StringComparison.Ordinal);
        Assert.Equal(2f, await host.OnWorldAsync(() => host.WorldServices.GetService(typeof(ArcaneCore.World.AntiCheat.AntiCheatFeature)) is ArcaneCore.World.AntiCheat.AntiCheatFeature f ? f.Options.DecayPerSecond : -1f));
    }

    [Fact]
    public async Task SourcesAndDump_AreAdministratorReads_AndNeverPrintAHostPath()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        string sources = await RunAsync(admin, ".server config sources Autosave");
        Assert.StartsWith("Option sources (page 1/1", sources, StringComparison.Ordinal);
        Assert.Contains("World:AutosaveIntervalMs: ", sources, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", sources, StringComparison.Ordinal);

        string dump = await RunAsync(admin, ".server config dump World:Maps");
        Assert.StartsWith("Effective values under World:Maps (", dump, StringComparison.Ordinal);
        Assert.Contains("World:Maps:GridUnload = ", dump, StringComparison.Ordinal);
        Assert.StartsWith("Syntax: .server config dump", await RunAsync(admin, ".server config dump"), StringComparison.Ordinal);
        Assert.Contains("no option path starts with", await RunAsync(admin, ".server config dump Nope:"), StringComparison.Ordinal);
    }
}

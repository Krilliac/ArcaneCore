using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Creatures;

/// <summary><c>.debug script</c> and <c>.debug scriptcoverage</c>: the console (map required), a player (own map), parsing and the listing cap.</summary>
public sealed class ScriptCatalogCommandsTests
{
    private const uint Deadmines = 36;
    private const uint Unscripted = 99_001;
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private sealed class Sink : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate) _lines.Add((value ?? string.Empty).Replace(WorldConsoleExecutor.Prefix, string.Empty, StringComparison.Ordinal));
        }

        public string[] Take()
        {
            lock (_gate)
            {
                string[] lines = [.. _lines];
                _lines.Clear();
                return lines;
            }
        }
    }

    private static CreatureTemplate T(uint entry, string script = "") => new() { Entry = entry, Name = "Creature" + entry, ScriptName = script };

    private static CreatureSpawn S(uint guid, uint entry, uint map) => new() { Guid = guid, Entry = entry, MapId = map, X = 0, Y = 0, Z = 0 };

    private static async Task InstallAsync(WorldTestHost host, IEnumerable<CreatureTemplate> templates, IEnumerable<CreatureSpawn> spawns)
        => await host.OnWorldAsync(() =>
        {
            host.WorldServices.GetRequiredService<CreatureWorldFeature>().Install(new CreatureContent(templates, spawns, [], [], []));
            return 0;
        });

    private static async Task<string[]> ConsoleAsync(WorldTestHost host, string line)
    {
        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        var sink = new Sink();
        WorldConsoleExecutor executor = WorldConsoleExecutor.Create(scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, sink);
        await host.World.InvokeAsync(() =>
        {
            executor.Execute(line);
            return 0;
        });
        return sink.Take();
    }

    private static async Task<List<string>> SayAsync(WorldTestClient gm, string command)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        List<string> lines = [(await gm.ReadChatAsync()).Text];
        lines.AddRange((await gm.CollectRepliesAsync(Quiet)).Where(m => m.Opcode == WorldOpcode.SmsgMessagechat).Select(m => ChatMessage.Parse(m.Payload).Text));
        return lines;
    }

    [Fact]
    public void Levels_BothAreGameMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string leaf in new[] { "debug script", "debug scriptcoverage" })
        {
            Assert.Null(table.Resolve(leaf, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(leaf, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task Console_NeedsAMap_AndAnswersForTheGivenOne()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await InstallAsync(host, [T(646, "boss_mr_smite"), T(Unscripted, "boss_nobody")], []);

        Assert.StartsWith("Syntax: .debug script", (await ConsoleAsync(host, ".debug script 646"))[0], StringComparison.Ordinal); // no map, no player

        string[] scripted = await ConsoleAsync(host, $".debug script 646 {Deadmines}");
        Assert.Contains("InstanceScriptAi", scripted[0], StringComparison.Ordinal);
        Assert.Contains("boss_mr_smite", scripted[0], StringComparison.Ordinal);
        Assert.Contains("creature_ai_scripts rows: 0", scripted[1], StringComparison.Ordinal);

        Assert.Contains("NO IMPLEMENTATION", (await ConsoleAsync(host, $".debug script {Unscripted} {Deadmines}"))[0], StringComparison.Ordinal);
        Assert.Contains("has no creature_template row", (await ConsoleAsync(host, $".debug script 12345 {Deadmines}"))[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".debug script 1 2 3")]
    [InlineData(".debug script abc 36")]
    [InlineData(".debug script 646 xyz")]
    [InlineData(".debug script -5 36")]
    [InlineData(".debug script 646 -1")]
    [InlineData(".debug scriptcoverage 1 2")]
    [InlineData(".debug scriptcoverage abc")]
    [InlineData(".debug scriptcoverage -3")]
    public async Task BadArguments_PrintTheSyntax(string line)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await InstallAsync(host, [T(646, "boss_mr_smite")], []);

        Assert.StartsWith("Syntax:", (await ConsoleAsync(host, line))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithEmptyContent_SaysTheTemplateIsMissing_AndCoverageCountsNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        Assert.Equal(["Creature entry 646 has no creature_template row."], await ConsoleAsync(host, $".debug script 646 {Deadmines}"));
        Assert.Equal([$"map {Deadmines}: 0 ScriptNames: 0 implemented, 0 with no implementation; 0 AINames (0 unknown).", "0 ScriptNames: 0 implemented, 0 with no implementation; 0 AINames (0 unknown)."],
            await ConsoleAsync(host, $".debug scriptcoverage {Deadmines}"));
    }

    [Fact]
    public async Task Coverage_CountsAMapAndListsTheUnresolved()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await InstallAsync(host, [T(646, "boss_mr_smite"), T(Unscripted, "boss_nobody"), T(7)], [S(1, 646, Deadmines), S(2, Unscripted, Deadmines), S(3, 7, Deadmines)]);

        string[] lines = await ConsoleAsync(host, $".debug scriptcoverage {Deadmines}");

        Assert.Equal($"map {Deadmines}: 2 ScriptNames: 1 implemented, 1 with no implementation; 0 AINames (0 unknown).", lines[0]);
        Assert.Equal("2 ScriptNames: 1 implemented, 1 with no implementation; 0 AINames (0 unknown).", lines[1]);
        Assert.Single(lines, l => l.Contains("unresolved:", StringComparison.Ordinal));
        Assert.Contains($"entry {Unscripted}", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Coverage_CapsTheListing()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        List<CreatureTemplate> templates = [];
        List<CreatureSpawn> spawns = [];
        for (uint i = 0; i < 45; i++)
        {
            templates.Add(T(80_000 + i, "boss_missing_" + i));
            spawns.Add(S(i + 1, 80_000 + i, 7));
        }

        await InstallAsync(host, templates, spawns);
        string[] lines = await ConsoleAsync(host, ".debug scriptcoverage 7");

        Assert.Equal(40, lines.Count(l => l.Contains("unresolved:", StringComparison.Ordinal)));
        Assert.Equal("  ... and 5 more.", lines[^1]);
    }

    [Fact]
    public async Task Coverage_WithoutAMap_TotalsTheDungeonMapsOfTheRegistry()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await InstallAsync(host, [T(646, "boss_mr_smite")], [S(1, 646, Deadmines)]);

        string[] lines = await ConsoleAsync(host, ".debug scriptcoverage");

        // The test host's map registry has the Deadmines as its only dungeon: its line, then the total.
        Assert.Equal([$"map {Deadmines}: 1 ScriptNames: 1 implemented, 0 with no implementation; 0 AINames (0 unknown).", "1 ScriptNames: 1 implemented, 0 with no implementation; 0 AINames (0 unknown)."], lines);
    }

    [Fact]
    public async Task Player_DefaultsToTheOwnMap()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SCGM", "Scgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Scgm")!.Flags |= PlayerFlags.Gm);
        await InstallAsync(host, [T(Unscripted, "boss_nobody")], []);
        uint map = await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Scgm")!.MapId);

        List<string> lines = await SayAsync(gm, $".debug script {Unscripted}");

        Assert.Contains($"on map {map}:", lines[0], StringComparison.Ordinal);
        Assert.Contains("NO IMPLEMENTATION", lines[0], StringComparison.Ordinal);
    }
}

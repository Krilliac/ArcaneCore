using System.Text;
using ArcaneCore.Game.Rules;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Rules;

/// <summary><c>.rules check</c>, <c>.rules vocab</c> and <c>.rules trace</c> (design 5.1-5.3), driven through the world console.</summary>
public sealed class RuleCommandsTests
{
    private sealed class Sink : TextWriter
    {
        private readonly Lock _gate = new();
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

    [Fact]
    public void EveryLeaf_IsGameMaster()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string leaf in new[] { "rules check", "rules vocab", "rules trace", "rules stats" })
        {
            Assert.Null(table.Resolve(leaf, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(leaf, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task Check_WithNothingLoaded_SaysSo()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        Assert.Equal(["Rules: 0 rules (0 disabled), 0 lists, 0 filters, 0 texts, 0 bot goals, 0 rejected rows.", "No rule row was rejected."],
            await ConsoleAsync(host, ".rules check"));
    }

    [Fact]
    public async Task Check_ListsTheRejectedRows_AndNarrowsToAHostAndOwner()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        host.WorldServices.GetRequiredService<RuleFeature>().Replace(RuleCatalog.Build(new RuleContent
        {
            Rules =
            [
                new RuleRecord { Id = 5, Host = (byte)RuleHostKind.GameObject, OwnerKind = (byte)RuleOwnerKind.Entry, OwnerId = 99, EventType = 1 },
                new RuleRecord { Id = 6, Host = (byte)RuleHostKind.Bot, EventType = 1 },
            ],
        }, RuleVocabulary.Default, new RuleBuildOptions()));

        string[] all = await ConsoleAsync(host, ".rules check");
        Assert.Equal("Rules: 0 rules (0 disabled), 0 lists, 0 filters, 0 texts, 0 bot goals, 2 rejected rows.", all[0]);
        Assert.Equal("2 rejected row(s).", all[^1]);
        Assert.Equal(4, all.Length);

        string[] creature = await ConsoleAsync(host, ".rules check gameobject 99");
        Assert.Equal(["rule 5: host 3 is not registered (registered: 1 bot, 2 creature, 5 world_event) [gameobject entry 99]", "1 rejected row(s)."], creature[1..]);
        Assert.Equal("No rejected rule row matches.", (await ConsoleAsync(host, ".rules check gameobject 98"))[1]);
        Assert.StartsWith("Unknown rule host 'dragon'. Hosts: bot, creature,", (await ConsoleAsync(host, ".rules check dragon"))[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax:", (await ConsoleAsync(host, ".rules check gameobject x"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vocab_PrintsTheSharedPrimitives_AndWarnsAboutAnUnregisteredHost()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        string[] lines = await ConsoleAsync(host, ".rules vocab");
        Assert.Equal("116 primitive(s).", lines[^1]); // the 6 shared primitives plus the 5 of the bot host, the 100 of the creature host and the 5 of the world_event host
        Assert.Contains(lines, l => l.StartsWith("action 4 cast(spell_id:SpellId, cast_flags:NonNegative) [shared]: ", StringComparison.Ordinal));

        string[] bot = await ConsoleAsync(host, ".rules vocab bot");
        Assert.StartsWith("event 1 bot.goal_evaluate() [bot]: ", bot[0], StringComparison.Ordinal);
        Assert.Equal("11 primitive(s).", bot[^1]);
        string[] creature = await ConsoleAsync(host, ".rules vocab gameobject");
        Assert.Equal("Host gameobject is not registered in this build; its rules are rejected. The shared primitives follow.", creature[0]);
    }

    [Fact]
    public async Task Trace_TogglesASubject_AndListsWhoIsTraced()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        Assert.Equal(["No subject is traced."], await ConsoleAsync(host, ".rules trace"));
        Assert.StartsWith("Tracing the rules of 0x1F40", (await ConsoleAsync(host, ".rules trace 0x1f40"))[0], StringComparison.Ordinal);
        Assert.Equal(["Traced: 0x1F40."], await ConsoleAsync(host, ".rules trace"));
        Assert.Equal(["Stopped tracing 0x1F40."], await ConsoleAsync(host, ".rules trace 8000"));
        Assert.StartsWith("Syntax:", (await ConsoleAsync(host, ".rules trace nope"))[0], StringComparison.Ordinal);
        Assert.StartsWith("Syntax:", (await ConsoleAsync(host, ".rules trace 0"))[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stats_PrintsTheRegisteredEvaluatorCounters_NarrowedToAHost()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        RuleFeature feature = host.WorldServices.GetRequiredService<RuleFeature>();

        Assert.Equal(["No rule host is running an evaluator."], await ConsoleAsync(host, ".rules stats"));

        var counters = new RuleEvaluatorCounters { Fired = 7, ActionsRun = 9, Reentrant = 2, Faults = 1 };
        feature.Counters.Register(RuleHostKind.Bot, "bot test", counters);
        feature.Counters.Register(RuleHostKind.Creature, "creature map 0", new RuleEvaluatorCounters());

        Assert.Equal(["bot bot test: fired=7 actions=9 failed=0 dropped_runs=0 depth_limited=0 reentrant=2 invoker_lost=0 faults=1"],
            await ConsoleAsync(host, ".rules stats bot"));
        Assert.Equal(2, (await ConsoleAsync(host, ".rules stats")).Length);
        Assert.Equal(["No gameobject evaluator is running."], await ConsoleAsync(host, ".rules stats gameobject"));
        Assert.StartsWith("Unknown rule host 'dragon'.", (await ConsoleAsync(host, ".rules stats dragon"))[0], StringComparison.Ordinal);
    }
}

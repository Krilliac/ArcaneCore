using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Chaos;
using ArcaneCore.World.Playerbots.Qa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotChaosTests
{
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

    private sealed class CapturingLogger : ILogger
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private static async Task<string[]> RunConsoleAsync(WorldTestHost host, string line)
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
    public void Options_Validate_EnforcesBounds()
    {
        var opts = new PlayerbotOptions();
        opts.Validate();

        opts.Chaos.ActiveChaosBots = -1;
        Assert.Throws<InvalidOperationException>(() => opts.Validate());

        opts.Chaos.ActiveChaosBots = PlayerbotChaosOptions.MaxActiveChaosBots + 1;
        Assert.Throws<InvalidOperationException>(() => opts.Validate());
        opts.Chaos.ActiveChaosBots = 4;

        opts.Chaos.ProbesPerThink = 0;
        Assert.Throws<InvalidOperationException>(() => opts.Validate());

        opts.Chaos.ProbesPerThink = PlayerbotChaosOptions.MaxProbesPerThink + 1;
        Assert.Throws<InvalidOperationException>(() => opts.Validate());
    }

    [Fact]
    public async Task Engine_BurstAcrossAllCategories_ExecutesProbesWithoutUnhandledFaults_AndPreservesInvariants()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("chaos-acct", "Chaosbot");
        var options = new PlayerbotOptions
        {
            Enabled = true,
            Chaos = new PlayerbotChaosOptions
            {
                Enabled = true,
                ActiveChaosBots = 4,
                ProbesPerThink = 4,
                Seed = 1337,
                MalformedPayloads = true,
                StateRaces = true,
                BoundaryMovement = true,
            },
        };
        var logger = new CapturingLogger();
        var engine = new PlayerbotChaosEngine(options, host.Opcodes, logger);
        Guid botId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            int executed = engine.RunBurst(botId, session, player, 200);
            Assert.Equal(200, executed);
            Assert.True(float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z));
            Assert.Equal(SessionState.InWorld, session.State);
            return 0;
        });

        ChaosEngineStatus status = engine.Status();
        Assert.Equal(200, status.ProbesExecuted);
        Assert.True(
            status.FaultsCaught == 0,
            $"Chaos burst caught {status.FaultsCaught} faults:\n" +
            string.Join("\n", status.RecentFaults.Select(f => $"  [{f.Category}/{f.ProbeName}] {f.Opcode} (len={f.PayloadLength}, hex={f.PayloadHex}) -> {f.ExceptionType}: {f.Message}")));
        Assert.Empty(status.RecentFaults);
        Assert.True(status.MalformedRejected > 0, "Expected malformed/truncated wire probes to be rejected by MalformedPacket guards.");
        Assert.All(Enum.GetValues<ChaosProbeCategory>(), cat => Assert.True(status.ProbesByCategory[cat] > 0, $"Category {cat} was not exercised"));
    }

    [Fact]
    public async Task Engine_WhenHandlerThrowsUnexpectedExceptionOrCorruptsCoordinates_ContainsFault_RestoresPosition_AndEmitsBotQaSignal()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("chaos-acct-2", "ChaosbotTwo");

        var customTable = new OpcodeTable();
        customTable.OnWorld(WorldOpcode.CmsgPing, (session, player, _) =>
        {
            player.Relocate(float.NaN, player.Y, player.Z, player.Orientation, session.World.NowMs);
            throw new InvalidOperationException("simulated handler bug");
        });
        host.Opcodes.Replace(customTable);

        var options = new PlayerbotOptions
        {
            Enabled = true,
            Chaos = new PlayerbotChaosOptions
            {
                Enabled = true,
                ActiveChaosBots = 1,
                ProbesPerThink = 2,
                Seed = 42,
                MalformedPayloads = true,
                StateRaces = false,
                BoundaryMovement = false,
            },
        };
        var logger = new CapturingLogger();
        var engine = new PlayerbotChaosEngine(options, customTable, logger);
        Guid botId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            float startX = player.X;
            int ran = engine.StepBot(botId, 0, session, player);
            Assert.Equal(2, ran);
            // Position must be restored after the handler corrupted X to NaN.
            Assert.Equal(startX, player.X, 2);
            Assert.Equal(SessionState.InWorld, session.State);
            return 0;
        });

        ChaosEngineStatus status = engine.Status();
        Assert.True(status.FaultsCaught >= 2);
        Assert.True(status.InvariantRepairs >= 2);
        Assert.NotEmpty(status.RecentFaults);

        // Verify the logged error matches PlayerbotQaSignalParser.FaultPattern so Bot QA ingests it under Goal="chaos".
        (LogLevel level, string message, Exception? _) = logger.Entries.First(e => e.Level == LogLevel.Error);
        Assert.True(PlayerbotQaSignalParser.TryParse(
            message,
            DateTime.UtcNow,
            botId,
            out QaSignal? signal));
        Assert.NotNull(signal);
        Assert.Equal(QaSignalKind.ActionFault, signal!.Kind);
        Assert.Equal("chaos", signal.Goal);
    }

    [Fact]
    public async Task Commands_ToggleStatusBurstAndClear_WorkThroughConsole()
    {
        await using WorldTestHost host = WorldTestHost.Start();

        string[] initial = await RunConsoleAsync(host, ".playerbot chaos status");
        Assert.Contains(initial, l => l.StartsWith("Playerbot chaos: off,", StringComparison.Ordinal));

        string[] turnedOn = await RunConsoleAsync(host, ".playerbot chaos on 6 3");
        Assert.Contains(turnedOn, l => l.Contains("activeBots=6, probesPerThink=3", StringComparison.Ordinal));

        string[] turnedOnAll = await RunConsoleAsync(host, ".playerbot chaos on all 2");
        Assert.Contains(turnedOnAll, l => l.Contains($"activeBots={PlayerbotChaosOptions.MaxActiveChaosBots}, probesPerThink=2", StringComparison.Ordinal));

        string[] afterOn = await RunConsoleAsync(host, ".playerbot chaos");
        Assert.Contains(afterOn, l => l.StartsWith($"Playerbot chaos: on, activeBots={PlayerbotChaosOptions.MaxActiveChaosBots}", StringComparison.Ordinal));

        string[] faults = await RunConsoleAsync(host, ".playerbot chaos faults");
        Assert.Equal(["No chaos faults recorded."], faults);

        string[] cleared = await RunConsoleAsync(host, ".playerbot chaos clear all");
        Assert.Equal(["Chaos stats, recent faults and explicit bot assignments cleared."], cleared);

        string[] turnedOff = await RunConsoleAsync(host, ".playerbot chaos off");
        Assert.Equal(["Playerbot chaos disabled."], turnedOff);
    }

    [Fact]
    public async Task ChaosProbes_AreExcludedFromBotQaIdentify_AndAwakenPeonHasNoGap()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.EnterWorldAsync("chaosqa", "Chaosqa");

        var customTable = new OpcodeTable();
        bool observedInProbe = false;
        customTable.OnWorld(WorldOpcode.CmsgPing, (s, p, _) =>
        {
            if (s.IsInChaosProbe)
            {
                observedInProbe = true;
                Assert.Null(PlayerbotQaBots.Identify(p));
            }
        });
        host.Opcodes.Replace(customTable);

        var options = new PlayerbotOptions
        {
            Enabled = true,
            Chaos = new PlayerbotChaosOptions
            {
                Enabled = true,
                ActiveChaosBots = 1,
                ProbesPerThink = 2,
                Seed = 42,
                MalformedPayloads = true,
                StateRaces = false,
                BoundaryMovement = false,
            },
        };
        var engine = new PlayerbotChaosEngine(options, customTable, NullLogger.Instance);
        Guid botId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        await host.World.InvokeAsync(() =>
        {
            WorldSession session = host.Registry.Sessions.Single();
            Player player = session.Player!;
            Assert.False(session.IsInChaosProbe);
            int ran = engine.StepBot(botId, 0, session, player);
            Assert.Equal(2, ran);
            Assert.True(observedInProbe);
            Assert.False(session.IsInChaosProbe);
            return 0;
        });

        ArcaneCore.Game.Spells.SpellSystem spellSystem = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System;
        var analyzer = new PlayerbotQaSpellGapAnalyzer(spellSystem);
        var awakenPeon = new ArcaneCore.Game.Spells.SpellInfo
        {
            Id = ArcaneCore.Game.Creatures.Scripts.LazyPeonAI.SpellAwakenPeon,
            Name = "Awaken Peon",
            Effects =
            [
                new ArcaneCore.Game.Spells.SpellEffectInfo
                {
                    Effect = ArcaneCore.Game.Spells.SpellEffectName.Dummy,
                },
            ],
        };
        Assert.Empty(analyzer.Of(awakenPeon));
    }
}

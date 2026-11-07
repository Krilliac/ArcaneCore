using ArcaneCore.Game;
using ArcaneCore.Game.Diagnostics;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The interaction signals of a real managed bot session in a running world: the opcode gate of <c>TryManagedAction</c>, the packet tap of the session's
/// send path, and the CMSG_LOOT handler, each through the DI-registered monitor into the QA feature harness and out as an issue.
/// </summary>
public sealed class PlayerbotQaInteractionWorldTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static WorldTestHost Host(QaInteractionHarness harness) => WorldTestHost.Start(configureServices: services =>
    {
        // The harness's sink (and so its bus and feature) is the host's: what the bot sessions report lands in the harness.
        services.AddSingleton(harness.Sink);
        services.AddSingleton(sp => new PlayerbotQaInteractionMonitor(sp.GetRequiredService<IPlayerbotQaSink>(), sp));
        services.AddSingleton<IGameQaObserver>(sp => sp.GetRequiredService<PlayerbotQaInteractionMonitor>());
        services.AddSingleton<IManagedPacketTap>(sp => sp.GetRequiredService<PlayerbotQaInteractionMonitor>());
    });

    [Fact]
    public async Task OpcodeUnhandled_ABotSendingAnOpcodeWithNoWorldHandler_BecomesOneIssue()
    {
        using var harness = new QaInteractionHarness(_directory);
        await using WorldTestHost host = Host(harness);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            WorldOpcode none = Enum.GetValues<WorldOpcode>().First(o => o.ToString().StartsWith("Cmsg", StringComparison.Ordinal) && !host.Opcodes.TryGet(o, out _));
            WorldOpcode sessionOnly = Enum.GetValues<WorldOpcode>()
                .First(o => o.ToString().StartsWith("Cmsg", StringComparison.Ordinal) && host.Opcodes.TryGet(o, out var h) && h.World is null && h.Session is not null);
            bool[] results = await host.World.InvokeAsync(() => new[]
            {
                session.TryManagedAction(none, []), session.TryManagedAction(none, []), session.TryManagedAction(sessionOnly, []),
            });
            Assert.All(results, Assert.False); // refused as before

            Assert.Equal(2, harness.Feature.Drain()); // one per opcode (the second of the same is throttled)
            await harness.PassAsync();
            QaFindingView noHandler = Assert.Single(harness.Findings, f => f.Reason == "no-handler");
            Assert.Equal((QaSignalKind.OpcodeUnhandled, QaCause.NotImplemented, 1), (noHandler.Kind, noHandler.Cause, noHandler.DistinctBots));
            Assert.Contains(WorldOpcodeNames.GetName(none), noHandler.Subject, StringComparison.Ordinal);
            QaFindingView session2 = Assert.Single(harness.Findings, f => f.Reason == "session-only");
            Assert.Equal(QaCause.BehaviourBug, session2.Cause);
            Assert.Single(harness.Transport.Created); // the kind's cap: one opcode issue a pass
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ThePacketTap_SeesAnInventoryFailureTheBotReceives()
    {
        using var harness = new QaInteractionHarness(_directory, git => git.MinDistinctBots = 1);
        await using WorldTestHost host = Host(harness);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.World.InvokeAsync(() =>
            {
                session.Send(WorldOpcode.SmsgInventoryChangeFailure, ItemPackets.InventoryChangeFailure(InventoryResult.SlotIsEmpty, default, default));
                session.Send(WorldOpcode.SmsgInventoryChangeFailure, ItemPackets.InventoryChangeFailure(InventoryResult.BagFull, default, default)); // expected: once is nothing
                return true;
            });
            session.Send(WorldOpcode.SmsgInventoryChangeFailure, ItemPackets.InventoryChangeFailure(InventoryResult.ItemNotFound, default, default)); // off the world thread: not looked at

            Assert.Equal(1, harness.Feature.Drain());
            QaFindingView finding = Assert.Single(harness.Findings);
            Assert.Equal((QaSignalKind.InteractionFailed, "inventory", (int)InventoryResult.SlotIsEmpty), (finding.Kind, finding.Reason, ParseCode(finding)));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task TheLootHandler_ReportsABotsRepeatedRefusal()
    {
        using var harness = new QaInteractionHarness(_directory, git => git.MinDistinctBots = 1);
        await using WorldTestHost host = Host(harness);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            ObjectGuid nothing = ObjectGuid.WithEntry(HighGuid.Unit, 3098, 424242);
            byte[] payload = BitConverter.GetBytes(nothing.Value);
            await host.World.InvokeAsync(() =>
            {
                for (int i = 0; i < PlayerbotQaInteractionMonitor.RepeatThreshold; i++) session.TryManagedAction(WorldOpcode.CmsgLoot, payload);
                return true;
            });

            Assert.Equal(1, harness.Feature.Drain());
            QaFindingView finding = Assert.Single(harness.Findings);
            Assert.Equal(("loot", 3098u, QaCause.BehaviourBug), (finding.Reason, finding.Npc, finding.Cause));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>The code of an interaction finding, from its key (<c>...|code=N|...</c>).</summary>
    private static int ParseCode(QaFindingView finding)
    {
        string part = finding.Key.Split('|').Single(p => p.StartsWith("code=", StringComparison.Ordinal));
        return int.Parse(part["code=".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }
}

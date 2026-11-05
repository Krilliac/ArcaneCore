using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The QA packet tap under Lean client fidelity (the default): a lean bot session drops most packets before they are copied or queued, and the four
/// quest and inventory packets the QA monitor reads must still reach the tap, or every credited kill would look like a kill with no progress.
/// </summary>
public sealed class PlayerbotQaInteractionLeanTests
{
    private static WorldTestHost LeanHost(IManagedPacketTap tap) => WorldTestHost.Start(configureServices: services =>
    {
        BotFidelity.Services(PlayerbotClientFidelity.Lean)(services);
        services.AddSingleton<IManagedPacketTap>(tap);
    });

    [Fact]
    public async Task ALeanBotSession_HandsTheTapItsFourOpcodes_AndNothingElseThatTheLeanIntakeDrops()
    {
        var tap = new RecordingTap();
        await using WorldTestHost host = LeanHost(tap);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Assert.False(session.WantsUpdateObjects); // the session really is lean
            tap.Clear();
            session.Send(WorldOpcode.SmsgNotification, [3, 0]); // not read back by any bot: dropped by the lean intake and not the tap's
            session.Send(WorldOpcode.SmsgQuestupdateAddKill, PlayerbotQaInteractionMonitorTests.AddKill(QaInteractionKit.KillQuest, QaInteractionKit.ObjectiveCreature));
            session.Send(WorldOpcode.SmsgQuestupdateAddItem, new byte[16]);
            session.Send(WorldOpcode.SmsgItemPushResult, new byte[29]);
            session.Send(WorldOpcode.SmsgInventoryChangeFailure, ItemPackets.InventoryChangeFailure(InventoryResult.BagFull, default, default));

            WorldOpcode[] seen = tap.Seen();
            Assert.DoesNotContain(WorldOpcode.SmsgNotification, seen);
            foreach (WorldOpcode wanted in new[]
            {
                WorldOpcode.SmsgQuestupdateAddKill, WorldOpcode.SmsgQuestupdateAddItem, WorldOpcode.SmsgItemPushResult, WorldOpcode.SmsgInventoryChangeFailure,
            })
            {
                Assert.Equal(1, seen.Count(o => o == wanted));
            }
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task UnderLean_AQuestUpdateResetsTheObjectiveAttempts_SoCreditedKillsReportNothing()
    {
        var sink = new QaInteractionRecordingSink();
        using var kit = new QaInteractionKit(sink);
        await using WorldTestHost host = LeanHost(new KitTap(kit));
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Assert.False(session.WantsUpdateObjects);
            kit.GiveQuest(kit.Bot, QaInteractionKit.KillQuest);
            Creature boar = kit.Victim(QaInteractionKit.ObjectiveCreature);
            byte[] credit = PlayerbotQaInteractionMonitorTests.AddKill(QaInteractionKit.KillQuest, QaInteractionKit.ObjectiveCreature);

            // Many kills, each run of four followed by the server's credit through the lean session: the count never reaches five.
            for (int round = 0; round < 6; round++)
            {
                for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts - 1; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);
                session.Send(WorldOpcode.SmsgQuestupdateAddKill, credit);
            }

            Assert.Empty(sink.Signals);

            // The control: the same kills with no credit are reported.
            for (int i = 0; i < PlayerbotQaInteractionMonitor.ObjectiveAttempts; i++) kit.Monitor.OnUnitKilled(kit.Bot, boar);
            QaSignal signal = Assert.Single(sink.Signals);
            Assert.Equal("no-progress-kill", signal.Reason);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>Records the opcodes the session hands it.</summary>
    private sealed class RecordingTap : IManagedPacketTap
    {
        private readonly List<WorldOpcode> _seen = [];

        public bool Active => true;

        public void OnPacket(WorldSession session, WorldOpcode opcode, byte[] payload)
        {
            lock (_seen) _seen.Add(opcode);
        }

        public void OnUnhandledOpcode(WorldSession session, WorldOpcode opcode, bool sessionOnly)
        {
        }

        public void Clear()
        {
            lock (_seen) _seen.Clear();
        }

        public WorldOpcode[] Seen()
        {
            lock (_seen) return [.. _seen];
        }
    }

    /// <summary>Hands the packets of the host's bot to the kit's monitor as if they were the kit bot's (the monitor reads the kit's quest log).</summary>
    private sealed class KitTap(QaInteractionKit kit) : IManagedPacketTap
    {
        public bool Active => true;

        public void OnPacket(WorldSession session, WorldOpcode opcode, byte[] payload) => kit.Monitor.OnPlayerPacket(kit.Bot, opcode, payload);

        public void OnUnhandledOpcode(WorldSession session, WorldOpcode opcode, bool sessionOnly)
        {
        }
    }
}

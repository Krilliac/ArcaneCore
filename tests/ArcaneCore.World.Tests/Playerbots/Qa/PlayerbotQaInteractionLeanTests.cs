using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    /// <summary>
    /// End to end under Lean fidelity through the real monitor tap (<see cref="PlayerbotQaInteractionMonitor.OnPacket"/>, not the test entry
    /// <c>OnPlayerPacket</c>): the credit packet a lean bot session captures reaches the monitor only from the world thread, and from there resets the attempts.
    /// </summary>
    [Fact]
    public async Task UnderLean_KillCreditPacketsThroughTheRealTap_ResetTheAttempts_OnlyFromTheWorldThread()
    {
        var sink = new QaInteractionRecordingSink();
        Player? bot = null;
        QuestNpcServices? quests = null;
        var monitor = new PlayerbotQaInteractionMonitor(sink, unit => bot is not null && ReferenceEquals(unit, bot) ? new QaBot(QaInteractionKit.BotKey, 1) : null,
            () => 1_000_000, () => quests);
        await using WorldTestHost host = LeanHost(monitor);
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Assert.False(session.WantsUpdateObjects); // the session really is lean
            Creature boar = await host.World.InvokeAsync(() =>
            {
                bot = session.Player!;
                var content = new QuestContent(
                [
                    new QuestTemplate
                    {
                        Entry = QaInteractionKit.KillQuest, Method = 2, MinLevel = 1, QuestLevel = 5, Title = "Kill", Objectives = "Kill", Details = "Kill", RequestItemsText = "Kill",
                        ReqCreatureOrGOId1 = (int)QaInteractionKit.ObjectiveCreature, ReqCreatureOrGOCount1 = 8,
                    },
                ], [], []);
                quests = new QuestNpcServices(new QuestStore(content), new NpcStore(NpcContent.Empty), new QuestNpcDependencies(), new QuestNpcOptions(), new NullQuestSink(),
                    () => 1_000, NullLogger.Instance);
                PlayerNpcState state = quests.Track(bot);
                quests.CompleteLoad(state, new CharacterQuestData([], []));
                int slot = state.Quests.FindSlot(0);
                state.Quests.GetOrAdd(QaInteractionKit.KillQuest).Status = QuestStatus.Incomplete;
                state.Quests.SetSlot(slot, QaInteractionKit.KillQuest);
                Assert.True(bot.Map is { InstanceId: 0 }, "the bot is not on an open-world map: the kill hook would ignore it");
                var template = new CreatureTemplate { Entry = QaInteractionKit.ObjectiveCreature, Name = "Boar", Faction = 14, MinLevel = 5, MaxLevel = 5 };
                return new Creature(996090, template, new CreatureSpawn { Guid = 996090, Entry = QaInteractionKit.ObjectiveCreature, MapId = 0, X = bot.X + 2, Y = bot.Y, Z = bot.Z },
                    CreatureContent.Empty, new Random(1));
            });
            byte[] credit = PlayerbotQaInteractionMonitorTests.AddKill(QaInteractionKit.KillQuest, QaInteractionKit.ObjectiveCreature);
            const int Almost = PlayerbotQaInteractionMonitor.ObjectiveAttempts - 1;

            // Credited from the world thread: each run of four kills is cleared by the credit, so the count never reaches five.
            for (int round = 0; round < 6; round++)
            {
                await host.World.InvokeAsync(() =>
                {
                    for (int i = 0; i < Almost; i++) monitor.OnUnitKilled(bot, boar);
                    session.Send(WorldOpcode.SmsgQuestupdateAddKill, credit);
                    return 0;
                });
            }

            Assert.Empty(sink.Signals);

            // The same credit sent from this thread is not the world thread's: the tap ignores it, so the fifth kill is reported.
            await host.World.InvokeAsync(() =>
            {
                for (int i = 0; i < Almost; i++) monitor.OnUnitKilled(bot, boar);
                return 0;
            });
            session.Send(WorldOpcode.SmsgQuestupdateAddKill, credit);
            await host.World.InvokeAsync(() =>
            {
                monitor.OnUnitKilled(bot, boar);
                return 0;
            });
            Assert.Equal("no-progress-kill", Assert.Single(sink.Signals).Reason);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class NullQuestSink : IQuestNpcSink
    {
        public void QuestsChanged(Player player, IReadOnlyList<CharacterQuestStatus> rows)
        {
        }

        public void TaxiMaskChanged(Player player, IReadOnlyList<uint> mask)
        {
        }

        public void CharacterChanged(Player player)
        {
        }
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

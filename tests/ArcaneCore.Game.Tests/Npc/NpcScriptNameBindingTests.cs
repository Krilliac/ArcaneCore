using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.QuestFlowTestKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// A gossip-only or quest-only script is bound by the template's ScriptName (<see cref="CreatureAiFactory.RegisterNpcScript"/>), and what is
/// registered is what runs: the real quest reward / accept path and the real gossip-hello path reach the handler, and a throwing reward hook
/// is contained after the commit (<see cref="CreatureAI.OnQuestRewarded"/>).
/// </summary>
public sealed class NpcScriptNameBindingTests
{
    private const uint First = 930201;
    private const uint Second = 930202;
    private const uint ScriptId = 930300;
    private const string Script = "npc_test_quest_only";

    private sealed class RecordingQuestScript : INpcQuestScript
    {
        public List<(Player Player, Creature Creature, uint Quest)> Accepted { get; } = [];

        public List<(Player Player, Creature Creature, uint Quest)> Rewarded { get; } = [];

        public void OnQuestAccept(Player player, Creature creature, uint questId) => Accepted.Add((player, creature, questId));

        public void OnQuestRewarded(Player player, Creature creature, uint questId) => Rewarded.Add((player, creature, questId));
    }

    private sealed class ThrowingQuestScript(string message) : INpcQuestScript
    {
        public void OnQuestRewarded(Player player, Creature creature, uint questId) => throw new InvalidOperationException(message);
    }

    private sealed class ThrowingAi(Creature creature, string message) : CreatureAI(creature)
    {
        public override void OnQuestRewarded(Player player, uint questId) => throw new InvalidOperationException(message);
    }

    /// <summary>Records the quest status and the NPC emote state (what the CompleteScript sets) the moment the hook runs.</summary>
    private sealed class OrderProbeAi(Creature creature, QuestFlowTestKit kit, List<string> log) : CreatureAI(creature)
    {
        public override void OnQuestRewarded(Player player, uint questId)
        {
            QuestStatusData data = kit.State.Quests.Get(questId)!;
            log.Add($"hook rewarded={data.Rewarded} emote={kit.Creature.GetUInt32(UpdateFields.UnitNpcEmotestate)}");
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static QuestFlowTestKit ChainKit(string scriptName = "", uint completeScript = 0)
        => new([Task(First, nextInChain: Second, completeScript: completeScript), Task(Second)], [Row(First, QuestStatus.Complete, kills: 2)],
            starters: [First, Second], enders: [First], rewardable: [First], scriptName: scriptName);

    private static CreatureMapSystem CompleteScriptSystem(QuestFlowTestKit kit)
    {
        // quest_template.CompleteScript = ScriptId: one step, emote state 13 on the source (the giver).
        var step = new RelayScriptStep(ScriptId, 0, 0, 1, 13, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestEnd, step)]) };
        var system = new CreatureMapSystem(kit.Player.Map!, new CreatureContent([], [], [], [], [], ai));
        kit.Player.Map!.AddUpdater(system);
        return system;
    }

    private static void TurnIn(QuestFlowTestKit kit)
    {
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, First, 0, out QuestRewardPlan? plan));
        kit.Drain();
        kit.Services.ApplyReward(plan!);
    }

    private static uint Emote(QuestFlowTestKit kit) => kit.Creature.GetUInt32(UpdateFields.UnitNpcEmotestate);

    // --- blocker 1: a registered handler is invoked -----------------------------------------------------------

    [Fact]
    public void AQuestOnlyScript_OnQuestRewarded_FiresThroughTheRealRewardPath()
    {
        var factory = new CreatureAiFactory();
        var script = new RecordingQuestScript();
        factory.RegisterNpcScript(Script, "TestQuestScript", quest: script);
        using QuestFlowTestKit kit = ChainKit(Script);
        Assert.Null(kit.Creature.AI); // no AI of its own: the script is bound by ScriptName alone
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null, factory: () => factory);

        Assert.Empty(script.Rewarded);
        TurnIn(kit);

        (Player player, Creature creature, uint quest) = Assert.Single(script.Rewarded);
        Assert.Same(kit.Player, player);
        Assert.Same(kit.Creature, creature);
        Assert.Equal(First, quest);
    }

    [Fact]
    public void AQuestOnlyScript_OnQuestAccept_FiresThroughTheRealAcceptPath()
    {
        var factory = new CreatureAiFactory();
        var script = new RecordingQuestScript();
        factory.RegisterNpcScript(Script, "TestQuestScript", quest: script);
        using QuestFlowTestKit kit = new([Task(First, method: 0)], starters: [First], enders: [First], scriptName: Script);
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null, factory: () => factory);

        Assert.True(kit.Accept(First));

        Assert.Equal(First, Assert.Single(script.Accepted).Quest);
    }

    [Fact]
    public void AScriptRegisteredForAnotherName_IsNotInvoked()
    {
        var factory = new CreatureAiFactory();
        var script = new RecordingQuestScript();
        factory.RegisterNpcScript("npc_some_other_script", "Other", quest: script);
        using QuestFlowTestKit kit = ChainKit(Script);
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null, factory: () => factory);

        TurnIn(kit);

        Assert.Empty(script.Rewarded);
    }

    [Fact]
    public void AGossipOnlyScript_IsReachedByTheRealGossipHello_ForEveryEntryWithThatScriptName()
    {
        const string Name = "npc_test_gossip_only";
        var factory = new CreatureAiFactory();
        var gossip = new ScriptNameGossip();
        factory.RegisterNpcScript(Name, "TestGossipScript", gossip: gossip);

        using var kit = new NpcServiceTestKit(NpcFlags.Gossip);
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null, factory: () => factory);
        Map map = kit.World.GetMap(0);
        uint[] entries = [NpcServiceTestKit.Entry, NpcServiceTestKit.Entry + 1, NpcServiceTestKit.Entry + 2];
        CreatureTemplate[] templates =
        [
            .. entries.Take(2).Select(e => CreatureTestKit.Template(e) with { ScriptName = Name }),
            CreatureTestKit.Template(entries[2]) with { ScriptName = "npc_unregistered" },
        ];
        var content = new CreatureContent(templates, [], [], [], []);
        var creatures = new CreatureMapSystem(map, content);
        map.AddUpdater(creatures);

        for (int i = 0; i < entries.Length; i++)
        {
            Creature creature = creatures.SpawnTemporary(content.FindTemplate(entries[i])!, kit.Npc.X, kit.Npc.Y, kit.Npc.Z, 0);
            kit.Npc = kit.Npc with { Guid = creature.Guid, Entry = entries[i] };
            kit.Session.Clear();
            gossip.Hellos.Clear();

            kit.Services.GossipHello(kit.Player, creature.Guid);

            if (i < 2)
            {
                Assert.Equal([entries[i]], gossip.Hellos); // the registered handler answered for both entries
                Assert.Equal(7777u, TextId(kit.Single(WorldOpcode.SmsgGossipMessage)));
            }
            else
            {
                Assert.Empty(gossip.Hellos); // a ScriptName with no registration is not claimed
            }
        }
    }

    private sealed class ScriptNameGossip : INpcGossipScript
    {
        public List<uint> Hellos { get; } = [];

        public ScriptedGossipMenu? Hello(Player player, NpcInfo npc)
        {
            Hellos.Add(npc.Entry);
            return new ScriptedGossipMenu(false, 7777, [new ScriptedGossipItem(0, "Go on.", 1, 1)]);
        }

        public uint Select(Player player, NpcInfo npc, uint sender, uint action) => 0;
    }

    private static uint TextId(byte[] gossipMessage)
    {
        var reader = new PacketReader(gossipMessage);
        reader.ReadUInt64();
        return reader.ReadUInt32();
    }

    [Fact]
    public void DetachingTheHooks_UnbindsTheScriptNameGossip()
    {
        var factory = new CreatureAiFactory();
        factory.RegisterNpcScript("npc_x", "X", gossip: new ScriptNameGossip());
        using var kit = new NpcServiceTestKit(NpcFlags.Gossip);
        IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, _ => null, factory: () => factory);
        Assert.NotNull(kit.Services.GossipByScriptName);
        hooks.Dispose();
        Assert.Null(kit.Services.GossipByScriptName);
    }

    // --- blocker 2: a throwing hook never undoes the committed reward -------------------------------------------

    [Fact]
    public void AThrowingAiHook_DoesNotStopTheCompleteScript_TheSubscribers_OrTheNextQuestOffer()
    {
        const string Message = "boom-ai-hook-1d1b";
        using QuestFlowTestKit kit = ChainKit(completeScript: ScriptId);
        kit.Creature.AI = new ThrowingAi(kit.Creature, Message);
        CreatureMapSystem system = CompleteScriptSystem(kit);
        var logger = new CapturingLogger();
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, m => ReferenceEquals(m, kit.Player.Map) ? system : null, logger: logger);
        var laterSubscriber = new List<uint>();
        kit.Services.QuestRewarded += (_, _, quest) => laterSubscriber.Add(quest.Id);
        Assert.True(kit.Services.TryPrepareReward(kit.Player, kit.Creature.Guid, First, 0, out QuestRewardPlan? plan));
        kit.Drain();

        kit.Services.ApplyReward(plan!); // must not throw: the publication's catch would quarantine the character and kick the session

        Assert.True(kit.State.Quests.Get(First)!.Rewarded);
        Assert.Equal(13u, Emote(kit)); // the DB CompleteScript still ran
        Assert.Equal([First], laterSubscriber); // so did the other subscribers
        Assert.Contains(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails); // and the next quest was offered
        AssertFaultRecorded(Message);
        string line = Assert.Single(logger.Lines, l => l.Contains(nameof(ThrowingAi), StringComparison.Ordinal));
        Assert.Contains(First.ToString(), line);
        Assert.Contains(QuestFlowTestKit.CreatureEntry.ToString(), line);
    }

    [Fact]
    public void AThrowingRegisteredScript_IsContainedTheSameWay_AndTheAiHookStillRuns()
    {
        const string Message = "boom-script-hook-9c42";
        var factory = new CreatureAiFactory();
        factory.RegisterNpcScript(Script, "Throwing", quest: new ThrowingQuestScript(Message));
        using QuestFlowTestKit kit = ChainKit(Script, completeScript: ScriptId);
        var order = new List<string>();
        kit.Creature.AI = new OrderProbeAi(kit.Creature, kit, order);
        CreatureMapSystem system = CompleteScriptSystem(kit);
        var logger = new CapturingLogger();
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, m => ReferenceEquals(m, kit.Player.Map) ? system : null,
            factory: () => factory, logger: logger);

        TurnIn(kit);

        Assert.Single(order);
        Assert.Equal(13u, Emote(kit));
        Assert.Contains(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgQuestgiverQuestDetails);
        AssertFaultRecorded(Message);
        Assert.Contains(logger.Lines, l => l.Contains(Script, StringComparison.Ordinal) && l.Contains(First.ToString(), StringComparison.Ordinal));
    }

    private static void AssertFaultRecorded(string message)
        => Assert.Contains(FaultLedger.Shared.Snapshot(),
            f => f.Site == "CreatureQuestScripts.OnQuestRewarded" && f.SampleMessage.Contains(message, StringComparison.Ordinal));

    // --- the order of the hook and the CompleteScript, against a real creature system ---------------------------

    [Fact]
    public void TheHook_RunsAfterTheCommit_AndBeforeTheCompleteScript()
    {
        using QuestFlowTestKit kit = ChainKit(completeScript: ScriptId);
        var order = new List<string>();
        kit.Creature.AI = new OrderProbeAi(kit.Creature, kit, order);
        CreatureMapSystem system = CompleteScriptSystem(kit);
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, m => ReferenceEquals(m, kit.Player.Map) ? system : null);
        Assert.Equal(0u, Emote(kit));

        TurnIn(kit);

        // The quest was already Rewarded when the hook ran, and the CompleteScript (emote 13) had not run yet; afterwards it has.
        Assert.Equal(["hook rewarded=True emote=0"], order);
        Assert.Equal(13u, Emote(kit));
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// A throwing quest-accept hook (the creature AI's <see cref="IQuestScriptAI"/> or a ScriptName-bound <see cref="INpcQuestScript"/>) is
/// contained like the reward hooks: logged, recorded in the <see cref="FaultLedger"/>, and the accept carries on (DB start script, later
/// subscribers, source spell) instead of being cut short and kicking the session.
/// </summary>
public sealed class QuestAcceptHookFaultTests
{
    private const uint Id = 930401;
    private const uint Spell = 8326;
    private const uint ScriptId = 930402;
    private const string Script = "npc_test_throwing_accept";

    private sealed class ThrowingAcceptAi(Creature creature, string message) : CreatureAI(creature), IQuestScriptAI
    {
        public void OnQuestAccept(Player player, uint questId) => throw new InvalidOperationException(message);
    }

    private sealed class ThrowingAcceptScript(string message) : INpcQuestScript
    {
        public void OnQuestAccept(Player player, Creature creature, uint questId) => throw new InvalidOperationException(message);
    }

    private sealed class RecordingCaster : IQuestSpellCaster
    {
        public List<uint> Casts { get; } = [];

        public bool CastOnSelf(Player player, uint spellId)
        {
            Casts.Add(spellId);
            return true;
        }
    }

    private static QuestTemplate SpellAndScriptQuest() => new()
    {
        Entry = Id, Method = 2, QuestLevel = 1, MinLevel = 1, Title = "Throwing accept", SrcSpell = Spell, StartScript = ScriptId,
        ReqCreatureOrGOId1 = 90, ReqCreatureOrGOCount1 = 1,
    };

    private static CreatureMapSystem StartScriptSystem(QuestFlowKit kit)
    {
        // quest_template.StartScript = ScriptId: one step, emote state 13 on the source (the giver).
        var step = new RelayScriptStep(ScriptId, 0, 0, 1, 13, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var ai = new CreatureAiContent([], []) { DbScripts = new DbScriptCatalog([(DbScriptKind.QuestStart, step)]) };
        var system = new CreatureMapSystem(kit.Player.Map!, new CreatureContent([], [], [], [], [], ai));
        kit.Player.Map!.AddUpdater(system);
        return system;
    }

    private static void AssertAcceptContinued(QuestFlowKit kit, RecordingCaster caster, List<uint> later, string message)
    {
        Assert.Equal(13u, kit.Creature.GetUInt32(UpdateFields.UnitNpcEmotestate)); // StartScript ran
        Assert.Equal([Spell], caster.Casts); // SrcSpell cast
        Assert.Equal([Id], later); // later QuestAccepted subscribers ran
        Assert.Equal(QuestStatus.Incomplete, kit.State.Quests.GetStatus(Id));
        Assert.Contains(FaultLedger.Shared.Snapshot(),
            f => f.Site == "CreatureQuestScripts.OnQuestAccept" && f.SampleMessage.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void AThrowingAiAcceptHook_DoesNotStopStartScript_SubscribersOrSrcSpell()
    {
        const string Message = "boom-accept-ai-77a1";
        var caster = new RecordingCaster();
        using var kit = new QuestFlowKit([SpellAndScriptQuest()], starters: [Id], spellCaster: caster);
        kit.Creature.AI = new ThrowingAcceptAi(kit.Creature, Message);
        CreatureMapSystem system = StartScriptSystem(kit);
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, m => ReferenceEquals(m, kit.Player.Map) ? system : null);
        var later = new List<uint>();
        kit.Services.QuestAccepted += (_, _, quest) => later.Add(quest.Id);

        Assert.True(kit.Accept(Id)); // must not throw

        AssertAcceptContinued(kit, caster, later, Message);
    }

    [Fact]
    public void AThrowingRegisteredAcceptScript_IsContainedTheSameWay()
    {
        const string Message = "boom-accept-script-31c9";
        var factory = new CreatureAiFactory();
        factory.RegisterNpcScript(Script, "ThrowingAccept", quest: new ThrowingAcceptScript(Message));
        var caster = new RecordingCaster();
        using var kit = new QuestFlowKit([SpellAndScriptQuest()], starters: [Id], spellCaster: caster, scriptName: Script);
        CreatureMapSystem system = StartScriptSystem(kit);
        using IDisposable hooks = CreatureQuestScripts.Attach(kit.Services, m => ReferenceEquals(m, kit.Player.Map) ? system : null, factory: () => factory);
        var later = new List<uint>();
        kit.Services.QuestAccepted += (_, _, quest) => later.Add(quest.Id);

        Assert.True(kit.Accept(Id));

        AssertAcceptContinued(kit, caster, later, Message);
    }
}

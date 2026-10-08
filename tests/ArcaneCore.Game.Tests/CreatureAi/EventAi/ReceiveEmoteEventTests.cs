using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>
/// EVENT_T_RECEIVE_EMOTE (22): EmoteId, ConditionId (cmangos CreatureEventAI.h:64, 739-744). A player's text emote aimed at the creature
/// (vmangos WorldSession::HandleTextEmoteOpcode -> CreatureAI::ReceiveEmote, Handlers/ChatHandler.cpp:751-752) readies every row whose
/// emote id is the text emote, with the player as the invoker (cmangos CreatureEventAI::ReceiveEmote, :1829-1842); a row with a
/// condition fires only when the conditions table says yes for that player (CheckEvent, :467-472). classic-db z2815 has 90 such rows.
/// </summary>
public sealed class ReceiveEmoteEventTests
{
    private const uint TextEmoteWave = 101;   // EmotesText.dbc TEXTEMOTE_WAVE
    private const uint TextEmoteSalute = 78;  // TEXTEMOTE_SALUTE

    private sealed class Conditions(bool answer) : IConditionEvaluator
    {
        public List<(uint Id, Player Player)> Asked { get; } = [];

        public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source)
        {
            Asked.Add((conditionId, player));
            return answer;
        }
    }

    private static CreatureAiEvent Row(uint id, uint emote, uint condition = 0, uint flags = 1)
        => new()
        {
            Id = id,
            CreatureId = WolfEntry,
            EventType = 22,
            Flags = flags,
            Param1 = (int)emote,
            Param2 = (int)condition,
            Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, (int)id, (int)EventAiTarget.Invoker, 0),
        };

    private static (WorldRuntime World, Creature Creature, Player Player, FakeCaster Spells) Start(IEnumerable<CreatureAiEvent> rows, IConditionEvaluator? conditions = null)
    {
        CreatureContent content = new([Template() with { AIName = CreatureAiFactory.EventAIName, Civilian = true }], [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(rows, []));
        var spells = new FakeCaster();
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = spells, Conditions = conditions });
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        return (world, Assert.Single(system.Creatures), player, spells);
    }

    [Fact]
    public void TheMatchingEmote_FiresTheRow_WithThePlayerAsTheInvoker()
    {
        (WorldRuntime w, Creature creature, Player player, FakeCaster spells) = Start([Row(1, TextEmoteWave), Row(2, TextEmoteSalute)]);
        using WorldRuntime world = w;

        creature.ReceiveEmote(player, TextEmoteWave);

        (uint spell, Unit? target, _) = Assert.Single(spells.Casts);
        Assert.Equal(1u, spell);
        Assert.Same(player, target);
        Assert.False(creature.Combat.IsInCombat);
    }

    [Fact]
    public void ARowWithoutTheRepeatableFlag_FiresOnce_UntilTheCreatureResets()
    {
        (WorldRuntime w, Creature creature, Player player, FakeCaster spells) = Start([Row(1, TextEmoteWave, flags: 0), Row(2, TextEmoteSalute)]);
        using WorldRuntime world = w;

        creature.ReceiveEmote(player, TextEmoteWave);
        creature.ReceiveEmote(player, TextEmoteWave);
        creature.ReceiveEmote(player, TextEmoteSalute);
        creature.ReceiveEmote(player, TextEmoteSalute);

        Assert.Equal(1, spells.Casts.Count(c => c.Spell == 1));
        Assert.Equal(2, spells.Casts.Count(c => c.Spell == 2));
    }

    [Fact]
    public void AConditionedRow_AsksTheConditionsTable_ForTheEmotingPlayer()
    {
        var no = new Conditions(false);
        (WorldRuntime w, Creature creature, Player player, FakeCaster spells) = Start([Row(1, TextEmoteWave, condition: 4)], no);
        using WorldRuntime world = w;
        creature.ReceiveEmote(player, TextEmoteWave);
        Assert.Empty(spells.Casts);
        Assert.Equal((4u, player), Assert.Single(no.Asked));

        var yes = new Conditions(true);
        (WorldRuntime w2, Creature creature2, Player player2, FakeCaster spells2) = Start([Row(1, TextEmoteWave, condition: 4)], yes);
        using WorldRuntime world2 = w2;
        creature2.ReceiveEmote(player2, TextEmoteWave);
        Assert.Single(spells2.Casts);
    }

    [Fact]
    public void WithoutAConditionsTable_AConditionedRowNeverFires()
    {
        (WorldRuntime w, Creature creature, Player player, FakeCaster spells) = Start([Row(1, TextEmoteWave, condition: 4), Row(2, TextEmoteWave)]);
        using WorldRuntime world = w;

        creature.ReceiveEmote(player, TextEmoteWave);

        Assert.Equal([2u], spells.Casts.Select(c => c.Spell));
    }

    [Fact]
    public void ADeadCreature_HearsNothing()
    {
        (WorldRuntime w, Creature creature, Player player, FakeCaster spells) = Start([Row(1, TextEmoteWave)]);
        using WorldRuntime world = w;
        world.GetMap(0).Combat.DealDamage(player, creature, creature.Health, direct: false);
        Assert.False(creature.IsAlive);
        spells.Casts.Clear();

        creature.ReceiveEmote(player, TextEmoteWave);

        Assert.Empty(spells.Casts);
    }
}

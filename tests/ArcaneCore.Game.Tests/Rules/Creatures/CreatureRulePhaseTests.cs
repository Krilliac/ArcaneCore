using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.Creatures;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.Kernel.Rules.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Rules.Creatures;

/// <summary>
/// The creature host's phase handling for rules written by hand (no <c>creature.row</c> marker): a rule is gated on the phase it was readied in,
/// as cmangos EventAI checks it when it readies a row, and every phase action applies to the live phase. Each test runs a real creature in a
/// map world, with the rule AI on a catalog of authored rules.
/// </summary>
public sealed class CreatureRulePhaseTests
{
    private const uint Entry = 990_501;
    private const short SetPhase = CreatureRuleIds.SharedActionSetPhase;
    private const int Repeatable = (int)RuleFlags.Repeatable;

    private static RuleRecord Rule(int id, short eventType, int p1 = 0, int p2 = 0, int mask = 0, short priority = 0) => new()
    {
        Id = id,
        Host = CreatureRuleIds.Host,
        OwnerKind = CreatureRuleIds.OwnerEntry,
        OwnerId = (int)Entry,
        EventType = eventType,
        EventParam1 = p1,
        EventParam2 = p2,
        PhaseMask = mask,
        Priority = priority,
    };

    private static RuleActionRecord Act(int rule, byte seq, short type, int p1 = 0, int p2 = 0) => new()
    {
        OwnerId = rule,
        Seq = seq,
        ActionType = type,
        Param1 = p1,
        Param2 = p2,
        TargetType = CreatureRuleIds.TargetSelf,
    };

    private sealed class Fight : IDisposable
    {
        private readonly WorldRuntime _world;
        private readonly Map _map;
        private readonly Player _player;
        private readonly Creature _wolf;

        public Fight(IReadOnlyList<RuleRecord> rules, IReadOnlyList<RuleActionRecord> actions)
        {
            RuleCatalog catalog = RuleCatalog.Build(new RuleContent { Rules = rules, Actions = actions }, RuleVocabulary.Default);
            Assert.Empty(catalog.Rejected.Select(r => $"{r.Table} {r.Id}: {r.Reason}"));
            var factory = new CreatureAiFactory();
            CreatureRuleAI.Register(factory, catalog);
            var services = new CreatureAiServices { Factory = factory };
            var content = new CreatureContent([Template(Entry, t => t.AIName = CreatureRuleAI.AiName)], [Spawn(1, Entry, 5, 0)], [], [], [], CreatureAiContent.Empty);
            (_world, _map, CreatureMapSystem system) = CreateAiSystem(content, services);
            (_player, _) = AddPlayer(_world, 1, 0, 0);
            _wolf = system.Creatures.Single(c => c.Template.Entry == Entry);
            Runner = Assert.IsType<CreatureRuleAI>(_wolf.AI).Runner;
        }

        public CreatureRuleRunner Runner { get; }

        public int Phase => Runner.Phase;

        public long Fired => Runner.Evaluator.Counters.Fired;

        public void Pull() => _map.Combat.DealDamage(_player, _wolf, 1, direct: false);

        public void Wait(uint ms)
        {
            for (uint done = 0; done < ms; done += 100)
            {
                _world.RunTick(Math.Min(100, ms - done));
            }
        }

        public void Dispose() => _world.Dispose();
    }

    [Fact]
    public void AuthoredAggroRule_SetPhase_IsKept_AndAPhaseGatedTimerRuleThenFires()
    {
        using var fight = new Fight(
            [
                Rule(1_000_001, CreatureRuleIds.EventAggro),
                Rule(1_000_002, CreatureRuleIds.EventTimerInCombat, 500, 500, mask: 1 << 2),
                Rule(1_000_003, CreatureRuleIds.EventTimerInCombat, 500, 500, mask: 1 << 0),
            ],
            [Act(1_000_001, 0, SetPhase, 2), Act(1_000_002, 0, SetPhase, 7), Act(1_000_003, 0, SetPhase, 9)]);

        fight.Pull();
        fight.Wait(100);
        Assert.Equal(2, fight.Phase);

        fight.Wait(2000);
        Assert.Equal(7, fight.Phase);
        Assert.Equal(2, fight.Fired);
    }

    [Fact]
    public void AuthoredAggroRules_IncPhaseRunsOnTheLivePhase()
    {
        using var fight = new Fight(
            [Rule(1_000_001, CreatureRuleIds.EventAggro), Rule(1_000_002, CreatureRuleIds.EventAggro, priority: 1)],
            [Act(1_000_001, 0, SetPhase, 2), Act(1_000_002, 0, CreatureRuleIds.ActionIncPhase, 1)]);

        fight.Pull();
        fight.Wait(100);

        Assert.Equal(3, fight.Phase);
    }

    /// <summary>A guard rather than a reproduction: it passes before the fix as well, because the explicit set lands on the readied phase.</summary>
    [Fact]
    public void AuthoredRules_SetBackToTheReadiedPhase_IsKept()
    {
        using var fight = new Fight(
            [
                Rule(1_000_001, CreatureRuleIds.EventAggro),
                Rule(1_000_002, CreatureRuleIds.EventTimerInCombat, 500, 500),
                Rule(1_000_003, CreatureRuleIds.EventTimerInCombat, 500, 500, priority: 1),
            ],
            [Act(1_000_001, 0, SetPhase, 1), Act(1_000_002, 0, SetPhase, 2), Act(1_000_003, 0, SetPhase, 1)]);

        fight.Pull();
        fight.Wait(2000);

        Assert.Equal(1, fight.Phase);
    }

    [Fact]
    public void NestedProcessEvent_KeepsTheInnerPhaseWrite_AndTheOuterContinuesOnIt()
    {
        using var fight = new Fight(
            [Rule(1_000_001, CreatureRuleIds.EventAggro), Rule(1_000_002, CreatureRuleIds.EventReceiveAiEvent, 101, mask: 1 << 2)],
            [
                Act(1_000_001, 0, SetPhase, 2),
                Act(1_000_001, 1, CreatureRuleIds.ActionSendAiEvent, 101, 10),
                Act(1_000_001, 2, CreatureRuleIds.ActionIncPhase, 1),
                Act(1_000_002, 0, SetPhase, 4),
            ]);

        fight.Pull();
        fight.Wait(100);

        Assert.Equal(5, fight.Phase);
        Assert.Equal(2, fight.Fired);
    }
}

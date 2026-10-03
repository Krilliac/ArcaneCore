using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi.EventAi;

/// <summary>The spell-system adapter behind the aura and target-casting EventAI events, through the real spell system.</summary>
public sealed class UnitSpellQueriesTests
{
    /// <summary>The real creature caster, recording every cast request first.</summary>
    private sealed class RecordingCaster(ICreatureSpellCaster inner) : ICreatureSpellCaster
    {
        public List<uint> Requests { get; } = [];

        public event Action<Unit, Unit, SpellInfo>? SpellHit
        {
            add => inner.SpellHit += value;
            remove => inner.SpellHit -= value;
        }

        public CreatureCastResult Cast(Creature caster, uint spellId, Unit? target, bool triggered)
        {
            Requests.Add(spellId);
            return inner.Cast(caster, spellId, target, triggered);
        }

        public bool IsCasting(Creature caster) => inner.IsCasting(caster);

        public bool HasAura(Unit unit, uint spellId) => inner.HasAura(unit, spellId);

        public void Interrupt(Creature caster) => inner.Interrupt(caster);

        public void OnCreatureRemoved(Creature creature) => inner.OnCreatureRemoved(creature);
    }

    [Fact]
    public void AuraStacks_ReadTheRealSpellSystem_AndAnAuraEventFiresOnThem()
    {
        using var kit = new SpellTestKit();
        var queries = new SpellSystemUnitSpellQueries(kit.System);
        var caster = new RecordingCaster(new SpellSystemCreatureCaster(kit.System));
        CreatureContent content = new(
            [Template(configure: t => t.AIName = CreatureAiFactory.EventAIName)], [Spawn(1, WolfEntry, 5, 0)], [], [], [],
            new CreatureAiContent(
            [
                new CreatureAiEvent
                {
                    Id = 1, CreatureId = WolfEntry, EventType = (byte)EventAiEventType.Aggro,
                    Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, (int)SpellTestKit.DotSpell, (int)EventAiTarget.Victim, 0),
                },
                new CreatureAiEvent
                {
                    // TARGET_AURA (24): the victim carries the damage-over-time aura: marker cast on self.
                    Id = 2, CreatureId = WolfEntry, EventType = 24, Param1 = (int)SpellTestKit.DotSpell, Param2 = 1,
                    Action1 = new CreatureAiAction((byte)EventAiActionType.Cast, 77777, (int)EventAiTarget.Self, 0),
                },
            ], []));
        (_, Map map, CreatureMapSystem system) = CreateAiSystem(content, new CreatureAiServices { Spells = caster, UnitSpells = queries }, world: kit.World);
        (Player player, _) = kit.AddPlayer(1);
        Creature wolf = Assert.Single(system.Creatures);
        Assert.Equal(0, queries.GetAuraStacks(player, SpellTestKit.DotSpell));
        Assert.False(queries.IsCasting(wolf));

        map.Combat.DealDamage(player, wolf, 1, direct: false);

        Assert.Equal(1, queries.GetAuraStacks(player, SpellTestKit.DotSpell));
        Assert.Equal(0, queries.GetAuraStacks(wolf, SpellTestKit.DotSpell));

        // The aura event is evaluated at the next batch. The marker spell is unknown to the spell system, so the cast is
        // refused, but the request proves the event fired on the real aura state.
        Assert.DoesNotContain(77777u, caster.Requests);
        Run(kit.World, 700);
        Assert.Contains(77777u, caster.Requests);
    }
}

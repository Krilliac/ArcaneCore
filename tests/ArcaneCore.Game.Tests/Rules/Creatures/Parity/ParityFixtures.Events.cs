using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

internal static partial class ParityFixtures
{
    private static CreatureAiAction Mark(uint rowId) => Cast((int)(1000 + rowId));

    private static Creature Friend(ParityContext c) => c.System.Creatures.Single(x => x.Template.Entry == ParityScenario.FriendEntry);

    private static IEnumerable<ParityScenario> EventScenarios()
    {
        yield return new ParityScenario
        {
            Name = "aggro-texts",
            Rows =
            [
                Row(1, 4, a1: Act(1, -1, -2, -3)),
                Row(2, 4, a1: Act(54, -1, Victim)),
                Row(3, 4, a1: Act(1, 7, -2), a2: Act(5, 11)),
                Row(4, 4, a1: Act(1, -3, 0, -2)),
            ],
            Texts = [new(-1, "Fixture says one", 0, 0, 0), new(-2, "Fixture yells two", 1, 0, 5) { Sound = 77 }, new(-3, "Fixture emotes three", 2, 0, 0)],
            Broadcast = [new BroadcastText(7, "Broadcast line", "", 0, 0, 1234, [11, 0, 0], [0, 0, 0])],
            Steps = [Wait(500), Pull(), Wait(1000)],
        };

        yield return new ParityScenario
        {
            Name = "timers",
            Rows =
            [
                Row(10, 0, 500, 1000, 1000, 2000, flags: Repeatable, a1: Mark(10)),
                Row(11, 0, 700, 700, 0, 0, a1: Mark(11)),
                Row(12, 1, 400, 800, 1500, 1500, flags: Repeatable, a1: Mark(12)),
                Row(13, 29, 300, 300, 2000, 2500, flags: Repeatable, a1: Mark(13)),
                Row(14, 0, 5000, 1000, 100, 100, flags: Repeatable, a1: Mark(14)),
            ],
            Steps = [Wait(3000), Pull(), Wait(6000)],
        };

        yield return new ParityScenario
        {
            Name = "health-power-target-percent",
            Rows =
            [
                Row(20, 2, 80, 0, 1000, 1000, flags: Repeatable, a1: Mark(20)),
                Row(21, 2, 40, 0, 0, 0, p5: 1, a1: Mark(21)),
                Row(22, 3, 100, 50, 1000, 1000, flags: Repeatable, a1: Mark(22)),
                Row(23, 31, 100, 0, 1000, 1000, a1: Mark(23)),
                Row(24, 12, 100, 90, 1000, 1000, flags: Repeatable, a1: Mark(24)),
                Row(25, 18, 100, 0, 1000, 1000, a1: Mark(25)),
            ],
            Steps = [Pull(), Wait(1500), HealthTo(60), Wait(1500), HealthTo(30), Wait(1500)],
        };

        yield return new ParityScenario
        {
            Name = "range-casting-facing-unreachable",
            Rows =
            [
                Row(30, 9, 0, 10, 1000, 1000, flags: Repeatable, a1: Mark(30)),
                Row(31, 9, 20, 40, 1000, 1000, flags: Repeatable, a1: Mark(31)),
                Row(32, 13, 1000, 1000, flags: Repeatable, a1: Mark(32)),
                Row(33, 33, 1, 0, 1000, 1000, flags: Repeatable, a1: Mark(33)),
                Row(34, 33, 0, 0, 1000, 1000, flags: Repeatable, a1: Mark(34)),
                Row(35, 36, 0, 0, 1000, 1000, flags: Repeatable, a1: Mark(35)),
            ],
            Steps =
            [
                Pull(),
                Wait(1500),
                Step("victim casts", c => c.UnitSpells.Casters.Add(c.Player)),
                Wait(1500),
                Step("victim moves away", c => c.Player.Relocate(30, 0, 83.5f, 0, 0)),
                Wait(1500),
            ],
        };

        yield return new ParityScenario
        {
            Name = "auras",
            Rows =
            [
                Row(40, 23, 5000, 2, 1000, 1000, flags: Repeatable, a1: Mark(40)),
                Row(41, 24, 5001, 1, 1000, 1000, flags: Repeatable, a1: Mark(41)),
                Row(42, 27, 5002, 1, 1000, 1000, flags: Repeatable, a1: Mark(42)),
                Row(43, 28, 5003, 1, 1000, 1000, flags: Repeatable, a1: Mark(43)),
            ],
            Steps =
            [
                Pull(),
                Wait(1500),
                Step("auras on", c =>
                {
                    c.UnitSpells.Stacks[(c.Wolf, 5000)] = 3;
                    c.UnitSpells.Stacks[(c.Player, 5001)] = 1;
                    c.UnitSpells.Stacks[(c.Wolf, 5002)] = 1;
                    c.UnitSpells.Stacks[(c.Player, 5003)] = 1;
                }),
                Wait(1500),
            ],
        };

        yield return new ParityScenario
        {
            Name = "friendly-and-threat-search",
            Rows =
            [
                Row(50, 14, 10, 30, 1000, 1000, flags: Repeatable, a1: Cast(2050, DefaultTarget)),
                Row(51, 14, 50, 30, 1000, 1000, p5: 1, flags: Repeatable, a1: Act(11, 2051, EventTarget)),
                Row(52, 15, 0, 30, 1000, 1000, flags: Repeatable, a1: Act(11, 2052, EventTarget)),
                Row(53, 16, 5004, 30, 1000, 1000, 0, flags: Repeatable, a1: Act(11, 2053, EventTarget)),
                Row(54, 32, 8, 25, 1000, 1000, flags: Repeatable, a1: Act(11, 2054, Invoker)),
            ],
            Steps =
            [
                Pull(),
                Wait(1500),
                Step("friend is hurt", c => Friend(c).Health = Friend(c).MaxHealth / 4),
                Wait(1500),
                Step("player steps back", c => c.Player.Relocate(15, 0, 83.5f, 0, 0)),
                Wait(1500),
            ],
        };

        yield return new ParityScenario
        {
            Name = "lifecycle",
            Rows =
            [
                Row(60, 4, a1: Mark(60)),
                Row(61, 5, 1000, 1000, 0, a1: Mark(61)),
                Row(62, 5, 0, 0, 1, flags: Repeatable, a1: Mark(62)),
                Row(63, 6, a1: Mark(63)),
                Row(64, 7, a1: Mark(64)),
                Row(65, 11, a1: Mark(65)),
                Row(66, 11, 1, 0, a1: Mark(66)),
                Row(67, 11, 1, 1, a1: Mark(67)),
                Row(68, 11, 2, 9999, a1: Mark(68)),
                Row(69, 21, a1: Mark(69)),
            ],
            Steps =
            [
                Step("respawn", c => c.Stimulus("respawn", ai => ai.OnRespawn())),
                Pull(),
                Wait(500),
                Step("killed a unit", c => c.Stimulus("killed", ai => ai.OnKilledUnit(c.Player))),
                Wait(1500),
                Step("evade", c => c.Stimulus("evade", ai => ai.OnEvade())),
                Step("home", c => c.Stimulus("home", ai => ai.OnReachedHome())),
                Step("death", c => c.Stimulus("death", ai => ai.OnDeath(c.Player))),
            ],
        };

        yield return new ParityScenario
        {
            Name = "spell-emote-aievent-sight",
            Rows =
            [
                Row(70, 8, 133, 0xFF, 1000, 1000, flags: Repeatable, a1: Act(11, 3070, Invoker)),
                Row(71, 8, 0, 0xFF, 0, 0, a1: Mark(71)),
                Row(72, 34, 134, 0xFF, 1000, 1000, flags: Repeatable, a1: Act(11, 3072, Invoker)),
                Row(73, 22, 3, 0, 1000, 1000, flags: Repeatable, a1: Act(11, 3073, Invoker)),
                Row(74, 30, 5, 0, 1000, 1000, flags: Repeatable, a1: Act(11, 3074, Sender)),
                Row(75, 10, 0, 20, 1000, 1000, 0, flags: Repeatable, a1: Act(11, 3075, Invoker)),
                Row(76, 10, 1, 20, 1000, 1000, 1, flags: Repeatable, a1: Mark(76)),
            ],
            Steps =
            [
                Step("spell hits", c =>
                {
                    c.Caster.RaiseHit(c.Player, c.Wolf, new SpellInfo { Id = 133 });
                    c.Caster.RaiseHit(c.Player, c.Wolf, new SpellInfo { Id = 9 });
                    c.Observe();
                }),
                Step("own spell lands", c =>
                {
                    c.Caster.RaiseHit(c.Wolf, c.Player, new SpellInfo { Id = 134 });
                    c.Observe();
                }),
                Step("emote", c => c.Stimulus("emote", ai => ai.OnReceiveEmote(c.Player, 3))),
                Step("other emote", c => c.Stimulus("emote 4", ai => ai.OnReceiveEmote(c.Player, 4))),
                Step("ai event", c => c.Stimulus("ai event", ai => ai.OnReceiveAiEvent(5, Friend(c), c.Player, 0))),
                Step("sight", c => c.Stimulus("sight", ai => ai.MoveInLineOfSight(c.Player))),
                Wait(1500),
                Step("sight again", c => c.Stimulus("sight", ai => ai.MoveInLineOfSight(c.Player))),
            ],
        };

        yield return new ParityScenario
        {
            Name = "summoned-events",
            Rows =
            [
                Row(80, 4, a1: Act(12, (int)ParityScenario.SummonEntry, Self, 60000), a2: Act(32, (int)ParityScenario.SummonEntryB, Self, 1)),
                Row(81, 17, (int)ParityScenario.SummonEntry, 0, 1000, 1000, flags: Repeatable, a1: Act(11, 4081, Invoker)),
                Row(82, 25, (int)ParityScenario.SummonEntry, 0, 1000, 1000, flags: Repeatable, a1: Act(11, 4082, Invoker)),
                Row(83, 26, (int)ParityScenario.SummonEntryB, 0, 1000, 1000, flags: Repeatable, a1: Mark(83)),
            ],
            Summons = [new CreatureAiSummon(1, 8f, 2f, 83.5f, 1f, 30000)],
            Steps =
            [
                Pull(),
                Wait(500),
                Step("summon dies", c =>
                {
                    Creature summoned = c.System.Creatures.First(x => x.Template.Entry == ParityScenario.SummonEntry);
                    c.Map.Combat.DealDamage(c.Player, summoned, summoned.Health + 1, direct: false);
                }),
                Wait(1000),
                Step("summon despawns", c =>
                {
                    Creature summoned = c.System.Creatures.First(x => x.Template.Entry == ParityScenario.SummonEntryB);
                    c.Stimulus("summon despawn", ai => ai.OnSummonedCreatureDespawn(summoned));
                }),
            ],
        };
    }
}

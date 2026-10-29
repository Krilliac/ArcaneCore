using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

internal static partial class ParityFixtures
{
    private static IEnumerable<ParityScenario> ActionScenarios()
    {
        yield return new ParityScenario
        {
            Name = "sound-and-emote-actions",
            Rows =
            [
                Row(100, 4, a1: Act(4, 1001), a2: Act(5, 11), a3: Act(9, 2001, 2002, 2003)),
                Row(101, 4, a1: Act(10, 21, 22, 23), a2: Act(9, 2004, -1, 0)),
                Row(102, 4, flags: RandomAction, a1: Act(4, 3001), a2: Act(5, 31), a3: Act(10, 41, 42, 43)),
            ],
            Steps = [Pull(), Wait(500), Step("again", c => c.Stimulus("aggro", ai => ai.OnAggro(c.Player))), Wait(500)],
        };

        yield return new ParityScenario
        {
            Name = "unit-state-actions",
            Rows =
            [
                Row(110, 4, a1: Act(2, 35), a2: Act(3, (int)ParityScenario.SummonEntry), a3: Act(43, 0, 1234)),
                Row(111, 4, a1: Act(17, 10, 77, Self), a2: Act(18, 0x800, Self), a3: Act(19, 0x20, Self)),
                Row(112, 4, a1: Act(40, 1), a2: Act(47, 3), a3: Act(50, 1)),
                Row(113, 4, a1: Act(42, 1), a2: Act(63, 7), a3: Act(64, 1)),
                Row(114, 4, a1: Act(36, (int)ParityScenario.SummonEntry)),
                Row(115, 4, a1: Act(18, 0x1000, Victim), a2: Act(19, 0x10, Invoker)),
            ],
            Steps = [Pull(), Wait(500), Step("poke", c => c.Stimulus("aggro", ai => ai.OnAggro(c.Player))), Wait(500)],
        };

        yield return new ParityScenario
        {
            Name = "movement-and-combat-actions",
            Rows =
            [
                Row(120, 4, a1: Act(21, 0), a2: Act(20, 1)),
                Row(121, 4, a1: Act(29, 10, 0), a2: Act(57, 3, 18)),
                Row(122, 4, a1: Act(58, 1), a2: Act(59, Victim, 0), a3: Act(61, 1, 1)),
                Row(123, 4, a1: Act(48, 0, 0, 0), a2: Act(51, 1), a3: Act(55, Victim)),
                Row(124, 4, a1: Act(39, 10), a2: Act(45, 7, 10, Invoker), a3: Act(38)),
                Row(125, 4, a1: Act(53, 99, Self)),
                Row(126, 4, a1: Act(34, 3, 4), a2: Act(35, 5, Invoker), a3: Act(56, (int)ParityScenario.SummonEntry)),
                Row(127, 1, 300, 300, 0, 0, a1: Act(59, 0, 1)),
            ],
            Steps =
            [
                Wait(500),
                Pull(),
                Wait(1000),
                Step("again", c => c.Stimulus("aggro", ai => ai.OnAggro(c.Player))),
                Step("evade", c => c.Stimulus("evade", ai => ai.OnEvade())),
                Wait(500),
            ],
        };

        yield return new ParityScenario
        {
            Name = "flee-evade-die-despawn",
            Rows =
            [
                Row(130, 2, 50, 0, 0, 0, a1: Act(25)),
                Row(131, 2, 30, 0, 0, 0, a1: Act(24, 1)),
                Row(132, 4, flags: 0, chance: 100, a1: Act(41, 400)),
                Row(133, 2, 10, 0, 0, 0, a1: Act(37)),
            ],
            Steps = [Pull(), Wait(300), HealthTo(45), Wait(500), HealthTo(25), Wait(500), HealthTo(8), Wait(500)],
        };

        yield return new ParityScenario
        {
            Name = "threat-quest-credit-aura-actions",
            Rows =
            [
                Row(140, 4, a1: Act(13, 50, Victim, 1), a2: Act(14, 120), a3: Act(15, 8001, Invoker, 1)),
                Row(141, 4, a1: Act(33, 9001, Invoker), a2: Act(28, Invoker, 5000), a3: Act(13, -50, Victim, 0)),
                Row(142, 4, a1: Act(11, 6001, Invoker, 0x20), a2: Act(11, 6002, Invoker, 0x02), a3: Act(11, 6003, Invoker, 0x01)),
            ],
            Steps =
            [
                Step("player has the aura", c => c.UnitSpells.Stacks[(c.Player, 5000)] = 1),
                Step("player has 6001", c => c.Caster.Auras.Add((c.Player, 6001))),
                Pull(),
                Wait(500),
            ],
        };

        yield return new ParityScenario
        {
            Name = "phase-actions",
            Rows =
            [
                Row(150, 4, a1: Phase(1)),
                Row(151, 0, 500, 500, 500, 500, inversePhase: ~2u, flags: Repeatable, a1: Mark(151), a2: Act(23, 1)),
                Row(152, 0, 400, 400, 400, 400, inversePhase: 2u, flags: Repeatable, a1: Mark(152)),
                Row(153, 0, 900, 900, 1500, 1500, inversePhase: ~4u, flags: Repeatable, a1: Mark(153), a2: Act(30, 0, 2, 3)),
                Row(154, 0, 2000, 2000, 3000, 3000, inversePhase: ~8u, flags: Repeatable, a1: Mark(154), a2: Act(31, 0, 4)),
                Row(155, 0, 3000, 3000, 0, 0, inversePhase: 0, a1: Mark(155), a2: Phase(0)),
            ],
            Steps = [Pull(), Wait(7000)],
        };

        yield return new ParityScenario
        {
            Name = "phase-set-then-increment-in-one-batch",
            Rows =
            [
                Row(156, 0, 500, 500, 0, 0, a1: Phase(2)),
                Row(157, 0, 500, 500, 0, 0, a1: Act(23, 1)),
                Row(158, 0, 1000, 1000, 1000, 1000, inversePhase: ~8u, flags: Repeatable, a1: Mark(158)),
            ],
            Steps = [Pull(), Wait(2500)],
        };

        yield return new ParityScenario
        {
            Name = "phase-set-then-back-in-one-batch",
            Rows =
            [
                Row(170, 0, 500, 500, 0, 0, a1: Phase(2)),
                Row(171, 0, 500, 500, 0, 0, a1: Phase(0)),
                Row(172, 0, 1000, 1000, 1000, 1000, inversePhase: ~1u, flags: Repeatable, a1: Mark(172)),
            ],
            Steps = [Pull(), Wait(2500)],
        };

        yield return new ParityScenario
        {
            Name = "every-target-type",
            Rows =
            [
                Row(160, 4, a1: Cast(7000, Self), a2: Cast(7001, Victim), a3: Cast(7002, SecondOnThreat)),
                Row(161, 4, a1: Cast(7003, LastOnThreat), a2: Cast(7004, RandomOnThreat), a3: Cast(7005, RandomNotTop)),
                Row(162, 4, a1: Cast(7006, Invoker), a2: Cast(7007, InvokerOwner), a3: Cast(7008, RandomPlayer)),
                Row(163, 4, a1: Cast(7009, RandomNotTopPlayer), a2: Cast(7010, Sender), a3: Cast(7011, Spawner)),
                Row(164, 4, a1: Cast(7014, 13)),
                Row(165, 4, a1: Cast(7012, EventTarget), a2: Cast(7013, DefaultTarget)),
            ],
            Unconvertible = [164],
            Steps =
            [
                Step("second player", c =>
                {
                    Player second = c.AddSecondPlayer(1, 0);
                    c.Wolf.Combat.Threat.AddThreat(second, 10);
                }),
                Pull(5),
                Wait(500),
            ],
        };
    }
}

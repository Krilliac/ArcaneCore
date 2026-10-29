using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

internal static partial class ParityFixtures
{
    private static IEnumerable<ParityScenario> EdgeScenarios()
    {
        yield return new ParityScenario
        {
            Name = "chance-and-random-action",
            Rows =
            [
                Row(200, 0, 500, 500, 500, 500, chance: 50, flags: Repeatable, a1: Mark(200)),
                Row(201, 0, 500, 500, 500, 500, chance: 25, flags: Repeatable, a1: Mark(201), a2: Mark(202)),
                Row(203, 0, 700, 700, 700, 700, flags: Repeatable | RandomAction, a1: Mark(203), a2: Mark(204), a3: Mark(205)),
                Row(206, 0, 700, 700, 700, 700, chance: 60, flags: Repeatable | RandomAction, a1: Mark(206), a2: Mark(207)),
                Row(208, 0, 600, 600, 600, 600, chance: 200, flags: Repeatable, a1: Mark(208)),
                Row(209, 4, a1: Act(1, -1, -2, -3), a2: Mark(209)),
            ],
            Texts = [new(-1, "one", 0, 0, 0), new(-2, "two", 0, 0, 0), new(-3, "three", 0, 0, 0)],
            Steps = [Pull(), Wait(8000)],
        };

        yield return new ParityScenario
        {
            Name = "disabled-rows",
            Rows =
            [
                Row(210, 0, 500, 500, 500, 500, chance: 0, flags: Repeatable, a1: Mark(210)),
                Row(211, 0, 500, 500, 500, 500, flags: Repeatable | DebugOnly, a1: Mark(211)),
                Row(212, 0, 500, 500, 500, 500, inversePhase: uint.MaxValue, flags: Repeatable, a1: Mark(212)),
                Row(213, 0, 500, 500, 500, 500, chance: 50, flags: Repeatable, a1: Mark(213)),
            ],
            Disabled = [210, 211, 212],
            Steps = [Pull(), Wait(4000)],
        };

        yield return new ParityScenario
        {
            Name = "combat-action-retry",
            Rows =
            [
                Row(220, 0, 500, 500, 700, 700, flags: Repeatable | CombatAction, a1: Cast(9100), a2: Mark(220)),
                Row(221, 0, 500, 500, 0, 0, flags: CombatAction, a1: Cast(9100), a2: Mark(221)),
                Row(222, 0, 500, 500, 600, 600, flags: Repeatable, a1: Cast(9100), a2: Mark(222)),
                Row(223, 0, 500, 500, 600, 600, flags: Repeatable | CombatAction, a1: Cast(9101), a2: Mark(223)),
            ],
            Steps =
            [
                Step("refuse 9100", c => c.Caster.Refuse.Add(9100)),
                Pull(),
                Wait(3000),
                Step("allow 9100", c => c.Caster.Refuse.Clear()),
                Wait(3000),
            ],
        };

        yield return new ParityScenario
        {
            Name = "ranged-and-melee-only",
            Rows =
            [
                Row(230, 0, 500, 500, 500, 500, flags: Repeatable | RangedOnly, a1: Mark(230)),
                Row(231, 0, 500, 500, 500, 500, flags: Repeatable | MeleeOnly, a1: Mark(231)),
                Row(232, 0, 2000, 2000, 0, 0, a1: Act(57, 3, 20)),
                Row(233, 0, 4000, 4000, 0, 0, a1: Act(57, 0, 0)),
            ],
            Steps = [Pull(), Wait(6000)],
        };

        yield return new ParityScenario
        {
            Name = "repeat-edges-and-guid-rows",
            Rows =
            [
                Row(240, 0, 400, 400, 1000, 500, flags: Repeatable, a1: Mark(240)),
                Row(241, 0, 400, 400, 0, 0, flags: Repeatable, a1: Mark(241)),
                Row(242, 0, 400, 400, 86_400_000, 86_400_000, flags: Repeatable, a1: Mark(242)),
                Row(243, 4, a1: Mark(243)),
                Row(244, 4, guid: 1, a1: Mark(244)),
                Row(245, 4, a1: Mark(245), a2: Act(37)),
            ],
            Steps = [Pull(), Wait(5000), Step("again", c => c.Stimulus("aggro", ai => ai.OnAggro(c.Player))), Wait(1000)],
        };

        yield return new ParityScenario
        {
            Name = "rows-the-converter-refuses",
            Rows =
            [
                Row(250, 19, a1: Mark(250)),
                Row(251, 4, a1: Act(16, 1, 2, 3)),
                Row(252, 4, a1: Act(54, -1, Victim, 4)),
                Row(253, 0, 86_400_001, 1, 100, 100, a1: Mark(253)),
                Row(254, 4, a1: Mark(254)),
                Row(255, 11, 7, 0, a1: Mark(255)),
                Row(256, 4, flags: CombatAction, a1: default, a2: Mark(256)),
            ],
            Texts = [new(-1, "one", 0, 0, 0)],
            Unconvertible = [250, 251, 252, 253, 255, 256],
            Steps = [Step("respawn", c => c.Stimulus("respawn", ai => ai.OnRespawn())), Pull(), Wait(1000)],
        };
    }
}

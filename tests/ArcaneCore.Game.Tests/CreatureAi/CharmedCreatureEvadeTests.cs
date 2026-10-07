using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.CreatureAi;

/// <summary>
/// Evade of a charmed creature (vmangos CreatureAI::EnterEvadeMode, AI/CreatureAI.cpp:323-346; Creature::IsInEvadeMode,
/// Objects/Creature.cpp:3239-3260): it is sent nowhere, so it is not left in evade mode.
/// </summary>
public sealed class CharmedCreatureEvadeTests
{
    private static readonly ulong SomeCharmer = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, 999).Value;

    private static void Tick(WorldRuntime world, int ticks, uint step = 50)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunTick(step);
        }
    }

    [Fact]
    public void CharmedCreature_Evading_IsNotLeftInEvadeMode_AndCanFightAgainOnceTheCharmEnds()
    {
        // vmangos CreatureAI::EnterEvadeMode (AI/CreatureAI.cpp:323-346) sends a charmed creature nowhere, and IsInEvadeMode
        // (Creature.cpp:3239) is true only while the home generator runs; so the charmed creature is not in evade mode.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        using WorldRuntime world = runtime;
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature wolf = Assert.Single(system.Creatures);
        map.Combat.DealDamage(player, wolf, 1, direct: false);
        Assert.Same(player, wolf.Combat.Victim);

        wolf.SetUInt64(UpdateFields.UnitFieldCharmedby, SomeCharmer);
        system.EnterEvadeMode(wolf);

        Assert.Null(wolf.Combat.Victim);
        Assert.DoesNotContain(MovementGeneratorType.Home, wolf.Motion.ActiveTypes);
        Assert.False(wolf.IsInEvadeMode, "a charmed creature with no home move must not stay in evade mode");

        wolf.SetUInt64(UpdateFields.UnitFieldCharmedby, 0);
        Tick(world, 10);
        Assert.True(system.AttackStart(wolf, player), "the creature still refuses attacks after the charm ended");
    }
}

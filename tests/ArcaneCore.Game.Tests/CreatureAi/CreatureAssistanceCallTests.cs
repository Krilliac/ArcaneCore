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
/// The assistance call (vmangos Creature::CallAssistance, Objects/Creature.cpp:2520-2545; cmangos Entities/Creature.cpp:2159-2176)
/// and its delayed execution (vmangos AssistDelayEvent::Execute, Objects/Creature.cpp:150-173).
/// </summary>
public sealed class CreatureAssistanceCallTests
{
    private static readonly ulong SomeCharmer = ObjectGuid.WithEntry(HighGuid.Unit, WolfEntry, 999).Value;

    private static void Tick(WorldRuntime world, int ticks, uint step = 50)
    {
        for (int i = 0; i < ticks; i++)
        {
            world.RunTick(step);
        }
    }

    private static (WorldRuntime World, Map Map, CreatureMapSystem System, Player Player, Creature Caller, Creature Helper) AssistSetup(
        CreatureTemplate template, float helperX)
    {
        CreatureContent content = Content([template], [Spawn(1, WolfEntry, 10, 0), Spawn(2, WolfEntry, helperX, 0)]);
        (WorldRuntime runtime, Map map, CreatureMapSystem system) = CreateAiSystem(content);
        (Player player, _) = AddPlayer(runtime, 1, 0, 0);
        return (runtime, map, system, player,
            system.Creatures.Single(c => c.Spawn!.Guid == 1), system.Creatures.Single(c => c.Spawn!.Guid == 2));
    }

    [Fact]
    public void VMangosTemplate_WithACallForHelpRangeOfZero_CallsNobody()
    {
        // vmangos Creature::CallAssistance (Creature.cpp:2522): m_callForHelpDist (call_for_help_range) > 0 gates the call.
        var s = AssistSetup(Template() with { CallForHelp = 0f, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos }, helperX: 17);
        using WorldRuntime world = s.World;

        s.Map.Combat.DealDamage(s.Player, s.Caller, 1, direct: false);

        Assert.Equal(0, s.System.PendingAssistCount);
        Tick(world, 40);
        Assert.Null(s.Helper.Combat.Victim);
    }

    [Fact]
    public void VMangosTemplate_WithACallForHelpRange_SearchesTheConfiguredAssistanceRadius()
    {
        // vmangos uses CONFIG_FLOAT_CREATURE_FAMILY_ASSISTANCE_RADIUS (10 yd) for the search, not the template range.
        var near = AssistSetup(Template() with { CallForHelp = 5f, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos }, helperX: 17);
        using (near.World)
        {
            near.Map.Combat.DealDamage(near.Player, near.Caller, 1, direct: false);
            Assert.Equal(1, near.System.PendingAssistCount); // 7 yd: inside 10, outside the template's 5
        }

        var far = AssistSetup(Template() with { CallForHelp = 25f, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos }, helperX: 30);
        using (far.World)
        {
            far.Map.Combat.DealDamage(far.Player, far.Caller, 1, direct: false);
            Assert.Equal(0, far.System.PendingAssistCount); // 20 yd: outside 10
        }
    }

    [Fact]
    public void CMangosTemplate_WithACallForHelpRange_SearchesThatRange()
    {
        // cmangos Creature::CallAssistance (Entities/Creature.cpp:2171-2173): a positive CallForHelp replaces the configured radius.
        var s = AssistSetup(Template() with { CallForHelp = 25f, ExtraFlagsDialect = CreatureExtraFlagsDialect.CMangos }, helperX: 30);
        using WorldRuntime world = s.World;

        s.Map.Combat.DealDamage(s.Player, s.Caller, 1, direct: false);

        Assert.Equal(1, s.System.PendingAssistCount);
        Tick(world, 40);
        Assert.Same(s.Player, s.Helper.Combat.Victim);
    }

    [Fact]
    public void CMangosTemplate_WithNoCallAssist_CallsNobody()
    {
        // cmangos CREATURE_EXTRA_FLAG_NO_CALL_ASSIST 0x800 (Creature.cpp:464, :2168).
        var s = AssistSetup(Template() with { ExtraFlags = 0x800, ExtraFlagsDialect = CreatureExtraFlagsDialect.CMangos }, helperX: 17);
        using WorldRuntime world = s.World;

        s.Map.Combat.DealDamage(s.Player, s.Caller, 1, direct: false);

        Assert.Equal(0, s.System.PendingAssistCount);
    }

    [Fact]
    public void CharmedCreature_DoesNotCallForAssistance()
    {
        var s = AssistSetup(Template(), helperX: 17);
        using WorldRuntime world = s.World;
        s.Caller.SetUInt64(UpdateFields.UnitFieldCharmedby, SomeCharmer);

        s.System.CallAssistance(s.Caller, s.Player);

        Assert.Equal(0, s.System.PendingAssistCount);
    }

    [Fact]
    public void DelayedAssistance_AttacksTheStoredEnemy_EvenWhenTheCallerSwitchedVictimsMeanwhile()
    {
        // vmangos AssistDelayEvent::Execute (Creature.cpp:150-173) attacks m_victimGuid; the caller's current victim is not read.
        var s = AssistSetup(Template(), helperX: 17);
        using WorldRuntime world = s.World;
        (Player second, _) = AddPlayer(world, 2, 0, 4);
        s.Map.Combat.DealDamage(s.Player, s.Caller, 1, direct: false);
        Assert.Equal(1, s.System.PendingAssistCount);

        s.Map.Combat.DealDamage(second, s.Caller, 20, direct: false); // the second player takes the threat lead
        Tick(world, 28); // 1.4 s: still inside the delay
        Assert.Same(second, s.Caller.Combat.Victim);
        Assert.Equal(1, s.System.PendingAssistCount);

        Tick(world, 4); // past the 1.5 s delay
        Assert.Same(second, s.Caller.Combat.Victim);
        Assert.Same(s.Player, s.Helper.Combat.Victim);
    }
}

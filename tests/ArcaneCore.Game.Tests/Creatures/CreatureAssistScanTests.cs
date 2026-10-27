using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestKit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// EventAI CALL_FOR_HELP (<see cref="CreatureMapSystem.CallForHelp"/>) and the civilian's guard call (<see cref="CreatureMapSystem.CallNearestGuard"/>)
/// search the grid cells around the caller (vmangos Cell::VisitGridObjects with the search radius), not every creature of the map, and find
/// exactly the creatures a full scan with the same checks finds.
/// </summary>
public sealed class CreatureAssistScanTests
{
    private const float Radius = 10f;
    private const int FarCrowd = 200;

    /// <summary>Every creature is friendly to every unit (the guard search needs the guards friendly to the civilian) and hostile to none.</summary>
    private sealed class FriendlyTown : ICreatureHostility
    {
        public bool IsHostile(Creature creature, Unit target) => false;

        public bool CanAssist(Creature helper, Creature caller) => helper.FactionTemplate == caller.FactionTemplate;

        public bool IsFriendly(Creature creature, Unit other) => true;
    }

    private static CreatureTemplate GuardTemplate()
        => Template(GuardEntry) with { ExtraFlags = 0x400, ExtraFlagsDialect = CreatureExtraFlagsDialect.CMangos };

    /// <summary>A caller at (10, 0) with creatures inside, on and outside its call radius, and a crowd of the same faction far away.</summary>
    private static (WorldRuntime World, CreatureMapSystem System, Player Player, Creature Caller) Seeded(IEnumerable<CreatureSpawn> nearby, CreatureAiServices? services = null, params CreatureTemplate[] extra)
    {
        var spawns = new List<CreatureSpawn> { Spawn(1, WolfEntry, 10, 0) };
        spawns.AddRange(nearby);
        for (int i = 0; i < FarCrowd; i++)
        {
            spawns.Add(Spawn((uint)(1000 + i), WolfEntry, 300 + ((i % 20) * 6), 300 + ((i / 20) * 6)));
        }

        CreatureContent content = Content([Template(), .. extra], spawns);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateAiSystem(content, services);
        (Player player, _) = AddPlayer(world, 1, 0, 0);
        Creature caller = system.Creatures.Single(c => c.Spawn!.Guid == 1);
        return (world, system, player, caller);
    }

    [Fact]
    public void CallForHelp_FindsTheSameHelpersAsAFullScan_AndExaminesOnlyTheNearbyCreatures()
    {
        float edge = 10 + Radius;
        var s = Seeded(
        [
            Spawn(2, WolfEntry, 15, 0),            // inside
            Spawn(3, WolfEntry, edge, 0),          // on the radius (the bounding radii make it inside)
            Spawn(4, WolfEntry, 10, Radius + 0.5f), // just past the radius, inside the bounding radii
            Spawn(5, WolfEntry, edge + 6, 0),      // outside
            Spawn(6, WolfEntry, 10, -(Radius + 8)), // outside
        ]);
        using WorldRuntime world = s.World;
        Assert.True(s.System.AttackStart(s.Caller, s.Player));
        Assert.Same(s.Player, s.Caller.Combat.Victim);

        List<ulong> expected = s.System.Creatures.Where(h => s.System.CanAssist(h, s.Caller, s.Player, Radius)).Select(h => h.Guid.Value).Order().ToList();
        Assert.NotEmpty(expected);
        Assert.Contains(expected, g => s.System.Creatures.Single(c => c.Guid.Value == g).Spawn!.Guid == 3);
        long before = s.System.AssistScanExamined;

        int joined = s.System.CallForHelp(s.Caller, Radius);

        List<ulong> actual = s.System.Creatures.Where(c => !ReferenceEquals(c, s.Caller) && ReferenceEquals(c.Combat.Victim, s.Player))
            .Select(c => c.Guid.Value).Order().ToList();
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Count, joined);
        long examined = s.System.AssistScanExamined - before;
        Assert.InRange(examined, expected.Count, 6 + 10); // the caller and its five neighbours, never the far crowd of 200
    }

    [Fact]
    public void CallNearestGuard_PicksTheSameGuardAsAFullScan_AndExaminesOnlyTheNearbyCreatures()
    {
        var s = Seeded(
        [
            Spawn(2, GuardEntry, 40, 0),   // 30 yd
            Spawn(3, GuardEntry, 10, 25),  // 25 yd: nearest
            Spawn(4, GuardEntry, 10, -25), // 25 yd, spawned later: a tie, the later one in map order wins (distance <= best)
            Spawn(5, GuardEntry, 75, 0),   // 65 yd: past the 50 yd search
            Spawn(6, WolfEntry, 12, 0),    // not a guard
        ], new CreatureAiServices { Hostility = new FriendlyTown() }, GuardTemplate());
        using WorldRuntime world = s.World;

        Creature? bruteForce = null;
        float best = GuardPostTable.NearestGuardRadius * GuardPostTable.NearestGuardRadius;
        foreach (Creature c in s.System.Creatures)
        {
            if (!ReferenceEquals(c, s.Caller) && c.IsAlive && !c.Combat.IsInCombat && (c.Template.Behaviour & CreatureBehaviourFlags.Guard) != 0)
            {
                float d = ((c.X - s.Caller.X) * (c.X - s.Caller.X)) + ((c.Y - s.Caller.Y) * (c.Y - s.Caller.Y)) + ((c.Z - s.Caller.Z) * (c.Z - s.Caller.Z));
                if (d <= best)
                {
                    best = d;
                    bruteForce = c;
                }
            }
        }

        // The same filters as the system: not the caller, alive, out of combat, a guard, friendly (FriendlyTown: always), in sight (no geometry: always).
        Assert.NotNull(bruteForce);
        Assert.Equal(4u, bruteForce!.Spawn!.Guid);
        long before = s.System.AssistScanExamined;

        Creature? called = s.System.CallNearestGuard(s.Caller, s.Player);

        Assert.Same(bruteForce, called);
        Assert.InRange(s.System.AssistScanExamined - before, 1, 6 + 10);
    }
}

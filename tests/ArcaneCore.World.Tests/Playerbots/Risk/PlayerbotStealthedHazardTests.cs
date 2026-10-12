using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Stealth;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// Stealthed ambushers are hazards although the bot never sees them. Live (bot-QA #298/#302/#303/#312 Ashenvale, #287/#288/#296 Badlands
/// graveyard camp): Wildthorn Lurkers (3821, level 28-29, Hide 6920 out of combat) and Ridge Stalkers (2731, level 36-37, Sneak 7939 and
/// 22766 out of combat) hide with SPELL_AURA_MOD_STEALTH (Spell 6920 and 22766 have aura 16 in spell_template; 7939 is a dummy
/// aura), the stealth visibility rule removes them from the bot's visible objects, and the hazard scan, which reads only those, never
/// marked them: retreat and explore routes walked into them again and again. The scan now also looks at the stealthed creatures of the
/// bot's map within <see cref="PlayerbotRecovery.StealthedHazardYards"/>. A creature the bot cannot see is still never chosen as a target.
/// </summary>
public sealed class PlayerbotStealthedHazardTests(ITestOutputHelper output)
{
    /// <summary>A level-28 creature (the Wildthorn Lurker's range) against a level-20 bot of 900 health: beyond its strength.</summary>
    private static readonly CreatureTemplate Lurker = RiskTestWorld.Template(991328, level: 28, health: 1500, minDamage: 40, maxDamage: 60);

    private static async Task<RiskTestWorld> StartLevelTwentyAsync()
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 20;
            player.MaxHealth = 900;
            player.Health = 900;
            return true;
        });
        return world;
    }

    /// <summary>World thread: the creature enters the Stealth visibility group (the MOD_STEALTH handler's own effect) and leaves the bot's sight.</summary>
    private static void Hide(RiskTestWorld world, Creature creature)
    {
        StealthServices stealth = StealthServices.Find(world.Player.Map!) ?? throw new InvalidOperationException("no stealth services on the map");
        stealth.Registry.SetVisibility(creature, StealthVisibility.NoDetect);
        world.Player.Map!.RefreshVisibility(creature);
        stealth.Registry.SetVisibility(creature, StealthVisibility.Stealth);
        world.Player.Map!.RefreshVisibility(creature);
    }

    private static async Task<Creature> SpawnHiddenAsync(RiskTestWorld world, float dx, float dy)
    {
        Creature creature = await world.OnWorldAsync(() => world.Spawn(Lurker, dx, dy));
        await world.SeeAsync(creature);
        await world.OnWorldAsync(() =>
        {
            Hide(world, creature);
            return true;
        });
        await world.Host.World.AdvanceClockAsync(RiskTestWorld.ThinkMs);
        await world.OnWorldAsync(() =>
        {
            Assert.DoesNotContain(creature.Guid, world.Player.VisibleObjects); // the premise: the bot does not see it
            return true;
        });
        return creature;
    }

    /// <summary>A stealthed level-28 creature 15 yards off the route is a strong hazard and the straight route past it is refused.</summary>
    [Fact]
    public async Task AStealthedCreatureBeyondTheBotsStrength_OffTheRoute_IsAStrongHazard_AndTheRouteIsRefused()
    {
        await using RiskTestWorld world = await StartLevelTwentyAsync();
        Creature lurker = await SpawnHiddenAsync(world, 15, 30);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            world.Brain.Risk.ScanHazards(player);
            PlayerbotHazard hazard = Assert.Single(world.Brain.Risk.Hazards.Active(player.MapId, world.Host.World.NowMs));
            Assert.Equal("strong", hazard.Kind);
            Assert.Equal(lurker.Guid, hazard.Creature);

            var start = new Vector3(player.X, player.Y, player.Z);
            Vector3[] straight = [start, new Vector3(start.X, start.Y + 60, start.Z)];
            PlayerbotHazard? refused = world.Brain.Risk.Hazards.FirstOnRoute(player.MapId, straight, world.Host.World.NowMs);
            Assert.NotNull(refused);
            Assert.Equal(lurker.Guid, refused!.Value.Creature);
            return true;
        });
    }

    /// <summary>The same creature out of the scan range is no hazard (negative control).</summary>
    [Fact]
    public async Task AStealthedCreature_BeyondTheScanRange_IsNoHazard()
    {
        await using RiskTestWorld world = await StartLevelTwentyAsync();
        await SpawnHiddenAsync(world, 15, PlayerbotRecovery.StealthedHazardYards + 10f);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            world.Brain.Risk.ScanHazards(player);
            Assert.Empty(world.Brain.Risk.Hazards.Active(player.MapId, world.Host.World.NowMs));
            return true;
        });
    }

    /// <summary>A stealthed creature the bot could take is no hazard, as a visible one is none (the ordinary level rules).</summary>
    [Fact]
    public async Task AStealthedCreatureTheBotCanTake_IsNoHazard()
    {
        await using RiskTestWorld world = await StartLevelTwentyAsync();
        Creature weak = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991329, level: 5, health: 102, minDamage: 7, maxDamage: 10), 15, 30));
        await world.SeeAsync(weak);
        await world.OnWorldAsync(() =>
        {
            Hide(world, weak);
            return true;
        });
        await world.Host.World.AdvanceClockAsync(RiskTestWorld.ThinkMs);
        await world.OnWorldAsync(() =>
        {
            Assert.DoesNotContain(weak.Guid, world.Player.VisibleObjects);
            world.Brain.Risk.ScanHazards(world.Player);
            Assert.Empty(world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs));
            return true;
        });
    }

    /// <summary>A stealthed creature the bot did detect is both visible and stealthed: one hazard, not two.</summary>
    [Fact]
    public async Task AStealthedCreatureTheBotSees_IsOneHazard()
    {
        await using RiskTestWorld world = await StartLevelTwentyAsync();
        Creature lurker = await world.OnWorldAsync(() => world.Spawn(Lurker, 15, 30));
        await world.SeeAsync(lurker);
        await world.OnWorldAsync(() =>
        {
            StealthServices.Find(world.Player.Map!)!.Registry.SetVisibility(lurker, StealthVisibility.Stealth); // registered, but still in sight
            Assert.Contains(lurker.Guid, world.Player.VisibleObjects);
            world.Brain.Risk.ScanHazards(world.Player);
            Assert.Single(world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs), h => h.Kind == "strong");
            return true;
        });
    }

    /// <summary>
    /// The stealthed creature is a hazard only: the target choice reads the visible creatures, so the bot never "attacks" what it cannot
    /// see (a visible weak creature is chosen as before).
    /// </summary>
    [Fact]
    public async Task AStealthedCreature_IsNeverChosenAsATarget()
    {
        await using RiskTestWorld world = await StartLevelTwentyAsync();
        Creature lurker = await SpawnHiddenAsync(world, 5, 5); // the nearest of all
        Creature wolf = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(991330, level: 18, health: 300, minDamage: 8, maxDamage: 12), 0, 30));
        await world.SeeAsync(wolf);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            world.Brain.Risk.ScanHazards(player);
            Assert.Contains(world.Brain.Risk.Hazards.Active(player.MapId, world.Host.World.NowMs), h => h.Creature == lurker.Guid);
            Assert.NotSame(lurker, PlayerbotBrain.FindTarget(player));
            Assert.NotSame(lurker, world.Brain.Risk.ChooseTarget(player, 0, _ => false, questObjective: false));
            Assert.DoesNotContain(PlayerbotRecovery.Threats(player), t => ReferenceEquals(t.Source, lurker));
            return true;
        });
    }

    /// <summary>The body-keep look (<see cref="PlayerbotRecovery.ThreatsAround"/>) counts a stealthed creature, with the reach of its own detection range.</summary>
    [Fact]
    public async Task TheBodyThreats_IncludeAStealthedCreature_WithItsOwnDetectionReach()
    {
        await using RiskTestWorld world = await StartLevelTwentyAsync();
        CreatureTemplate buzzard = RiskTestWorld.Template(991331, level: 20, health: 200, minDamage: 5, maxDamage: 8) with { Detection = 30f };
        Creature hidden = await SpawnHiddenAsync(world, 20, 0);
        Creature wide = await world.OnWorldAsync(() => world.Spawn(buzzard, -20, 0));
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var body = new Vector3(player.X, player.Y, player.Z);
            List<PlayerbotThreat> around = PlayerbotRecovery.ThreatsAround(player, body, PlayerbotRecovery.BodyThreatYards);
            PlayerbotThreat stealthed = Assert.Single(around, t => ReferenceEquals(t.Source, hidden));
            PlayerbotThreat detecting = Assert.Single(around, t => ReferenceEquals(t.Source, wide));
            Assert.True(detecting.Radius >= 30f, $"the buzzard's reach is {detecting.Radius}");
            Assert.True(stealthed.Radius > 18f, $"the lurker's reach is {stealthed.Radius}"); // 18 + its 8 levels above
            return true;
        });
    }

    /// <summary>The scan with stealthed creatures in range allocates no more than the scan without them.</summary>
    [Fact]
    public async Task TheStealthedPass_AddsNoAllocationToTheScan()
    {
        await using RiskTestWorld world = await StartLevelTwentyAsync();
        var hidden = new List<Creature>();
        for (int i = 0; i < 16; i++) hidden.Add(await SpawnHiddenAsync(world, 20 + i, 20 - i));
        var next = typeof(PlayerbotRisk).GetField("_nextScanMs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        double bytes = await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            Player player = world.Player;
            for (int i = 0; i < 200; i++) { next.SetValue(risk, 0u); risk.ScanHazards(player); }
            double least = double.MaxValue;
            for (int repeat = 0; repeat < 3; repeat++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500; i++) { next.SetValue(risk, 0u); risk.ScanHazards(player); }
                least = Math.Min(least, (GC.GetAllocatedBytesForCurrentThread() - before) / 500d);
            }

            Assert.Equal(16, risk.Hazards.Active(player.MapId, world.Host.World.NowMs).Count(h => h.Kind == "strong"));
            return least;
        });
        output.WriteLine($"stealthed scan: {bytes:F1} bytes per scan with 16 stealthed hazards");
        Console.WriteLine($"stealthed scan: {bytes:F1} bytes per scan with 16 stealthed hazards");
        // The boxed FieldInfo.SetValue call allocates its own 24 bytes; the scan itself nothing.
        Assert.True(bytes <= 32, $"{bytes:F1} bytes per scan");
    }
}

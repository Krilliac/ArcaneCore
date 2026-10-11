using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The way out of a ring of creatures beyond a solo bot's strength (<see cref="PlayerbotRisk.Refused"/>, ring-escape spec of
/// 2026-10-11): granted only to goal walks refused while the bot made no progress for <see cref="PlayerbotRisk.BoxedMs"/> of world
/// time and the fan finds no open line (<see cref="PlayerbotHazards.TryFindGap"/>); the exit is the least exposed gap; the route is
/// pinned, exempt from strong hazards only, bounded by <see cref="PlayerbotRisk.EscapeLifeMs"/>, and ended by a death.
/// <para>
/// The gap ring: a level 10 bot (400 health) and twelve level-1 creatures of 100000 health (1-1 damage) 44 yards round it, every 30
/// degrees. Beyond the bot's strength by the risk (not by level), aggro reach 18 - 9 = 9 yards (CreatureOptions.AggroRate 1, no
/// bounding radius), hazard radius 12: neighbours 22.8 yards apart leave a 4.8-yard gap between their aggro reaches while their
/// hazards overlap, so every line is blocked, and the bisectors at 15 + 30k degrees pass 11.4 yards from the creatures (inside the
/// hazards, outside the reaches). Fan line 2 is 15 degrees.
/// </para>
/// </summary>
public sealed class PlayerbotRingEscapeTests
{
    private static readonly CreatureTemplate Golem = RiskTestWorld.Template(991501, level: 1, health: 100000, minDamage: 1, maxDamage: 1);
    private static readonly CreatureTemplate Cutpurse = RiskTestWorld.Template(991502, level: 5, health: 102, minDamage: 7, maxDamage: 10);
    private const float RingYards = 44f;
    private const float AggroYards = 9f;

    private static async Task<(RiskTestWorld World, Creature[] Ring)> GapRingAsync(Action<PlayerbotOptions>? configure = null)
    {
        RiskTestWorld world = await RiskTestWorld.StartAsync(configure);
        Creature[] ring = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 10;
            player.MaxHealth = 400;
            player.Health = 400;
            var list = new Creature[12];
            for (int i = 0; i < 12; i++)
            {
                float angle = i * MathF.Tau / 12f;
                list[i] = world.Spawn(Golem, MathF.Cos(angle) * RingYards, MathF.Sin(angle) * RingYards);
            }

            return list;
        });
        await world.SeeAsync(ring);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            CreatureSpellThreat none = CreatureSpellThreat.None;
            Assert.True(world.Brain.Risk.BeyondStrength(player, ring[0], none), "the ring creature is not beyond the bot's strength");
            Assert.Equal(AggroYards, world.Creatures.GetAttackDistance(ring[0], player), 3);
            return true;
        });
        return (world, ring);
    }

    private static Vector3 Far(Player player, float degrees, float yards = 90f)
    {
        float angle = degrees * MathF.PI / 180f;
        return new Vector3(player.X + (MathF.Cos(angle) * yards), player.Y + (MathF.Sin(angle) * yards), player.Z);
    }

    /// <summary>A goal walk through the ring: scan first (strong hazards lapse 6 s after the last look), then plan.</summary>
    private static Task<bool> RefuseAsync(RiskTestWorld world, float degrees = 0f) => world.OnWorldAsync(() =>
    {
        Player player = world.Player;
        world.Brain.Risk.ScanHazards(player);
        PlayerbotNavigation.Guard(player, world.Brain.Risk);
        try { return PlayerbotNavigation.TryPlan(player, Far(player, degrees), world.Options, out _); }
        finally { PlayerbotNavigation.Guard(player, null); }
    });

    /// <summary>Three refusals 3 s apart without moving: the third grants the escape.</summary>
    private static async Task EscapeAsync(RiskTestWorld world, float degrees = 0f)
    {
        Assert.False(await RefuseAsync(world, degrees));
        await world.Host.World.AdvanceClockAsync(3_000);
        Assert.False(await RefuseAsync(world, degrees));
        await world.OnWorldAsync(() => { Assert.Equal(0, world.Brain.Risk.Escapes); return true; });
        await world.Host.World.AdvanceClockAsync(3_000);
        await RefuseAsync(world, degrees);
        await world.OnWorldAsync(() => { Assert.Equal(1, world.Brain.Risk.Escapes); return true; });
    }

    private static IEnumerable<Vector3> Sample(IReadOnlyList<Vector3> points)
    {
        for (int i = 1; i < points.Count; i++)
        {
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(points[i - 1], points[i]) / 0.5f));
            for (int step = 0; step <= steps; step++) yield return Vector3.Lerp(points[i - 1], points[i], (float)step / steps);
        }
    }

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));

    /// <summary>1. The pull estimate's trial plans are not evidence: one ChooseTarget in a ring of Cutpurses does not even start the clock.</summary>
    [Fact]
    public async Task TheTrialPlansOfAPullEstimate_AreNoEvidenceOfARing()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature[] ring = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 2;
            player.MaxHealth = 70;
            player.Health = 70;
            return Enumerable.Range(0, 12).Select(i => world.Spawn(Cutpurse,
                MathF.Cos(i * MathF.Tau / 12f) * 35f, MathF.Sin(i * MathF.Tau / 12f) * 35f)).ToArray();
        });
        await world.SeeAsync(ring);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;
            risk.ScanHazards(player);
            PlayerbotNavigation.Guard(player, risk);
            try
            {
                Assert.Null(risk.ChooseTarget(player, 0, _ => false, questObjective: false));
            }
            finally { PlayerbotNavigation.Guard(player, null); }

            Assert.Equal(0u, risk.BoxedSinceMs);
            Assert.Equal(0, risk.Escapes);
            Assert.Null(risk.EscapeRoute);
            return true;
        });
    }

    /// <summary>2. Refusals in one tick count for nothing: eight of them grant no escape.</summary>
    [Fact]
    public async Task RefusalsInOneTick_GrantNoEscape()
    {
        (RiskTestWorld world, _) = await GapRingAsync();
        await using RiskTestWorld owned = world;
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            world.Brain.Risk.ScanHazards(player);
            PlayerbotNavigation.Guard(player, world.Brain.Risk);
            try
            {
                for (int i = 0; i < 8; i++) Assert.False(PlayerbotNavigation.TryPlan(player, Far(player, i * 45f), world.Options, out _));
            }
            finally { PlayerbotNavigation.Guard(player, null); }

            Assert.Equal(0, world.Brain.Risk.Escapes);
            return true;
        });
    }

    /// <summary>
    /// 3. The gate is world time without progress: refused at 0, 3 and 6 s standing still, the third grants the escape, whose route
    /// leaves every strong hazard and never enters a creature's aggro reach (the gap between two of them).
    /// </summary>
    [Fact]
    public async Task SixSecondsBoxedIn_GrantTheEscape_ThroughTheGap()
    {
        (RiskTestWorld world, Creature[] ring) = await GapRingAsync();
        await using RiskTestWorld owned = world;
        await EscapeAsync(world);
        await world.OnWorldAsync(() =>
        {
            PlayerbotRoute route = Assert.IsType<PlayerbotRoute>(world.Brain.Risk.EscapeRoute);
            Vector3 end = route.Points[^1];
            Assert.All(world.Brain.Risk.Hazards.Active(world.Player.MapId, world.Host.World.NowMs),
                h => Assert.False(h.Covers(end), $"the escape ends inside {h}"));
            foreach (Vector3 p in Sample(route.Points))
                foreach (Creature c in ring)
                    Assert.True(Flat(p, new Vector3(c.X, c.Y, c.Z)) > AggroYards, $"the escape passes {p}, in the aggro reach of a ring creature");
            return true;
        });
    }

    /// <summary>3b. A bot that moves 15 yards between its refusals is making progress: no escape.</summary>
    [Fact]
    public async Task ABotMovingBetweenRefusals_IsNotBoxedIn()
    {
        (RiskTestWorld world, _) = await GapRingAsync();
        await using RiskTestWorld owned = world;
        Vector3 start = await world.OnWorldAsync(() => new Vector3(world.Player.X, world.Player.Y, world.Player.Z));
        for (int step = 0; step < 4; step++)
        {
            await RefuseAsync(world);
            await world.Host.World.AdvanceClockAsync(3_000);
            await world.OnWorldAsync(() =>
            {
                float dx = step % 2 == 0 ? 15f : 0f;
                world.Player.Relocate(start.X + dx, start.Y, start.Z, 0, world.Host.World.NowMs);
                return true;
            });
        }

        await world.OnWorldAsync(() => { Assert.Equal(0, world.Brain.Risk.Escapes); return true; });
    }

    /// <summary>4. Two strong creatures guarding a quest giver are no ring: refused every 2 s for 20 s, no escape.</summary>
    [Fact]
    public async Task AGuardedDestination_IsNoRing()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        Creature[] guards = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 10;
            player.MaxHealth = 400;
            player.Health = 400;
            return new[] { world.Spawn(Golem, -10, 40), world.Spawn(Golem, 10, 40) };
        });
        await world.SeeAsync(guards);
        for (int i = 0; i <= 10; i++)
        {
            Assert.False(await world.OnWorldAsync(() =>
            {
                Player player = world.Player;
                world.Brain.Risk.ScanHazards(player);
                PlayerbotNavigation.Guard(player, world.Brain.Risk);
                try { return PlayerbotNavigation.TryPlan(player, new Vector3(player.X, player.Y + 40, player.Z), world.Options, out _); }
                finally { PlayerbotNavigation.Guard(player, null); }
            }), "the guarded destination was not refused");
            await world.Host.World.AdvanceClockAsync(2_000);
        }

        await world.OnWorldAsync(() => { Assert.Equal(0, world.Brain.Risk.Escapes); return true; });
    }

    /// <summary>
    /// 5. The escape route is walked through the strong hazards (not dropped at the first step, nor 12 s in), and its bound holds: past
    /// <see cref="PlayerbotRisk.EscapeLifeMs"/> it is no escape any more and the strong hazards hold against it again.
    /// </summary>
    [Fact]
    public async Task TheEscapeRoute_IsWalked_AndBounded()
    {
        // A MoveSpeed under the run speed walks (PlayerbotMotion.Speed): 2.5 yards a second, so 20 s do not finish the ~51-yard way.
        (RiskTestWorld world, _) = await GapRingAsync(o => o.MoveSpeed = 2f);
        await using RiskTestWorld owned = world;
        await EscapeAsync(world);
        PlayerbotRoute route = await world.OnWorldAsync(() =>
        {
            PlayerbotRoute escape = world.Brain.Risk.EscapeRoute!;
            PlayerbotNavigation.Guard(world.Player, world.Brain.Risk);
            world.Session.ManagedBudget = new ArcaneCore.World.Net.ManagedActionBudget(8);
            Assert.True(PlayerbotNavigation.TryAdvance(world.Session, escape, world.Options, 100, world.Host.World.NowMs), "dropped at once");
            return escape;
        });
        await world.Host.World.AdvanceClockAsync(12_000);
        await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.ScanHazards(world.Player);
            world.Session.ManagedBudget = new ArcaneCore.World.Net.ManagedActionBudget(8);
            Assert.True(PlayerbotNavigation.TryAdvance(world.Session, route, world.Options, 100, world.Host.World.NowMs), "dropped 12 s in");
            Assert.Same(route, world.Brain.Risk.EscapeRoute);
            return true;
        });
        await world.Host.World.AdvanceClockAsync(PlayerbotRisk.EscapeLifeMs - 12_000 + 100);
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotRisk risk = world.Brain.Risk;
            risk.ScanHazards(player);
            Assert.Null(risk.EscapeRoute);
            Assert.False(risk.IsEscape(route));
            // The whole way again, from where it began: the strong hazards hold against it now.
            Assert.NotNull(risk.Blocking(player, route.Points.ToList(), strong: !risk.IsEscape(route)));
            PlayerbotNavigation.Guard(player, null);
            return true;
        });
    }

    /// <summary>6. A death ends the escape and resets the clock: the next refusal starts it again, no new escape.</summary>
    [Fact]
    public async Task ADeath_EndsTheEscape_AndResetsTheClock()
    {
        (RiskTestWorld world, _) = await GapRingAsync();
        await using RiskTestWorld owned = world;
        await EscapeAsync(world);
        await world.OnWorldAsync(() =>
        {
            world.Brain.Risk.OnDeath(world.Player);
            Assert.Null(world.Brain.Risk.EscapeRoute);
            Assert.Equal(0u, world.Brain.Risk.BoxedSinceMs);
            return true;
        });
        await world.Host.World.AdvanceClockAsync(2 * PlayerbotRisk.HazardScanMs);
        Assert.False(await RefuseAsync(world));
        await world.OnWorldAsync(() => { Assert.Equal(1, world.Brain.Risk.Escapes); return true; });
    }

    /// <summary>
    /// 7. Brain-driven: the ringed bot walks out through the gap on its own (its explore legs refused, then the escape), never in
    /// combat with any ring creature, which stand where they were.
    /// </summary>
    [Fact]
    public async Task ARingedBot_WalksOutThroughTheGap_WithoutAggro()
    {
        (RiskTestWorld world, Creature[] ring) = await GapRingAsync();
        await using RiskTestWorld owned = world;
        (Vector3 start, Vector3[] spawns) = await world.OnWorldAsync(() => (new Vector3(world.Player.X, world.Player.Y, world.Player.Z),
            ring.Select(c => new Vector3(c.X, c.Y, c.Z)).ToArray()));
        bool escaping = false, fought = false;
        bool @out = await world.ThinkUntilAsync(30_000, () =>
        {
            Player player = world.Player;
            var here = new Vector3(player.X, player.Y, player.Z);
            return Flat(here, start) >= 50f && !world.Brain.Risk.Hazards.Active(player.MapId, world.Host.World.NowMs)
                .Any(h => h.Kind == PlayerbotHazards.StrongKind && h.Covers(here));
        }, () =>
        {
            escaping |= world.Brain.Goal == PlayerbotGoalKind.Escape;
            fought |= world.Player.Combat.IsInCombat || ring.Any(c => c.Combat.IsInCombat);
        });
        await world.OnWorldAsync(() =>
        {
            Assert.True(@out, $"the bot did not walk out: at {world.Player.X - start.X:F1},{world.Player.Y - start.Y:F1} goal {world.Brain.Goal} escapes {world.Brain.Risk.Escapes}");
            Assert.True(escaping, "the goal was never Escape");
            Assert.False(fought, "the bot or a ring creature entered combat");
            Assert.Equal(1, world.Brain.Risk.Escapes);
            for (int i = 0; i < ring.Length; i++)
                Assert.True(Flat(new Vector3(ring[i].X, ring[i].Y, ring[i].Z), spawns[i]) < 0.5f, "a ring creature moved");
            return true;
        });
    }

    /// <summary>8. A death place across the nearest gap: the chosen exit keeps out of it (another gap).</summary>
    [Fact]
    public async Task TheExit_KeepsOutOfADeathPlace()
    {
        (RiskTestWorld world, _) = await GapRingAsync();
        await using RiskTestWorld owned = world;
        Vector3 death = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            Vector3 at = Far(player, 15f, RingYards);
            world.Brain.Risk.Hazards.Add(player.MapId, at, PlayerbotHazards.DeathYards, world.Host.World.NowMs, 300, "death");
            // The fan itself chooses a line that keeps out of it (not only the check of the planned route after it).
            world.Brain.Risk.ScanHazards(player);
            var here = new Vector3(player.X, player.Y, player.Z);
            Assert.True(world.Brain.Risk.Hazards.TryFindGap(player.MapId, here, world.Host.World.NowMs, 0, out Vector3 exit, out _));
            for (float t = 0; t <= 1f; t += 0.01f)
                Assert.True(Flat(Vector3.Lerp(here, exit, t), at) >= PlayerbotHazards.DeathYards - 0.01f, "the fan's exit line passes the death place");
            return at;
        });
        await EscapeAsync(world, 195f); // a goal walk the other way: its first hazard is a ring creature's
        await world.OnWorldAsync(() =>
        {
            PlayerbotRoute route = world.Brain.Risk.EscapeRoute!;
            Assert.All(Sample(route.Points), p => Assert.True(Flat(p, death) >= PlayerbotHazards.DeathYards - 0.01f, $"the escape passes the death place at {p}"));
            return true;
        });
    }
}

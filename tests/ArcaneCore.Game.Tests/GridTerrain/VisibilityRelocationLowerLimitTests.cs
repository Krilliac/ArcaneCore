using System.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// vmangos Unit::OnRelocated (Unit.cpp:10151-10167): a relocation schedules a visibility pass only once the unit is more than
/// <c>Visibility.RelocationLowerLimit</c> (10 yards, World.cpp:830) from where the last pass was scheduled. The live 2026-10-11
/// "Slow map update ... visibility 52 ms; players 203, moved 2715, visibility candidates 2561" ran a pass on every move.
/// </summary>
public sealed class VisibilityRelocationLowerLimitTests(ITestOutputHelper output)
{
    private sealed class SilentSession(int accountId) : IPlayerSession
    {
        public int AccountId { get; } = accountId;
        public AccountSecurity Security => AccountSecurity.Player;
        public void Send(WorldOpcode opcode, ReadOnlySpan<byte> payload) { }
        public void ProcessWorldPackets(Player player) { }
        public void Kick() { }
        public void OnLoggedOut() { }
    }

    [Fact]
    public void ACreatureWalkingInSmallStepsGetsOnePassPerTenYardsAndIsSeenOnceItIsInRange()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Assert.Equal(10f, world.Options.Maps.VisibilityRelocationLowerLimit); // the vmangos default
        Map map = world.GetMap(0);
        Player watcher = TestWorld.CreatePlayer(1, 0, 0, new SilentSession(1));
        world.AddPlayer(watcher);
        var walker = new TestUnit(1, 130, 0);
        map.AddObject(walker);
        map.Update(50);
        Assert.DoesNotContain(walker.Guid, watcher.VisibleObjects);

        long candidatesBefore = map.ObjectVisibilityCandidates;
        long skippedBefore = map.RelocationsWithoutVisibility;
        int passes = 0;
        // 1 yard a tick from 130 to 60 yards: 70 moves.
        for (int step = 1; step <= 70; step++)
        {
            long before = map.ObjectVisibilityCandidates;
            walker.SetPosition(130 - step, 0, 0, 0);
            map.Update(50);
            if (map.ObjectVisibilityCandidates != before) passes++;
        }

        // The first move after joining passes (no notified position yet), then one per 10+ yards: at most 8 of 70 moves.
        Assert.InRange(passes, 6, 8);
        Assert.Equal(70 - passes, map.RelocationsWithoutVisibility - skippedBefore);
        Assert.True(map.ObjectVisibilityCandidates - candidatesBefore <= passes);
        // At 60 yards it has been well inside the 100-yard range for more than the limit: it is visible.
        Assert.Contains(walker.Guid, watcher.VisibleObjects);
    }

    [Fact]
    public void AGameObjectKeepsAPassOnEveryMove()
    {
        // The lower limit is a Unit rule (Unit::OnRelocated / m_lastNotifiedPosition): a moving game object (an elevator, a
        // transport) is re-evaluated on every move.
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new SilentSession(1)));
        var template = new ArcaneCore.Kernel.WorldData.GameObjects.GameObjectTemplate
        {
            Entry = 1, Type = 5, DisplayId = 1, Name = "Lift", Size = 1f,
            Data = new uint[ArcaneCore.Kernel.WorldData.GameObjects.GameObjectTemplate.DataCount],
        };
        var lift = new ArcaneCore.Game.GameObjects.GameObject(1, template, null);
        lift.SetPosition(50, 0, 0, 0);
        map.AddObject(lift);
        map.Update(50);
        long skippedBefore = map.RelocationsWithoutVisibility;
        int passes = 0;
        for (int step = 1; step <= 20; step++)
        {
            long before = map.ObjectVisibilityCandidates;
            lift.SetPosition(50 - (step * 0.5f), 0, 0, 0);
            map.Update(50);
            if (map.ObjectVisibilityCandidates != before) passes++;
        }

        Assert.Equal(20, passes);
        Assert.Equal(skippedBefore, map.RelocationsWithoutVisibility);
    }

    [Fact]
    public void ALimitOfZeroRunsAPassOnEveryMove()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        world.Options.Maps.VisibilityRelocationLowerLimit = 0;
        Map map = world.GetMap(0);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new SilentSession(1)));
        var walker = new TestUnit(1, 50, 0);
        map.AddObject(walker);
        map.Update(50);
        long skippedBefore = map.RelocationsWithoutVisibility;
        int passes = 0;
        for (int step = 1; step <= 20; step++)
        {
            long before = map.ObjectVisibilityCandidates;
            walker.SetPosition(50 - (step * 0.5f), 0, 0, 0);
            map.Update(50);
            if (map.ObjectVisibilityCandidates != before) passes++;
        }

        Assert.Equal(20, passes);
        Assert.Equal(skippedBefore, map.RelocationsWithoutVisibility);
    }

    [Fact]
    public void AMovingPlayerIsReevaluatedOncePerTenYards()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Player mover = TestWorld.CreatePlayer(1, 0, 0, new SilentSession(1));
        world.AddPlayer(mover);
        var far = new TestUnit(1, 150, 0);
        map.AddObject(far);
        map.Update(50);
        Assert.DoesNotContain(far.Guid, mover.VisibleObjects);

        int passes = 0;
        for (int step = 1; step <= 60; step++)
        {
            long before = map.PlayerVisibilityCandidates;
            mover.SetPosition(step, 0, 0, 0);
            map.Update(50);
            if (map.PlayerVisibilityCandidates != before) passes++;
        }

        Assert.InRange(passes, 5, 7);
        Assert.Contains(far.Guid, mover.VisibleObjects); // the last pass was at 56 yards: 94 yards away, in range
    }

    /// <summary>
    /// Reproducible before/after for the visibility phase: 200 players crowded in one grid (the live Elwynn/Westfall clusters held
    /// 143 and 137 characters in single grids) and 1500 creatures, every unit moving 0.35 yards a tick (7 yd/s, the bot speed),
    /// 100 ticks, limit 0 (a pass per move, before) against 10 (vmangos).
    /// </summary>
    [Fact]
    public void MeasureCrowdedVisibilityPhase()
    {
        (double Ms, long Candidates) Run(float limit)
        {
            using WorldRuntime world = TestWorld.CreateRuntime();
            world.Options.Maps.VisibilityRelocationLowerLimit = limit;
            Map map = world.GetMap(0);
            var random = new Random(5875);
            var players = new List<Player>();
            for (uint i = 0; i < 200; i++)
            {
                Player p = TestWorld.CreatePlayer(i + 1, -9000 + (float)random.NextDouble() * 300, -100 + (float)random.NextDouble() * 300,
                    new SilentSession((int)i + 1));
                world.AddPlayer(p);
                players.Add(p);
            }

            var creatures = new List<WorldObject>();
            for (uint i = 0; i < 1500; i++)
            {
                var c = new TestUnit(i + 1, -9000 + (float)random.NextDouble() * 500, -100 + (float)random.NextDouble() * 500);
                map.AddObject(c);
                creatures.Add(c);
            }

            map.Update(50);
            long candidates = map.ObjectVisibilityCandidates + map.PlayerVisibilityCandidates;
            long visibilityMicros = 0;
            var headings = Enumerable.Range(0, players.Count + creatures.Count).Select(_ => random.NextDouble() * Math.PI * 2).ToArray();
            for (int tick = 0; tick < 100; tick++)
            {
                int k = 0;
                foreach (WorldObject o in players.Cast<WorldObject>().Concat(creatures))
                {
                    double h = headings[k++];
                    o.SetPosition(o.X + (float)(Math.Cos(h) * 0.35), o.Y + (float)(Math.Sin(h) * 0.35), 0, 0);
                }

                var diagnostics = new MapUpdateDiagnostics();
                map.Update(50, diagnostics);
                visibilityMicros += diagnostics.VisibilityMicros;
            }

            return (visibilityMicros / 1000.0 / 100, map.ObjectVisibilityCandidates + map.PlayerVisibilityCandidates - candidates);
        }

        (double beforeMs, long beforeCandidates) = Run(0);
        (double afterMs, long afterCandidates) = Run(10);
        output.WriteLine($"200 players + 1500 creatures moving 0.35 yd/tick: visibility phase mean {beforeMs:F2} ms/tick with a pass per move "
            + $"({beforeCandidates} candidates), {afterMs:F2} ms/tick with the 10-yard limit ({afterCandidates} candidates)");
        // 0.35 yd a tick crosses 10 yards every 29 ticks: about 1/29 of the passes (first moves after joining count once).
        Assert.True(afterCandidates * 10 < beforeCandidates, $"{afterCandidates} vs {beforeCandidates}");
    }
}

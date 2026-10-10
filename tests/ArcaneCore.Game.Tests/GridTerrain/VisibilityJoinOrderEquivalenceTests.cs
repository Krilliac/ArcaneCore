using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// Wave 18: visibility merges join-ordered cells instead of collecting and sorting. These tests hold the merge to the old
/// collect-and-sort result: the same candidate and viewer lists, element for element, on a populated map with wandering,
/// patrolling and formation creatures, rotating pools, moving players, a remote camera and despawns; and the join-ordered list primitives against
/// a plain sort.
/// </summary>
[Collection("World tick load")]
public sealed class VisibilityJoinOrderEquivalenceTests
{
    private static void MovePlayers(Player[] players, int tick)
    {
        for (int i = 0; i < players.Length; i++)
        {
            Player p = players[i];
            float angle = (tick * 0.02f) + i;
            p.SetPosition(p.X + (MathF.Cos(angle) * 3f), p.Y + (MathF.Sin(angle) * 3f), p.Z, angle);
        }
    }

    [Fact]
    public void MergedCandidatesAndViewers_EqualTheSortedReference_EveryTick()
    {
        (WorldRuntime w, CreatureMapSystem creatures, GameObjectMapSystem objects, Player[] players) = CreatureTickAllocationTests.Build();
        using WorldRuntime world = w;
        Map map = players[0].Map!;
        var merged = new List<WorldObject>();
        var sorted = new List<WorldObject>();
        int compared = 0;
        for (int tick = 0; tick < 400; tick++)
        {
            if (tick == 100)
            {
                // A remote camera: player 0 looks through a creature far away (its surroundings become candidates).
                Creature far = creatures.Creatures.OrderByDescending(c => (c.X * c.X) + (c.Y * c.Y)).First();
                players[0].SetUInt64(UpdateFields.PlayerFarsight, far.Guid.Value);
            }

            MovePlayers(players, tick);
            CreatureTickAllocationTests.Step(world, creatures, objects, tick);
            foreach (Player player in players)
            {
                merged.Clear();
                sorted.Clear();
                map.VisibilityCandidates(player, merged);
                map.VisibilityCandidatesBySort(player, sorted);
                Assert.Equal(sorted, merged, ReferenceEqualityComparer.Instance);
                compared += merged.Count;
            }

            foreach (Creature creature in creatures.Creatures.Where((_, i) => i % 10 == tick % 10))
            {
                merged.Clear();
                sorted.Clear();
                map.VisibilityCandidates(creature, merged);
                map.VisibilityCandidatesBySort(creature, sorted);
                Assert.Equal(sorted, merged, ReferenceEqualityComparer.Instance);

                List<Player> viewers = [.. map.ObjectVisibilityViewers(creature)];
                map.UseReferenceVisibilityOrder = true;
                List<Player> reference = [.. map.ObjectVisibilityViewers(creature)];
                map.UseReferenceVisibilityOrder = false;
                Assert.Equal(reference, viewers, ReferenceEqualityComparer.Instance);
            }
        }

        Assert.True(compared > 10_000, $"only {compared} candidates compared");
    }

    private sealed class Probe(long id) : WorldObject(new ObjectGuid((ulong)id), 0, 1, 10)
    {
        public override ReadOnlySpan<ushort> FieldFlags => new ushort[10];

        public override ReadOnlySpan<bool> GuidFieldStarts => new bool[10];

        public override ObjectUpdateFlags CreateUpdateFlags => default;
    }

    [Fact]
    public void JoinOrderLists_InsertRemoveAndMerge_MatchASortedReference()
    {
        var random = new Random(5875);
        var cells = Enumerable.Range(0, 9).Select(_ => new List<WorldObject>()).ToList();
        var all = new List<WorldObject>();
        long next = 1;
        for (int op = 0; op < 20_000; op++)
        {
            List<WorldObject> cell = cells[random.Next(cells.Count)];
            if (cell.Count > 0 && random.Next(3) == 0)
            {
                WorldObject victim = cell[random.Next(cell.Count)];
                Assert.True(JoinOrder.Remove(cell, victim));
                all.Remove(victim);
            }
            else
            {
                // Mostly fresh joins (highest sequence), sometimes an older object relocating into the cell.
                long sequence = random.Next(4) == 0 && next > 10 ? random.NextInt64(1, next) * 1000 : next++ * 1000;
                var probe = new Probe(sequence) { MapSequence = sequence + random.Next(1000) };
                JoinOrder.Insert(cell, probe);
                all.Add(probe);
            }

            Assert.True(cell.Zip(cell.Skip(1)).All(p => p.First.MapSequence <= p.Second.MapSequence));
            if (op % 500 == 0)
            {
                var merged = new List<WorldObject>();
                JoinOrder.Merge(cells.Where(c => c.Count > 0).ToList(), merged);
                Assert.Equal(all.OrderBy(o => o.MapSequence).Select(o => o.MapSequence), merged.Select(o => o.MapSequence));
                Assert.Equal(all.Count, merged.Distinct(ReferenceEqualityComparer.Instance).Count());

                List<WorldObject> extra = [.. all.Where((_, i) => i % 5 == 0).Concat(all.Take(3)).Distinct().OrderBy(o => o.MapSequence)];
                var list = new List<WorldObject>(merged.Where((_, i) => i % 2 == 0));
                var expected = list.Concat(extra).Distinct(ReferenceEqualityComparer.Instance).Cast<WorldObject>().OrderBy(o => o.MapSequence).ToList();
                JoinOrder.MergeDistinct(list, extra, []);
                Assert.Equal(expected, list, ReferenceEqualityComparer.Instance);
            }
        }
    }
}

using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// Worldthread audit lane 6 (finding 2.2): when two players both moved, A's visibility pass ran A-B and B-A and B's pass ran
/// both again. Inside one Output stage an ordered (viewer, target) pair is now evaluated once. The dedupe is per ordered pair,
/// so asymmetric rules still answer each direction, and it never applies outside the stage. Counters:
/// <see cref="Map.VisibilityPairEvaluations"/> and <see cref="Map.VisibilityPairDuplicatesSkipped"/>.
/// </summary>
public sealed class VisibilityPairDedupeTests
{
    private sealed class HidesFrom(Player blindViewer, Player hidden) : IVisibilityRule
    {
        public bool CanSee(Player viewer, WorldObject target, bool alreadyVisible, bool detect)
            => !(ReferenceEquals(viewer, blindViewer) && ReferenceEquals(target, hidden));
    }

    private static (WorldRuntime World, Map Map, Player A, Player B) Setup()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Player a = TestWorld.CreatePlayer(1, 0, 0, new NullPlayerSession(1));
        world.AddPlayer(a);
        Player b = TestWorld.CreatePlayer(2, 10, 0, new NullPlayerSession(2));
        world.AddPlayer(b);
        return (world, map, a, b);
    }

    private static void MoveBoth(WorldRuntime world, Player a, Player b, float bx)
    {
        a.SetPosition(a.X + 1f, a.Y, a.Z, 0f);
        b.SetPosition(bx, b.Y, b.Z, 0f);
        a.NeedsVisibilityUpdate = true;
        b.NeedsVisibilityUpdate = true;
        world.RunTick(50);
    }

    [Fact]
    public void TwoMovingPlayersInRange_EachOrderedPairIsEvaluatedOnce()
    {
        (WorldRuntime world, Map map, Player a, Player b) = Setup();
        using (world)
        {
            long evaluations = map.VisibilityPairEvaluations;
            long skipped = map.VisibilityPairDuplicatesSkipped;
            MoveBoth(world, a, b, 11f);

            long performed = (map.VisibilityPairEvaluations - evaluations) - (map.VisibilityPairDuplicatesSkipped - skipped);
            Assert.Equal(2, performed);
            Assert.True(map.VisibilityPairDuplicatesSkipped - skipped >= 2);
            Assert.Contains(b.Guid, a.VisibleObjects);
            Assert.Contains(a.Guid, b.VisibleObjects);
        }
    }

    /// <summary>Packed guid as written in an update block (mask byte, then the non-zero bytes).</summary>
    private static byte[] Packed(ulong value)
    {
        var bytes = new List<byte> { 0 };
        for (int i = 0; i < 8; i++)
        {
            byte part = (byte)(value >> (8 * i));
            if (part != 0)
            {
                bytes[0] |= (byte)(1 << i);
                bytes.Add(part);
            }
        }

        return [.. bytes];
    }

    /// <summary>The create / out-of-range update packets a session received, as (block type, packed guid) in send order.</summary>
    private static List<(byte Type, bool ForGuid)> Blocks(FakeSession session, ulong guid)
    {
        byte[] packed = Packed(guid);
        var result = new List<(byte, bool)>();
        foreach ((WorldOpcode opcode, byte[] payload) in session.Sent)
        {
            if (opcode != WorldOpcode.SmsgUpdateObject)
            {
                continue;
            }

            byte type = payload[5];
            if (type is (byte)ObjectUpdateType.CreateObject or (byte)ObjectUpdateType.CreateObject2 or (byte)ObjectUpdateType.OutOfRangeObjects)
            {
                // A create block is [type][packed guid]; an out-of-range block is [type][u32 count][packed guids].
                int guidAt = type == (byte)ObjectUpdateType.OutOfRangeObjects ? 10 : 6;
                Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
                result.Add((type, payload.AsSpan(guidAt).StartsWith(packed)));
            }
        }

        return result;
    }

    [Fact]
    public void ReEnteringRange_CreatesEachOtherExactlyOnce_InTheSamePacketOrder()
    {
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            var sa = new FakeSession(1);
            var sb = new FakeSession(2);
            Player a = TestWorld.CreatePlayer(1, 0, 0, sa);
            Player b = TestWorld.CreatePlayer(2, 10, 0, sb);
            world.AddPlayer(a);
            world.AddPlayer(b);
            world.RunTick(50);
            sa.Clear();
            sb.Clear();

            MoveBoth(world, a, b, 5000f);   // both leave each other's range
            MoveBoth(world, a, b, 12f);     // both re-enter
            MoveBoth(world, a, b, 13f);     // in range moves: no further create or out-of-range block

            byte create = (byte)ObjectUpdateType.CreateObject;
            byte create2 = (byte)ObjectUpdateType.CreateObject2;
            byte outOfRange = (byte)ObjectUpdateType.OutOfRangeObjects;
            foreach ((FakeSession session, Player other) in new[] { (sa, b), (sb, a) })
            {
                List<(byte Type, bool ForGuid)> blocks = Blocks(session, other.Guid.Value);
                Assert.Equal(2, blocks.Count);                       // exactly one out-of-range, then exactly one create
                Assert.Equal(outOfRange, blocks[0].Type);
                Assert.True(blocks[1].Type == create || blocks[1].Type == create2);
                Assert.True(blocks[0].ForGuid);                      // both blocks name the other player
                Assert.True(blocks[1].ForGuid);
            }

            Assert.Contains(b.Guid, a.VisibleObjects);
            Assert.Contains(a.Guid, b.VisibleObjects);
        }
    }

    [Fact]
    public void AFiftyPlayerCluster_PerformsEachOrderedPairOnce_AndSkipsAboutHalfTheEvaluations()
    {
        const int n = 50;
        WorldRuntime world = TestWorld.CreateRuntime();
        using (world)
        {
            Map map = world.GetMap(0);
            var players = new List<Player>();
            for (uint i = 1; i <= n; i++)
            {
                Player p = TestWorld.CreatePlayer(i, i, 0, new NullPlayerSession((int)i));
                world.AddPlayer(p);
                players.Add(p);
            }

            world.RunTick(50);
            foreach (Player p in players)
            {
                p.SetPosition(p.X + 0.5f, p.Y, p.Z, 0f);
                p.NeedsVisibilityUpdate = true;
            }

            long evaluations = map.VisibilityPairEvaluations;
            long skipped = map.VisibilityPairDuplicatesSkipped;
            world.RunTick(50);
            long total = map.VisibilityPairEvaluations - evaluations;
            long dup = map.VisibilityPairDuplicatesSkipped - skipped;

            // Every moved player evaluates (self, other) and (other, self) for each of the n - 1 others, so without the dedupe
            // the stage runs 2 * n * (n - 1) evaluations; with it each of the n * (n - 1) ordered pairs is performed once.
            Assert.Equal(n * (n - 1), total - dup);
            double ratio = (double)dup / total;
            Assert.InRange(ratio, 0.45, 0.55);
        }
    }

    [Fact]
    public void AnAsymmetricRule_StillAnswersEachDirectionSeparately()
    {
        (WorldRuntime world, Map map, Player a, Player b) = Setup();
        using (world)
        {
            map.AddVisibilityRule(new HidesFrom(a, b));
            MoveBoth(world, a, b, 5000f);
            MoveBoth(world, a, b, 12f);

            Assert.DoesNotContain(b.Guid, a.VisibleObjects); // A is blind to B
            Assert.Contains(a.Guid, b.VisibleObjects);       // B still sees A
        }
    }

    [Fact]
    public void EvaluationsOutsideTheOutputStage_AreNeverDeduplicated()
    {
        (WorldRuntime world, Map map, Player a, Player b) = Setup();
        using (world)
        {
            long skipped = map.VisibilityPairDuplicatesSkipped;
            map.RefreshVisibility(a);
            map.RefreshVisibility(a);
            map.RefreshVisibility(b);
            Assert.Equal(skipped, map.VisibilityPairDuplicatesSkipped);
        }
    }
}

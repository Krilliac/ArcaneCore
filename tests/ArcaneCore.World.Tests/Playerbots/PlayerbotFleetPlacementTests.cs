using System.Numerics;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>Ring placement of summoned bots (pure; the ground and sight queries are passed in).</summary>
public sealed class PlayerbotFleetPlacementTests
{
    private static readonly Vector3 Anchor = new(100f, 200f, 50f);

    private static float Flat(float x, float y, float z) => z;

    private static bool Open(Vector3 slot) => true;

    [Fact]
    public void Ring_GivesDistinctSlotsAtLeastTwoYardsApart()
    {
        IReadOnlyList<FleetSlot> slots = PlayerbotFleetPlacement.Ring(Anchor, 0.7f, 120, Flat, Open);

        Assert.Equal(120, slots.Count);
        for (int i = 0; i < slots.Count; i++)
        {
            for (int j = i + 1; j < slots.Count; j++)
            {
                float apart = Vector2.Distance(new Vector2(slots[i].X, slots[i].Y), new Vector2(slots[j].X, slots[j].Y));
                Assert.True(apart >= 2f, $"slots {i} and {j} are {apart} yards apart");
            }
        }

        // The first slot is straight ahead of the anchor's facing, three yards out, and it faces the anchor.
        Assert.Equal(Anchor.X + (3f * MathF.Cos(0.7f)), slots[0].X, 3);
        Assert.Equal(Anchor.Y + (3f * MathF.Sin(0.7f)), slots[0].Y, 3);
        Assert.All(slots, s => Assert.InRange(s.Orientation, 0f, MathF.Tau));
        float towards = MathF.Atan2(Anchor.Y - slots[0].Y, Anchor.X - slots[0].X);
        Assert.Equal(towards < 0 ? towards + MathF.Tau : towards, slots[0].Orientation, 3);
    }

    [Fact]
    public void Ring_UsesGroundHeight_AndSkipsSlotsOffTheStepLimitOrOutOfSight()
    {
        // Ground rises 1 yard per yard east of the anchor (slots more than 5 yards higher are dropped);
        // slots west of x = 99 are behind a wall (not in the anchor's line of sight).
        static float Slope(float x, float y, float z) => 50f + Math.Max(0f, x - 100f);
        static bool Wall(Vector3 slot) => slot.X >= 99f;

        IReadOnlyList<FleetSlot> slots = PlayerbotFleetPlacement.Ring(Anchor, 0f, 60, Slope, Wall);

        Assert.NotEmpty(slots);
        Assert.All(slots, s =>
        {
            Assert.True(s.X >= 99f);
            Assert.True(s.X - 100f <= PlayerbotFleetPlacement.MaxStep + 0.001f, $"x={s.X}");
            Assert.Equal(50f + Math.Max(0f, s.X - 100f), s.Z, 3);
        });
        Assert.Contains(slots, s => s.Z > 50f);
        Assert.True(slots.Count < 60);
    }

    [Fact]
    public void Ring_NoTerrainData_KeepsAnchorZ()
    {
        IReadOnlyList<FleetSlot> invalid = PlayerbotFleetPlacement.Ring(Anchor, 0f, 10, (_, _, _) => TerrainTile.InvalidHeightValue, Open);
        IReadOnlyList<FleetSlot> notFinite = PlayerbotFleetPlacement.Ring(Anchor, 0f, 10, (_, _, _) => float.NaN, Open);

        Assert.Equal(10, invalid.Count);
        Assert.All(invalid, s => Assert.Equal(Anchor.Z, s.Z));
        Assert.All(notFinite, s => Assert.Equal(Anchor.Z, s.Z));
    }

    [Fact]
    public void Ring_NotEnoughFreeSlots_ReturnsFewer_NeverStacks()
    {
        IReadOnlyList<FleetSlot> some = PlayerbotFleetPlacement.Ring(Anchor, 0f, 10_000, Flat, Open);
        IReadOnlyList<FleetSlot> none = PlayerbotFleetPlacement.Ring(Anchor, 0f, 10, Flat, _ => false);

        Assert.InRange(some.Count, 100, 600);
        Assert.Equal(some.Count, some.Select(s => (s.X, s.Y)).Distinct().Count());
        Assert.Empty(none);
        Assert.Empty(PlayerbotFleetPlacement.Ring(Anchor, 0f, 0, Flat, Open));
        Assert.All(some, s => Assert.True(Vector2.Distance(new Vector2(s.X, s.Y), new Vector2(Anchor.X, Anchor.Y)) <= 18.76f));
    }
}

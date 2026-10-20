using System.Numerics;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// How far apart (yd, at most) a creature leg's ground is sampled. The terrain is a 4.17 yd lattice with centre vertices, rock and
    /// doodad tops are narrower: at 4 yd (<see cref="GroundHeight.DrapePath"/>'s default) the as-sent terrain offset had p95 0.17 and a
    /// worst leg 0.66 yd, at 2 yd p95 0.17 and 0.27, at 1 yd p95 0.15 and 0.17 (docs/areas/collision-pathing.md, "Ground snap").
    /// </summary>
    internal const float GroundSampleYards = 1f;

    /// <summary>
    /// How far (yd) a sample may lie off the chord of the points kept before it. The offset the client sees adds the stand offset
    /// (<see cref="GroundHeight.StandOffset"/>, 0.05) to this, and the gates are p95 under 0.15: 0.15 here measured p95 0.17.
    /// </summary>
    internal const float GroundToleranceYards = 0.11f;

    /// <summary>
    /// A creature leg draped over the ground (<see cref="GroundHeight.DrapePath"/>): every point on the floor plus the stand offset, and
    /// split every 4 yd where the chord between points would pass over or under it. Walkers and swimmers; a flier keeps the mesh's heights
    /// and a creature that only swims (<c>InhabitType</c> 2) stays in its water. A creature that also swims is put on the liquid surface
    /// where the liquid covers the floor, a pure walker on the floor. Where no floor is in the window (another storey, no data) a
    /// point keeps its height. On the calling thread, which must be allowed to read the model trees: the world thread, or a map thread
    /// inside the serial gate (an ungated wander leg enters it for the drape, like its other model reads).
    /// </summary>
    private List<Vector3> DrapeOnGround(Creature creature, Vector3 start, IReadOnlyList<Vector3> corners)
    {
        uint inhabit = creature.Template.InhabitType;
        if (corners.Count == 0 || (inhabit & 4) != 0 || inhabit == 2)
        {
            return corners as List<Vector3> ?? [.. corners];
        }

        GroundQuery query = GroundHeight.Walker with { Liquid = (inhabit & 2) != 0 ? LiquidMode.Surface : LiquidMode.Bottom };
        if (_ungatedMotion)
        {
            using (Serial())
            {
                return GroundHeight.DrapePath(Map.Collision, start, corners, step: GroundSampleYards, tolerance: GroundToleranceYards, query: query);
            }
        }

        return GroundHeight.DrapePath(Map.Collision, start, corners, step: GroundSampleYards, tolerance: GroundToleranceYards, query: query);
    }
}

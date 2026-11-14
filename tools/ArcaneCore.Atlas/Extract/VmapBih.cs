using System.Numerics;
using ArcaneCore.Game.Maps.Collision.VMaps;

namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// Builds the bounding interval hierarchy of a vmap file the way the reference assembler does, node for node: the same
/// longest-axis spatial splits, the same swap-to-the-right partition, the same single-child and "BVH2" clip nodes, the
/// same word layout. Given the same primitive bounds in the same order it yields the same tree and object list, which
/// is what lets a model file come out equal to the reference tool's. The server's <see cref="BihTree"/> reads the result.
/// Stateless and thread-safe.
/// </summary>
/// <remarks>
/// All arithmetic is single precision in the order the reference writes it; comparisons are written out instead of
/// calling <c>Math.Min</c>/<c>Math.Max</c>, whose treatment of signed zeros differs from the reference's <c>std::min</c>
/// and <c>std::max</c>.
/// </remarks>
internal static class VmapBih
{
    /// <summary>Primitives per leaf of a spawn tree and of a triangle tree.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/BIH.h:87 (build: leafSize = 3 unless given)
    public const int DefaultLeafSize = 3;

    /// <summary>Primitives per leaf of a model's group tree.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/WorldModel.cpp:415 (groupTree.build(groupModels, getBounds, 6))
    public const int GroupLeafSize = 6;

    // ref: vmangos@0e3ff01 src/game/vmap/BIH.h:32 (MAX_STACK_SIZE 64: a node at this depth becomes a leaf)
    private const int MaxDepth = 64;

    private const uint LeafWord = 3u << 30;
    private const uint Bvh2Bit = 1u << 29;

    // ref: vmangos@0e3ff01 dep/g3dlite/source/g3dmath.cpp:82-85 (fnan: the quiet NaN, sign bit clear) and include/G3D/AABox.h:48 (an empty box is all NaN)
    private static readonly float QuietNaN = BitConverter.UInt32BitsToSingle(0x7FC00000);

    /// <summary>The tree of no primitives: one empty leaf and the reference's empty (all NaN) bounds.</summary>
    // ref: vmangos@0e3ff01 src/game/vmap/BIH.h:75-81 (init_empty) and :89-93
    public static BihTree Empty { get; } = BihTree.FromRaw(new Vector3(QuietNaN), new Vector3(QuietNaN), [LeafWord, 0, 0], []);

    /// <summary>Builds the tree over <paramref name="bounds"/> with at most <paramref name="leafSize"/> primitives per leaf where a split is possible.</summary>
    /// <exception cref="InvalidDataException">The bounds are not usable (the reference tool stops with "negative node extents" or "invalid node overlap").</exception>
    public static BihTree Build(IReadOnlyList<(Vector3 Low, Vector3 High)> bounds, int leafSize = DefaultLeafSize)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentOutOfRangeException.ThrowIfLessThan(leafSize, 1);
        if (bounds.Count == 0)
        {
            return Empty;
        }

        // ref: vmangos@0e3ff01 src/game/vmap/BIH.h:94-115 (indices 0..n-1; the tree bounds are the first box merged with every box)
        var indices = new uint[bounds.Count];
        var boxes = new Box[bounds.Count];
        Box all = default;
        for (int i = 0; i < bounds.Count; i++)
        {
            indices[i] = (uint)i;
            boxes[i] = new Box { Lo = bounds[i].Low, Hi = bounds[i].High };
            all = i == 0 ? boxes[0] : Merge(all, boxes[i]);
        }

        // ref: vmangos@0e3ff01 src/game/vmap/BIH.cpp:23-35 (buildHierarchy: a dummy leaf, then subdivide from depth 1)
        var tree = new List<uint> { LeafWord, 0, 0 };
        Box gridBox = all;
        Box nodeBox = all;
        Subdivide(0, bounds.Count - 1, tree, indices, boxes, leafSize, ref gridBox, ref nodeBox, 0, 1);
        return BihTree.FromRaw(all.Lo, all.Hi, [.. tree], indices);
    }

    // ref: vmangos@0e3ff01 src/game/vmap/BIH.cpp:37-253 (BIH::subdivide)
    private static void Subdivide(int left, int right, List<uint> tree, uint[] indices, Box[] boxes, int leafSize, ref Box gridBox, ref Box nodeBox, int nodeIndex, int depth)
    {
        if ((right - left + 1) <= leafSize || depth >= MaxDepth)
        {
            CreateLeaf(tree, nodeIndex, left, right);
            return;
        }

        int axis = -1;
        int rightOrig;
        float clipL;
        float clipR;
        float prevClip = QuietNaN;
        float split = QuietNaN;
        bool wasLeft = true;
        while (true)
        {
            int prevAxis = axis;
            float prevSplit = split;

            Vector3 d = gridBox.Hi - gridBox.Lo;
            if (d.X < 0 || d.Y < 0 || d.Z < 0)
            {
                throw new InvalidDataException("negative node extents");
            }

            for (int i = 0; i < 3; i++)
            {
                if (Get(nodeBox.Hi, i) < Get(gridBox.Lo, i) || Get(nodeBox.Lo, i) > Get(gridBox.Hi, i))
                {
                    throw new InvalidDataException("invalid node overlap");
                }
            }

            // Split the longest axis of the grid box in the middle.
            axis = PrimaryAxis(d);
            split = 0.5f * (Get(gridBox.Lo, axis) + Get(gridBox.Hi, axis));

            // Partition: a primitive whose centre is right of the split is swapped to the end.
            clipL = float.NegativeInfinity;
            clipR = float.PositiveInfinity;
            rightOrig = right;
            float nodeL = float.PositiveInfinity;
            float nodeR = float.NegativeInfinity;
            for (int i = left; i <= right;)
            {
                Box box = boxes[indices[i]];
                float minb = Get(box.Lo, axis);
                float maxb = Get(box.Hi, axis);
                float center = (minb + maxb) * 0.5f;
                if (center <= split)
                {
                    i++;
                    if (clipL < maxb)
                    {
                        clipL = maxb;
                    }
                }
                else
                {
                    (indices[i], indices[right]) = (indices[right], indices[i]);
                    right--;
                    if (clipR > minb)
                    {
                        clipR = minb;
                    }
                }

                nodeL = minb < nodeL ? minb : nodeL;
                nodeR = nodeR < maxb ? maxb : nodeR;
            }

            // The primitives fill much less than the node box on this axis: clip the box with a one-child node.
            if (nodeL > Get(nodeBox.Lo, axis) && nodeR < Get(nodeBox.Hi, axis))
            {
                float nodeBoxW = Get(nodeBox.Hi, axis) - Get(nodeBox.Lo, axis);
                float nodeNewW = nodeR - nodeL;
                if (1.3f * nodeNewW < nodeBoxW)
                {
                    int child = tree.Count;
                    tree.Add(0);
                    tree.Add(0);
                    tree.Add(0);
                    tree[nodeIndex] = ((uint)axis << 30) | Bvh2Bit | (uint)child;
                    tree[nodeIndex + 1] = BitConverter.SingleToUInt32Bits(nodeL);
                    tree[nodeIndex + 2] = BitConverter.SingleToUInt32Bits(nodeR);
                    Set(ref nodeBox.Lo, axis, nodeL);
                    Set(ref nodeBox.Hi, axis, nodeR);
                    Subdivide(left, rightOrig, tree, indices, boxes, leafSize, ref gridBox, ref nodeBox, child, depth + 1);
                    return;
                }
            }

            if (right == rightOrig)
            {
                // Everything stayed left.
                if (prevAxis == axis && FuzzyEq(prevSplit, split))
                {
                    CreateLeaf(tree, nodeIndex, left, right);
                    return;
                }

                Set(ref gridBox.Hi, axis, split);
                if (clipL <= split)
                {
                    prevClip = clipL;
                    wasLeft = true;
                    continue;
                }

                prevClip = QuietNaN;
            }
            else if (left > right)
            {
                // Everything went right.
                right = rightOrig;
                if (prevAxis == axis && FuzzyEq(prevSplit, split))
                {
                    CreateLeaf(tree, nodeIndex, left, right);
                    return;
                }

                Set(ref gridBox.Lo, axis, split);
                if (clipR >= split)
                {
                    prevClip = clipR;
                    wasLeft = false;
                    continue;
                }

                prevClip = QuietNaN;
            }
            else
            {
                // A real split. A preceding one-sided pass that left empty space becomes its own one-child node first.
                if (prevAxis != -1 && !float.IsNaN(prevClip))
                {
                    int child = tree.Count;
                    tree.Add(0);
                    tree.Add(0);
                    tree.Add(0);
                    if (wasLeft)
                    {
                        tree[nodeIndex] = ((uint)prevAxis << 30) | (uint)child;
                        tree[nodeIndex + 1] = BitConverter.SingleToUInt32Bits(prevClip);
                        tree[nodeIndex + 2] = BitConverter.SingleToUInt32Bits(float.PositiveInfinity);
                    }
                    else
                    {
                        tree[nodeIndex] = ((uint)prevAxis << 30) | (uint)(child - 3);
                        tree[nodeIndex + 1] = BitConverter.SingleToUInt32Bits(float.NegativeInfinity);
                        tree[nodeIndex + 2] = BitConverter.SingleToUInt32Bits(prevClip);
                    }

                    depth++;
                    nodeIndex = child;
                }

                break;
            }
        }

        // Children: the left one at nextIndex, the right one three words on; a side without primitives gets no words.
        int nextIndex = tree.Count;
        int nl = right - left + 1;
        int nr = rightOrig - (right + 1) + 1;
        if (nl > 0)
        {
            tree.Add(0);
            tree.Add(0);
            tree.Add(0);
        }
        else
        {
            nextIndex -= 3;
        }

        if (nr > 0)
        {
            tree.Add(0);
            tree.Add(0);
            tree.Add(0);
        }

        tree[nodeIndex] = ((uint)axis << 30) | (uint)nextIndex;
        tree[nodeIndex + 1] = BitConverter.SingleToUInt32Bits(clipL);
        tree[nodeIndex + 2] = BitConverter.SingleToUInt32Bits(clipR);

        Box gridBoxL = gridBox;
        Box gridBoxR = gridBox;
        Box nodeBoxL = nodeBox;
        Box nodeBoxR = nodeBox;
        Set(ref gridBoxL.Hi, axis, split);
        Set(ref gridBoxR.Lo, axis, split);
        Set(ref nodeBoxL.Hi, axis, clipL);
        Set(ref nodeBoxR.Lo, axis, clipR);
        if (nl > 0)
        {
            Subdivide(left, right, tree, indices, boxes, leafSize, ref gridBoxL, ref nodeBoxL, nextIndex, depth + 1);
        }

        if (nr > 0)
        {
            Subdivide(right + 1, rightOrig, tree, indices, boxes, leafSize, ref gridBoxR, ref nodeBoxR, nextIndex + 3, depth + 1);
        }
    }

    // ref: vmangos@0e3ff01 src/game/vmap/BIH.h:401-406 (word 0: leaf marker and the first object; word 1: the count; word 2 is left as allocated)
    private static void CreateLeaf(List<uint> tree, int nodeIndex, int left, int right)
    {
        tree[nodeIndex] = LeafWord | (uint)left;
        tree[nodeIndex + 1] = (uint)(right - left + 1);
    }

    // ref: vmangos@0e3ff01 dep/g3dlite/source/Vector3.cpp:130-153 (x only when strictly longest; y only when longer than z and not shorter than x)
    private static int PrimaryAxis(Vector3 v)
    {
        float nx = MathF.Abs(v.X);
        float ny = MathF.Abs(v.Y);
        float nz = MathF.Abs(v.Z);
        if (nx > ny)
        {
            return nx > nz ? 0 : 2;
        }

        return ny > nz ? 1 : 2;
    }

    // ref: vmangos@0e3ff01 dep/g3dlite/include/G3D/g3dmath.h:839-856 (a == b, or |a - b| within 0.00001 * (|a| + 1))
    private static bool FuzzyEq(float a, float b)
    {
        if (a == b)
        {
            return true;
        }

        float aa = MathF.Abs(a) + 1.0f;
        float eps = float.IsPositiveInfinity(aa) ? 0.00001f : 0.00001f * aa;
        return MathF.Abs(a - b) <= eps;
    }

    // ref: vmangos@0e3ff01 dep/g3dlite/include/G3D/AABox.h:106-114 and include/G3D/Vector3.h:778-785 (min(v, this) per component)
    private static Box Merge(Box a, Box b) => new()
    {
        Lo = new Vector3(a.Lo.X < b.Lo.X ? a.Lo.X : b.Lo.X, a.Lo.Y < b.Lo.Y ? a.Lo.Y : b.Lo.Y, a.Lo.Z < b.Lo.Z ? a.Lo.Z : b.Lo.Z),
        Hi = new Vector3(b.Hi.X < a.Hi.X ? a.Hi.X : b.Hi.X, b.Hi.Y < a.Hi.Y ? a.Hi.Y : b.Hi.Y, b.Hi.Z < a.Hi.Z ? a.Hi.Z : b.Hi.Z),
    };

    private static float Get(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    private static void Set(ref Vector3 v, int axis, float value)
    {
        switch (axis)
        {
            case 0:
                v.X = value;
                break;
            case 1:
                v.Y = value;
                break;
            default:
                v.Z = value;
                break;
        }
    }

    private struct Box
    {
        public Vector3 Lo;
        public Vector3 Hi;
    }
}

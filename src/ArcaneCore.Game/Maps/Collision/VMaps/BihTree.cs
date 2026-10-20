using System.Numerics;
using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>
/// The bounding interval hierarchy the vmap files store (format of cMaNGOS/vmangos
/// <c>BIH::readFromFile</c>, after Wächter &amp; Keller's BIH as used by Sunflow): the tree's
/// bounds, a flat array of 32-bit node words and the primitive index list.
/// <para>
/// A node is three words. Word 0 holds the axis in bits 30–31 (3 = leaf), a "BVH2" flag in bit 29
/// and a 29-bit offset. A leaf lists <c>word1</c> primitives starting at <c>objects[offset]</c>.
/// An interior node's children sit at <c>offset</c> (left) and <c>offset + 3</c> (right); word 1 is
/// the left child's upper bound and word 2 the right child's lower bound on the axis, as float
/// bits. A BVH2 node has one child at <c>offset</c> bounded by [word1, word2] on the axis.
/// </para>
/// <para>
/// The traversal here is ArcaneCore's own (an interval-clipping descent with an explicit stack),
/// written from that description; it is bounds-checked so a corrupt tree yields no hits instead
/// of throwing or looping: a child is only entered when it lies after its parent.
/// </para>
/// <para>
/// A node whose left side is empty stores <c>offset = right child - 3</c> with a left clip of -inf
/// (vmangos <c>BIH::subdivide</c>, "nextIndex -= 3"); for the most recently allocated node that
/// offset is the node itself. The clip keeps the left side from ever being entered, so the
/// "after the parent" rule is applied to the child actually visited, not to the offset word.
/// </para>
/// </summary>
public sealed class BihTree
{
    private const int LeafAxis = 3;
    private const uint Bvh2Bit = 1u << 29;
    private const uint OffsetMask = (1u << 29) - 1;
    private const int MaxStack = 256;

    private readonly uint[] _tree;
    private readonly uint[] _objects;

    private BihTree(Vector3 low, Vector3 high, uint[] tree, uint[] objects)
    {
        Low = low;
        High = high;
        _tree = tree;
        _objects = objects;
    }

    /// <summary>An empty tree (vmangos <c>init_empty</c>: one leaf with no primitives).</summary>
    public static BihTree Empty { get; } = new(Vector3.Zero, Vector3.Zero, [(uint)LeafAxis << 30, 0, 0], []);

    public Vector3 Low { get; }

    public Vector3 High { get; }

    /// <summary>Number of primitive slots (vmangos <c>primCount</c>: the object list length).</summary>
    public int PrimitiveCount => _objects.Length;

    internal IReadOnlyList<uint> Nodes => _tree;

    internal IReadOnlyList<uint> Objects => _objects;

    /// <summary>A tree from raw words (tests and readers).</summary>
    internal static BihTree FromRaw(Vector3 low, Vector3 high, uint[] tree, uint[] objects)
    {
        if (tree.Length < 3)
        {
            throw new InvalidDataException("a BIH needs at least one node");
        }

        return new BihTree(low, high, tree, objects);
    }

    internal static BihTree Read(ref CollisionDataReader reader)
    {
        Vector3 low = reader.ReadVector3();
        Vector3 high = reader.ReadVector3();
        uint[] tree = ReadWords(ref reader);
        uint[] objects = ReadWords(ref reader);
        return FromRaw(low, high, tree, objects);
    }

    internal void Write(BinaryWriter writer)
    {
        WriteVector(writer, Low);
        WriteVector(writer, High);
        writer.Write((uint)_tree.Length);
        foreach (uint word in _tree)
        {
            writer.Write(word);
        }

        writer.Write((uint)_objects.Length);
        foreach (uint index in _objects)
        {
            writer.Write(index);
        }
    }

    /// <summary>
    /// Build a tree over primitive bounds (object median split on the widest centroid axis, at
    /// most <paramref name="leafSize"/> primitives per leaf). Used for test fixtures and tools;
    /// readers accept any valid BIH, including BVH2 nodes this builder never emits.
    /// </summary>
    public static BihTree Build(IReadOnlyList<(Vector3 Low, Vector3 High)> bounds, int leafSize = 3)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentOutOfRangeException.ThrowIfLessThan(leafSize, 1);
        if (bounds.Count == 0)
        {
            return Empty;
        }

        Vector3 low = bounds[0].Low;
        Vector3 high = bounds[0].High;
        foreach ((Vector3 l, Vector3 h) in bounds)
        {
            low = Vector3.Min(low, l);
            high = Vector3.Max(high, h);
        }

        var tree = new List<uint> { 0, 0, 0 };
        var objects = new List<uint>(bounds.Count);
        int[] indices = Enumerable.Range(0, bounds.Count).ToArray();
        BuildNode(tree, objects, 0, indices, bounds, leafSize, 0);
        return new BihTree(low, high, [.. tree], [.. objects]);
    }

    private static void BuildNode(List<uint> tree, List<uint> objects, int node, int[] indices, IReadOnlyList<(Vector3 Low, Vector3 High)> bounds, int leafSize, int depth)
    {
        if (indices.Length <= leafSize || depth >= 48)
        {
            tree[node] = ((uint)LeafAxis << 30) | (uint)objects.Count;
            tree[node + 1] = (uint)indices.Length;
            tree[node + 2] = 0;
            objects.AddRange(indices.Select(i => (uint)i));
            return;
        }

        Vector3 cLow = Centroid(bounds[indices[0]]);
        Vector3 cHigh = cLow;
        foreach (int i in indices)
        {
            Vector3 c = Centroid(bounds[i]);
            cLow = Vector3.Min(cLow, c);
            cHigh = Vector3.Max(cHigh, c);
        }

        Vector3 extent = cHigh - cLow;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        int[] sorted = [.. indices.OrderBy(i => Axis(Centroid(bounds[i]), axis)).ThenBy(i => i)];
        int half = sorted.Length / 2;
        int[] left = sorted[..half];
        int[] right = sorted[half..];

        float clipLeft = left.Max(i => Axis(bounds[i].High, axis));
        float clipRight = right.Min(i => Axis(bounds[i].Low, axis));

        int child = tree.Count;
        tree.AddRange([0, 0, 0, 0, 0, 0]);
        tree[node] = ((uint)axis << 30) | (uint)child;
        tree[node + 1] = BitConverter.SingleToUInt32Bits(clipLeft);
        tree[node + 2] = BitConverter.SingleToUInt32Bits(clipRight);
        BuildNode(tree, objects, child, left, bounds, leafSize, depth + 1);
        BuildNode(tree, objects, child + 3, right, bounds, leafSize, depth + 1);
    }

    /// <summary>
    /// Visit the primitives whose leaves the ray reaches within <paramref name="maxDistance"/>,
    /// nearest region first. The callback tests a primitive and may shorten
    /// <paramref name="maxDistance"/> (returning true for a hit); with
    /// <paramref name="stopAtFirstHit"/> the walk ends at the first hit.
    /// </summary>
    public void IntersectRay(Vector3 origin, Vector3 direction, ref float maxDistance, RayPrimitiveCallback callback, bool stopAtFirstHit)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var wrapper = new DelegateRayCallback(callback);
        IntersectRay(origin, direction, ref maxDistance, ref wrapper, stopAtFirstHit);
    }

    internal void IntersectRay<TCallback>(Vector3 origin, Vector3 direction, ref float maxDistance, ref TCallback callback, bool stopAtFirstHit)
        where TCallback : struct, IRayPrimitiveCallback
    {
        uint[] objects = _objects;
        if (objects.Length == 0 || !(maxDistance > 0))
        {
            return;
        }

        if (!ClipToBox(origin, direction, Low, High, 0, maxDistance, out float tMin, out float tMax))
        {
            return;
        }

        uint[] tree = _tree;
        Span<(int Node, float Min, float Max)> stack = stackalloc (int, float, float)[MaxStack];
        int depth = 0;
        stack[depth++] = (0, tMin, tMax);
        while (depth > 0)
        {
            (int node, float min, float max) = stack[--depth];
            if (min > maxDistance)
            {
                continue;
            }

            max = MathF.Min(max, maxDistance);
            while (true)
            {
                if (node < 0 || node + 2 >= tree.Length)
                {
                    break;
                }

                uint word = tree[node];
                int axis = (int)(word >> 30);
                int offset = (int)(word & OffsetMask);
                if (axis == LeafAxis)
                {
                    if ((word & Bvh2Bit) != 0)
                    {
                        break;
                    }

                    uint count = tree[node + 1];
                    if ((uint)offset < (uint)objects.Length)
                    {
                        int end = (int)Math.Min((long)objects.Length, (long)offset + count);
                        for (int i = offset; i < end; i++)
                        {
                            if (callback.Invoke((int)objects[i], ref maxDistance) && stopAtFirstHit)
                            {
                                return;
                            }
                        }
                    }

                    break;
                }

                float clipLow = BitConverter.UInt32BitsToSingle(tree[node + 1]);
                float clipHigh = BitConverter.UInt32BitsToSingle(tree[node + 2]);
                float o = Axis(origin, axis);
                float d = Axis(direction, axis);

                if ((word & Bvh2Bit) != 0)
                {
                    if (offset <= node || !ClipSlab(o, d, clipLow, clipHigh, ref min, ref max))
                    {
                        break;
                    }

                    node = offset;
                    continue;
                }

                // Children always follow their parent; an entered child that does not is corrupt.
                int leftChild = offset > node ? offset : -1;
                int rightChild = offset + 3 > node ? offset + 3 : -1;
                if (MathF.Abs(d) < 1e-12f)
                {
                    bool visitLeft = leftChild >= 0 && o <= clipLow;
                    bool visitRight = rightChild >= 0 && o >= clipHigh;
                    if (visitLeft && visitRight && depth < MaxStack)
                    {
                        stack[depth++] = (rightChild, min, max);
                    }

                    if (visitLeft)
                    {
                        node = leftChild;
                        continue;
                    }

                    if (visitRight)
                    {
                        node = rightChild;
                        continue;
                    }

                    break;
                }

                float tLeft = (clipLow - o) / d;
                float tRight = (clipHigh - o) / d;
                (int near, float nearMax, int far, float farMin) = d > 0
                    ? (leftChild, MathF.Min(max, tLeft), rightChild, MathF.Max(min, tRight))
                    : (rightChild, MathF.Min(max, tRight), leftChild, MathF.Max(min, tLeft));

                if (far >= 0 && farMin <= max && depth < MaxStack)
                {
                    stack[depth++] = (far, farMin, max);
                }

                if (near >= 0 && min <= nearMax)
                {
                    node = near;
                    max = nearMax;
                    continue;
                }

                break;
            }
        }
    }

    private readonly struct DelegateRayCallback(RayPrimitiveCallback callback) : IRayPrimitiveCallback
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Invoke(int primitive, ref float maxDistance) => callback(primitive, ref maxDistance);
    }

    /// <summary>Visit the primitives whose leaves contain the point.</summary>
    public void IntersectPoint(Vector3 point, Action<int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var wrapper = new DelegatePointCallback(callback);
        IntersectPoint(point, ref wrapper);
    }

    internal void IntersectPoint<TCallback>(Vector3 point, ref TCallback callback)
        where TCallback : struct, IPointPrimitiveCallback
    {
        uint[] objects = _objects;
        if (objects.Length == 0 || !Contains(Low, High, point))
        {
            return;
        }

        uint[] tree = _tree;
        Span<int> stack = stackalloc int[MaxStack];
        int depth = 0;
        stack[depth++] = 0;
        while (depth > 0)
        {
            int node = stack[--depth];
            while (true)
            {
                if (node < 0 || node + 2 >= tree.Length)
                {
                    break;
                }

                uint word = tree[node];
                int axis = (int)(word >> 30);
                int offset = (int)(word & OffsetMask);
                if (axis == LeafAxis)
                {
                    if ((word & Bvh2Bit) == 0 && (uint)offset < (uint)objects.Length)
                    {
                        uint count = tree[node + 1];
                        int end = (int)Math.Min((long)objects.Length, (long)offset + count);
                        for (int i = offset; i < end; i++)
                        {
                            callback.Invoke((int)objects[i]);
                        }
                    }

                    break;
                }

                float clipLow = BitConverter.UInt32BitsToSingle(tree[node + 1]);
                float clipHigh = BitConverter.UInt32BitsToSingle(tree[node + 2]);
                float p = Axis(point, axis);
                if ((word & Bvh2Bit) != 0)
                {
                    if (offset <= node || p < clipLow || p > clipHigh)
                    {
                        break;
                    }

                    node = offset;
                    continue;
                }

                // As in IntersectRay: only a child after its parent is entered.
                bool visitLeft = offset > node && p <= clipLow;
                bool visitRight = offset + 3 > node && p >= clipHigh;
                if (visitLeft && visitRight && depth < MaxStack)
                {
                    stack[depth++] = offset + 3;
                }

                if (visitLeft)
                {
                    node = offset;
                    continue;
                }

                if (visitRight)
                {
                    node = offset + 3;
                    continue;
                }

                break;
            }
        }
    }

    private readonly struct DelegatePointCallback(Action<int> callback) : IPointPrimitiveCallback
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Invoke(int primitive) => callback(primitive);
    }

    /// <summary>Clip the ray interval [<paramref name="tMin"/>, <paramref name="tMax"/>] to a box (slab test).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ClipToBox(Vector3 origin, Vector3 direction, Vector3 low, Vector3 high, float tMin, float tMax, out float outMin, out float outMax)
    {
        outMin = tMin;
        outMax = tMax;
        return ClipSlab(origin.X, direction.X, low.X, high.X, ref outMin, ref outMax)
            && ClipSlab(origin.Y, direction.Y, low.Y, high.Y, ref outMin, ref outMax)
            && ClipSlab(origin.Z, direction.Z, low.Z, high.Z, ref outMin, ref outMax);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Contains(Vector3 low, Vector3 high, Vector3 p)
        => p.X >= low.X && p.X <= high.X && p.Y >= low.Y && p.Y <= high.Y && p.Z >= low.Z && p.Z <= high.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Axis(Vector3 v, int axis) => axis switch
    {
        0 => v.X,
        1 => v.Y,
        _ => v.Z,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ClipSlab(float o, float d, float low, float high, ref float min, ref float max)
    {
        if (MathF.Abs(d) < 1e-12f)
        {
            return o >= low && o <= high && min <= max;
        }

        float t1 = (low - o) / d;
        float t2 = (high - o) / d;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        min = MathF.Max(min, t1);
        max = MathF.Min(max, t2);
        return min <= max;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Centroid((Vector3 Low, Vector3 High) box) => (box.Low + box.High) * 0.5f;

    private static uint[] ReadWords(ref CollisionDataReader reader)
    {
        int count = reader.ReadCount(4);
        uint[] words = new uint[count];
        for (int i = 0; i < count; i++)
        {
            words[i] = reader.ReadUInt32();
        }

        return words;
    }

    private static void WriteVector(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }
}

/// <summary>Tests one primitive against the ray; may shorten <paramref name="maxDistance"/>; returns whether it was hit.</summary>
public delegate bool RayPrimitiveCallback(int primitive, ref float maxDistance);

/// <summary>Struct callback contract for allocation-free <c>BihTree.IntersectRay</c> walks.</summary>
internal interface IRayPrimitiveCallback
{
    bool Invoke(int primitive, ref float maxDistance);
}

/// <summary>Struct callback contract for allocation-free <c>BihTree.IntersectPoint</c> walks.</summary>
internal interface IPointPrimitiveCallback
{
    void Invoke(int primitive);
}

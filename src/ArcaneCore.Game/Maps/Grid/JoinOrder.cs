using System.Buffers;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Maps.Grid;

/// <summary>
/// Lists kept in map-join order (<see cref="WorldObject.MapSequence"/>, unique per object of a map), so visibility can merge
/// already-ordered cells instead of collecting them and sorting the result for every mover.
/// </summary>
internal static class JoinOrder
{
    /// <summary>Insert <paramref name="item"/> at its join-order position (after any equal key, as an append would).</summary>
    public static void Insert<T>(List<T> list, T item)
        where T : WorldObject
    {
        long key = item.MapSequence;
        int count = list.Count;
        if (count == 0 || list[count - 1].MapSequence <= key)
        {
            list.Add(item); // the common case: a fresh join has the highest sequence of the map
            return;
        }

        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (list[mid].MapSequence <= key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        list.Insert(lo, item);
    }

    /// <summary>Remove <paramref name="item"/> (by reference) from a join-ordered list.</summary>
    public static bool Remove<T>(List<T> list, T item)
        where T : WorldObject
    {
        long key = item.MapSequence;
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (list[mid].MapSequence < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int i = lo; i < list.Count && list[i].MapSequence == key; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                list.RemoveAt(i);
                return true;
            }
        }

        // Defensive: a sequence that changed while listed (never expected) still removes the right entry.
        int index = list.FindIndex(o => ReferenceEquals(o, item));
        if (index < 0)
        {
            return false;
        }

        list.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Append the k-way merge of join-ordered <paramref name="sources"/> to <paramref name="results"/> (each object is in one
    /// source, so the output is ordered and distinct).
    /// </summary>
    public static void Merge<T>(List<List<T>> sources, List<T> results)
        where T : WorldObject
    {
        int k = sources.Count;
        if (k == 0)
        {
            return;
        }

        if (k == 1)
        {
            results.AddRange(sources[0]);
            return;
        }

        int total = 0;
        foreach (List<T> source in sources)
        {
            total += source.Count;
        }

        results.EnsureCapacity(results.Count + total);
        int[] heap = ArrayPool<int>.Shared.Rent(k);
        int[] position = ArrayPool<int>.Shared.Rent(k);
        try
        {
            int size = 0;
            for (int s = 0; s < k; s++)
            {
                position[s] = 0;
                if (sources[s].Count > 0)
                {
                    heap[size] = s;
                    SiftUp(sources, position, heap, size++);
                }
            }

            while (size > 0)
            {
                int s = heap[0];
                List<T> source = sources[s];
                results.Add(source[position[s]++]);
                if (position[s] == source.Count)
                {
                    heap[0] = heap[--size];
                }

                SiftDown(sources, position, heap, size);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(heap);
            ArrayPool<int>.Shared.Return(position);
        }
    }

    /// <summary>
    /// Merge the ordered, distinct <paramref name="extra"/> into the ordered, distinct <paramref name="list"/> in place
    /// (<paramref name="scratch"/> is working space), dropping objects present in both.
    /// </summary>
    public static void MergeDistinct<T>(List<T> list, List<T> extra, List<T> scratch)
        where T : WorldObject
    {
        if (extra.Count == 0)
        {
            return;
        }

        scratch.Clear();
        scratch.EnsureCapacity(list.Count + extra.Count);
        int a = 0, b = 0;
        while (a < list.Count && b < extra.Count)
        {
            T x = list[a], y = extra[b];
            if (ReferenceEquals(x, y))
            {
                scratch.Add(x);
                a++;
                b++;
            }
            else if (y.MapSequence < x.MapSequence)
            {
                scratch.Add(y);
                b++;
            }
            else
            {
                scratch.Add(x);
                a++;
            }
        }

        for (; a < list.Count; a++)
        {
            scratch.Add(list[a]);
        }

        for (; b < extra.Count; b++)
        {
            scratch.Add(extra[b]);
        }

        list.Clear();
        list.AddRange(scratch);
        scratch.Clear();
    }

    private static long Key<T>(List<List<T>> sources, int[] position, int s)
        where T : WorldObject
        => sources[s][position[s]].MapSequence;

    private static void SiftUp<T>(List<List<T>> sources, int[] position, int[] heap, int i)
        where T : WorldObject
    {
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (Key(sources, position, heap[parent]) <= Key(sources, position, heap[i]))
            {
                return;
            }

            (heap[parent], heap[i]) = (heap[i], heap[parent]);
            i = parent;
        }
    }

    private static void SiftDown<T>(List<List<T>> sources, int[] position, int[] heap, int size)
        where T : WorldObject
    {
        int i = 0;
        while (true)
        {
            int left = (2 * i) + 1;
            if (left >= size)
            {
                return;
            }

            int smallest = left;
            int right = left + 1;
            if (right < size && Key(sources, position, heap[right]) < Key(sources, position, heap[left]))
            {
                smallest = right;
            }

            if (Key(sources, position, heap[i]) <= Key(sources, position, heap[smallest]))
            {
                return;
            }

            (heap[smallest], heap[i]) = (heap[i], heap[smallest]);
            i = smallest;
        }
    }
}

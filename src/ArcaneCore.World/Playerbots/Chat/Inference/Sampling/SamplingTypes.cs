// Ported from Krilliac/Sonder-Inference src/sampling/include/sonder/sampling/types.hpp and src/sampling/src/types.cpp @1c4c152 (Apache-2.0)

namespace ArcaneCore.World.Playerbots.Chat.Inference.Sampling;

/// <summary>Token ids and the sentinels the sampling chain uses. Negative ids are never valid tokens.</summary>
internal static class SamplingTokens
{
    /// <summary>The id a result carries when no token was chosen.</summary>
    public const int Invalid = -1;
}

/// <summary>
/// One candidate token. <see cref="Logit"/> is the working (possibly transformed) logit; <see cref="P"/> is only meaningful after a stage
/// that normalises (see <see cref="SamplingMath.Softmax"/>).
/// </summary>
internal struct TokenData
{
    public int Id;
    public float Logit;
    public float P;

    public TokenData(int id, float logit, float p = 0f)
    {
        Id = id;
        Logit = logit;
        P = p;
    }
}

/// <summary>
/// Working set of candidates passed through the sampler chain. The backing array is reused between steps, so after warm-up a step does
/// not allocate unless the vocabulary grows.
/// </summary>
/// <remarks>
/// Invariants kept by the built-in stages: no candidate carries a NaN logit (NaN is mapped to -inf on load); when <see cref="Sorted"/>
/// is true the live span is in canonical order (logit descending, ties by id ascending), which makes every stage deterministic whatever
/// order the backend produced logits in. <see cref="IdOrdered"/> (an ArcaneCore addition) is true while candidate i still holds token i
/// for every i, which lets sparse stages (logit bias, penalties, constraints) index by token id instead of scanning the whole vocabulary.
/// </remarks>
internal sealed class Candidates
{
    private TokenData[] _data;

    public Candidates(int capacity = 0) => _data = capacity > 0 ? new TokenData[capacity] : [];

    /// <summary>Number of live candidates.</summary>
    public int Count { get; private set; }

    /// <summary>True when the live span is in canonical order.</summary>
    public bool Sorted { get; set; }

    /// <summary>True while <c>Span[i].Id == i</c> for every live candidate.</summary>
    public bool IdOrdered { get; set; }

    public bool IsEmpty => Count == 0;

    /// <summary>The live candidates. Valid until the next <see cref="Load"/>, <see cref="Add"/> or <see cref="Truncate"/>.</summary>
    public Span<TokenData> Span => _data.AsSpan(0, Count);

    public ref TokenData this[int index] => ref _data[index];

    /// <summary>Build a candidate set whose token i has logit <c>logits[i]</c> (NaN becomes -inf).</summary>
    public static Candidates FromLogits(ReadOnlySpan<float> logits)
    {
        var c = new Candidates(logits.Length);
        c.Load(logits);
        return c;
    }

    /// <summary>Replace the set with one candidate per logit (index = token id). NaN logits become -inf.</summary>
    public void Load(ReadOnlySpan<float> logits)
    {
        if (_data.Length < logits.Length)
        {
            _data = new TokenData[logits.Length];
        }

        Span<TokenData> span = _data.AsSpan(0, logits.Length);
        for (int i = 0; i < span.Length; i++)
        {
            float l = logits[i];
            span[i] = new TokenData(i, float.IsNaN(l) ? float.NegativeInfinity : l);
        }

        Count = logits.Length;
        Sorted = false;
        IdOrdered = true;
    }

    /// <summary>Append one candidate (tests and hand-built sets). Clears <see cref="IdOrdered"/> unless the id equals its position.</summary>
    public void Add(TokenData token)
    {
        if (Count == _data.Length)
        {
            Array.Resize(ref _data, Math.Max(4, _data.Length * 2));
        }

        if (Count == 0)
        {
            IdOrdered = true;
        }

        IdOrdered &= token.Id == Count;
        _data[Count++] = token;
        Sorted = false;
    }

    /// <summary>Keep the first <paramref name="count"/> candidates.</summary>
    public void Truncate(int count)
    {
        if ((uint)count > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        Count = count;
    }

    public void Clear()
    {
        Count = 0;
        Sorted = false;
        IdOrdered = false;
    }

    /// <summary>Deep copy (used when a chain is forked).</summary>
    public Candidates Clone()
    {
        var copy = new Candidates(_data.Length);
        Span.CopyTo(copy._data);
        copy.Count = Count;
        copy.Sorted = Sorted;
        copy.IdOrdered = IdOrdered;
        return copy;
    }
}

/// <summary>Canonical ordering, softmax and viability over a candidate set.</summary>
internal static class SamplingMath
{
    /// <summary>Canonical order: logit descending, then token id ascending. A total order (ids are unique), so unstable sorts are deterministic.</summary>
    public static readonly Comparison<TokenData> CanonicalComparison = static (a, b) =>
    {
        if (a.Logit != b.Logit)
        {
            return a.Logit > b.Logit ? -1 : 1;
        }

        return a.Id.CompareTo(b.Id);
    };

    /// <summary>True when <paramref name="a"/> comes before <paramref name="b"/> in canonical order.</summary>
    public static bool Precedes(in TokenData a, in TokenData b) => a.Logit != b.Logit ? a.Logit > b.Logit : a.Id < b.Id;

    /// <summary>Sort into canonical order. No-op when already sorted.</summary>
    public static void SortCanonical(Candidates c)
    {
        if (c.Sorted)
        {
            return;
        }

        c.Span.Sort(CanonicalComparison);
        c.Sorted = true;
        c.IdOrdered = false;
    }

    /// <summary>
    /// Move the <paramref name="k"/> canonically-first candidates to the front of <paramref name="span"/>, sorted (std::partial_sort):
    /// O(n log k) with an in-place heap whose root is the worst of the kept set. The tail is left in unspecified order.
    /// </summary>
    public static void PartialSortCanonical(Span<TokenData> span, int k)
    {
        if (k <= 0)
        {
            return;
        }

        if (k >= span.Length)
        {
            span.Sort(CanonicalComparison);
            return;
        }

        for (int i = (k / 2) - 1; i >= 0; i--)
        {
            SiftDownWorstRoot(span, i, k);
        }

        for (int i = k; i < span.Length; i++)
        {
            if (Precedes(span[i], span[0]))
            {
                (span[0], span[i]) = (span[i], span[0]);
                SiftDownWorstRoot(span, 0, k);
            }
        }

        span[..k].Sort(CanonicalComparison);
    }

    private static void SiftDownWorstRoot(Span<TokenData> heap, int i, int n)
    {
        while (true)
        {
            int left = (2 * i) + 1;
            if (left >= n)
            {
                return;
            }

            int worse = left;
            if (left + 1 < n && Precedes(heap[left], heap[left + 1]))
            {
                worse = left + 1;
            }

            if (!Precedes(heap[i], heap[worse]))
            {
                return;
            }

            (heap[i], heap[worse]) = (heap[worse], heap[i]);
            i = worse;
        }
    }

    /// <summary>
    /// Compute <see cref="TokenData.P"/> for every candidate with a numerically stable softmax. -inf logits get p = 0. If any logit is
    /// +inf, the +inf candidates share the mass uniformly. If every logit is -inf (or the set is empty), every p is 0 and the result is
    /// false (no viable candidate); otherwise true.
    /// </summary>
    public static bool Softmax(Candidates c)
    {
        Span<TokenData> span = c.Span;
        if (span.IsEmpty)
        {
            return false;
        }

        float max = float.NegativeInfinity;
        for (int i = 0; i < span.Length; i++)
        {
            max = Math.Max(max, span[i].Logit);
        }

        if (float.IsNegativeInfinity(max))
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i].P = 0f;
            }

            return false;
        }

        if (float.IsPositiveInfinity(max))
        {
            int infinite = 0;
            for (int i = 0; i < span.Length; i++)
            {
                infinite += span[i].Logit == max ? 1 : 0;
            }

            float share = 1f / infinite;
            for (int i = 0; i < span.Length; i++)
            {
                span[i].P = span[i].Logit == max ? share : 0f;
            }

            return true;
        }

        // Same float/double mix as the source (p stored as float, sum accumulated from the double exponentials), so a seed reproduces
        // the source's golden sequences.
        double sum = 0.0;
        for (int i = 0; i < span.Length; i++)
        {
            double e = Math.Exp((double)span[i].Logit - max);
            span[i].P = (float)e;
            sum += e;
        }

        for (int i = 0; i < span.Length; i++)
        {
            span[i].P = (float)(span[i].P / sum);
        }

        return true;
    }

    /// <summary>Number of candidates whose logit is not -inf (p could be &gt; 0).</summary>
    public static int ViableCount(Candidates c)
    {
        int n = 0;
        foreach (ref readonly TokenData t in c.Span)
        {
            n += float.IsNegativeInfinity(t.Logit) ? 0 : 1;
        }

        return n;
    }
}

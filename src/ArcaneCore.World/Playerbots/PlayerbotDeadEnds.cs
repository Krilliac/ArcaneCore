using System.Runtime.CompilerServices;
using ArcaneCore.Game.Maps.Collision.MMaps;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Which walks a map's region graph (<see cref="NavRegionGraph"/>) cannot make, as bots proved them: a bot that stood at the walkable point
/// nearest a goal whose region is not joined to its own and found no route closer (<see cref="PlayerbotNavigation.TryPlanToward"/>) records
/// the pair (the bot's region, the goal's region). Every later walk between the pair is refused at once, so no bot walks to the same dead
/// end again (bot QA cluster C3: the same point to 0.1 yard for 205 bots).
/// <para>
/// One memory per graph instance (a rebuilt graph renumbers its regions, so its memory starts empty); bounded to
/// <see cref="MaxPairs"/> pairs (the earliest expiry goes first) and each pair is forgotten after <see cref="ExpireMs"/>. Thread-safe:
/// one lock, held for a dictionary lookup. The lookup allocates nothing.
/// </para>
/// </summary>
internal sealed class PlayerbotDeadEnds
{
    /// <summary>How long a proven pair is remembered (ms). The graph is static, so this only bounds a stale renumbering.</summary>
    internal const long ExpireMs = 30 * 60 * 1000;

    /// <summary>The most pairs one graph remembers.</summary>
    internal const int MaxPairs = 256;

    private static readonly ConditionalWeakTable<NavRegionGraph, PlayerbotDeadEnds> Memories = new();

    private readonly object _gate = new();
    private readonly Dictionary<long, long> _until = [];

    /// <summary>The memory of <paramref name="graph"/>.</summary>
    internal static PlayerbotDeadEnds For(NavRegionGraph graph) => Memories.GetValue(graph, static _ => new PlayerbotDeadEnds());

    private static long Key(int from, int to) => ((long)(uint)from << 32) | (uint)to;

    /// <summary>Whether a walk from region <paramref name="from"/> to region <paramref name="to"/> was proven impossible and the proof has not expired.</summary>
    internal bool Known(int from, int to, long nowMs)
    {
        lock (_gate)
        {
            if (!_until.TryGetValue(Key(from, to), out long until)) return false;
            if (until - nowMs > 0) return true;
            _until.Remove(Key(from, to));
            return false;
        }
    }

    /// <summary>Remember that no walk leads from region <paramref name="from"/> to region <paramref name="to"/>.</summary>
    internal void Record(int from, int to, long nowMs)
    {
        lock (_gate)
        {
            if (_until.Count >= MaxPairs && !_until.ContainsKey(Key(from, to)))
            {
                long oldest = 0;
                bool any = false;
                foreach ((long key, long until) in _until)
                    if (!any || until < _until[oldest]) { oldest = key; any = true; }
                if (any) _until.Remove(oldest);
            }

            _until[Key(from, to)] = nowMs + ExpireMs;
        }
    }

    /// <summary>Pairs remembered (tests).</summary>
    internal int Count
    {
        get { lock (_gate) return _until.Count; }
    }
}

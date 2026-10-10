using System.Collections.Concurrent;

namespace ArcaneCore.World.Packets;

/// <summary>
/// Built query replies (SMSG_CREATURE_QUERY_RESPONSE, SMSG_GAMEOBJECT_QUERY_RESPONSE, SMSG_ITEM_QUERY_SINGLE_RESPONSE), one per
/// template entry: TrinityCore builds each template's reply once (CreatureTemplate::InitializeQueryData,
/// GameObjectTemplate::InitializeQueryData, ItemTemplate query data; ObjectMgr::InitializeQueriesData, QueryPackets.cpp) and
/// rebuilds it on reload. Here a reply is built on the first query and kept with the template object it was built from; the
/// templates are immutable records and every reload installs new ones, so a reply whose template is no longer the current
/// one is rebuilt (and the reload paths also <see cref="Clear"/>). Replies for unknown entries are not kept, so a client
/// asking for random entries cannot grow the cache past the content's template count. 1.12 replies carry no locale.
/// Thread-safe: session tasks query concurrently; a race builds the same bytes twice.
/// </summary>
public sealed class QueryResponseCache<TTemplate>
    where TTemplate : class
{
    private sealed record Entry(TTemplate Template, byte[] Reply);

    private readonly ConcurrentDictionary<uint, Entry> _entries = new();
    private long _hits;
    private long _builds;

    /// <summary>Replies currently kept.</summary>
    public int Count => _entries.Count;

    /// <summary>Queries answered from the cache.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Replies built (cache misses, unknown entries included).</summary>
    public long Builds => Interlocked.Read(ref _builds);

    /// <summary>
    /// The reply for <paramref name="entry"/>, whose current template is <paramref name="template"/> (null: unknown). The
    /// returned array is shared: senders copy it into their frame and must not modify it.
    /// </summary>
    public byte[] Get(uint entry, TTemplate? template, Func<uint, TTemplate?, byte[]> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (template is null)
        {
            Interlocked.Increment(ref _builds);
            return build(entry, null);
        }

        if (_entries.TryGetValue(entry, out Entry? cached) && ReferenceEquals(cached.Template, template))
        {
            Interlocked.Increment(ref _hits);
            return cached.Reply;
        }

        Interlocked.Increment(ref _builds);
        byte[] reply = build(entry, template);
        _entries[entry] = new Entry(template, reply);
        return reply;
    }

    /// <summary>Drop every kept reply (content reload).</summary>
    public void Clear() => _entries.Clear();
}

namespace ArcaneCore.Kernel.WorldData.Transports;

/// <summary>One TransportAnimation.dbc row: the offset of an elevator or tram at <paramref name="TimeSeg"/> ms into its cycle.</summary>
public readonly record struct TransportAnimationNode(uint TimeSeg, float X, float Y, float Z);

/// <summary>
/// The animations of the type 11 game objects (elevators and trams) by <c>gameobject_template</c> entry, from TransportAnimation.dbc
/// (vmangos TransportMgr::LoadTransportAnimationAndRotation / AddPathNodeToTransport, Transports/TransportMgr.cpp:32-37, 334-341): the
/// nodes in time order and the cycle length, which is the largest TimeSeg of the entry. Immutable.
/// </summary>
public sealed class TransportAnimationCatalog
{
    private readonly Dictionary<uint, (uint TotalTime, TransportAnimationNode[] Nodes)> _animations;

    public static TransportAnimationCatalog Empty { get; } = new([]);

    /// <param name="rows">(transport entry, node) rows; a later row with the same entry and TimeSeg replaces an earlier one, as the map does.</param>
    public TransportAnimationCatalog(IEnumerable<(uint TransportEntry, TransportAnimationNode Node)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _animations = rows
            .GroupBy(r => r.TransportEntry)
            .ToDictionary(g => g.Key, g =>
            {
                TransportAnimationNode[] nodes = [.. g.GroupBy(r => r.Node.TimeSeg).Select(t => t.Last().Node).OrderBy(n => n.TimeSeg)];
                return (nodes.Length == 0 ? 0u : nodes[^1].TimeSeg, nodes);
            });
    }

    /// <summary>Entries with an animation.</summary>
    public int Count => _animations.Count;

    /// <summary>The cycle length of <paramref name="entry"/> in ms (vmangos TransportAnimation::TotalTime), 0 without an animation.</summary>
    public uint TotalTime(uint entry) => _animations.TryGetValue(entry, out var animation) ? animation.TotalTime : 0;

    /// <summary>The nodes of <paramref name="entry"/> in time order (empty without an animation).</summary>
    public IReadOnlyList<TransportAnimationNode> Nodes(uint entry) => _animations.TryGetValue(entry, out var animation) ? animation.Nodes : [];

    /// <summary>
    /// The offset at <paramref name="pathProgress"/> ms (vmangos ElevatorTransport::Update, Transport.cpp:396-430): linear between the last node
    /// before it and the first node at or after it (std::map lower_bound), the earlier node's position when both are the same, null when either
    /// is missing (the first node's time itself has no earlier node).
    /// </summary>
    public (float X, float Y, float Z)? Offset(uint entry, uint pathProgress)
    {
        if (!_animations.TryGetValue(entry, out var animation))
        {
            return null;
        }

        TransportAnimationNode[] nodes = animation.Nodes;
        int next = Array.FindIndex(nodes, n => n.TimeSeg >= pathProgress);
        if (next <= 0)
        {
            return null;
        }

        TransportAnimationNode prev = nodes[next - 1];
        TransportAnimationNode after = nodes[next];
        if (prev.X == after.X && prev.Y == after.Y && prev.Z == after.Z)
        {
            return (prev.X, prev.Y, prev.Z);
        }

        float elapsed = pathProgress - prev.TimeSeg;
        float span = after.TimeSeg - prev.TimeSeg;
        return (prev.X + (elapsed * (after.X - prev.X) / span), prev.Y + (elapsed * (after.Y - prev.Y) / span), prev.Z + (elapsed * (after.Z - prev.Z) / span));
    }
}

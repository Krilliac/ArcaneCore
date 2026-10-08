namespace ArcaneCore.Game.Transports;

/// <summary>
/// Ships and zeppelins, bound from the <c>World:Transports</c> configuration section (restart-only).
/// </summary>
public sealed class TransportOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "World:Transports";

    /// <summary>
    /// Master switch. Set it to true once the content is present: the gameobject_template type 15 rows and the transports
    /// periods (tools/content/refresh-world-content.ps1 writes both) and a build-5875 TaxiPathNode.dbc through
    /// <c>NpcServices:TaxiPathNodeDbcPath</c>; the world then logs "Transports: 9 routes, 9 ships sailing". Off (the default),
    /// no ship is built or spawned and a client that claims to stand on a transport is treated as standing on nothing.
    /// vmangos always runs its ships (World.cpp:1451 LoadTransportTemplates).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Only these <c>gameobject_template</c> entries become ships; empty (the default) means every type 15 row with a
    /// usable path. A switch for bringing routes up one at a time.
    /// </summary>
    public List<uint> Entries { get; set; } = [];
}

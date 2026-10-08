namespace ArcaneCore.Game.Transports;

/// <summary>
/// Ships and zeppelins, bound from the <c>World:Transports</c> configuration section (restart-only).
/// </summary>
public sealed class TransportOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "World:Transports";

    /// <summary>
    /// Master switch. Off (the default until the content is present: gameobject_template type 15 rows and a build-5875
    /// TaxiPathNode.dbc through <c>NpcServices:TaxiPathNodeDbcPath</c>), no ship is built or spawned and a client that
    /// claims to stand on a transport is treated as standing on nothing, exactly as before this feature existed. vmangos
    /// always runs its ships (World.cpp:1451 LoadTransportTemplates).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Only these <c>gameobject_template</c> entries become ships; empty (the default) means every type 15 row with a
    /// usable path. A switch for bringing routes up one at a time.
    /// </summary>
    public List<uint> Entries { get; set; } = [];
}

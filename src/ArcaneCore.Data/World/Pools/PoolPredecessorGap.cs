using ArcaneCore.Data.Schema;

namespace ArcaneCore.Data.World.Pools;

/// <summary>
/// World v45 belongs to the wave-10 movement-scripts lane. This placeholder holds it so the pools branch (v46) composes on its own; it yields
/// when the real module is merged (delete this file then). Production bootstrap and upgrade refuse to cross it until then
/// (<see cref="ReservedSchemaGaps"/>).
/// </summary>
public sealed class PoolPredecessorGap : ReservedSchemaGap
{
    public override DatabaseComponent Component => DatabaseComponent.World;

    public override int SchemaVersion => 45;
}

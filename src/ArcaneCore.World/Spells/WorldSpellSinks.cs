using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Teleport;

namespace ArcaneCore.World.Spells;

/// <summary>Resolve players and creatures through the map's shared object registry.</summary>
internal sealed class WorldSpellUnitResolver : ISpellUnitResolver
{
    public Unit? Find(Unit reference, ObjectGuid guid)
        => guid.IsEmpty ? null : reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
}

/// <summary>World-thread spell effects use map combat for damage, death and threat (<see cref="MapCombatDamageSink"/>).</summary>
internal sealed class WorldSpellDamageSink : MapCombatDamageSink;

/// <summary>Players use the shared teleport state machine; non-players only move within their map.</summary>
internal sealed class WorldSpellTeleportSink(Func<TeleportService> teleports) : ITeleportSink
{
    private readonly NearTeleportSink _near = new();

    public bool CanTeleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        => unit is Player player
            ? teleports().CanTeleportTo(player, mapId, x, y, z, orientation)
            : _near.CanTeleport(unit, mapId, x, y, z, orientation);

    public bool Teleport(Unit unit, uint mapId, float x, float y, float z, float orientation)
        => unit is Player player
            ? teleports().TeleportTo(player, mapId, x, y, z, orientation)
            : _near.Teleport(unit, mapId, x, y, z, orientation);
}

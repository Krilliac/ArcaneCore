namespace ArcaneCore.Game;

/// <summary>
/// An in-world player's renderable state. Values feed the create object-update sent on
/// world entry. Everything here is sourced from the character row and the DB-driven world
/// data (start position, display id, faction, base stats), so it is fully tunable without
/// client extraction.
/// </summary>
public sealed class PlayerObject
{
    public required uint Guid { get; init; }
    public required Race Race { get; init; }
    public required Class Class { get; init; }
    public required Gender Gender { get; init; }
    public required PowerType PowerType { get; init; }

    public byte Skin { get; init; }
    public byte Face { get; init; }
    public byte HairStyle { get; init; }
    public byte HairColor { get; init; }
    public byte FacialHair { get; init; }

    public byte Level { get; init; } = 1;
    public uint FactionTemplate { get; init; }
    public uint DisplayId { get; init; }

    public uint Health { get; init; }
    public uint MaxHealth { get; init; }
    public uint Power { get; init; }
    public uint MaxPower { get; init; }

    public uint MapId { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
    public float Orientation { get; init; }

    public float Scale { get; init; } = 1.0f;
    public float BoundingRadius { get; init; } = 0.382f;
    public float CombatReach { get; init; } = 1.5f;
    public uint Coinage { get; init; }

    public ObjectGuid ObjectGuid => Game.ObjectGuid.Player(Guid);
}

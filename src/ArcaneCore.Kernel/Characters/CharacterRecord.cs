namespace ArcaneCore.Kernel.Characters;

/// <summary>A persisted player character.</summary>
public sealed class CharacterRecord
{
    public int Id { get; set; }

    /// <summary>Owning account id (links to the auth account).</summary>
    public int AccountId { get; set; }

    public required string Name { get; set; }

    public byte Race { get; set; }
    public byte Class { get; set; }
    public byte Gender { get; set; }

    public byte Skin { get; set; }
    public byte Face { get; set; }
    public byte HairStyle { get; set; }
    public byte HairColor { get; set; }
    public byte FacialHair { get; set; }

    public byte Level { get; set; } = 1;

    public uint MapId { get; set; }
    public uint ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Orientation { get; set; }

    /// <summary>Total played seconds; 0 means the character has never logged in (first-login flag).</summary>
    public uint PlayedTime { get; set; }
}

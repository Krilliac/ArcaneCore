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

    /// <summary>Played seconds at the current level (characters schema v2).</summary>
    public uint LevelPlayedTime { get; set; }

    /// <summary>Copper (PLAYER_FIELD_COINAGE).</summary>
    public uint Money { get; set; }

    /// <summary>Which optional action bars are shown (PLAYER_FIELD_BYTES byte 2).</summary>
    public byte ActionBarToggles { get; set; }

    /// <summary>Hearthstone bind point (SMSG_BINDPOINTUPDATE). Defaults to the start position.</summary>
    public uint HomeMapId { get; set; }

    public uint HomeZoneId { get; set; }

    public float HomeX { get; set; }

    public float HomeY { get; set; }

    public float HomeZ { get; set; }
}

/// <summary>One action-bar slot: packed as action | type &lt;&lt; 24 on the wire (vmangos ACTION_BUTTON_*).</summary>
public sealed record ActionButton(byte Button, uint Action, byte Type);

/// <summary>The identity fields of a character, for name queries and /who of offline players.</summary>
public sealed record CharacterIdentity(int Id, int AccountId, string Name, byte Race, byte Gender, byte Class);

using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Game.Entities;

/// <summary>
/// An in-world player character. Owned by the world thread once added to a map; the network
/// side talks to it only through <see cref="Session"/>.
/// </summary>
public sealed class Player : Unit
{
    /// <summary>vmangos ObjectDefines.h DEFAULT_WORLD_OBJECT_SIZE (used until model data is imported).</summary>
    public const float DefaultBoundingRadius = 0.388999998569489f;

    /// <summary>vmangos ObjectDefines.h DEFAULT_COMBAT_REACH.</summary>
    public const float DefaultCombatReach = 1.5f;

    private readonly uint _playedTimeAtLogin;
    private uint _loginTimeMs;

    public Player(CharacterRecord character, PlayerAppearance appearance, IPlayerSession session)
        : base(ObjectGuid.Player((uint)character.Id), Game.TypeId.Player, TypeMask.PlayerObject, UpdateFields.PlayerEnd)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(session);

        Session = session;
        AccountId = character.AccountId;
        Name = character.Name;
        ZoneId = character.ZoneId;
        MapId = character.MapId;
        X = character.X;
        Y = character.Y;
        Z = character.Z;
        Orientation = character.Orientation;
        _playedTimeAtLogin = character.PlayedTime;

        InitializeFields(character, appearance);
    }

    public IPlayerSession Session { get; }

    public int AccountId { get; }

    public string Name { get; }

    public uint ZoneId { get; set; }

    /// <summary>GUIDs of the objects this player's client currently has (vmangos m_visibleGUIDs).</summary>
    public HashSet<ObjectGuid> VisibleObjects { get; } = [];

    /// <summary>Update blocks queued for this player's client, flushed at the end of each map tick.</summary>
    internal UpdateData PendingUpdates { get; } = new();

    /// <summary>Set when the player moved and its visibility must be recomputed this tick.</summary>
    internal bool NeedsVisibilityUpdate { get; set; }

    /// <summary>
    /// Accept a movement update from this player's client (world thread). A changed position
    /// schedules a visibility pass for the end of the tick.
    /// </summary>
    public void ApplyClientMovement(in Protocol.MovementInfo movement, uint serverTimeMs)
    {
        bool moved = movement.X != X || movement.Y != Y || movement.Z != Z;
        ApplyMovement(movement, serverTimeMs);
        if (moved)
        {
            NeedsVisibilityUpdate = true;
        }
    }

    /// <summary>Total played seconds at the given world time.</summary>
    public uint PlayedTimeAt(uint nowMs) => _playedTimeAtLogin + ((nowMs - _loginTimeMs) / 1000);

    /// <summary>Start counting played time from this world time (called when the player enters the world).</summary>
    internal void StartPlayedTime(uint nowMs) => _loginTimeMs = nowMs;

    /// <summary>The persistent state to save (world thread).</summary>
    public CharacterState CreateSnapshot(uint nowMs) => new(
        (int)Guid.Low, MapId, ZoneId, X, Y, Z, Orientation, Level, Math.Max(PlayedTimeAt(nowMs), 1u));

    private void InitializeFields(CharacterRecord c, PlayerAppearance appearance)
    {
        // Field set and byte layout follow vmangos Player::Create for build 1.12.1.
        SetByte(UpdateFields.UnitFieldBytes0, 0, c.Race);
        SetByte(UpdateFields.UnitFieldBytes0, 1, c.Class);
        SetByte(UpdateFields.UnitFieldBytes0, 2, c.Gender);
        SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)appearance.PowerType);

        DisplayId = appearance.DisplayId;
        NativeDisplayId = appearance.DisplayId;
        FactionTemplate = appearance.FactionTemplate;
        Level = c.Level;

        // UNIT_FIELD_BYTES_1 byte 1 (pet loyalty) = 0xEE for rage/mana users (vmangos comment:
        // "only in pre-2.x used").
        if (appearance.PowerType is PowerType.Rage or PowerType.Mana)
        {
            SetByte(UpdateFields.UnitFieldBytes1, 1, 0xEE);
        }

        // UNIT_FIELD_BYTES_2 byte 1 = UNIT_BYTE2_FLAG_UNK3 | UNIT_BYTE2_FLAG_UNK5 | UNIT_BYTE2_FLAG_PVP.
        SetByte(UpdateFields.UnitFieldBytes2, 1, 0x08 | 0x20 | 0x01);
        UnitFlags = UnitFlags.PlayerControlled;
        SetFloat(UpdateFields.UnitModCastSpeed, 1.0f);
        SetInt32(UpdateFields.PlayerFieldWatchedFactionIndex, -1);

        SetByte(UpdateFields.PlayerBytes, 0, c.Skin);
        SetByte(UpdateFields.PlayerBytes, 1, c.Face);
        SetByte(UpdateFields.PlayerBytes, 2, c.HairStyle);
        SetByte(UpdateFields.PlayerBytes, 3, c.HairColor);
        SetByte(UpdateFields.PlayerBytes2, 0, c.FacialHair);
        SetByte(UpdateFields.PlayerBytes2, 1, 0xEE);
        SetByte(UpdateFields.PlayerBytes2, 3, 0x02); // REST_STATE_NORMAL
        SetUInt16(UpdateFields.PlayerBytes3, 0, c.Gender);

        SetFloat(UpdateFields.UnitFieldBoundingradius, DefaultBoundingRadius);
        SetFloat(UpdateFields.UnitFieldCombatreach, DefaultCombatReach);
        SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
        SetUInt32(UpdateFields.UnitFieldBaseattacktime + 1, 2000);

        MaxHealth = appearance.MaxHealth;
        Health = appearance.MaxHealth;
        SetUInt32(UpdateFields.UnitFieldBaseHealth, appearance.BaseHealth);
        SetUInt32(UpdateFields.UnitFieldBaseMana, appearance.BaseMana);

        // UNIT_FIELD_POWER1 + power type (vmangos Unit::SetPower / SetMaxPower).
        int powerIndex = (int)appearance.PowerType;
        SetUInt32(UpdateFields.UnitFieldMaxpower1 + powerIndex, appearance.MaxPower);
        SetUInt32(UpdateFields.UnitFieldPower1 + powerIndex, appearance.StartPower);

        SetUInt32(UpdateFields.PlayerNextLevelXp, appearance.NextLevelXp);
    }
}

/// <summary>
/// The race/class-derived values a player is created with. Sourced from the world data
/// (race display/faction, class power type and base stats).
/// </summary>
public sealed record PlayerAppearance(
    uint DisplayId,
    uint FactionTemplate,
    PowerType PowerType,
    uint BaseHealth,
    uint BaseMana,
    uint MaxHealth,
    uint MaxPower,
    uint StartPower,
    uint NextLevelXp);

using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Entities;

/// <summary>PLAYER_FLAGS bits (vmangos Player.h PlayerFlags).</summary>
[Flags]
public enum PlayerFlags : uint
{
    None = 0x0000,
    GroupLeader = 0x0001,
    Afk = 0x0002,
    Dnd = 0x0004,
    Gm = 0x0008,
    Ghost = 0x0010,
    Resting = 0x0020,
    FfaPvp = 0x0080,
    ContestedPvp = 0x0100,
    PvpDesired = 0x0200,
    HideHelm = 0x0400,
    HideCloak = 0x0800,
}

/// <summary>The two factions.</summary>
public enum Team
{
    Alliance,
    Horde,
}

/// <summary>
/// An in-world player character. Owned by the world thread once added to a map; the network
/// side talks to it only through <see cref="Session"/>.
/// </summary>
public sealed partial class Player : Unit
{
    /// <summary>vmangos ObjectDefines.h DEFAULT_WORLD_OBJECT_SIZE (used until model data is imported).</summary>
    public const float DefaultBoundingRadius = 0.388999998569489f;

    /// <summary>vmangos ObjectDefines.h DEFAULT_COMBAT_REACH.</summary>
    public const float DefaultCombatReach = 1.5f;

    /// <summary>Action-bar slots in 1.12 (vmangos MAX_ACTION_BUTTONS; SMSG_ACTION_BUTTONS carries 120 × u32).</summary>
    public const int ActionButtonCount = 120;

    /// <summary>Delay before a requested logout completes (vmangos WorldSession::ShouldLogOut: 20 s).</summary>
    public const uint DefaultLogoutDelayMs = 20_000;

    /// <summary>Faction template a GM takes in GM mode (vmangos Player::SetGameMaster: 35, friendly to all).</summary>
    public const uint GameMasterFactionTemplate = 35;

    /// <summary>PLAYER_FIELD_BYTES byte 0 flag shown while logging out (vmangos PLAYER_FIELD_BYTE_LOGGING_OUT).</summary>
    private const byte FieldByteLoggingOut = 0x04;

    private readonly uint[] _actionButtons = new uint[ActionButtonCount];
    private readonly uint _playedTimeAtLogin;
    private uint _levelPlayedTimeAtLogin;
    private readonly uint _raceFactionTemplate;
    private uint _loginTimeMs;
    private uint _levelStartMs;
    private uint? _logoutRequestedAtMs;
    private uint _movementCounter;
    private bool _actionButtonsChanged;
    private ObjectGuid _selection;

    public Player(CharacterRecord character, PlayerAppearance appearance, IPlayerSession session, IReadOnlyList<ActionButton>? actionButtons = null)
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
        Home = new HomeBind(character.HomeMapId, character.HomeZoneId, character.HomeX, character.HomeY, character.HomeZ);
        _playedTimeAtLogin = character.PlayedTime;
        _levelPlayedTimeAtLogin = character.LevelPlayedTime;
        _raceFactionTemplate = appearance.FactionTemplate;

        InitializeFields(character, appearance);
        Inventory = new Items.PlayerInventory(this);

        foreach (ActionButton button in actionButtons ?? [])
        {
            if (button.Button < ActionButtonCount)
            {
                _actionButtons[button.Button] = Pack(button.Action, button.Type);
            }
        }
    }

    public IPlayerSession Session { get; }

    /// <summary>Equipment, bags, bank and keyring (items area, <see cref="Items.PlayerInventory"/>).</summary>
    public Items.PlayerInventory Inventory { get; }

    /// <summary>Weapon damage entries and combat abilities that have no update field (docs/areas/stats.md).</summary>
    public global::ArcaneCore.Game.Stats.PlayerStatState StatState => field ??= new global::ArcaneCore.Game.Stats.PlayerStatState(this);

    public int AccountId { get; }

    public string Name { get; }

    public AccountSecurity Security => Session.Security;

    public uint ZoneId { get; set; }

    public HomeBind Home { get; set; }

    /// <summary>GUIDs of the objects this player's client currently has (vmangos m_visibleGUIDs).</summary>
    public HashSet<ObjectGuid> VisibleObjects { get; } = [];

    /// <summary>
    /// The unit this client has selected (CMSG_SET_SELECTION). As in vmangos
    /// Player::SetSelectionGuid, it is also published as UNIT_FIELD_TARGET.
    /// </summary>
    public ObjectGuid Selection
    {
        get => _selection;
        set
        {
            _selection = value;
            Target = value;
        }
    }

    public Team Team => Race is Race.Human or Race.Dwarf or Race.NightElf or Race.Gnome ? Team.Alliance : Team.Horde;

    public PlayerFlags Flags
    {
        get => (PlayerFlags)GetUInt32(UpdateFields.PlayerFlags);
        set => SetUInt32(UpdateFields.PlayerFlags, (uint)value);
    }

    /// <summary>GM mode (vmangos PLAYER_EXTRA_GM_ON, mirrored in PLAYER_FLAGS_GM for the client).</summary>
    public bool IsGameMaster => (Flags & PlayerFlags.Gm) != 0;

    /// <summary>The staff badge on chat (vmangos PLAYER_EXTRA_GM_CHAT); only shown for moderators and up.</summary>
    public bool GmChat { get; set; }

    public bool IsAfk => (Flags & PlayerFlags.Afk) != 0;

    public bool IsDnd => (Flags & PlayerFlags.Dnd) != 0;

    /// <summary>Auto-reply sent to whisperers while AFK (vmangos MasterPlayer::afkMsg).</summary>
    public string AfkMessage { get; set; } = string.Empty;

    /// <summary>Auto-reply sent to whisperers while DND (vmangos MasterPlayer::dndMsg).</summary>
    public string DndMessage { get; set; } = string.Empty;

    /// <summary>Whether the server has rooted this player (SMSG_FORCE_MOVE_ROOT sent, not yet unrooted).</summary>
    public bool IsRooted { get; private set; }

    /// <summary>World time of the last player-requested save (.save), for rate limiting.</summary>
    public uint? LastSaveRequestMs { get; set; }

    /// <summary>Copper (PLAYER_FIELD_COINAGE).</summary>
    public uint Money
    {
        get => GetUInt32(UpdateFields.PlayerFieldCoinage);
        set
        {
            EnsureQuestSettlementMutationAllowed();
            SetUInt32(UpdateFields.PlayerFieldCoinage, value);
        }
    }

    /// <summary>Visible optional action bars (vmangos PLAYER_FIELD_BYTES_OFFSET_ACTION_BARS = byte 2).</summary>
    public byte ActionBarToggles
    {
        get => GetByte(UpdateFields.PlayerFieldBytes, 2);
        set => SetByte(UpdateFields.PlayerFieldBytes, 2, value);
    }

    /// <summary>
    /// The tag shown with this player's chat. vmangos Player::GetChatTag checks, in order, the
    /// GM chat badge (IsGMChat: security ≥ moderator and the badge on), DND, then AFK.
    /// </summary>
    public ChatTag ChatTag => GmChat && Security >= AccountSecurity.Moderator ? ChatTag.Gm
        : IsDnd ? ChatTag.Dnd
        : IsAfk ? ChatTag.Afk
        : ChatTag.None;

    public bool IsLoggingOut => _logoutRequestedAtMs is not null;

    public ReadOnlySpan<uint> ActionButtons => _actionButtons;

    /// <summary>Update blocks queued for this player's client, flushed at the end of each map tick.</summary>
    internal UpdateData PendingUpdates { get; } = new();

    /// <summary>Set when the player moved and its visibility must be recomputed this tick.</summary>
    internal bool NeedsVisibilityUpdate { get; set; }

    /// <summary>
    /// Whether the player may speak a language. Until skills exist (M12) this is the race's
    /// starting languages, verified against classic-db playercreateinfo_spell (language spells
    /// 668/669/670/671/672/7340/7341/17737, vmangos lang_description); GMs speak every language.
    /// </summary>
    public bool KnowsLanguage(Language language)
    {
        if (language is Language.Universal || IsGameMaster)
        {
            return true;
        }

        return Race switch
        {
            Race.Human => language is Language.Common,
            Race.Orc => language is Language.Orcish,
            Race.Dwarf => language is Language.Common or Language.Dwarvish,
            Race.NightElf => language is Language.Common or Language.Darnassian,
            Race.Undead => language is Language.Orcish or Language.Gutterspeak,
            Race.Tauren => language is Language.Orcish or Language.Taurahe,
            Race.Gnome => language is Language.Common or Language.Gnomish,
            Race.Troll => language is Language.Orcish or Language.Troll,
            _ => false,
        };
    }

    /// <summary>
    /// Set or clear an action-bar slot from CMSG_SET_ACTIONBUTTON's packed value
    /// (action = low 24 bits, type = high 8; vmangos HandleSetActionButtonOpcode). Returns false
    /// for an out-of-range slot or an unknown type.
    /// </summary>
    public bool SetActionButton(byte button, uint packed)
    {
        if (button >= ActionButtonCount)
        {
            return false;
        }

        byte type = (byte)(packed >> 24);
        if (packed != 0 && type is not (ActionButtonTypes.Spell or ActionButtonTypes.Macro or ActionButtonTypes.CMacro or ActionButtonTypes.Item))
        {
            return false;
        }

        if (_actionButtons[button] != packed)
        {
            _actionButtons[button] = packed;
            _actionButtonsChanged = true;
        }

        return true;
    }

    /// <summary>
    /// GM mode on/off (vmangos Player::SetGameMaster): PLAYER_FLAGS_GM and faction template 35
    /// while on; the race's faction template is restored when off.
    /// </summary>
    public void SetGameMaster(bool on)
    {
        if (on)
        {
            Flags |= PlayerFlags.Gm;
            FactionTemplate = GameMasterFactionTemplate;
        }
        else
        {
            Flags &= ~PlayerFlags.Gm;
            FactionTemplate = _raceFactionTemplate;
        }
    }

    /// <summary>Flip PLAYER_FLAGS_AFK; returns the new state (vmangos Player::ToggleAFK).</summary>
    public bool ToggleAfk()
    {
        Flags ^= PlayerFlags.Afk;
        return IsAfk;
    }

    /// <summary>Flip PLAYER_FLAGS_DND; returns the new state (vmangos Player::ToggleDND).</summary>
    public bool ToggleDnd()
    {
        Flags ^= PlayerFlags.Dnd;
        return IsDnd;
    }

    /// <summary>
    /// Change the stand state; the client is told with SMSG_STANDSTATE_UPDATE (u8 state) as in
    /// vmangos Unit::SetStandState for players. Observers see the UNIT_FIELD_BYTES_1 change.
    /// </summary>
    public void SetStandState(StandState state)
    {
        if (StandState == state)
        {
            return;
        }

        StandState = state;
        Session.Send(WorldOpcode.SmsgStandstateUpdate, [(byte)state]);
    }

    /// <summary>
    /// Root or unroot the player. The owning client is ordered with SMSG_FORCE_MOVE_ROOT /
    /// SMSG_FORCE_MOVE_UNROOT: packed GUID + u32 movement counter. vmangos
    /// (MovementPacketSender::SendMovementFlagChangeToController), cmangos-classic
    /// (Unit::SetRoot) and mangoszero (Player::SetRoot) all send the packed GUID for 1.12;
    /// gtker/wow_messages lists a full GUID for 1.12 — the three servers win. The client's
    /// CMSG_FORCE_MOVE_(UN)ROOT_ACK is relayed to observers as MSG_MOVE_(UN)ROOT.
    /// </summary>
    public void SetRooted(bool rooted)
    {
        if (IsRooted == rooted)
        {
            return;
        }

        IsRooted = rooted;
        var packet = new PacketWriter(13);
        packet.WritePackedGuid(Guid.Value);
        packet.WriteUInt32(NextMovementCounter());
        Session.Send(rooted ? WorldOpcode.SmsgForceMoveRoot : WorldOpcode.SmsgForceMoveUnroot, packet.AsSpan());
    }

    /// <summary>
    /// Accept a movement update from this player's client (world thread). A changed position
    /// schedules a visibility pass for the end of the tick.
    /// </summary>
    public void ApplyClientMovement(in MovementInfo movement, uint serverTimeMs)
    {
        bool moved = movement.X != X || movement.Y != Y || movement.Z != Z;
        ApplyMovement(movement, serverTimeMs);
        if (moved)
        {
            NeedsVisibilityUpdate = true;
        }
    }

    /// <summary>Counter for forced movement changes (SMSG_FORCE_MOVE_ROOT etc.).</summary>
    public uint NextMovementCounter() => _movementCounter++;

    // --- logout (vmangos HandleLogoutRequestOpcode / HandleLogoutCancelOpcode) -----

    /// <summary>
    /// Start the 20-second logout (vmangos HandleLogoutRequestOpcode): sit down unless swimming
    /// or on a spline, root, stun, and set the LOGGING_OUT byte flag.
    /// </summary>
    public void BeginLogout(uint nowMs)
    {
        _logoutRequestedAtMs = nowMs;
        if (StandState == StandState.Stand && (Movement.Flags & (MovementFlags.Swimming | MovementFlags.SplineEnabled)) == 0)
        {
            SetStandState(StandState.Sit);
        }

        SetRooted(true);
        UnitFlags |= UnitFlags.Stunned;
        SetByte(UpdateFields.PlayerFieldBytes, 0, (byte)(GetByte(UpdateFields.PlayerFieldBytes, 0) | FieldByteLoggingOut));
    }

    /// <summary>Undo <see cref="BeginLogout"/> (vmangos HandleLogoutCancelOpcode): unroot, stand up, unstun, clear the flag.</summary>
    public void CancelLogout()
    {
        _logoutRequestedAtMs = null;
        SetRooted(false);
        if (StandState == StandState.Sit)
        {
            SetStandState(StandState.Stand);
        }

        UnitFlags &= ~UnitFlags.Stunned;
        SetByte(UpdateFields.PlayerFieldBytes, 0, (byte)(GetByte(UpdateFields.PlayerFieldBytes, 0) & ~FieldByteLoggingOut));
    }

    /// <summary>True once a requested logout has waited <paramref name="delayMs"/>.</summary>
    public bool IsLogoutDue(uint nowMs, uint delayMs) => _logoutRequestedAtMs is { } at && nowMs - at >= delayMs;

    // --- played time and persistence ---------------------------------------------

    /// <summary>Total played seconds at the given world time.</summary>
    public uint PlayedTimeAt(uint nowMs) => _playedTimeAtLogin + ((nowMs - _loginTimeMs) / 1000);

    /// <summary>Played seconds at the current level, at the given world time.</summary>
    public uint LevelPlayedTimeAt(uint nowMs) => _levelPlayedTimeAtLogin + ((nowMs - _levelStartMs) / 1000);

    /// <summary>A level-up restarts the played time at the new level (vmangos GiveLevel: m_playedTime[PLAYED_TIME_LEVEL] = 0).</summary>
    public void ResetLevelPlayedTime(uint nowMs)
    {
        _levelPlayedTimeAtLogin = 0;
        _levelStartMs = nowMs;
    }

    /// <summary>Start counting played time from this world time (called when the player enters the world).</summary>
    internal void StartPlayedTime(uint nowMs)
    {
        _loginTimeMs = nowMs;
        _levelStartMs = nowMs;
    }

    /// <summary>The persistent state to save (world thread). Action buttons are included only when changed.</summary>
    public CharacterState CreateSnapshot(uint nowMs)
    {
        IReadOnlyList<ActionButton>? buttons = null;
        if (_actionButtonsChanged)
        {
            var list = new List<ActionButton>();
            for (int i = 0; i < ActionButtonCount; i++)
            {
                if (_actionButtons[i] != 0)
                {
                    list.Add(new ActionButton((byte)i, _actionButtons[i] & 0x00FFFFFF, (byte)(_actionButtons[i] >> 24)));
                }
            }

            buttons = list;
            _actionButtonsChanged = false;
        }

        return new CharacterState(
            (int)Guid.Low, MapId, ZoneId, X, Y, Z, Orientation, Level,
            Math.Max(PlayedTimeAt(nowMs), 1u), LevelPlayedTimeAt(nowMs), Money, ActionBarToggles, buttons, Home,
            Inventory.TakeSnapshotIfChanged());
    }

    private static uint Pack(uint action, byte type) => (action & 0x00FFFFFF) | ((uint)type << 24);

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
        Money = c.Money;
        ActionBarToggles = c.ActionBarToggles;
    }
}

/// <summary>Action-button types (vmangos Player.h ActionButtonType).</summary>
public static class ActionButtonTypes
{
    public const byte Spell = 0x00;
    public const byte Click = 0x01;
    public const byte Macro = 0x40;
    public const byte CMacro = Click | Macro;
    public const byte Item = 0x80;
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

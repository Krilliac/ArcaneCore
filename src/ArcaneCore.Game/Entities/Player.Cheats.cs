namespace ArcaneCore.Game.Entities;

/// <summary>The GM cheat options a player can carry (a subset of vmangos PlayerCheatOptions, Player.h). Not saved.</summary>
[Flags]
public enum PlayerCheats : uint
{
    None = 0,

    /// <summary>PLAYER_CHEAT_NO_COOLDOWN: no spell, category or global cooldown starts (Spell.cpp:3492, 3994, 4347).</summary>
    NoCooldown = 0x01,

    /// <summary>PLAYER_CHEAT_NO_CAST_TIME: every cast is instant (Spell.cpp:3435).</summary>
    NoCastTime = 0x02,

    /// <summary>PLAYER_CHEAT_NO_POWER: spells cost nothing (Spell.cpp:3398, 5057).</summary>
    NoPowerCost = 0x04,

    /// <summary>Water walking given by <c>.cheat waterwalk</c> (only <c>.cheat status</c> reads it; vmangos tests the movement flag without a water walk aura).</summary>
    WaterWalk = 0x08,

    /// <summary>PLAYER_CHEAT_FLY (<c>.cheat fly</c>): the client swims in the air; the movement checks that would flag that stand down while it is on.</summary>
    Fly = 0x10,

    /// <summary>PLAYER_CHEAT_FIXED_Z (<c>.cheat fixedz</c>): MOVEFLAG_FIXED_Z on the player; a jump glides at a fixed height.</summary>
    FixedZ = 0x20,

    /// <summary>Feather fall given by <c>.cheat featherfall</c> (not a vmangos cheat option): falls never hurt, and the SafeFall flag is expected.</summary>
    FeatherFall = 0x40,
}

public sealed partial class Player
{
    /// <summary>The GM cheats on this player (<c>.cheat cooldown/casttime/powercost/waterwalk/fly/fixedz/featherfall</c>), never saved; god mode is <c>InvincibilityHpThreshold</c> 1.</summary>
    public PlayerCheats Cheats { get; set; }

    /// <summary>Whether every flag of <paramref name="cheat"/> is on.</summary>
    public bool HasCheat(PlayerCheats cheat) => cheat != PlayerCheats.None && (Cheats & cheat) == cheat;

    /// <summary>
    /// Whether <see cref="PlayerCheats.Fly"/> or <see cref="PlayerCheats.FixedZ"/> is on: the client then sends swimming, levitating and
    /// flying flags in mid-air and never falls, so the fall, void, speed and anticheat checks skip it (vmangos MovementAnticheat.cpp:789-806, 1090).
    /// </summary>
    public bool HasMovementCheat => (Cheats & (PlayerCheats.Fly | PlayerCheats.FixedZ)) != PlayerCheats.None;

    /// <summary>
    /// <c>.gm visible off</c> (vmangos Player::SetGMVisible false, PLAYER_EXTRA_GM_INVISIBLE): players of a lower account security than this
    /// one stop seeing it (<see cref="Maps.GmInvisibilityRule"/>). Not saved.
    /// </summary>
    public bool GmInvisible { get; set; }
}
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Game.Death;

/// <summary>
/// A stored <see cref="CharacterLife"/> and the health and power values <see cref="PlayerLife.ApplyVitals"/>
/// actually set from it (clamped to the maximums of that moment).
/// </summary>
public sealed record LoadedLife(CharacterLife Stored, uint AppliedHealth, IReadOnlyList<uint> AppliedPowers);

/// <summary>
/// Capture and application of a player's <see cref="CharacterLife"/> (vmangos Player::SaveToDB
/// and the vitals part of Player::LoadFromDB). Pure field work: no map, no I/O.
/// </summary>
public static class PlayerLife
{
    /// <summary>Powers saved with a character (vmangos MAX_POWERS, SharedDefines.h:170).</summary>
    public const int PowerCount = 5;

    /// <summary>
    /// The 1.12 limit on remembered deaths: the expiry is capped at <c>now + 3 * 5 minutes - 1</c>
    /// on load (Player.cpp:14915-14917, DEATH_EXPIRE_STEP = 5 minutes, MAX_DEATH_COUNT = 3).
    /// </summary>
    private static long MaxDeathWindowSeconds => CombatConstants.MaxDeathCount * CombatConstants.DeathExpireStepSeconds;

    /// <summary>
    /// The life of <paramref name="player"/> right now: health, the five powers, experience, the
    /// recent-death window, and, for a released spirit, the ghost flag with its corpse.
    /// </summary>
    public static CharacterLife Capture(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var powers = new uint[PowerCount];
        for (int i = 0; i < PowerCount; i++)
        {
            powers[i] = player.GetUInt32(UpdateFields.UnitFieldPower1 + i);
        }

        UnitCombat c = player.Combat;
        CorpseSnapshot? corpse = c.Corpse is { } body
            ? new CorpseSnapshot(body.MapId, body.X, body.Y, body.Z, body.Orientation, c.GhostTime, (byte)body.Type, body.Map?.InstanceId ?? 0)
            : null;
        return new CharacterLife(
            player.Health,
            powers,
            player.GetUInt32(UpdateFields.PlayerXp),
            c.DeathExpireTime,
            (player.Flags & PlayerFlags.Ghost) != 0,
            corpse);
    }

    /// <summary>
    /// Apply the stored health and power, never above the current maximums ("restore remembered
    /// power/health values (but not more max values)", Player.cpp:15062-15070). Call it after
    /// everything that sets the maximums (items, auras, level stats) has run. A character stored
    /// dead without being a ghost has no body to walk back to: it comes back at half health and
    /// mana like vmangos <c>Player::LoadCorpse</c> ("Prevent Dead Player login without corpse",
    /// Player.cpp:15434-15439, <c>ResurrectPlayer(0.5f)</c>).
    /// </summary>
    public static LoadedLife ApplyVitals(Player player, CharacterLife life)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(life);
        // PLAYER_SELF_RES_SPELL is a current-death selection, not a stored vital:
        // vmangos SaveToDB (16347-16536) omits it, and LoadFromDB creates fresh
        // fields. Explicitly clear it even when a loading adapter reuses an object.
        player.SetUInt32(UpdateFields.PlayerSelfResSpell, 0);
        if (HasNoBodyToReturnTo(life))
        {
            MapCombat.RestoreFraction(player, CombatConstants.CorpseReclaimRestorePercent);
        }
        else
        {
            player.Health = Math.Min(life.Health, player.MaxHealth);
            for (int i = 0; i < PowerCount && i < life.Powers.Count; i++)
            {
                MapCombat.SetPower(player, (PowerType)i, life.Powers[i]);
            }
        }

        var applied = new uint[PowerCount];
        for (int i = 0; i < PowerCount; i++)
        {
            applied[i] = player.GetUInt32(UpdateFields.UnitFieldPower1 + i);
        }

        return new LoadedLife(life, player.Health, applied);
    }

    /// <summary>
    /// Dead without being a ghost, or a ghost without a body: vmangos <c>Player::LoadCorpse</c>
    /// resurrects both at half health ("Prevent Dead Player login without corpse", Player.cpp:15434-15439).
    /// </summary>
    public static bool HasNoBodyToReturnTo(CharacterLife life)
    {
        ArgumentNullException.ThrowIfNull(life);
        return life.IsGhost ? life.Corpse is null : life.Health == 0;
    }

    /// <summary>
    /// Whether the stored life is a ghost that goes back to its body at login (a ghost with a corpse).
    /// </summary>
    public static bool IsGhostWithBody(CharacterLife life)
    {
        ArgumentNullException.ThrowIfNull(life);
        return life.IsGhost && life.Corpse is not null;
    }

    /// <summary>
    /// The state of a released spirit that does not need a map: PLAYER_FLAGS_GHOST and the dead
    /// state, so the create block it enters the world with already shows a ghost (vmangos gets
    /// the same from the ghost aura, whose PLAYER_FLAGS_GHOST is applied during the aura load:
    /// Player.cpp:14972-14975, SpellAuras.cpp:5639-5659). The body and the water walking (the
    /// login relocates the player, which clears movement flags) are put back on the world thread
    /// by <see cref="MapCombat.RestoreGhost"/>.
    /// </summary>
    public static void ApplyGhostState(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.Flags |= PlayerFlags.Ghost;
        player.Combat.DeathState = DeathState.Dead;
        player.Combat.DeathTimer = 0;
    }

    /// <summary>
    /// Passive spells and saved auras are applied once the player is in the world, after
    /// <see cref="ApplyVitals"/>; if they raised the maximums, bring health and power up to the
    /// stored values that were clamped to the smaller maximums (vmangos has the auras and the
    /// final stats before it restores them: Player.cpp:15057-15070). A value that changed since
    /// the load (damage, spending) is left alone, and so is a player that is not alive.
    /// </summary>
    public static void ReapplyAfterAuras(Player player, LoadedLife loaded)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(loaded);
        if (!player.IsAlive)
        {
            return;
        }

        CharacterLife life = loaded.Stored;
        if (life.Health > 0 && player.Health == loaded.AppliedHealth && loaded.AppliedHealth < life.Health)
        {
            player.Health = Math.Min(life.Health, player.MaxHealth);
        }

        for (int i = 0; i < PowerCount && i < life.Powers.Count; i++)
        {
            var power = (PowerType)i;
            if (MapCombat.GetPower(player, power) == loaded.AppliedPowers[i] && loaded.AppliedPowers[i] < life.Powers[i])
            {
                MapCombat.SetPower(player, power, life.Powers[i]);
            }
        }
    }

    /// <summary>
    /// Restore the recent-death window, capped at three steps minus a second so a hand-edited
    /// or ancient value cannot lock the reclaim delay at its maximum (Player.cpp:14915-14917).
    /// </summary>
    public static void ApplyDeathWindow(Player player, CharacterLife life, long nowUnix)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(life);
        long expire = life.DeathExpireUnix;
        long cap = nowUnix + MaxDeathWindowSeconds - 1;
        player.Combat.DeathExpireTime = expire > cap ? cap : expire;
    }
}

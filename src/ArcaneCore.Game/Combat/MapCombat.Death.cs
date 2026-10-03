using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

public sealed partial class MapCombat
{
    /// <summary>PLAYER_FIELD_BYTES byte 0 flag: show the auto-release countdown (vmangos Player.h PLAYER_FIELD_BYTE_RELEASE_TIMER).</summary>
    public const byte FieldByteReleaseTimer = 0x08;

    /// <summary>
    /// vmangos Player::KillPlayer (the update after death): CORPSE state, dynamic flags cleared,
    /// the release-timer byte flag on non-instanced maps, the 6-minute auto release, and the
    /// recent-death count.
    /// </summary>
    public void KillPlayer(Player player)
    {
        if (IsQuestSettlementPending(player))
        {
            return;
        }

        UnitCombat c = player.Combat;
        SetDeathState(player, DeathState.Corpse);
        player.SetUInt32(UpdateFields.UnitDynamicFlags, 0);

        byte bytes = player.GetByte(UpdateFields.PlayerFieldBytes, 0);
        bytes = Hooks.IsInstanceable(player.MapId) ? (byte)(bytes & ~FieldByteReleaseTimer) : (byte)(bytes | FieldByteReleaseTimer);
        player.SetByte(UpdateFields.PlayerFieldBytes, 0, bytes);

        c.DeathTimer = CombatConstants.CorpseRepopTimeMs;
        UpdateCorpseReclaimDelay(player);
    }

    /// <summary>
    /// Release the spirit (vmangos Player::BuildPlayerRepop then ScheduleRepopAtGraveyard):
    /// ghost form, a corpse at the body, health 1, unrooted unless logging out,
    /// SMSG_CORPSE_RECLAIM_DELAY, the ghost timer reset, DEAD — then the graveyard hook.
    /// Returns false when the player is alive or already a ghost (HandleRepopRequestOpcode).
    /// </summary>
    public bool RepopPlayer(Player player)
    {
        UnitCombat c = player.Combat;
        if (IsQuestSettlementPending(player) || IsAliveState(player) || (player.Flags & PlayerFlags.Ghost) != 0)
        {
            return false;
        }

        if (c.DeathState == DeathState.JustDied)
        {
            KillPlayer(player); // released before its update ran (HandleRepopRequestOpcode)
        }

        Hooks.ApplyGhostForm(player);
        SetGhost(player, true);

        if (c.Corpse is { } old)
        {
            RemoveCorpse(old);
        }

        Corpse corpse = Corpse.CreateFor(player, c.PvpDeath);
        c.PvpDeath = false;
        c.Corpse = corpse;
        AddCorpse(corpse);

        player.Health = 1;
        if (!player.IsLoggingOut)
        {
            player.SetRooted(false);
        }

        player.UnitFlags &= ~UnitFlags.Skinnable;
        SendCorpseReclaimDelay(player);
        c.GhostTime = NowSeconds;
        c.DeathTimer = 0;
        SetDeathState(player, DeathState.Dead);

        Hooks.RepopAtGraveyard(player);
        return true;
    }

    /// <summary>
    /// CMSG_RECLAIM_CORPSE (vmangos HandleReclaimCorpseOpcode): a ghost with a corpse whose
    /// reclaim delay has passed, within 39 yd (3D) of it, is resurrected at 50% and the corpse
    /// goes away.
    /// </summary>
    public bool TryReclaimCorpse(Player player)
    {
        UnitCombat c = player.Combat;
        if (IsQuestSettlementPending(player) || IsAliveState(player) || (player.Flags & PlayerFlags.Ghost) == 0 || c.Corpse is not { } corpse)
        {
            return false;
        }

        if (c.GhostTime + GetCorpseReclaimDelay(player, corpse.Type == CorpseType.ResurrectablePvp) > NowSeconds)
        {
            return false;
        }

        if (!ReferenceEquals(corpse.Map, player.Map))
        {
            return false;
        }

        // WorldObject::IsWithinDistInMap(3D): distance minus both bounding radii.
        float dx = corpse.X - player.X;
        float dy = corpse.Y - player.Y;
        float dz = corpse.Z - player.Z;
        float max = CombatConstants.CorpseReclaimRadius + corpse.BoundingRadius + player.BoundingRadius;
        if ((dx * dx) + (dy * dy) + (dz * dz) >= max * max)
        {
            return false;
        }

        ResurrectPlayer(player, CombatConstants.CorpseReclaimRestorePercent, applySickness: false);
        RemoveCorpse(corpse);
        c.Corpse = null;
        return true;
    }

    /// <summary>
    /// vmangos Player::ResurrectPlayer: ALIVE, ghost form removed, unrooted, health/mana/energy
    /// restored to <paramref name="restorePercent"/> and rage emptied (when &gt; 0).
    /// </summary>
    public void ResurrectPlayer(Player player, float restorePercent, bool applySickness)
    {
        if (IsQuestSettlementPending(player))
        {
            return;
        }

        SetDeathState(player, DeathState.Alive);
        Hooks.RemoveGhostForm(player);
        SetGhost(player, false);
        player.SetRooted(false);

        if (restorePercent > 0f)
        {
            player.Health = Math.Max(1u, (uint)(player.MaxHealth * restorePercent));
            SetPower(player, PowerType.Mana, (uint)(GetMaxPower(player, PowerType.Mana) * restorePercent));
            SetPower(player, PowerType.Rage, 0);
            SetPower(player, PowerType.Energy, (uint)(GetMaxPower(player, PowerType.Energy) * restorePercent));
        }

        player.Combat.DeathTimer = 0;
        player.NeedsVisibilityUpdate = true;
        Hooks.OnResurrected(player, applySickness);
    }

    /// <summary>MSG_CORPSE_QUERY reply body (vmangos HandleCorpseQueryOpcode; dungeon ghost entrances need Map.dbc).</summary>
    public static byte[] BuildCorpseQuery(Player player)
    {
        if (player.Combat.Corpse is not { } corpse)
        {
            return CombatPackets.CorpseQueryNotFound();
        }

        return CombatPackets.CorpseQueryFound((int)corpse.MapId, corpse.X, corpse.Y, corpse.Z, corpse.MapId);
    }

    /// <summary>
    /// PLAYER_FLAGS_GHOST and water walking. vmangos gets both from the ghost aura (8326:
    /// SPELL_AURA_GHOST + SPELL_AURA_WATER_WALK); nothing casts that aura yet (the ghost-form
    /// hooks have no override), so combat applies them directly, and
    /// the move change goes to the client as SMSG_MOVE_WATER_WALK / SMSG_MOVE_LAND_WALK.
    /// </summary>
    private static void SetGhost(Player player, bool ghost)
    {
        bool isGhost = (player.Flags & PlayerFlags.Ghost) != 0;
        if (isGhost == ghost)
        {
            return;
        }

        player.Flags = ghost ? player.Flags | PlayerFlags.Ghost : player.Flags & ~PlayerFlags.Ghost;
        player.Session.Send(ghost ? WorldOpcode.SmsgMoveWaterWalk : WorldOpcode.SmsgMoveLandWalk,
            CombatPackets.MovementFlagChange(player.Guid, player.NextMovementCounter()));
    }

    /// <summary>
    /// vmangos Player::GetCorpseReclaimDelay: 30/60/120 s by deaths in the last 5-minute steps
    /// (both PvE and PvP delays are on by default: Death.CorpseReclaimDelay.PvE/PvP = 1).
    /// </summary>
    public uint GetCorpseReclaimDelay(Player player, bool pvp)
    {
        _ = pvp; // both kinds use the scaling delay by default
        long now = NowSeconds;
        long expire = player.Combat.DeathExpireTime;
        uint count = now < expire ? (uint)((expire - now) / CombatConstants.DeathExpireStepSeconds) : 0;
        if (count >= 2)
        {
            count = 2;
        }

        return CombatConstants.CorpseReclaimDelaySeconds[(int)count];
    }

    /// <summary>vmangos Player::UpdateCorpseReclaimDelay: extend the recent-death window by one 5-minute step (max 3).</summary>
    private void UpdateCorpseReclaimDelay(Player player)
    {
        UnitCombat c = player.Combat;
        long now = NowSeconds;
        uint step = CombatConstants.DeathExpireStepSeconds;
        if (now < c.DeathExpireTime)
        {
            long count = ((c.DeathExpireTime - now) / step) + 1;
            c.DeathExpireTime = count < CombatConstants.MaxDeathCount
                ? now + ((count + 1) * step)
                : now + (CombatConstants.MaxDeathCount * step);
        }
        else
        {
            c.DeathExpireTime = now + step;
        }
    }

    private void SendCorpseReclaimDelay(Player player)
    {
        if (player.Combat.Corpse is not { } corpse)
        {
            return;
        }

        uint delay = GetCorpseReclaimDelay(player, corpse.Type == CorpseType.ResurrectablePvp);
        player.Session.Send(WorldOpcode.SmsgCorpseReclaimDelay, CombatPackets.CorpseReclaimDelay(delay * 1000));
    }

    // --- PvP flag (vmangos Player::UpdatePvP, UpdatePvPFlagTimer, HandleTogglePvP) ------

    /// <summary>
    /// CMSG_TOGGLE_PVP (vmangos HandleTogglePvP): with a state byte set PLAYER_FLAGS_PVP_DESIRED
    /// to it, otherwise flip it; wanting PvP flags the player at once and refreshes the 5-minute
    /// timer.
    /// </summary>
    public void TogglePvp(Player player, bool? desired)
    {
        if (IsQuestSettlementPending(player))
        {
            return;
        }

        bool want = desired ?? (player.Flags & PlayerFlags.PvpDesired) == 0;
        player.Flags = want ? player.Flags | PlayerFlags.PvpDesired : player.Flags & ~PlayerFlags.PvpDesired;
        if (want)
        {
            UpdatePvp(player, true);
        }

        // Not wanting it any more lets the 5-minute timer (refreshed by the last UpdatePvP(true))
        // run out in UpdatePvpFlagTimer.
    }

    /// <summary>vmangos Player::UpdatePvP(state, override = false): on → flag and refresh the 5-minute timer; off → drop the flag once the timer is over.</summary>
    public static void UpdatePvp(Player player, bool state)
    {
        if (IsQuestSettlementPending(player))
        {
            return;
        }

        if (state)
        {
            player.Combat.PvpFlagTimer = CombatConstants.PvpFlagTimerMs;
            player.UnitFlags |= UnitFlags.Pvp;
        }
        else if (player.Combat.PvpFlagTimer == 0 && (player.UnitFlags & UnitFlags.Pvp) != 0)
        {
            player.UnitFlags &= ~UnitFlags.Pvp;
        }
    }

    /// <summary>vmangos Player::UpdatePvPFlagTimer: the linger counts down while not desired and not in PvP combat.</summary>
    private static void UpdatePvpFlagTimer(Player player, uint diff)
    {
        UnitCombat c = player.Combat;
        if ((player.UnitFlags & UnitFlags.Pvp) == 0)
        {
            return;
        }

        if (!c.InPvpCombat && (player.Flags & PlayerFlags.PvpDesired) == 0)
        {
            c.PvpFlagTimer = c.PvpFlagTimer > diff ? c.PvpFlagTimer - diff : 0;
            UpdatePvp(player, false);
        }
    }

    /// <summary>vmangos Unit::TogglePlayerPvPFlagOnAttackVictim: attacking a PvP-flagged unit flags the attacker.</summary>
    private static void TogglePlayerPvpFlagOnAttackVictim(Player attacker, Unit victim)
    {
        if ((victim.UnitFlags & UnitFlags.Pvp) != 0 && !ReferenceEquals(attacker, victim))
        {
            attacker.Combat.InPvpCombat = true;
            UpdatePvp(attacker, true);
        }
    }
}

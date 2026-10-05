using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Combat;

/// <summary>
/// Per-unit combat state (the combat members of vmangos Unit/Player): death state, melee
/// victim and attackers, swing timers, combat timer, threat, regeneration and — for players —
/// the corpse, recent deaths and the PvP flag timer. Logic lives in <see cref="MapCombat"/>;
/// everything here is touched on the world thread only.
/// </summary>
public sealed class UnitCombat
{
    private readonly uint[] _attackTimers = new uint[3];
    private ThreatList? _threat;
    private uint _extraAttacks;
    private bool _extraAttacksReady;
    private bool _extraAttacksLocked;

    internal UnitCombat(Unit owner) => Owner = owner;

    public Unit Owner { get; }

    public DeathState DeathState { get; internal set; } = DeathState.Alive;

    /// <summary>The unit being auto-attacked (vmangos Unit::m_attacking).</summary>
    public Unit? Victim { get; internal set; }

    /// <summary>True while <see cref="Victim"/> is attacked in melee (vmangos m_meleeAttack).</summary>
    public bool IsMeleeAttacking { get; internal set; }

    /// <summary>Units auto-attacking this one (vmangos Unit::m_attackers).</summary>
    public IReadOnlySet<Unit> Attackers => AttackersInternal;

    internal HashSet<Unit> AttackersInternal { get; } = [];

    /// <summary>Units whose threat lists hold this one (vmangos HostileRefManager).</summary>
    public IReadOnlySet<Unit> ThreatenedBy => ThreatenedByInternal;

    internal HashSet<Unit> ThreatenedByInternal { get; } = [];

    /// <summary>The unit's own threat list (non-players; vmangos Unit::m_ThreatManager).</summary>
    public ThreatList Threat => _threat ??= new ThreatList(Owner);

    public bool HasThreatList => _threat is { IsEmpty: false };

    public bool IsInCombat => (Owner.UnitFlags & UnitFlags.InCombat) != 0;

    /// <summary>Pending vmangos extra attacks (Unit::m_extraAttacks).</summary>
    public uint ExtraAttacks => _extraAttacks;

    public bool HasPendingExtraAttacks => _extraAttacks != 0;

    internal bool HasReadyExtraAttacks => _extraAttacksReady && _extraAttacks != 0;

    public bool ExtraAttacksLocked => _extraAttacksLocked;

    /// <summary>Queue one bounded extra-attack batch; while locked or already queued, ignore it like vmangos.</summary>
    public bool QueueExtraAttacks(int count)
    {
        if (count <= 0 || !Owner.IsAlive || _extraAttacksLocked || _extraAttacks != 0) return false;
        _extraAttacks = (uint)Math.Min(count, 100);
        return true;
    }

    internal void MarkExtraAttacksReady()
    {
        _extraAttacksReady = _extraAttacks != 0;
    }

    internal void ClearExtraAttacksReady() => _extraAttacksReady = false;

    internal void LockExtraAttacks() => _extraAttacksLocked = true;
    internal void UnlockExtraAttacks() => _extraAttacksLocked = false;
    /// <summary>Clear pending extra attacks during death/reset transitions; never unlocks an active drain.</summary>
    public void ResetExtraAttacks()
    {
        _extraAttacks = 0;
        _extraAttacksReady = false;
    }
    internal bool ConsumeExtraAttack()
    {
        if (_extraAttacks == 0) return false;
        _extraAttacks--;
        return true;
    }

    /// <summary>Remaining PvP combat linger for players (vmangos Unit::m_CombatTimer).</summary>
    public uint CombatTimer { get; internal set; }

    internal uint CombatCheckTimer { get; set; }

    /// <summary>The last auto-attack error sent to this player's client (vmangos Player::m_swingErrorMsg).</summary>
    public AttackCheckResult LastSwingError { get; internal set; }

    // --- regeneration (vmangos Player::m_regenTimer, Unit::m_lastManaUseTimer) --------

    internal int RegenTimer { get; set; }

    internal float HealthRegenCarry { get; set; }

    /// <summary>Remaining "five second rule" time after spending mana; the spell system sets it via <see cref="NoteManaUsed"/>.</summary>
    public uint LastManaUseTimer { get; internal set; }

    // --- player death (vmangos Player::m_deathTimer, m_deathExpireTime, corpse) --------

    /// <summary>Time left before an unreleased corpse is auto-released (vmangos m_deathTimer).</summary>
    public uint DeathTimer { get; internal set; }

    /// <summary>Unix second until which recent deaths count (vmangos m_deathExpireTime, a time_t).</summary>
    internal long DeathExpireTime { get; set; }

    /// <summary>Unix second the corpse was last reset (vmangos Corpse::m_time / GetGhostTime, a time_t).</summary>
    internal long GhostTime { get; set; }

    /// <summary>Killed by a player (vmangos Player::m_pvpDeath).</summary>
    public bool PvpDeath { get; internal set; }

    /// <summary>The player's corpse while released (vmangos Player::GetCorpse).</summary>
    public Corpse? Corpse { get; internal set; }

    // --- PvP flag (vmangos Player::pvpInfo) -----------------------------------------

    /// <summary>In combat with a PvP-flagged enemy (vmangos pvpInfo.inPvPCombat).</summary>
    public bool InPvpCombat { get; internal set; }

    /// <summary>Remaining PvP flag linger (vmangos pvpInfo.timerPvPRemaining).</summary>
    public uint PvpFlagTimer { get; internal set; }

    internal MapCombat? Tracker { get; set; }

    /// <summary>The swing timer for one hand (vmangos Unit::getAttackTimer).</summary>
    public uint GetAttackTimer(WeaponAttackType type) => _attackTimers[(int)type];

    internal void SetAttackTimer(WeaponAttackType type, uint time) => _attackTimers[(int)type] = time;

    internal bool IsAttackReady(WeaponAttackType type) => _attackTimers[(int)type] == 0;

    /// <summary>vmangos Unit::GetAttackTime: UNIT_FIELD_BASEATTACKTIME + slot (haste mods arrive with spells).</summary>
    public uint GetAttackTime(WeaponAttackType type)
    {
        uint time = Owner.GetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)type);
        return time == 0 && type == WeaponAttackType.BaseAttack ? CombatConstants.BaseAttackTimeMs : time;
    }

    /// <summary>vmangos Unit::resetAttackTimer.</summary>
    public void ResetAttackTimer(WeaponAttackType type = WeaponAttackType.BaseAttack) => _attackTimers[(int)type] = GetAttackTime(type);

    /// <summary>
    /// The spell that last took mana (vmangos Unit::m_lastManaUseSpellId): while the unit still channels it, the
    /// five second timer does not run out (Unit::Update, patch 1.7 fix). 0 when none.
    /// </summary>
    public uint LastManaUseSpellId { get; internal set; }

    /// <summary>Start the five second rule (vmangos Spell::TakePower → SetLastManaUse). For the spells area.</summary>
    public void NoteManaUsed() => NoteManaUsed(0);

    /// <summary>Start the five second rule for <paramref name="spellId"/> (vmangos Unit::SetLastManaUse(spellId)).</summary>
    public void NoteManaUsed(uint spellId)
    {
        LastManaUseTimer = CombatConstants.ManaRegenInterruptMs;
        LastManaUseSpellId = spellId;
    }

    internal void TickAttackTimers(uint diff)
    {
        for (int i = 0; i < _attackTimers.Length; i++)
        {
            _attackTimers[i] = _attackTimers[i] > diff ? _attackTimers[i] - diff : 0;
        }
    }
}

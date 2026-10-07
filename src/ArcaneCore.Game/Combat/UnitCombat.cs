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

    /// <summary>vmangos Player::m_repopAtGraveyardPending: the spirit was released and is sent to its graveyard once no movement change is pending.</summary>
    internal bool RepopPending { get; set; }

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

    // --- attack speed (ranged (autorepeat lane); vmangos Unit::m_modAttackSpeedPct) -----------------------

    private readonly float[] _speedPct = [1.0f, 1.0f, 1.0f];
    private readonly float[] _field = new float[3];

    /// <summary>
    /// The attack-speed multiplier of one slot (vmangos Unit::m_modAttackSpeedPct; 1 = none, below 1 = faster).
    /// Written only through <see cref="ApplyAttackTimePercentMod"/>.
    /// </summary>
    public float GetAttackSpeedPct(WeaponAttackType type) => _speedPct[(int)type];

    /// <summary>
    /// vmangos stores UNIT_FIELD_*ATTACKTIME as a float and sends it as uint32 (Object.cpp:752-756), so repeated percent
    /// mods keep sub-millisecond precision. The update field here is a uint and many writers set it directly, so the float
    /// is a shadow: whenever the uint no longer matches the truncated shadow someone wrote the field and the shadow
    /// follows it.
    /// </summary>
    private float Field(WeaponAttackType type)
    {
        int slot = (int)type;
        uint raw = Owner.GetUInt32(UpdateFields.UnitFieldBaseattacktime + slot);
        if (_field[slot] < 0 || (uint)_field[slot] != raw)
        {
            _field[slot] = raw;
        }

        return _field[slot];
    }

    private void StoreField(WeaponAttackType type, float value)
    {
        int slot = (int)type;
        _field[slot] = value;
        Owner.SetUInt32(UpdateFields.UnitFieldBaseattacktime + slot, value < 0 ? 0u : (uint)value);
    }

    /// <summary>
    /// vmangos Unit::GetAttackTime (Unit.h:406): the UNHASTED base time, the field divided by the speed multiplier. Damage
    /// scaling (attack power per weapon speed, normalized weapon damage) reads this, so haste does not change damage per hit.
    /// With no haste it is exactly the field value.
    /// </summary>
    public uint GetAttackTime(WeaponAttackType type)
    {
        uint time = GetUnhastedTime(type);
        return time == 0 && type == WeaponAttackType.BaseAttack ? CombatConstants.BaseAttackTimeMs : time;
    }

    /// <summary>The unhasted time without the 2000 ms default for an unset main hand (what damage scaling reads).</summary>
    internal uint GetUnhastedTime(WeaponAttackType type) => (uint)(Field(type) / _speedPct[(int)type]);

    /// <summary>vmangos Unit::SetAttackTime (Unit.h:407): the field holds <paramref name="value"/> times the speed multiplier.</summary>
    public void SetAttackTime(WeaponAttackType type, uint value, bool resetTimer = true)
    {
        StoreField(type, value * _speedPct[(int)type]);
        if (resetTimer)
        {
            ResetAttackTimer(type);
        }
    }

    /// <summary>
    /// vmangos Unit::ApplyAttackTimePercentMod (Unit.cpp:9678-9697) with Util.h:91-96 / Object.h:248-252: a positive
    /// <paramref name="percent"/> is haste (the time shrinks by 100/(100+percent)), a negative one slows. The -100 guards
    /// keep the multiplier and the field from reaching zero.
    /// </summary>
    public void ApplyAttackTimePercentMod(WeaponAttackType type, float percent, bool apply)
    {
        float field = Field(type);
        if (percent > 0)
        {
            _speedPct[(int)type] = PercentVar(_speedPct[(int)type], percent, !apply);
            StoreField(type, PercentVar(field, percent, !apply, fieldGuard: true));
        }
        else
        {
            _speedPct[(int)type] = PercentVar(_speedPct[(int)type], -percent, apply);
            StoreField(type, PercentVar(field, -percent, apply, fieldGuard: true));
        }
    }

    private static float PercentVar(float value, float percent, bool apply, bool fieldGuard = false)
    {
        if (percent == -100.0f)
        {
            percent = fieldGuard ? -99.9f : -99.99f;
        }

        return value * (apply ? (100.0f + percent) / 100.0f : 100.0f / (100.0f + percent));
    }

    /// <summary>vmangos Unit::resetAttackTimer (Unit.cpp:529-532): the unhasted time times the multiplier, which is the hasted field.</summary>
    public void ResetAttackTimer(WeaponAttackType type = WeaponAttackType.BaseAttack)
        => _attackTimers[(int)type] = (uint)(GetAttackTime(type) * _speedPct[(int)type]);

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

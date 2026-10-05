using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Combat;

/// <summary>The result of one white swing (vmangos DamageStructs.h CalcDamageInfo, physical only).</summary>
public sealed class MeleeDamageInfo
{
    public required Unit Attacker { get; init; }

    public required Unit Target { get; init; }

    public WeaponAttackType AttackType { get; init; }

    public MeleeHitOutcome Outcome { get; set; }

    public HitInfo HitInfo { get; set; }

    public VictimState TargetState { get; set; }

    /// <summary>Damage that reaches the victim's health.</summary>
    public uint TotalDamage { get; set; }

    /// <summary>Damage removed by armor, block, glancing, dodge and parry (vmangos cleanDamage).</summary>
    public uint CleanDamage { get; set; }

    public uint Blocked { get; set; }
}

public sealed partial class MapCombat
{
    // --- attack start / stop (vmangos Unit::Attack, AttackStop, CombatStop) ------------

    /// <summary>
    /// Start attacking <paramref name="victim"/> (vmangos Unit::Attack): both alive, the victim
    /// in the world and not a GM / evading creature. Re-attacking the current victim only
    /// switches to melee; a new victim first stops the old attack. Sends SMSG_ATTACKSTART to
    /// the set including the attacker when <paramref name="melee"/>.
    /// </summary>
    public bool Attack(Unit attacker, Unit victim, bool melee = true)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(victim);
        if (IsQuestSettlementPending(attacker) || IsQuestSettlementPending(victim)
            || ReferenceEquals(attacker, victim) || !IsAliveState(attacker) || !victim.IsInWorld || !IsAliveState(victim))
        {
            return false;
        }

        if (victim is Player { IsGameMaster: true } || victim is ICombatCreature { IsInEvadeMode: true })
        {
            return false;
        }

        UnitCombat c = attacker.Combat;
        if (c.Victim is { } current)
        {
            if (ReferenceEquals(current, victim))
            {
                if (melee && !c.IsMeleeAttacking)
                {
                    c.IsMeleeAttacking = true;
                    SendAttackStart(attacker, victim);
                    return true;
                }

                return false;
            }

            AttackStop(attacker, targetSwitch: true);
        }

        attacker.Target = victim.Guid;
        c.IsMeleeAttacking = melee;
        c.Victim = victim;
        victim.Combat.AttackersInternal.Add(attacker);
        Track(attacker);
        Track(victim);

        // delay the off-hand to the next attack time
        if (HasOffhandWeapon(attacker))
        {
            c.ResetAttackTimer(WeaponAttackType.OffAttack);
        }

        if (melee)
        {
            SendAttackStart(attacker, victim);
        }

        return true;
    }

    /// <summary>
    /// Stop attacking (vmangos Unit::AttackStop): clear the victim and UNIT_FIELD_TARGET and
    /// send SMSG_ATTACKSTOP to the set (is-dead flag = the attacker's health is 0, as vmangos
    /// SendMeleeAttackStop sends it).
    /// </summary>
    public bool AttackStop(Unit attacker, bool targetSwitch = false)
    {
        UnitCombat c = attacker.Combat;
        if (c.Victim is not { } victim)
        {
            return false;
        }

        victim.Combat.AttackersInternal.Remove(attacker);
        c.Victim = null;
        c.IsMeleeAttacking = false;
        attacker.Target = default;
        CombatEnvironment.For(_world).MeleeSpells?.OnMeleeAttackStopped(attacker);

        CombatPackets.SendToSet(attacker, WorldOpcode.SmsgAttackstop,
            CombatPackets.AttackStop(attacker.Guid, victim.Guid, attacker.Health == 0));
        return true;
    }

    /// <summary>
    /// Leave all fighting (vmangos Unit::CombatStop): stop attacking, make every attacker stop,
    /// SMSG_CANCEL_COMBAT to a player (SendAttackSwingCancelAttack) and clear the in-combat state.
    /// </summary>
    public void CombatStop(Unit unit)
    {
        AttackStop(unit);
        foreach (Unit attacker in unit.Combat.AttackersInternal.ToArray())
        {
            AttackStop(attacker);
        }

        unit.Combat.AttackersInternal.Clear();
        if (unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgCancelCombat, []);
        }

        ClearInCombat(unit);
    }

    private static void SendAttackStart(Unit attacker, Unit victim)
        => CombatPackets.SendToSet(attacker, WorldOpcode.SmsgAttackstart, CombatPackets.AttackStart(attacker.Guid, victim.Guid));

    // --- swing loop (vmangos Unit::UpdateMeleeAttackingState) -------------------------

    /// <summary>
    /// vmangos Unit::CanAutoAttackTarget: pacified/stunned → can't attack; either dead → dead;
    /// out of melee reach → not in range; farther than 1.4 yd center to center and outside the
    /// 2π/3 front arc → bad facing.
    /// </summary>
    public AttackCheckResult CanAutoAttackTarget(Unit attacker, Unit victim)
    {
        if (IsQuestSettlementPending(attacker) || IsQuestSettlementPending(victim))
        {
            return AttackCheckResult.CantAttack;
        }

        if ((attacker.UnitFlags & (UnitFlags.Pacified | UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) != 0)
        {
            return AttackCheckResult.CantAttack; // UNIT_STATE_CAN_NOT_REACT | UNIT_FLAG_PACIFIED
        }

        if (!IsAliveState(victim) || !IsAliveState(attacker))
        {
            return AttackCheckResult.Dead;
        }

        if (!CanReachWithMeleeAutoAttack(attacker, victim))
        {
            return AttackCheckResult.NotInRange;
        }

        float dx = victim.X - attacker.X;
        float dy = victim.Y - attacker.Y;
        if (MathF.Sqrt((dx * dx) + (dy * dy)) > CombatConstants.NoFacingChecksDistance
            && !HasInArc(attacker, victim, CombatConstants.AutoAttackArc))
        {
            return AttackCheckResult.BadFacing;
        }

        return AttackCheckResult.Ok;
    }

    /// <summary>
    /// vmangos Unit::CanReachWithMeleeAutoAttack / GetCombatReachToTarget: reach =
    /// max(reach, 1.5) of both + 4/3, at least 5 yd, plus 2.66 yd leeway when a player and its
    /// target both move without walking; 2D distance² &lt; reach², dz² &lt; 36.
    /// </summary>
    public static bool CanReachWithMeleeAutoAttack(Unit attacker, Unit victim)
    {
        if (!victim.IsInWorld)
        {
            return false;
        }

        float reach = MeleeReach(attacker) + MeleeReach(victim) + CombatConstants.BaseMeleeRangeOffset;
        if (reach < CombatConstants.AttackDistance)
        {
            reach = CombatConstants.AttackDistance;
        }

        if ((attacker is Player || victim is Player) && IsMovingButNotWalking(attacker) && IsMovingButNotWalking(victim))
        {
            reach += CombatConstants.LeewayBonusRange;
        }

        float dx = attacker.X - victim.X;
        float dy = attacker.Y - victim.Y;
        float dz = attacker.Z - victim.Z;
        return (dx * dx) + (dy * dy) < reach * reach && dz * dz < CombatConstants.DefaultMeleeZLimit;
    }

    private static float MeleeReach(Unit unit)
    {
        float reach = unit.GetFloat(UpdateFields.UnitFieldCombatreach);
        return reach < CombatConstants.MinMeleeCombatReach ? CombatConstants.MinMeleeCombatReach : reach;
    }

    private static bool IsMovingButNotWalking(Unit unit)
    {
        MovementFlags flags = unit.Movement.Flags;
        return (flags & MovementFlags.MaskMoving) != 0 && (flags & (MovementFlags.WalkMode | MovementFlags.Backward)) == 0;
    }

    /// <summary>One pass of the swing loop for a unit with a melee victim; returns true if it swung.</summary>
    public bool UpdateMeleeAttackingState(Unit attacker)
    {
        UnitCombat c = attacker.Combat;
        if (c.Victim is not { } victim)
        {
            return false;
        }

        if (IsQuestSettlementPending(attacker) || IsQuestSettlementPending(victim))
        {
            return false;
        }

        bool offhand = HasOffhandWeapon(attacker);
        if (!c.HasReadyExtraAttacks && !c.IsAttackReady(WeaponAttackType.BaseAttack)
            && !(offhand && c.IsAttackReady(WeaponAttackType.OffAttack)))
        {
            return false;
        }

        AttackCheckResult result = CanAutoAttackTarget(attacker, victim);
        var player = attacker as Player;
        switch (result)
        {
            case AttackCheckResult.Ok:
                bool extraAttackPerformed = false;
                if (c.HasReadyExtraAttacks)
                {
                    c.ClearExtraAttacksReady();
                    Unit? extraVictim = c.Victim;
                    c.LockExtraAttacks();
                    try
                    {
                        while (c.HasPendingExtraAttacks && extraVictim is not null)
                        {
                            AttackerStateUpdate(attacker, extraVictim, WeaponAttackType.BaseAttack);
                            c.ConsumeExtraAttack();
                            extraAttackPerformed = true;
                        }
                    }
                    finally
                    {
                        c.UnlockExtraAttacks();
                    }
                    c.ResetAttackTimer(WeaponAttackType.BaseAttack);
                }

                if (player is not null)
                {
                    TogglePlayerPvpFlagOnAttackVictim(player, victim);
                }

                if (!extraAttackPerformed && c.IsAttackReady(WeaponAttackType.BaseAttack))
                {
                    // never swing both hands at once: push the off-hand 200 ms
                    if (offhand && c.GetAttackTimer(WeaponAttackType.OffAttack) < CombatConstants.AttackDisplayDelayMs)
                    {
                        c.SetAttackTimer(WeaponAttackType.OffAttack, CombatConstants.AttackDisplayDelayMs);
                    }

                    AttackerStateUpdate(attacker, victim, WeaponAttackType.BaseAttack);
                    c.ResetAttackTimer(WeaponAttackType.BaseAttack);
                }

                if (offhand && c.IsAttackReady(WeaponAttackType.OffAttack) && c.Victim is not null)
                {
                    if (c.GetAttackTimer(WeaponAttackType.BaseAttack) < CombatConstants.AttackDisplayDelayMs)
                    {
                        c.SetAttackTimer(WeaponAttackType.BaseAttack, CombatConstants.AttackDisplayDelayMs);
                    }

                    AttackerStateUpdate(attacker, victim, WeaponAttackType.OffAttack);
                    c.ResetAttackTimer(WeaponAttackType.OffAttack);
                }

                break;

            case AttackCheckResult.NotInRange:
                DelayAutoAttacks(attacker, offhand);
                if (player is not null && c.LastSwingError != AttackCheckResult.NotInRange)
                {
                    player.Session.Send(WorldOpcode.SmsgAttackswingNotinrange, []);
                }

                break;

            case AttackCheckResult.BadFacing:
                DelayAutoAttacks(attacker, offhand);
                if (player is not null && c.LastSwingError != AttackCheckResult.BadFacing)
                {
                    player.Session.Send(WorldOpcode.SmsgAttackswingBadfacing, []);
                }

                break;

            case AttackCheckResult.CantAttack:
                DelayAutoAttacks(attacker, offhand);
                break;

            case AttackCheckResult.Dead:
                AttackStop(attacker, targetSwitch: true);
                if (player is not null && c.LastSwingError != AttackCheckResult.Dead)
                {
                    player.Session.Send(WorldOpcode.SmsgAttackswingDeadtarget, []);
                }

                break;

            case AttackCheckResult.FriendlyTarget:
                AttackStop(attacker, targetSwitch: true);
                if (player is not null && c.LastSwingError != AttackCheckResult.FriendlyTarget)
                {
                    player.Session.Send(WorldOpcode.SmsgAttackswingCantAttack, []);
                }

                break;
        }

        c.LastSwingError = result;
        return result == AttackCheckResult.Ok;
    }

    /// <summary>vmangos Unit::DelayAutoAttacks: a ready hand waits max(batch delay, 100 ms) before retrying.</summary>
    private static void DelayAutoAttacks(Unit attacker, bool offhand)
    {
        UnitCombat c = attacker.Combat;
        if (c.IsAttackReady(WeaponAttackType.BaseAttack))
        {
            c.SetAttackTimer(WeaponAttackType.BaseAttack, CombatConstants.AutoAttackRetryDelayMs);
        }

        if (offhand && c.IsAttackReady(WeaponAttackType.OffAttack))
        {
            c.SetAttackTimer(WeaponAttackType.OffAttack, CombatConstants.AutoAttackRetryDelayMs);
        }
    }

    /// <summary>
    /// A white swing's hit table result is known, before SMSG_ATTACKERSTATEUPDATE and the damage (vmangos runs
    /// <c>ProcDamageAndSpell</c> at this point, Unit.cpp:2260-2271): the reactive abilities hang off it.
    /// </summary>
    public event Action<MeleeDamageInfo>? MeleeSwingResolved;

    /// <summary>Weapon item procs run after the white hit's damage (vmangos Unit.cpp:1755-1777).</summary>
    public event Action<MeleeDamageInfo>? MeleeWeaponHitDealt;

    /// <summary>
    /// One white swing (vmangos Unit::AttackerStateUpdate): roll and calculate the damage,
    /// send SMSG_ATTACKERSTATEUPDATE to the set (before the damage, so the client can still
    /// resolve a victim that dies), deal it, then the victim's AI reaction. A unit that is casting a
    /// non-melee spell does not swing (<see cref="CombatOptions.MeleeCastingBlocksSwing"/>), and a queued
    /// next-swing spell is cast by the main-hand swing instead of the white hit
    /// (<see cref="IMeleeSpellHooks"/>, Unit.cpp:2239-2257); both return null.
    /// </summary>
    public MeleeDamageInfo? AttackerStateUpdate(Unit attacker, Unit victim, WeaponAttackType attackType)
    {
        if (IsQuestSettlementPending(attacker) || IsQuestSettlementPending(victim)
            || !IsAliveState(victim) || attackType == WeaponAttackType.RangedAttack)
        {
            return null;
        }

        CombatEnvironment environment = CombatEnvironment.For(_world);
        if (environment.MeleeSpells is { } spells)
        {
            if (environment.Options.MeleeCastingBlocksSwing && spells.IsNonMeleeSpellCasted(attacker))
            {
                return null;
            }

            if (attackType == WeaponAttackType.BaseAttack && spells.TryCastQueuedSwingSpell(attacker, victim))
            {
                return null;
            }
        }

        MeleeDamageInfo info = CalculateMeleeDamage(attacker, victim, attackType);
        MeleeSwingResolved?.Invoke(info);   // vmangos ProcDamageAndSpell, before the packet and the damage (Unit.cpp:2260-2271)
        SubDamage[] sub = [new SubDamage(0, info.TotalDamage, 0, 0)]; // physical: first school index 0, no absorb/resist without auras
        CombatPackets.SendToSet(attacker, WorldOpcode.SmsgAttackerstateupdate,
            CombatPackets.AttackerStateUpdate(info.HitInfo, attacker.Guid, victim.Guid, info.TotalDamage, sub, info.TargetState, info.Blocked));

        DealMeleeDamage(info);
        MeleeWeaponHitDealt?.Invoke(info);
        PlayerCombatSkills.OnMeleeResolved(attacker, victim, attackType, info.Outcome);
        MeleeSwingFinished?.Invoke(attacker, victim); // vmangos Unit.cpp:2285: the swing cancels ATTACKING auras
        return info;
    }

    /// <summary>Build the hit-table input for a white swing from the two units and the hooks.</summary>
    public MeleeRollInput BuildRollInput(Unit attacker, Unit victim, WeaponAttackType attackType)
    {
        CombatHooks hooks = Hooks;
        bool victimIsPlayer = victim is Player;
        var creatureVictim = victim as ICombatCreature;
        var creatureAttacker = attacker as ICombatCreature;
        bool victimCanDefend = (victim.UnitFlags & UnitFlags.Stunned) == 0; // UNIT_STATE_STUNNED proxy; casting arrives with spells

        float dodge = 0f, parry = 0f, block = 0f;
        if (victimCanDefend)
        {
            if (victim is Player pv)
            {
                dodge = pv.GetFloat(UpdateFields.PlayerDodgePercentage);
                parry = PlayerCanParry(pv) ? pv.GetFloat(UpdateFields.PlayerParryPercentage) : 0f;
                bool unarmedSheath = pv.GetByte(UpdateFields.UnitFieldBytes2, 0) == 0; // SHEATH_STATE_UNARMED
                block = !unarmedSheath && PlayerCanBlock(pv) ? pv.GetFloat(UpdateFields.PlayerBlockPercentage) : 0f;
            }
            else
            {
                dodge = 5f;
                parry = 5f;
                block = 5f;
            }
        }

        return new MeleeRollInput
        {
            AttackType = attackType,
            VictimIsPlayer = victimIsPlayer,
            AttackerIsCreature = attacker is not Player,
            AttackerIsPlayerControlled = attacker is Player,
            VictimIsPlayerControlled = victimIsPlayer,
            VictimEvading = creatureVictim is { IsInEvadeMode: true },
            VictimStanding = IsStandingUp(victim),
            FromBehind = !HasInArc(victim, attacker, CombatConstants.DefaultArc),
            AttackerCanCrush = creatureAttacker?.CanCrush ?? true,
            VictimCreatureCanParry = creatureVictim?.CanParry ?? true,
            VictimCreatureCanBlock = creatureVictim?.CanBlock ?? true,
            VictimLevel = victim.Level,
            AttackerMaxSkill = MeleeHitTable.SkillMaxForLevel(attacker, victim),
            VictimMaxSkill = MeleeHitTable.SkillMaxForLevel(victim, attacker),
            AttackerWeaponSkill = WeaponSkill(attacker, attackType, victim),
            VictimDefenseSkill = hooks.GetDefenseSkill(victim, attacker),
            DualWield = HasOffhandWeapon(attacker),
            HitBonus = 0f,
            BaseCritChance = attacker is Player pa ? pa.GetFloat(UpdateFields.PlayerCritPercentage) : 5f,
            DodgeChance = dodge,
            ParryChance = parry,
            BlockChance = block,
        };
    }

    /// <summary>
    /// vmangos Unit::CalculateMeleeDamage for a white swing: weapon damage, armor, the hit
    /// table, then the outcome's adjustment (crit ×2, crushing +50%, glancing, block value,
    /// dodge/parry/miss/evade zero) and the hit-info flags.
    /// </summary>
    public MeleeDamageInfo CalculateMeleeDamage(Unit attacker, Unit victim, WeaponAttackType attackType)
    {
        var info = new MeleeDamageInfo { Attacker = attacker, Target = victim, AttackType = attackType };

        // SetDamageIndependentHitInfoFlags
        HitInfo hit = attackType == WeaponAttackType.OffAttack ? HitInfo.LeftSwing : HitInfo.None;
        if (attacker is Player && victim is Player)
        {
            hit |= HitInfo.Pvp;
        }

        uint damage = Dither(CalculateDamage(attacker, attackType));
        uint afterArmor = Dither(MeleeHitTable.ApplyArmor(damage, victim.GetInt32(UpdateFields.UnitFieldResistances), attacker.Level));
        uint clean = damage > afterArmor ? damage - afterArmor : 0;
        damage = afterArmor;

        MeleeRollInput input = BuildRollInput(attacker, victim, attackType);
        MeleeRollResult roll = MeleeHitTable.Roll(input, Random);
        hit |= roll.HitInfo;
        info.Outcome = roll.Outcome;

        VictimState state = VictimState.Normal;
        uint blocked = 0;
        switch (roll.Outcome)
        {
            case MeleeHitOutcome.Evade:
                state = VictimState.Evades;
                damage = 0;
                clean = 0;
                break;
            case MeleeHitOutcome.Miss:
                state = VictimState.Unaffected;
                damage = 0;
                clean = 0;
                break;
            case MeleeHitOutcome.Crit:
                damage = (uint)(damage * (CombatConstants.CritDamagePercent / 100.0f));
                break;
            case MeleeHitOutcome.Parry:
                state = VictimState.Parry;
                clean += damage;
                damage = 0;
                break;
            case MeleeHitOutcome.Dodge:
                state = VictimState.Dodge;
                clean += damage;
                damage = 0;
                break;
            case MeleeHitOutcome.Block:
                blocked = ShieldBlockValue(victim);
                if (blocked >= damage)
                {
                    state = VictimState.Blocks;
                    blocked = damage;
                }

                damage -= blocked;
                clean += blocked;
                break;
            case MeleeHitOutcome.Glancing:
            {
                (float low, float high) = MeleeHitTable.GlancingRange(input.VictimDefenseSkill, input.AttackerWeaponSkill, attacker.Class);
                float reduce = Random.NextFloat(low, high);
                clean += (uint)((1.0f - reduce) * damage);
                damage = (uint)(reduce * damage);
                break;
            }

            case MeleeHitOutcome.Crushing:
                damage += damage / 2;
                break;
        }

        // SetDamageDependentHitInfoFlags (no absorb/resist for physical swings without auras)
        uint total = damage + clean;
        if (blocked > 0)
        {
            hit |= HitInfo.RolledBlock | HitInfo.Block;
        }

        if (state is VictimState.Dodge or VictimState.Parry or VictimState.Blocks or VictimState.Deflects or VictimState.Evades
            || (state == VictimState.Normal && (damage > 0 || blocked > 0)))
        {
            hit |= HitInfo.AffectsVictim;
        }

        if (victim is Player)
        {
            uint spurt = state is VictimState.Dodge or VictimState.Parry ? total : damage;
            if (spurt > victim.MaxHealth * 20 / 100)
            {
                hit |= HitInfo.BloodSpurt;
            }
        }

        info.HitInfo = hit;
        info.TargetState = state;
        info.TotalDamage = damage;
        info.CleanDamage = clean;
        info.Blocked = blocked;
        return info;
    }

    /// <summary>
    /// vmangos Unit::CalculateDamage (not normalized): frand(min, max) of the hand's damage
    /// fields, negatives clamped, swapped if reversed, max 0 → 5.
    /// </summary>
    public float CalculateDamage(Unit unit, WeaponAttackType attackType)
    {
        (int minIndex, int maxIndex) = attackType switch
        {
            WeaponAttackType.OffAttack => (UpdateFields.UnitFieldMinoffhanddamage, UpdateFields.UnitFieldMaxoffhanddamage),
            WeaponAttackType.RangedAttack => (UpdateFields.UnitFieldMinrangeddamage, UpdateFields.UnitFieldMaxrangeddamage),
            _ => (UpdateFields.UnitFieldMindamage, UpdateFields.UnitFieldMaxdamage),
        };

        float min = Math.Max(0f, unit.GetFloat(minIndex));
        float max = Math.Max(0f, unit.GetFloat(maxIndex));
        if (min > max)
        {
            (min, max) = (max, min);
        }

        if (max == 0f)
        {
            max = CombatConstants.FallbackMaxDamage;
        }

        return Random.NextFloat(min, max);
    }

    /// <summary>
    /// vmangos Unit::DealMeleeDamage: parry haste on the victim (a parried swing pulls its own
    /// timer to 20% when between 20% and 60% of its attack time, or back by 40% above that),
    /// then DealDamage.
    /// </summary>
    private void DealMeleeDamage(MeleeDamageInfo info)
    {
        Unit victim = info.Target;
        if (!IsAliveState(victim) || (victim.UnitFlags & UnitFlags.TaxiFlight) != 0 || victim is ICombatCreature { IsInEvadeMode: true })
        {
            return;
        }

        if (info.TargetState == VictimState.Parry)
        {
            UnitCombat vc = victim.Combat;
            float offTime = vc.GetAttackTimer(WeaponAttackType.OffAttack);
            float baseTime = vc.GetAttackTimer(WeaponAttackType.BaseAttack);
            WeaponAttackType hand = HasOffhandWeapon(victim) && offTime < baseTime ? WeaponAttackType.OffAttack : WeaponAttackType.BaseAttack;
            float timer = hand == WeaponAttackType.OffAttack ? offTime : baseTime;
            float percent20 = vc.GetAttackTime(hand) * 0.20f;
            float percent60 = 3.0f * percent20;
            if (timer > percent20 && timer <= percent60)
            {
                vc.SetAttackTimer(hand, (uint)percent20);
            }
            else if (timer > percent60)
            {
                timer -= 2.0f * percent20;
                vc.SetAttackTimer(hand, (uint)Math.Max(0f, timer));
            }
        }

        DealDamage(info.Attacker, victim, info.TotalDamage, info.Outcome, info.CleanDamage, direct: true);
    }

    // --- damage, combat state, kill (vmangos Unit::DealDamage, Kill) ---------------------

    /// <summary>
    /// Apply damage (vmangos Unit::DealDamage). A sitting player victim stands up; both enter
    /// combat; the attacker earns rage; lethal damage kills; otherwise health drops, a player
    /// attacker without a victim starts attacking, non-player victims gain threat and player
    /// victims earn rage. <paramref name="outcome"/> / <paramref name="cleanDamage"/> carry the
    /// dodge/parry rage case. <paramref name="startsCombat"/> false skips the combat link and the auto-attack start. Returns the damage dealt. Public for the spells area (direct
    /// spell damage uses <paramref name="direct"/> = false for DoTs, and
    /// <paramref name="meleeDamage"/> = false for every spell). vmangos Unit.cpp DealDamage
    /// distinguishes DIRECT_DAMAGE from SPELL_DIRECT_DAMAGE: only weapon damage rewards
    /// outgoing rage, and its auto-start Attack call enables melee only for DIRECT_DAMAGE.
    /// </summary>
    public uint DealDamage(Unit attacker, Unit victim, uint damage, MeleeHitOutcome outcome = MeleeHitOutcome.Normal, uint cleanDamage = 0, bool direct = true, bool meleeDamage = true, bool startsCombat = true, bool durabilityLoss = true, SpellInfo? spell = null)
    {
        if (IsQuestSettlementPending(attacker) || IsQuestSettlementPending(victim) || !IsAliveState(victim))
        {
            return 0;
        }

        if (victim is Player standing && !IsStandingUp(standing))
        {
            standing.SetStandState(StandState.Stand);
        }

        bool enterCombat = !ReferenceEquals(attacker, victim);
        bool combatLink = enterCombat && startsCombat; // false: a hunter trap's hit on a player (vmangos Spell.cpp:1650)
        if (damage == 0)
        {
            if (outcome is MeleeHitOutcome.Parry or MeleeHitOutcome.Dodge
                && cleanDamage > 0 && direct && meleeDamage && enterCombat && attacker is Player { PowerType: PowerType.Rage } ragePlayer)
            {
                RewardRage(ragePlayer, (uint)(cleanDamage * 0.75f), attacker: true, CombatEnvironment.For(_world));
            }

            if (combatLink)
            {
                SetInCombatWithAggressor(victim, attacker);
                SetInCombatWithVictim(attacker, victim);
            }

            if (enterCombat)
            {
                if (victim is not Player)
                {
                    // A missed creature still aggroes: its AI's AttackStart adds the attacker
                    // with 0 threat (vmangos CreatureAI::AttackedBy → AttackStart → AddThreat).
                    victim.Combat.Threat.AddThreat(attacker, 0f);
                }
            }

            AttackedBy(victim, attacker);
            return 0;
        }

        // Duels end at 1 hp (MapCombat.Duel.cs; vmangos Unit.cpp:762-779).
        bool duelEnded = ApplyDuelClamp(attacker, victim, ref damage);

        if (combatLink)
        {
            SetInCombatWithAggressor(victim, attacker);
            SetInCombatWithVictim(attacker, victim);
        }

        if (direct && meleeDamage && enterCombat && attacker is Player { PowerType: PowerType.Rage } rager)
        {
            RewardRage(rager, damage, attacker: true, CombatEnvironment.For(_world));
        }

        if (victim.Health <= damage)
        {
            Kill(attacker, victim, durabilityLoss, spell);
            if (duelEnded)
            {
                AfterLethalDuelDamage((Player)victim); // Unit.cpp:825-843
            }

            return damage;
        }

        victim.Health -= damage;

        if (direct && combatLink)
        {
            if (attacker.Combat.Victim is null && attacker is Player)
            {
                Attack(attacker, victim, melee: meleeDamage);
            }
        }

        if (victim is not Player)
        {
            if (enterCombat)
            {
                victim.Combat.Threat.AddThreat(attacker, damage);
            }
        }
        else if (enterCombat && victim.PowerType == PowerType.Rage)
        {
            RewardRage((Player)victim, damage, attacker: false, CombatEnvironment.For(_world));
        }

        ApplyHitDurability(attacker, victim);
        DamageDealt?.Invoke(attacker, victim, damage, direct, meleeDamage);
        AttackedBy(victim, attacker);
        if (duelEnded)
        {
            AfterClampedDuelDamage((Player)victim); // Unit.cpp:954-969
        }

        return damage;
    }

    /// <summary>vmangos Unit::AttackedBy: the creature AI reacts (pets arrive with their area).</summary>
    private static void AttackedBy(Unit victim, Unit attacker)
    {
        if (victim is ICombatCreature creature && IsAliveState(victim))
        {
            creature.OnAttackedBy(attacker);
        }
    }

    /// <summary>
    /// vmangos Player::RewardRage (Kalgan's formula): conversion = 0.0091107836·L² +
    /// 3.225598133·L + 4.2652911; dealing earns damage/conversion × 7.5, taking ×2.5; the power
    /// field holds rage × 10. This overload uses the retail rates and knows no auras; the world's
    /// rates and Berserker Rage come with <see cref="CombatEnvironment"/>.
    /// </summary>
    public static void RewardRage(Player player, uint damage, bool attacker) => RewardRage(player, damage, attacker, CombatEnvironment.Default);

    /// <summary>
    /// <see cref="RewardRage(Player, uint, bool)"/> with the world's power environment: Rate.Rage.Income, and
    /// Berserker Rage (18499, effect 0) multiplies rage taken by 1.3 (<see cref="PowerRules.RageFromDamage"/>).
    /// </summary>
    public static void RewardRage(Player player, uint damage, bool attacker, CombatEnvironment power)
    {
        ArgumentNullException.ThrowIfNull(power);
        if (IsQuestSettlementPending(player))
        {
            return;
        }

        bool berserkerRage = !attacker && power.HasAura(player, PowerRules.BerserkerRageSpell, 0);
        uint add = PowerRules.RageFromDamage(player.Level, damage, attacker, berserkerRage, power.Options.RateRageIncome);
        ModifyPower(player, PowerType.Rage, (int)add);
    }

    /// <summary>
    /// vmangos Unit::Kill: SMSG_PARTYKILLLOG to a player killer, the kill hook (XP/loot), health
    /// 0 and JUST_DIED (combat stop, threat cleared, player rooted, power emptied), removal from
    /// every threat list, the PvP-death mark; creatures go straight to CORPSE.
    /// </summary>
    public void Kill(Unit? killer, Unit victim, bool durabilityLoss = true, SpellInfo? spell = null)
    {
        if (IsQuestSettlementPending(killer) || IsQuestSettlementPending(victim) || !IsAliveState(victim))
        {
            return;
        }

        // Wear eligibility follows the source's owner/charmer lookup. Existing kill credit
        // and PvP corpse attribution retain their separate playerTap behavior.
        Player? durabilityPlayerTap = killer?.GetCharmerOrOwnerPlayerOrSelf();
        var playerTap = killer as Player;
        if (playerTap is not null && !ReferenceEquals(playerTap, victim))
        {
            playerTap.Session.Send(WorldOpcode.SmsgPartykilllog, CombatPackets.PartyKillLog(playerTap.Guid, victim.Guid));
        }

        if (killer is not null && !ReferenceEquals(killer, victim))
        {
            Hooks.OnKill(killer, victim);
        }

        victim.Health = 0;
        SetDeathState(victim, DeathState.JustDied);

        foreach (Unit holder in victim.Combat.ThreatenedByInternal.ToArray())
        {
            holder.Combat.Threat.Remove(victim);
        }

        if (victim is Player playerVictim)
        {
            playerVictim.Combat.PvpDeath = playerTap is not null;
            ApplyDeathDurability(playerVictim, durabilityLoss, durabilityPlayerTap, spell);
        }
        else
        {
            if (victim is ICombatCreature creature)
            {
                creature.OnJustDied(killer);
            }

            // vmangos Creature::Update turns JUST_DIED into CORPSE on its next update.
            victim.Combat.DeathState = DeathState.Corpse;
        }

        UnitKilled?.Invoke(killer, victim);
    }

    /// <summary>vmangos Unit::SetDeathState / Player::SetDeathState for the states combat drives.</summary>
    private void SetDeathState(Unit unit, DeathState state)
    {
        if (state is not (DeathState.Alive or DeathState.JustAlived))
        {
            CombatStop(unit);
            unit.Combat.ResetExtraAttacks();
            if (unit.Combat.HasThreatList)
            {
                unit.Combat.Threat.Clear();
            }
        }

        unit.Combat.DeathState = state;
        if (unit is Player revived && state is DeathState.Alive or DeathState.JustAlived)
        {
            // vmangos Player.cpp:1566-1570: any resurrection clears the previous offer.
            revived.SetUInt32(UpdateFields.PlayerSelfResSpell, 0);
        }

        if (state == DeathState.JustDied)
        {
            if (unit is Player player)
            {
                Death.PlayerResurrection.Clear(player);
                player.SetRooted(true);
            }

            SetPower(unit, unit.PowerType, 0);
        }
    }

    /// <summary>vmangos Unit::SetInCombatState: UNIT_FLAG_IN_COMBAT plus the PvP linger timer (rounded up to the 1.2 s check).</summary>
    public void SetInCombatState(Unit unit, uint combatTimer)
    {
        if (IsQuestSettlementPending(unit) || !IsAliveState(unit))
        {
            return;
        }

        UnitCombat c = unit.Combat;
        if (combatTimer > 0 && c.CombatTimer < combatTimer)
        {
            uint add = combatTimer - c.CombatTimer;
            uint interval = CombatConstants.CombatCheckIntervalMs;
            c.CombatTimer += (add + interval - 1) / interval * interval; // BatchifyTimer (vmangos Util.h)
        }

        unit.UnitFlags |= UnitFlags.InCombat;
        Track(unit);
    }

    /// <summary>vmangos Unit::SetInCombatWithAggressor: the victim's PvP pulse and combat state (players linger 5.5 s).</summary>
    private void SetInCombatWithAggressor(Unit victim, Unit aggressor)
    {
        // Duel opponents do not pulse each other (vmangos Unit.cpp:5973, !IsInDuelWith).
        if ((aggressor.UnitFlags & UnitFlags.Pvp) != 0 && victim is Player pv && aggressor is Player pa && !ReferenceEquals(pv, pa)
            && !DuelRules.IsInDuelWith(pv, pa))
        {
            pv.Combat.InPvpCombat = true;
            UpdatePvp(pv, true);
        }

        SetInCombatState(victim, victim is Player ? CombatConstants.PvpCombatTimerMs : 0);
    }

    /// <summary>vmangos Unit::SetInCombatWithVictim: PvP flag on attack, then combat (5.5 s when the victim is a player).</summary>
    private void SetInCombatWithVictim(Unit attacker, Unit victim)
    {
        if (attacker is Player player)
        {
            TogglePlayerPvpFlagOnAttackVictim(player, victim);
        }

        SetInCombatState(attacker, victim is Player ? CombatConstants.PvpCombatTimerMs : 0);
    }

    /// <summary>vmangos Unit::ClearInCombat.</summary>
    public static void ClearInCombat(Unit unit)
    {
        unit.Combat.CombatTimer = 0;
        unit.UnitFlags &= ~(UnitFlags.InCombat | UnitFlags.PetInCombat);
        unit.Combat.InPvpCombat = false;
    }

    /// <summary>
    /// vmangos Unit::Update combat timer: every 1.2 s check, a player (UsesPvPCombatTimer) in
    /// combat that no creature holds on its threat list leaves combat.
    /// </summary>
    private static void UpdateCombatTimer(Unit unit, uint diff)
    {
        UnitCombat c = unit.Combat;
        if (c.CombatTimer <= diff)
        {
            uint interval = CombatConstants.CombatCheckIntervalMs;
            c.CombatTimer = interval - Math.Min(diff - c.CombatTimer, interval);
            if (c.IsInCombat && unit is Player && c.ThreatenedByInternal.Count == 0)
            {
                ClearInCombat(unit);
            }
        }
        else
        {
            c.CombatTimer -= diff;
        }
    }
}

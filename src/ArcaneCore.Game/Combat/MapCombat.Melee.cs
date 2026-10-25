using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

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

    /// <summary>Damage removed by physical school and mana shields after armor and block.</summary>
    public uint Absorbed { get; set; }
}

public sealed partial class MapCombat
{
    /// <summary>The world's spell system, installed by SpellFeature for white-swing absorbs and damage interrupts.</summary>
    public SpellSystem? SpellMitigation { get; set; }

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

        // "player cannot attack in mount state" (vmangos Unit::Attack, Unit.cpp:4486-4488)
        if (attacker is Player && Locomotion.UnitSpeed.IsMounted(attacker))
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
        c.LastVictimGuid = victim.Guid;
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
        if (!targetSwitch && attacker is ICombatCreature me)
        {
            me.ResetDamageTakenOrigin(); // vmangos Unit::AttackStop: reset only at real combat stop (Unit.cpp:4607-4611)
        }

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

        // vmangos Player::CanAutoAttackTarget (Player.cpp:1103-1109): a player's target is re-validated every swing, so a charm or
        // faction change that made it friendly (or otherwise not attackable) stops the attack.
        if (attacker is Player && !Hooks.CanAttack(attacker, victim))
        {
            return AttackCheckResult.FriendlyTarget;
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
        if (((dx * dx) + (dy * dy)) > CombatConstants.NoFacingChecksDistance * CombatConstants.NoFacingChecksDistance
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

    /// <summary>
    /// vmangos WorldObject::CanReachWithMeleeSpellAttack (Object.cpp:1874-1888) with GetCombatReachToTarget(ability = true): the same reach
    /// as <see cref="CanReachWithMeleeAutoAttack"/>, but 2D only ("melee spells ignore Z-axis checks"). Limit: the ability leeway tests
    /// both units' flag-based speed above 70 % of run speed; this uses the auto-attack test (both moving, neither walking).
    /// </summary>
    public static bool CanReachWithMeleeSpellAttack(Unit attacker, Unit victim)
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
        return (dx * dx) + (dy * dy) < reach * reach;
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

        bool extraReady = c.HasReadyExtraAttacks;
        bool baseReady = c.IsAttackReady(WeaponAttackType.BaseAttack);
        bool offTimerReady = c.IsAttackReady(WeaponAttackType.OffAttack);
        if (!extraReady && !baseReady && !offTimerReady)
        {
            return false;
        }

        bool offhand = HasOffhandWeapon(attacker);
        if (!extraReady && !baseReady && !offhand)
        {
            return false;
        }

        // ranged (autorepeat lane): vmangos returns here, before any swing timer is acted on, while a non-melee spell is cast
        // (Unit::UpdateMeleeAttackingState, Unit.cpp:415-421), so the swing happens as soon as the cast ends. The auto-repeat
        // slot counts as casting for the hook. Combat:CastingConsumesSwing keeps the older behaviour (AttackerStateUpdate below).
        CombatEnvironment gate = Environment;
        if (!gate.Options.CastingConsumesSwing && gate.Options.MeleeCastingBlocksSwing
            && gate.MeleeSpells is { } castGate && castGate.IsNonMeleeSpellCasted(attacker))
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

        CombatEnvironment environment = Environment;
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

        // The swing's procs, its damage, the kill and the weapon procs it causes are one proc event (SpellSystem.BeginProcEvent).
        using SpellSystem.ProcEventScope swingEvent = SpellMitigation?.BeginProcEvent() ?? default;
        MeleeDamageInfo info = CalculateMeleeDamage(attacker, victim, attackType);
        // vmangos ProcDamageAndSpell runs ProcSkillsAndReactives first (SpellCaster.cpp:271-283), before the damage, so a killing swing still rolls the
        // victim's defense skill-up.
        PlayerCombatSkills.OnMeleeResolved(attacker, victim, attackType, info.Outcome, environment.ShapeshiftForms, environment.WorldBossLevelDiff);
        MeleeSwingResolved?.Invoke(info);   // vmangos ProcDamageAndSpell, before the packet and the damage (Unit.cpp:2260-2271)
        SpellMitigation?.OnMeleeSwingResolved(info); // the aura procs of that call (Spells/Procs, docs/areas/procs.md)
        SubDamage[] sub = [new SubDamage(0, info.TotalDamage, info.Absorbed, 0)]; // vmangos Unit.cpp:1510-1565; physical school after block/absorb
        CombatPackets.SendToSet(attacker, WorldOpcode.SmsgAttackerstateupdate,
            CombatPackets.AttackerStateUpdate(info.HitInfo, attacker.Guid, victim.Guid, info.TotalDamage, sub, info.TargetState, info.Blocked));

        DealMeleeDamage(info);
        MeleeWeaponHitDealt?.Invoke(info);
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
        // vmangos Unit::GetUnitDodgeChance / GetUnitParryChance / GetUnitBlockChance (Unit.cpp:2474-2550): nothing while stunned
        // (UNIT_STATE_STUNNED proxy) or casting a non-melee spell (IsNonMeleeSpellCasted(false)), and a totem never defends.
        bool victimCanDefend = (victim.UnitFlags & UnitFlags.Stunned) == 0
            && Environment.MeleeSpells?.IsNonMeleeSpellCasted(victim) != true
            && !IsTotem(victim);

        SpellSystem? auras = SpellMitigation;
        bool ranged = attackType == WeaponAttackType.RangedAttack;
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
                // A creature: 5% plus its MOD_DODGE/PARRY/BLOCK_PERCENT auras, never negative (Unit.cpp:2484-2486, 2504-2510, 2545-2549).
                dodge = Math.Max(0f, 5f + (auras?.GetTotalAuraModifier(victim, AuraType.ModDodgePercent) ?? 0));
                parry = Math.Max(0f, 5f + (auras?.GetTotalAuraModifier(victim, AuraType.ModParryPercent) ?? 0));
                block = Math.Max(0f, 5f + (auras?.GetTotalAuraModifier(victim, AuraType.ModBlockPercent) ?? 0));
            }
        }

        // vmangos Unit::GetUnitCriticalChance (Unit.cpp:2552-2577): a player's crit field for the hand (the ranged field for a ranged attack),
        // anything else 5 plus its MOD_CRIT_PERCENT auras.
        float baseCrit = attacker is Player pa
            ? pa.GetFloat(ranged ? UpdateFields.PlayerRangedCritPercentage : UpdateFields.PlayerCritPercentage)
            : 5f + (auras?.GetTotalAuraModifier(attacker, AuraType.ModCritPercent) ?? 0);

        return new MeleeRollInput
        {
            AttackType = attackType,
            VictimIsPlayer = victimIsPlayer,
            AttackerIsCreature = attacker is not Player,
            AttackerIsPlayerControlled = attacker.IsCharmerOrOwnerPlayerOrPlayerItself, // vmangos SpellCaster.cpp:548: a pet or charmed unit counts as its player
            VictimIsPlayerControlled = victim.IsCharmerOrOwnerPlayerOrPlayerItself,
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
            HitBonus = auras is null ? 0f : WeaponAuraModifiers.Get(auras, attacker, attackType, AuraType.ModHitChance), // SpellCaster.cpp:389-390
            VictimAttackerHitBonus = auras?.GetTotalAuraModifier(victim, ranged ? AuraType.ModAttackerRangedHitChance : AuraType.ModAttackerMeleeHitChance) ?? 0, // :414-418
            VictimAttackerCritBonus = auras?.GetTotalAuraModifier(victim, ranged ? AuraType.ModAttackerRangedCritChance : AuraType.ModAttackerMeleeCritChance) ?? 0, // Unit.cpp:2577-2581
            BaseCritChance = baseCrit,
            DodgeChance = dodge,
            ParryChance = parry,
            BlockChance = block,
        };
    }

    /// <summary>vmangos Creature::IsTotem (a summoned totem) or a creature of type TOTEM (GetUnitParryChance tests the type).</summary>
    private static bool IsTotem(Unit unit) => Totems.TotemQuery.IsTotem(unit) || Spells.Rules.CrowdControl.CcState.IsTotem(unit);

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
        if (attacker.IsCharmerOrOwnerPlayerOrPlayerItself && victim.IsCharmerOrOwnerPlayerOrPlayerItself) // Unit.cpp:1606-1613
        {
            hit |= HitInfo.Pvp;
        }

        // vmangos Unit::CalculateMeleeDamage (Unit.cpp:1362-1367): the weapon roll, the done bonuses (creature type and attack power versus,
        // damage done versus), dithered, then the victim's taken modifiers, dithered again.
        float rolled = CalculateDamage(attacker, attackType);
        uint damage;
        if (SpellMitigation is { } bonuses)
        {
            rolled = MeleeDamageBonus.Done(bonuses, attacker, victim, rolled, attackType);
            damage = Dither(MeleeDamageBonus.Taken(bonuses, attacker, victim, Dither(rolled), attackType, SpellSchoolMasks.Of(SpellSchool.Normal)));
        }
        else
        {
            damage = Dither(rolled);
        }

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
            {
                // vmangos Unit.cpp:1425-1436: (200 + the attacker's MOD_CRIT_PERCENT_VERSUS for the victim's creature type + physical MOD_CRIT_DAMAGE_BONUS) percent.
                uint typeMask = victim.CreatureTypeMask();
                int versus = SpellMitigation?.GetTotalAuraModifier(attacker, AuraType.ModCritPercentVersus, a => ((uint)a.MiscValue & typeMask) != 0) ?? 0;
                int critBonus = SpellMitigation?.GetTotalAuraModifier(attacker, AuraType.ModCritDamageBonus, static a => (a.MiscValue & 1) != 0) ?? 0;
                damage = (uint)(damage * (CombatConstants.CritDamagePercent + versus + critBonus) / 100.0f);
                break;
            }
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

        // vmangos Unit.cpp:1510-1565,1920-2083: block first, then school/mana absorbs.
        uint absorbed = damage > 0
            ? SpellMitigation?.AbsorbDamage(attacker, victim, SpellSchoolMasks.Of(SpellSchool.Normal), damage, null) ?? 0
            : 0;
        damage -= absorbed;
        uint total = damage + absorbed + clean;
        if (blocked > 0)
        {
            hit |= HitInfo.RolledBlock | HitInfo.Block;
        }

        if (absorbed > 0)
        {
            hit |= HitInfo.Absorb;
        }

        if (state is VictimState.Dodge or VictimState.Parry or VictimState.Blocks or VictimState.Deflects or VictimState.Evades
            || (state == VictimState.Normal && (damage > 0 || blocked > 0)))
        {
            hit |= HitInfo.AffectsVictim;
        }

        if (victim is Player)
        {
            uint spurt = state is VictimState.Dodge or VictimState.Parry ? total : damage + absorbed;
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
        info.Absorbed = absorbed;
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

        if (info.TargetState == VictimState.Parry && victim is not Creatures.Creature { ParryHastens: false }) // cmangos NO_PARRY_HASTEN
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

        uint dealt = DealDamage(info.Attacker, victim, info.TotalDamage, info.Outcome, info.CleanDamage, direct: true);
        RollHitDoneDurability(info, dealt);
        RollDefenseDurability(info);
        // DealDamage publishes positive hits through DamageDealt. Fully absorbed hits have no
        // health loss and need the zero-damage spell interruption path (vmangos Unit.cpp:733-746).
        if (dealt == 0 && info.Absorbed > 0)
        {
            SpellMitigation?.OnDamageTaken(victim, info.Attacker, 0, periodic: false, absorbed: info.Absorbed);
        }
    }

    // --- damage, combat state, kill (vmangos Unit::DealDamage, Kill) ---------------------

    /// <summary>
    /// Apply damage (vmangos Unit::DealDamage). A sitting player victim stands up; both enter
    /// combat; the attacker earns rage; lethal damage kills; otherwise health drops, a player
    /// attacker without a victim starts attacking, non-player victims gain threat and player
    /// victims earn rage. <paramref name="outcome"/> / <paramref name="cleanDamage"/> carry the
    /// dodge/parry rage case. <paramref name="startsCombat"/> false skips the combat link and the auto-attack start. Returns the damage dealt. <paramref name="threatSpell"/> and <paramref name="critical"/>: the spell the damage comes from and whether it crit, for the threat formula (<see cref="AddDamageThreat"/>);
    /// the spell also decides the death durability exemption (SPELL_ATTR_EX3_NO_DURABILITY_LOSS) and, with no threat to create
    /// (<see cref="SuppressesSpellThreat"/>), keeps the victim's AI out of AttackedBy. <paramref name="durabilityLoss"/> false is
    /// vmangos DealDamage's durabilityLoss argument (instant kill, split damage: no death wear). <paramref name="reflected"/> is its reflected
    /// argument: a reflected spell hitting its own caster cannot kill it in a duel (MapCombat.Duel.cs). Public for the spells area (direct
    /// spell damage uses <paramref name="direct"/> = false for DoTs, and
    /// <paramref name="meleeDamage"/> = false for every spell). vmangos Unit.cpp DealDamage
    /// distinguishes DIRECT_DAMAGE from SPELL_DIRECT_DAMAGE: only weapon damage rewards
    /// outgoing rage, and its auto-start Attack call enables melee only for DIRECT_DAMAGE. <paramref name="caller"/> is filled in by the compiler with
    /// the member that called this (a constant string, no allocation); it is the name the combat reports show as the member that flagged combat, so a
    /// caller that is itself a shared funnel passes its own constant (MapCombatDamageSink does, for spells and DoTs).
    /// </summary>
    public uint DealDamage(Unit attacker, Unit victim, uint damage, MeleeHitOutcome outcome = MeleeHitOutcome.Normal, uint cleanDamage = 0, bool direct = true, bool meleeDamage = true, bool startsCombat = true,
        SpellInfo? threatSpell = null, bool critical = false, bool durabilityLoss = true, bool reflected = false, [CallerMemberName] string caller = "")
    {
        if (IsQuestSettlementPending(attacker) || IsQuestSettlementPending(victim) || !IsAliveState(victim))
        {
            return 0;
        }

        damage = _world.Scripts.Unit.OnDamage(attacker, victim, damage);
        if (victim is Player standing && !IsStandingUp(standing))
        {
            standing.SetStandState(StandState.Stand);
        }

        bool enterCombat = !ReferenceEquals(attacker, victim);
        // false: a hunter trap's hit on a player does not start combat (Spell.cpp:1650); the flag still refreshes (:1688).
        bool combatLink = enterCombat && startsCombat;
        if (damage == 0)
        {
            bool suppressedSpellThreat = SuppressesSpellThreat(attacker, victim, threatSpell);
            if (outcome is MeleeHitOutcome.Parry or MeleeHitOutcome.Dodge
                && cleanDamage > 0 && direct && meleeDamage && enterCombat && attacker is Player { PowerType: PowerType.Rage } ragePlayer)
            {
                RewardRage(ragePlayer, (uint)(cleanDamage * 0.75f), attacker: true, CombatEnvironment.For(_world));
            }

            LinkHostileHit(attacker, victim, combatLink, direct, threatSpell, caller);

            if (enterCombat && !suppressedSpellThreat)
            {
                if (victim is not Player)
                {
                    // A missed creature still aggroes: its AI's AttackStart adds the attacker
                    // with 0 threat (vmangos CreatureAI::AttackedBy → AttackStart → AddThreat).
                    victim.Combat.Threat.AddThreat(attacker, 0f);
                }
            }

            if (!suppressedSpellThreat)
            {
                AttackedBy(victim, attacker);
            }
            return 0;
        }

        // Duels end at 1 hp (MapCombat.Duel.cs; vmangos Unit.cpp:762-779).
        bool duelEnded = ApplyDuelClamp(attacker, victim, ref damage, reflected);

        // vmangos Unit.cpp:788-789: a creature counts where its damage came from, the killing blow included, for the
        // damage-origin rule of the kill (Creature::IsLootAllowedDueToDamageOrigin).
        if (victim is ICombatCreature counted)
        {
            counted.CountDamageTaken(damage, attacker.CharmerOrOwnerOrOwnGuid.IsPlayer || ReferenceEquals(attacker, victim));
        }

        // The per-attacker damage history is recorded before the lethal check, so the killing blow counts
        // (vmangos Unit::UnitDamaged, Unit.cpp:788-796; the kill is at :825). Never for self damage.
        if (!ReferenceEquals(attacker, victim))
        {
            DamageTaken?.Invoke(attacker, victim, damage);
        }

        LinkHostileHit(attacker, victim, combatLink, direct, threatSpell, caller);

        if (direct && meleeDamage && enterCombat && attacker is Player { PowerType: PowerType.Rage } rager)
        {
            RewardRage(rager, damage, attacker: true, CombatEnvironment.For(_world));
        }

        // vmangos Unit::DealDamage (Unit.cpp:825-850): a unit with an invincibility threshold (Spirit of Redemption) never dies of damage,
        // and every hit, lethal or not, takes at most the health above the threshold (nothing at or below it). Only the health change is
        // clamped: threat, rage and the returned damage keep the full amount, as in vmangos.
        uint invincible = victim.InvincibilityHpThreshold;
        if (victim.Health <= damage && invincible == 0)
        {
            KillCore(attacker, victim, durabilityLoss, threatSpell);
            if (duelEnded)
            {
                AfterLethalDuelDamage((Player)victim); // Unit.cpp:825-843
            }

            return damage;
        }

        if (victim.Health > invincible)
        {
            victim.Health -= Math.Min(victim.Health - invincible, damage);
        }
        bool attackedBy = !SuppressesSpellThreat(attacker, victim, threatSpell); // before this damage's threat creates an entry

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
                AddDamageThreat(attacker, victim, damage, critical || outcome == MeleeHitOutcome.Crit, threatSpell);
            }
        }
        else if (enterCombat && victim.PowerType == PowerType.Rage)
        {
            RewardRage((Player)victim, damage, attacker: false, CombatEnvironment.For(_world));
        }

        RollHitTakenDurability(victim);
        DamageDealt?.Invoke(attacker, victim, damage, direct, meleeDamage);
        // A spell explicitly suppressed from threat must not enter CreatureAI's AttackedBy path:
        // AttackStart would create a fresh zero-threat reference after the real damage consumer.
        if (attackedBy)
        {
            AttackedBy(victim, attacker);
        }
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
    /// <remarks>
    /// A direct call (a script, a GM command or a test killing outright) stands for a lethal blow of the victim's remaining
    /// health by <paramref name="killer"/>: a creature victim counts it toward its damage origin, as Unit::DealDamage would have
    /// before reaching Unit::Kill (Unit.cpp:789, 825).
    /// This differs from vmangos Unit::Kill itself, which counts no damage. For Twin Emperors (boss_twinemperors.cpp:316), the brother
    /// keeps his own fight counters, but this extra player-attributed health can allow rewards when players dealt 35% or less of his
    /// damage; vmangos would deny them. The direct-Kill lethal-blow contract is retained for existing scripts and test callers.
    /// </remarks>
    public void Kill(Unit? killer, Unit victim, bool durabilityLoss = true, SpellInfo? spell = null)
    {
        if (killer is not null && victim is ICombatCreature counted && IsAliveState(victim))
        {
            counted.CountDamageTaken(Math.Max(victim.Health, 1u), killer.CharmerOrOwnerOrOwnGuid.IsPlayer || ReferenceEquals(killer, victim));
        }

        KillCore(killer, victim, durabilityLoss, spell);
    }

    private void KillCore(Unit? killer, Unit victim, bool durabilityLoss, SpellInfo? spell)
    {
        if (IsQuestSettlementPending(killer) || IsQuestSettlementPending(victim) || !IsAliveState(victim))
        {
            return;
        }

        // vmangos Unit::Kill (Unit.cpp:987-1008): before any credit resolves, a creature mostly damaged by non-players drops its tap.
        if (victim is ICombatCreature settled)
        {
            settled.SettleKillCredit();
        }

        // Wear eligibility follows the source's owner/charmer lookup. Existing kill credit
        // and PvP corpse attribution retain their separate playerTap behavior.
        // pPlayerTap: the killer's charmer or owner player, or the killer itself (a unit standing in for a player-controlled one
        // reports its player through IPlayerControlledUnit).
        Player? durabilityPlayerTap = killer is null ? null : killer.GetCharmerOrOwnerPlayerOrSelf() ?? DuelRules.ControllingPlayer(killer);
        var playerTap = killer as Player;
        if (playerTap is not null && !ReferenceEquals(playerTap, victim))
        {
            playerTap.Session.Send(WorldOpcode.SmsgPartykilllog, CombatPackets.PartyKillLog(playerTap.Guid, victim.Guid));
        }

        if (killer is not null && !ReferenceEquals(killer, victim))
        {
            Hooks.OnKill(killer, victim);
        }

        // vmangos Unit::Kill (Unit.cpp:1108-1140): a priest with the Spirit of Redemption talent becomes the spirit instead of dying (anything
        // but the spirit's own Suicide); the rest of the kill (credit, threat, PvP death, durability, the kill event) happens all the same.
        bool spirit = victim is Player && SpellMitigation?.TryEnterSpiritOfRedemption(victim, spell) == true;
        if (!spirit)
        {
            if (victim is Player revivedVictim && revivedVictim.Combat.ResurrectedUnflushed)
            {
                revivedVictim.Combat.ResurrectedUnflushed = false;
                _map.Logger.LogInformation(
                    "player {Player} (guid={Guid}) died on map {MapId} in the same tick as resurrection; flushing alive SMSG_UPDATE_OBJECT before death state",
                    revivedVictim.Name, revivedVictim.Guid.Low, _map.MapId);
                _map.FlushPlayerValuesNow(revivedVictim);
            }

            victim.Health = 0;
            SetDeathState(victim, DeathState.JustDied);
        }

        foreach (Unit holder in victim.Combat.ThreatenedByInternal.ToArray())
        {
            holder.Combat.Threat.Remove(victim);
        }

        if (victim is Player playerVictim)
        {
            // remember victim PvP death at the original death, not at the Spirit of Redemption's timeout (Unit.cpp:1177-1180)
            if (spell?.Id != Spells.SpellSystem.SpiritOfRedemptionSuicide)
            {
                playerVictim.Combat.PvpDeath = playerTap is not null;
            }

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

        Scripting.ScriptHookRegistry scripts = _world.Scripts;
        scripts.Unit.OnDeath(victim, killer);
        if (playerTap is not null && !ReferenceEquals(playerTap, victim))
        {
            scripts.Player.OnKill(playerTap, victim);
        }

        if (victim is Player killedPlayer)
        {
            scripts.Player.OnKilled(killedPlayer, killer);
        }
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
                Death.Resurrection.ResurrectionRequests.Clear(player); // Player::SetDeathState(JUST_DIED): ClearResurrectRequestData (Player.cpp:1522)
                player.LastReleasedGraveyard = null;
                player.RepopDestinationResolved = false;
                player.SetRooted(true);
            }

            SetPower(unit, unit.PowerType, 0);
        }
    }

    /// <summary>
    /// vmangos Unit::SetInCombatState: UNIT_FLAG_IN_COMBAT plus the PvP linger timer (rounded up to the 1.2 s check). <paramref name="caller"/>
    /// (DealDamage's caller for a damage hit) is kept for the combat/combat-never-left and creature/evade-loop reports (<see cref="UnitCombat.LastCombatSetBy"/>); <paramref name="via"/> is
    /// the helper it went through (a compile-time constant, <see cref="UnitCombat.LastCombatSetVia"/>). Both are string references copied
    /// from call sites: no allocation on the combat path.
    /// </summary>
    public void SetInCombatState(Unit unit, uint combatTimer, [CallerMemberName] string caller = "", string? via = null)
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
        c.LastCombatSetBy = caller;
        c.LastCombatSetVia = via;
        c.CombatSetsSinceClear++;
        Track(unit);
    }

    /// <summary>
    /// vmangos Unit::DealDamage's combat link, or the out-of-combat PvP flag when that link is skipped.
    /// A hunter trap's hit on a player does not start combat (Spell.cpp:1650) but
    /// <see cref="SetOutOfCombatWithVictim"/> still refreshes the attacker's flag (Spell.cpp:1688-1692).
    /// A damage shield does neither (Unit::DealDamage ShouldEnterCombat, Unit.cpp:704), nor does damage over time that is not a channel
    /// (<paramref name="direct"/> false: a DoT tick, SpellAuras.cpp:5921, or the split part of a damage split, Unit.cpp:2140, 2179): that
    /// refresh is the spell hit's (Spell.cpp:1260, 1684, 1692), and Unit::DealDamage itself never calls SetOutOfCombatWithVictim.
    /// </summary>
    private void LinkHostileHit(Unit attacker, Unit victim, bool combatLink, bool direct, SpellInfo? threatSpell, string caller)
    {
        if (combatLink)
        {
            SetInCombatWithAggressor(victim, attacker, caller);
            SetInCombatWithVictim(attacker, victim, caller);
        }
        else if (direct && !ReferenceEquals(attacker, victim) && threatSpell?.HasAura(AuraType.DamageShield) != true)
        {
            SetOutOfCombatWithVictim(attacker, victim);
        }
    }

    /// <summary>vmangos Unit::SetOutOfCombatWithVictim: flag the attacker, do not enter combat (touchOnly).</summary>
    private static void SetOutOfCombatWithVictim(Unit attacker, Unit victim)
    {
        if (attacker is Player player)
        {
            TogglePlayerPvpFlagOnAttackVictim(player, victim, touchOnly: true);
        }
    }

    /// <summary>
    /// vmangos Unit::SetInCombatWithAggressor (Unit.cpp:5985): the victim's PvP pulse and combat state; a victim that uses the PvP combat
    /// timer (<see cref="UsesPvpCombatTimer"/>: a player, a player's pet or charm, a NO_THREAT_LIST creature) lingers 5.5 s. It was players
    /// only, so a NO_THREAT_LIST creature (Mr. Smite) hit by a player had no linger and left combat - evaded - at the next 1.2 s check.
    /// </summary>
    private void SetInCombatWithAggressor(Unit victim, Unit aggressor, string caller)
    {
        // Duel opponents do not pulse each other (vmangos Unit.cpp:5973, !IsInDuelWith).
        if ((aggressor.UnitFlags & UnitFlags.Pvp) != 0 && victim is Player pv && aggressor is Player pa && !ReferenceEquals(pv, pa)
            && !DuelRules.IsInDuelWith(pv, pa))
        {
            pv.Combat.InPvpCombat = true;
            UpdatePvp(pv, true);
        }

        SetInCombatState(victim, UsesPvpCombatTimer(victim) ? CombatConstants.PvpCombatTimerMs : 0, caller, nameof(SetInCombatWithAggressor));
    }

    /// <summary>vmangos Unit::SetInCombatWithVictim (Unit.cpp:6070): PvP flag on attack, then combat (5.5 s when the victim uses the PvP combat timer).</summary>
    private void SetInCombatWithVictim(Unit attacker, Unit victim, string caller)
    {
        if (attacker is Player player)
        {
            TogglePlayerPvpFlagOnAttackVictim(player, victim);
        }

        bool wasInCombat = (attacker.UnitFlags & UnitFlags.InCombat) != 0;
        SetInCombatState(attacker, UsesPvpCombatTimer(victim) ? CombatConstants.PvpCombatTimerMs : 0, caller, nameof(SetInCombatWithVictim));

        // vmangos Unit::SetInCombatState (Unit.cpp:5942-5946) -> Creature::OnEnterCombat (Creature.cpp:3652-3653): a creature entering combat
        // through its own hit runs AttackedBy, so it attacks and lists the unit it hit instead of sitting in combat with no victim and
        // evading at its next update. Partial port: vmangos does this inside SetInCombatState for every entrant; here only this attacker-side
        // path does (DealDamage already runs AttackedBy for the victim), so other direct SetInCombatState callers do not get it.
        // vmangos gates on UNIT_STATE_CONFUSED|FLEEING (unit states); the Confused/Fleeing unit flags below mirror them (CcState keeps both in sync).
        // SuppressesSpellThreat is not checked here, unlike the victim-side AttackedBy in DealDamage: NO_HARMFUL_THREAT / EX_NO_THREAT only stop
        // the hit creating a threat reference for the attacker on the victim's list (Spell::DoSpellHitOnUnit skips the victim's AttackedBy for
        // that reason), while vmangos runs OnEnterCombat -> AttackedBy for the entrant in SetInCombatState whatever spell put it in combat.
        if (!wasInCombat && attacker is ICombatCreature creature && IsAliveState(attacker)
            && (attacker.UnitFlags & (UnitFlags.Confused | UnitFlags.Fleeing)) == 0)
        {
            creature.OnAttackedBy(victim);
        }
    }

    /// <summary>vmangos Unit::ClearInCombat.</summary>
    public static void ClearInCombat(Unit unit)
    {
        unit.Combat.CombatTimer = 0;
        unit.Combat.CombatSetsSinceClear = 0;
        unit.UnitFlags &= ~(UnitFlags.InCombat | UnitFlags.PetInCombat);
        unit.Combat.InPvpCombat = false;
    }

    /// <summary>
    /// vmangos Unit::Update combat timer (Objects/Unit.cpp:256-287): every 1.2 s check, a unit that uses the PvP combat timer
    /// (<see cref="UsesPvpCombatTimer"/>: a player, a player's pet, a unit a player charms, a NO_THREAT_LIST creature) leaves combat once no
    /// creature holds it on its threat list and its own pet is not held on one either. A NO_THREAT_LIST creature nobody controls leaves through
    /// its AI's evade (Creature::OnLeaveCombat, Creature.cpp:3632-3645); everything else just clears the flag (Unit::ClearInCombat).
    /// Before this ran for players only, so a pet whose fight ended (its victim died, or it was called back) stayed flagged in combat for
    /// good: nothing else clears a pet's flag, it has no threat list of its own to empty (vmangos Unit::CanHaveThreatList).
    /// Limits: the SPELL_AURA_INTERRUPT_REGEN hold and the feign-death exemption from the pet check are not modelled (feign death runs
    /// CombatStop itself, SpellSystem.Feign.cs).
    /// </summary>
    private void UpdateCombatTimer(Unit unit, uint diff)
    {
        UnitCombat c = unit.Combat;
        if (c.CombatTimer <= diff)
        {
            uint interval = CombatConstants.CombatCheckIntervalMs;
            c.CombatTimer = interval - Math.Min(diff - c.CombatTimer, interval);
            if (c.IsInCombat && UsesPvpCombatTimer(unit))
            {
                PruneStaleThreatHolders(unit);
                if (c.ThreatenedByInternal.Count == 0 && !IsOwnPetThreatened(unit))
                {
                    LeaveCombatOnTimer(unit);
                }
            }
        }
        else
        {
            c.CombatTimer -= diff;
        }
    }

    /// <summary>
    /// vmangos Unit::UsesPvPCombatTimer (Objects/Unit.cpp:368-385): a player, a pet owned by a player, a unit charmed by a player, or a
    /// NO_THREAT_LIST creature: the units that hold no threat list of their own, so the timer is what ends their combat.
    /// </summary>
    internal static bool UsesPvpCombatTimer(Unit unit)
    {
        if (unit is Player)
        {
            return true;
        }

        if (unit is Creatures.Creature creature)
        {
            return (creature.IsPet && creature.OwnerGuid.IsPlayer)
                || creature.CharmerGuid.IsPlayer
                || (creature.Template.Behaviour & Kernel.WorldData.Creatures.CreatureBehaviourFlags.NoThreatList) != 0;
        }

        return unit.CharmerGuid.IsPlayer;
    }

    /// <summary>vmangos Unit::Update "Pet in combat ?" (Unit.cpp:264-266): the unit's own pet is still held on a creature's threat list.</summary>
    private bool IsOwnPetThreatened(Unit unit)
    {
        if (unit.PetGuid is not { IsEmpty: false } petGuid
            || FindUnit(petGuid) is not { IsInWorld: true } pet
            || !ReferenceEquals(pet.Map, _map))
        {
            return false;
        }

        PruneStaleThreatHolders(pet);
        return pet.Combat.ThreatenedByInternal.Count > 0;
    }

    private void PruneStaleThreatHolders(Unit target)
    {
        HashSet<Unit> holders = target.Combat.ThreatenedByInternal;
        if (holders.Count == 0)
        {
            return;
        }

        Unit[]? toRemove = null;
        int removeCount = 0;
        foreach (Unit holder in holders)
        {
            if (!holder.IsAlive
                || !holder.IsInWorld
                || !ReferenceEquals(holder.Map, _map)
                || holder is Creatures.Creature { IsFrozenOutsideActiveCells: true })
            {
                toRemove ??= new Unit[holders.Count];
                toRemove[removeCount++] = holder;
            }
        }

        for (int i = 0; i < removeCount; i++)
        {
            Unit holder = toRemove![i];
            holder.Combat.Threat.Remove(target);
            holders.Remove(holder);
        }
    }

    internal static bool WithinEngagementRange(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy) <= EngagementRange * EngagementRange;
    }

    /// <summary>
    /// The combat timer ran out (vmangos Unit.cpp:268-283): a NO_THREAT_LIST creature that no player charms or owns evades through its AI
    /// (Creature::OnLeaveCombat); every other unit clears the flag. An evade that left the flag set (a creature already walking home) still
    /// clears it, so the timer always ends the combat it owns.
    /// </summary>
    private static void LeaveCombatOnTimer(Unit unit)
    {
        if (unit is Creatures.Creature { AI: { } ai } creature && !creature.CharmerOrOwnerGuid.IsPlayer
            && (creature.Template.Behaviour & Kernel.WorldData.Creatures.CreatureBehaviourFlags.NoThreatList) != 0)
        {
            ai.EnterEvadeMode();
            if (!unit.Combat.IsInCombat)
            {
                return;
            }
        }

        ClearInCombat(unit);
    }
}

using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Protocol;
using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    private const HitInfo NextMeleeSpellBaseHitInfo = HitInfo.NoAction;
    private readonly ConditionalWeakTable<SpellCast, HashSet<ulong>> _meleeSpellPacketTargets = new();

    private void FlushQueuedMeleeSpellDamage(OutcomeBuilder outcome)
    {
        if (outcome.MeleeSpellDamage.Count == 0)
        {
            return;
        }

        bool eligible = outcome.MeleeSpellPacketEligible;
        uint dealt = 0;
        uint absorbed = 0;
        uint resisted = 0;
        uint blocked = 0;
        bool critical = false;
        foreach (MeleeSpellDamageComponent component in outcome.MeleeSpellDamage)
        {
            dealt += component.Dealt;
            absorbed += component.Absorbed;
            resisted += component.Resisted;
            blocked += component.Blocked;
            critical |= component.Critical;
        }

        SendNextMeleeSpellAttackerStateUpdate(outcome.Cast.Caster, outcome.Target, outcome.Cast.Spell, eligible,
            dealt, absorbed, resisted, critical, outcome.Cast, components: outcome.MeleeSpellDamage, blocked: blocked);
        foreach (DeferredNonMeleeDamageLog log in outcome.DeferredNonMeleeLogs)
        {
            SendToSet(outcome.Cast.Caster, WorldOpcode.SmsgSpellnonmeleedamagelog,
                SpellPackets.BuildSpellNonMeleeDamageLog(log.Target, log.Caster, log.SpellId, log.Damage, log.School,
                    absorbed: log.Absorbed, resisted: log.Resisted, blocked: log.Blocked, hitInfo: log.HitInfo), includeSelf: true);
        }
    }

    /// <summary>
    /// Emits the vmangos spell-owned SMSG_ATTACKERSTATEUPDATE for a queued next-swing spell.
    /// The eligibility flag is captured before damage is delivered because lethal damage can clear
    /// the caster's combat victim as part of the damage sink.
    /// </summary>
    private void SendNextMeleeSpellAttackerStateUpdate(
        Unit caster, Unit target, SpellInfo spell, bool eligible, uint dealt, uint absorbed,
        uint resisted, bool critical, SpellCast? cast = null, SpellMissInfo miss = SpellMissInfo.None,
        IReadOnlyList<MeleeSpellDamageComponent>? components = null, uint blocked = 0)
    {
        if (!eligible || !spell.IsNextMeleeSwing)
        {
            return;
        }

        if (cast is not null && !_meleeSpellPacketTargets.GetOrCreateValue(cast).Add(target.Guid.Value))
        {
            return;
        }

        (HitInfo hitInfo, VictimState victimState) = NextMeleeSpellOutcome(miss);
        if (miss == SpellMissInfo.None)
        {
            if (dealt != 0 || blocked != 0)
            {
                hitInfo |= HitInfo.AffectsVictim;
            }

            if (blocked != 0)
            {
                // vmangos Unit::DealDamage melee-spell path (Unit.cpp:1621-1634): a partial block sets the rolled-block and block hit flags.
                hitInfo |= HitInfo.RolledBlock | HitInfo.Block;
            }

            if (critical)
            {
                hitInfo |= HitInfo.CriticalHit;
            }

            if (absorbed != 0)
            {
                hitInfo |= HitInfo.Absorb;
            }

            if (resisted != 0)
            {
                hitInfo |= HitInfo.Resist;
            }
        }

        // The sub-damage school goes out as a school INDEX, not a mask: vmangos Combat.cpp:203 (MeleeDamageLog serialisation) and
        // cmangos-classic Unit.cpp:6112 (Unit::SendAttackStateUpdate) both write GetFirstSchoolInMask(damageSchoolMask). Writing the mask
        // made a physical melee spell (mask 1) read as Holy (index 1) on the client.
        SubDamage[] subDamage = components is null
            ? dealt == 0 && absorbed == 0 && resisted == 0
                ? []
                : [new SubDamage((uint)SpellSchoolMasks.FirstSchoolIn(spell.SchoolMask()), dealt, absorbed, checked((int)resisted))]
            : [.. components.Where(component => component.Dealt != 0 || component.Absorbed != 0 || component.Resisted != 0)
                .GroupBy(component => component.SchoolMask).Select(school => new SubDamage(
                (uint)SpellSchoolMasks.FirstSchoolIn(school.Key),
                checked((uint)school.Sum(component => (long)component.Dealt)),
                checked((uint)school.Sum(component => (long)component.Absorbed)),
                checked((int)school.Sum(component => (long)component.Resisted))))];
        CombatPackets.SendToSet(caster, WorldOpcode.SmsgAttackerstateupdate,
            CombatPackets.AttackerStateUpdate(hitInfo, caster.Guid, target.Guid, dealt, subDamage, victimState, blocked: blocked, meleeSpellId: spell.Id));
    }

    private static (HitInfo HitInfo, VictimState VictimState) NextMeleeSpellOutcome(SpellMissInfo miss)
        => miss switch
        {
            SpellMissInfo.Dodge => (NextMeleeSpellBaseHitInfo, VictimState.Dodge),
            SpellMissInfo.Parry => (NextMeleeSpellBaseHitInfo, VictimState.Parry),
            SpellMissInfo.Block => (NextMeleeSpellBaseHitInfo, VictimState.Blocks),
            SpellMissInfo.Evade => (NextMeleeSpellBaseHitInfo, VictimState.Evades),
            SpellMissInfo.Resist => (NextMeleeSpellBaseHitInfo, VictimState.Unaffected),
            _ when miss != SpellMissInfo.None => (NextMeleeSpellBaseHitInfo, VictimState.Unaffected),
            _ => (NextMeleeSpellBaseHitInfo, VictimState.Normal),
        };

    /// <summary>
    /// Root hook for SpellSystem's target-miss and zero-damage completion path. Call immediately
    /// after the miss is known and before combat cleanup; no damage or melee outcome is invented.
    /// </summary>
    internal void SendNextMeleeSpellNoDamage(SpellCast cast, Unit target, SpellMissInfo miss = SpellMissInfo.None, bool? eligible = null)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(target);
        SendNextMeleeSpellAttackerStateUpdate(cast.Caster, target, cast.Spell,
            eligible ?? (cast.Spell.IsNextMeleeSwing && ReferenceEquals(cast.Caster.Combat.Victim, target)),
            dealt: 0, absorbed: 0, resisted: 0, critical: false, cast: cast, miss: miss);
    }
}

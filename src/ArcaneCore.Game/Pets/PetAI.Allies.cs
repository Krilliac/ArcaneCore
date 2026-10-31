using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// The positive half of vmangos <c>PetAI</c>'s autocast (PetAI.cpp:215-330) and <c>PetAI::UpdateAllies</c> (:353+): the ally set (the
/// owner and the owner's group members on the pet's map, refreshed on a timer) and the pick of a target for a beneficial spell.
/// <para>Thread affinity: world thread. The ally list is owned by the AI and only touched from <see cref="PetAI.OnUpdate"/>.</para>
/// </summary>
public sealed partial class PetAI
{
    /// <summary>vmangos <c>m_updateAlliesTimer</c> period: the ally set is rebuilt every 10 s.</summary>
    private const uint AllyRefreshPeriodMs = 10_000;

    /// <summary>Spell icon of Tainted Blood (Felhunter): PetAI.cpp:239-241 forces it to combat-only.</summary>
    private const uint TaintedBloodIconId = 153;

    /// <summary>The Fire Shield visual: with the imp-buff family bit it overrides the combat-only rules (PetAI.cpp:245-246).</summary>
    private const uint FireShieldVisual = 289;

    private const uint FamilyWarlock = 5;

    /// <summary>CF_WARLOCK_IMP_BUFFS (SpellClassMask.h:118).</summary>
    private const int WarlockImpBuffsBit = 23;

    private readonly List<ObjectGuid> _allies = [];
    private readonly List<Unit> _targetScratch = [];
    private readonly List<ObjectGuid> _memberScratch = [];
    private uint _allyRefreshMs;

    /// <summary>The guids of the pet's current ally set (the owner and its group members on the map); empty until the first refresh.</summary>
    public IReadOnlyList<ObjectGuid> Allies => _allies;

    /// <summary>vmangos PetAI::UpdateAllies: rebuild the ally set when the refresh timer ran out.</summary>
    private void UpdateAllies(SpellSystem spells)
    {
        if (_allyRefreshMs != 0)
        {
            return;
        }

        _allyRefreshMs = AllyRefreshPeriodMs;
        _allies.Clear();
        if (Owner is not { } owner || Me.Map is not { } map)
        {
            return;
        }

        _allies.Add(owner.Guid);
        _memberScratch.Clear();
        spells.Groups.AppendGroupMembers(owner, raid: false, _memberScratch);
        foreach (ObjectGuid guid in _memberScratch)
        {
            if (guid != owner.Guid && map.FindPlayer(guid) is not null && !_allies.Contains(guid))
            {
                _allies.Add(guid);
            }
        }
    }

    /// <summary>
    /// The positive branch of the autocast loop: vmangos only casts a combat-only spell (an instakill, a cooldown longer than its
    /// duration, Tainted Blood, a damage-done aura such as Furious Howl; Fire Shield is exempt) while the pet has a victim.
    /// A spell aimed at a friend goes on a random ally or the pet that lacks its aura and is in range; any other goes on the pet.
    /// </summary>
    private bool TryPickPositiveTarget(SpellSystem spells, SpellInfo spell, bool fighting, [NotNullWhen(true)] out Unit? target)
    {
        target = null;
        if (!fighting && IsCombatOnly(spell))
        {
            return false;
        }

        if (!AimsAtFriend(spell))
        {
            if (!CanAutoCast(spells, spell, Me))
            {
                return false;
            }

            target = Me;
            return true;
        }

        UpdateAllies(spells);
        _targetScratch.Clear();
        AddAllyTarget(spells, spell, Me);
        if (Me.Map is { } map)
        {
            foreach (ObjectGuid guid in _allies)
            {
                if (map.FindPlayer(guid) is { } ally)
                {
                    AddAllyTarget(spells, spell, ally);
                }
            }
        }

        if (_targetScratch.Count == 0)
        {
            return false;
        }

        target = _targetScratch[_random.Next(_targetScratch.Count)];
        return true;
    }

    private void AddAllyTarget(SpellSystem spells, SpellInfo spell, Unit candidate)
    {
        if (candidate.IsAlive && CanAutoCast(spells, spell, candidate)
            && SpellSystem.CheckRange(Me, spell, candidate, strict: false) == SpellCastResult.CastOk)
        {
            _targetScratch.Add(candidate);
        }
    }

    /// <summary>
    /// The parts of vmangos Spell::CanAutoCast (Spell.cpp:6764-6820) that decide a positive autocast: a spell that cancels auto-attack is
    /// held while the pet has a victim; Fire Shield only goes on a target that has an attacker; no recast over a live aura; Dash and Dive
    /// wait until the victim is out of melee reach; a heal skips a target at full health.
    /// </summary>
    private bool CanAutoCast(SpellSystem spells, SpellInfo spell, Unit target)
    {
        Unit? victim = Me.Combat.Victim;
        if (spell.HasAttribute(SpellAttributesCombat.CancelsAutoAttackCombat) && victim is not null)
        {
            return false;
        }

        if (IsFireShield(spell) && target.Combat.Victim is null && target.Combat.Attackers.Count == 0)
        {
            return false;
        }

        if (spells.HasAura(target, spell.Id))
        {
            return false;
        }

        if (spell.HasAura(AuraType.ModIncreaseSpeed) && victim is not null && MapCombat.CanReachWithMeleeAutoAttack(Me, victim))
        {
            return false;
        }

        return !(ProcFlagRules.IsHealSpell(spell) && target.Health == target.MaxHealth);
    }

    private static bool IsFireShield(SpellInfo spell)
        => spell.IsFitToFamily(FamilyWarlock, WarlockImpBuffsBit) && spell.SpellVisual == FireShieldVisual;

    /// <summary>vmangos PetAI.cpp:221-249, rule for rule and in order; a spell usable only out of combat is never combat-only.</summary>
    private static bool IsCombatOnly(SpellInfo spell)
    {
        if (spell.HasAttribute(SpellAttributesCombat.NotInCombatOnlyPeaceful))
        {
            return false;
        }

        bool combatOnly = spell.HasEffect(SpellEffectName.Instakill);
        if (!combatOnly)
        {
            int duration = spell.GetDuration();
            if (duration >= 0 && spell.RecoveryTime > (uint)duration)
            {
                combatOnly = true;
            }
        }

        if (spell.SpellIconId == TaintedBloodIconId)
        {
            combatOnly = true;
        }

        if (IsFireShield(spell))
        {
            combatOnly = false;
        }

        return combatOnly || spell.HasAura(AuraType.ModDamageDone);
    }

    private static bool AimsAtFriend(SpellInfo spell)
    {
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.TargetA is SpellImplicitTarget.UnitFriend or SpellImplicitTarget.UnitParty or SpellImplicitTarget.UnitFriendAndParty
                or SpellImplicitTarget.UnitRaid)
            {
                return true;
            }
        }

        return false;
    }
}

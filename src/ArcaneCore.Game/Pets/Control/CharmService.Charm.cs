using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets.Control;

public sealed partial class CharmService
{
    /// <summary>vmangos Aura::HandleAuraAoeCharm (SpellAuras.cpp:5740-5748): only Chains of Kel'Thuzad (28410) charms; the other AoE charm auras do nothing.</summary>
    private void OnAoeCharmAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (holder.Spell.Id == ChainsOfKelThuzad)
        {
            OnCharmAura(spells, holder, aura, apply);
        }
    }

    /// <summary>
    /// SPELL_AURA_MOD_CHARM (vmangos Aura::HandleModCharm, SpellAuras.cpp:3219-3442), the real apply and remove only. A unit cannot charm
    /// itself; the apply needs the caster, the remove releases the target even when the caster is gone.
    /// </summary>
    private void OnCharmAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (holder.CasterGuid == target.Guid)
        {
            return;
        }

        Unit? caster = ControllerOf(spells, holder);
        if (apply)
        {
            if (caster is not null)
            {
                ApplyCharm(caster, target, holder, spells);
            }

            return;
        }

        RemoveCharm(caster, target, holder.RemoveMode, spells);
    }

    /// <summary>
    /// The apply half of vmangos HandleModCharm: the possess-summon dummies and every other charm, possess and AoE charm aura on the target go;
    /// charmer GUID, the caster's faction and the caster's charm field are set; the target stops fighting and gets a pet bar (defensive,
    /// following, returning); a creature takes PetAI and, charmed by a warlock while being a demon, a pet number (its class is forced to mage
    /// when the template has none); a charmed player is controlled by the charmer (<see cref="MapUnitControl"/> runs its PlayerControlledAI);
    /// both sides are told about control. A player caster flags the target PLAYER_CONTROLLED, receives the charm bar and the target follows it.
    /// </summary>
    private void ApplyCharm(Unit caster, Unit target, SpellAuraHolder holder, SpellSystem spells)
    {
        foreach (uint dummy in SummonPossessedDummies)
        {
            spells.RemoveAuras(target, dummy);
        }

        foreach (SpellAuraHolder other in spells.GetAuras(target)
                     .Where(h => !ReferenceEquals(h, holder) && !h.IsRemoved && h.Spell.Id != holder.Spell.Id
                         && (h.HasAura(AuraType.ModCharm) || h.HasAura(AuraType.ModPossess) || h.HasAura(AuraType.AoeCharm)))
                     .ToArray())
        {
            spells.RemoveAuras(target, other.Spell.Id);
        }

        SetCharmerGuid(target, caster.Guid);
        target.FactionTemplate = caster.FactionTemplate;
        SetCharm(caster, target);

        StopAllFighting(target);

        CharmInfo info = InitCharmInfo(target);
        List<uint> charmSpells = InitCharmCreateSpells(target, info);
        info.ReactState = ReactState.Defensive;
        info.CommandState = CommandState.Follow;
        info.IsCommandAttack = false;
        info.IsAtStay = false;
        info.IsReturning = true;
        info.IsCommandFollow = true;
        info.IsFollowing = false;

        target.Map?.Combat.AttackStop(target);
        spells.InterruptNonMeleeSpells(target);

        if (target is Creature creature)
        {
            SwitchToCharmedAi(creature);
            if (caster is Player { Class: Class.Warlock } && creature.Template.CreatureType == CreatureTypeDemon)
            {
                // creature with pet number expected have class set
                if (creature.Class == 0)
                {
                    creature.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
                }

                // just to enable stat window
                info.PetNumber = _nextPetNumber();
                creature.SetUInt32(UpdateFields.UnitFieldPetnumber, info.PetNumber);

                // if charmed two demons the same session, the 2nd gets the 1st one's name
                creature.SetUInt32(UpdateFields.UnitFieldPetNameTimestamp, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
        }

        UpdateControl(target);

        if (caster is Player player)
        {
            target.UnitFlags |= UnitFlags.PlayerControlled;
            CharmSpellInitialize(player, charmSpells);
            if (target is Creature follower)
            {
                follower.Motion.MoveFollow(player, PetConstants.FollowDistance, PetConstants.FollowAngle);
            }

            ForceOwnerOnlyFields(target);
        }
        else
        {
            target.UnitFlags &= ~UnitFlags.PlayerControlled;
        }

        Track(caster, target);
    }

    /// <summary>
    /// The remove half of vmangos HandleModCharm: the charmer GUID goes; a creature gets its faction back (a pet its owner's), a warlock's demon
    /// loses its pet number; the caster loses its charm field and pet bar and inherits the threat on the target; control is updated, casts
    /// are interrupted, a player gets its race faction back. A charmed player whose living charmer is in combat stays in combat with it,
    /// anything else stops fighting. The target stops; a creature drops PLAYER_CONTROLLED, its AI is rebuilt and it turns on its former
    /// charmer (not after its own death); a player regains PLAYER_CONTROLLED.
    /// </summary>
    private void RemoveCharm(Unit? caster, Unit target, AuraRemoveMode removeMode, SpellSystem spells)
    {
        SetCharmerGuid(target, default);

        if (target is Creature creature)
        {
            if (creature.Summon is not null)
            {
                creature.FactionTemplate = creature.GetOwner() is { } owner ? owner.FactionTemplate : creature.Template.Faction;
            }
            else
            {
                creature.FactionTemplate = creature.Template.Faction;
            }

            if (caster is Player { Class: Class.Warlock } && creature.Template.CreatureType == CreatureTypeDemon && creature.GetCharmInfo() is { } info)
            {
                info.PetNumber = 0;
                creature.SetUInt32(UpdateFields.UnitFieldPetnumber, 0);
            }
        }

        if (caster is not null)
        {
            if (caster.CharmGuid == target.Guid)
            {
                SetCharm(caster, null);
            }

            if (caster is Player player)
            {
                RemovePetActionBar(player);
            }

            // Clear threat generated when charm ends.
            if (target is Creature)
            {
                RemoveAttackersThreat(target, caster);
            }
        }

        UpdateControl(target);
        spells.InterruptNonMeleeSpells(target);

        if (target is Player playerTarget)
        {
            RestoreFaction(playerTarget);
        }

        MapCombat? combat = target.Map?.Combat;
        if (target is Player { IsAlive: true } && caster is { IsAlive: true } && caster.Combat.IsInCombat && combat is not null)
        {
            ((Player)target).Session.Send(WorldOpcode.SmsgCancelCombat, []);
            combat.AttackStop(target);
            foreach (Unit attacker in target.Combat.Attackers.ToArray())
            {
                combat.AttackStop(attacker);
            }

            if (target.Combat.HasThreatList)
            {
                target.Combat.Threat.Clear();
            }

            Combat.Threat.HostileRefs.DeleteReferences(target);
            combat.SetInCombatState(caster, 0);
            combat.SetInCombatState(target, 0);
        }
        else
        {
            StopAllFighting(target);
        }

        StopAndIdle(target);

        if (target is not Creature { Summon.Charm: not null })
        {
            ClearCharmInfo(target);
        }

        if (target is Creature charmed)
        {
            charmed.UnitFlags &= ~UnitFlags.PlayerControlled;
            RestoreAi(charmed);
            TurnOnFormerController(charmed, caster, removeMode);
        }
        else if (target is Player)
        {
            target.UnitFlags |= UnitFlags.PlayerControlled;
        }

        Untrack(target);
    }

    /// <summary>
    /// A control aura ended after its caster left the world (logout, far teleport, despawn): vmangos ends every charm in the caster's
    /// RemoveFromWorld (Unit::Uncharm) first, so the target is released there; here the target half runs on its own.
    /// </summary>
    private void ReleaseOrphan(Unit target, ObjectGuid casterGuid, bool possess)
    {
        if (_spells is not { } spells || target.CharmerGuid != casterGuid)
        {
            Untrack(target);
            return;
        }

        if (possess && target.IsPossessedState)
        {
            Untrack(target);
            if (target is Creature { Summon.Kind: SummonKind.Pet } pet && pet.OwnerGuid == casterGuid)
            {
                SetCharmerGuid(pet, default);
                UnitControlState state = UnitControl.State(pet);
                state.Possessor = default;
                state.PossessedState = false;
                pet.UnitFlags &= ~UnitFlags.Possessed;
                return;
            }

            ReleasePossessedTarget(target, null, AuraRemoveMode.Default, spells);
        }
    }

    /// <summary>
    /// The controller half of a control's end when the controlled unit is gone (vmangos removes its auras with AURA_REMOVE_BY_DELETE, which
    /// runs only the caster side of HandleModPossess): mover, charm field, control, view and pet bar come back to the player.
    /// </summary>
    internal void ReleaseController(Player player, Unit controlled)
    {
        if (player.CharmGuid != controlled.Guid)
        {
            return;
        }

        SetMover(player, null);
        SetCharm(player, null);
        UpdateControl(player);
        SetView(player, null);
        RemovePetActionBar(player);
    }

    /// <summary>
    /// The target half when the control aura vanished without its remove handler (its holder was dropped with the controller's spell
    /// state): the unit is released as if the aura had ended.
    /// </summary>
    internal void ForceRelease(Unit target)
    {
        if (_spells is not { } spells || (target.CharmerGuid.IsEmpty && !target.IsPossessedState))
        {
            return;
        }

        ObjectGuid charmer = target.CharmerGuid;
        if (target.IsPossessedState || !target.PossessorGuid.IsEmpty)
        {
            ReleaseOrphan(target, charmer, possess: true);
            if (!target.CharmerGuid.IsEmpty)
            {
                ReleasePossessedTarget(target, null, AuraRemoveMode.Default, spells);
            }

            return;
        }

        RemoveCharm(null, target, AuraRemoveMode.Default, spells);
    }

    // --- the map registry -------------------------------------------------------------------------------------

    private void Track(Unit controller, Unit target)
    {
        if (target.Map is { } map)
        {
            SubscribeCombat(map);
            Registry(target)?.Track(this, controller, target);
        }
    }

    private static void Untrack(Unit target) => Registry(target)?.Untrack(target);
}

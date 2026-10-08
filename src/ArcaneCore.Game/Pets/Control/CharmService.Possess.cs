using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Pets.Control;

public sealed partial class CharmService
{
    /// <summary>
    /// SPELL_AURA_MOD_POSSESS (vmangos Aura::HandleModPossess, SpellAuras.cpp:2954-2995): only the real apply and remove; the caster
    /// takes the target over (<see cref="ModPossess"/>). The pre-1.10 delayed variant for a target that itself possesses something is not
    /// used by build 5875.
    /// </summary>
    private void OnPossessAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        Unit? caster = ControllerOf(spells, holder);
        if (caster is null)
        {
            if (!apply)
            {
                ReleaseOrphan(target, holder.CasterGuid, possess: true);
            }

            return;
        }

        ModPossess(caster, target, apply, holder.RemoveMode, holder.Spell);
    }

    /// <summary>The aura's caster in the target's map (the possession needs it; vmangos returns when GetCaster() is null).</summary>
    private static Unit? ControllerOf(SpellSystem spells, SpellAuraHolder holder)
        => spells.AuraCaster(holder) ?? (holder.Target.Map?.FindObject(holder.CasterGuid) as Unit);

    /// <summary>
    /// vmangos Unit::ModPossess (SpellAuras.cpp:2997-3140). Apply: the dummy auras of the possess-summon spells go, the target becomes
    /// possessed (UNIT_STATE_POSSESSED, UNIT_FLAG_POSSESSED, charmer and possessor GUIDs, the caster's faction), the caster's camera,
    /// charm field and mover move to the target, the target stops fighting, gets an empty possess bar (passive, stay), a creature takes
    /// PetAI, and both clients are told who moves what. Remove: threat on the target is moved to the caster, the caster gets its own
    /// control, view and an empty pet bar back; unless the aura is deleted, the target is released (faction, AI, combat stop) and a
    /// creature turns on its former master with threat equal to its maximum health. Only a player possesses.
    /// </summary>
    internal void ModPossess(Unit casterUnit, Unit target, bool apply, AuraRemoveMode removeMode, SpellInfo? spell)
    {
        if (ReferenceEquals(casterUnit, target) || casterUnit is not Player caster || _spells is not { } spells)
        {
            return;
        }

        if (apply)
        {
            foreach (uint dummy in SummonPossessedDummies)
            {
                spells.RemoveAuras(target, dummy);
            }

            UnitControlState state = UnitControl.State(target);
            state.PossessedState = true;
            target.UnitFlags |= UnitFlags.Possessed;
            SetCharmerGuid(target, caster.Guid);
            state.Possessor = caster.Guid;
            target.FactionTemplate = caster.FactionTemplate;

            // Target should become visible at SetView (if not visible before), otherwise the client ignores its packets.
            SetView(caster, target);
            SetCharm(caster, target);
            SetMover(caster, target);

            StopAllFighting(target);

            CharmInfo info = InitCharmInfo(target);
            InitPossessCreateSpells(target, info);
            info.ReactState = ReactState.Passive;
            info.CommandState = CommandState.Stay;

            PossessSpellInitialize(caster);

            if (target is Creature creature)
            {
                SwitchToCharmedAi(creature);
            }

            UpdateControl(target);
            StopAndIdle(target);
            ForceOwnerOnlyFields(target);
            Track(caster, target);
            return;
        }

        // Clear threat generated when MC ends.
        RemoveAttackersThreat(target, caster);

        SetMover(caster, null);
        SetCharm(caster, null);
        UpdateControl(caster);
        SetClientControl(caster, target, false);

        // There is a possibility that the target became invisible at ResetView: it must come after the control change.
        SetView(caster, null);
        RemovePetActionBar(caster);
        Untrack(target);

        // On delete only do caster related effects.
        if (removeMode == AuraRemoveMode.Delete)
        {
            return;
        }

        ReleasePossessedTarget(target, caster, removeMode, spells);
    }

    /// <summary>The target half of the possession's end (vmangos ModPossess remove, SpellAuras.cpp:3069-3135).</summary>
    private void ReleasePossessedTarget(Unit target, Unit? caster, AuraRemoveMode removeMode, SpellSystem spells)
    {
        UnitControlState state = UnitControl.State(target);
        state.PossessedState = false;
        target.UnitFlags &= ~UnitFlags.Possessed;
        SetCharmerGuid(target, default);
        state.Possessor = default;

        RestoreFaction(target);
        StopAllFighting(target);
        UpdateControl(target);
        StopAndIdle(target);

        if (target is not Creature { Summon.Charm: not null })
        {
            ClearCharmInfo(target);
        }

        if (target is Creature creature)
        {
            RestoreAi(creature);
            TurnOnFormerController(creature, caster, removeMode);

            // Razuvious' understudies cast Mind Exhaustion on themselves when the possession ends.
            if (creature.Entry == DeathKnightUnderstudyEntry && spells.Store.Get(MindExhaustionSpell) is not null)
            {
                spells.CastSpell(creature, MindExhaustionSpell, SpellCastTargets.ForSelf(), triggered: true);
            }
        }
    }

    // --- SPELL_AURA_MOD_POSSESS_PET (Eyes of the Beast) -------------------------------------------------------

    /// <summary>vmangos Aura::HandleModPossessPet (SpellAuras.cpp:3142-3157): a player caster and its pet only.</summary>
    private void OnPossessPetAura(SpellSystem spells, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (ControllerOf(spells, holder) is not Player caster)
        {
            if (!apply)
            {
                ReleaseOrphan(holder.Target, holder.CasterGuid, possess: true);
            }

            return;
        }

        if (holder.Target is not Creature { Summon.Kind: SummonKind.Pet } pet)
        {
            return;
        }

        ModPossessPet(caster, pet, apply);
    }

    /// <summary>
    /// vmangos Player::ModPossessPet (SpellAuras.cpp:3159-3217). Apply: the pet is possessed (state and flag, charmer and possessor), the
    /// player's camera, charm field and mover move to it, it stops and idles, both sides are told, and its stay/follow/return flags are
    /// cleared. Remove: the player gets its charm field, mover and view back and the pet its charmer GUIDs; the possessed state and flag
    /// stay until the client names its own mover again (<see cref="HandleSetActiveMover"/>), as in vmangos.
    /// </summary>
    internal void ModPossessPet(Player caster, Creature pet, bool apply)
    {
        if (apply)
        {
            UnitControlState state = UnitControl.State(pet);
            state.PossessedState = true;
            SetView(caster, pet);
            SetCharm(caster, pet);
            SetMover(caster, pet);

            pet.UnitFlags |= UnitFlags.Possessed;
            SetCharmerGuid(pet, caster.Guid);
            state.Possessor = caster.Guid;

            StopAndIdle(pet);
            UpdateControl(pet);

            if (pet.GetCharmInfo() is { } info)
            {
                info.IsAtStay = false;
                info.IsReturning = false;
                info.IsFollowing = false;
            }

            Track(caster, pet);
            return;
        }

        SetCharm(caster, null);
        SetMover(caster, null);

        // There is a possibility that the target became invisible at ResetView: it must come after the control change.
        SetView(caster, null);
        UpdateControl(pet);
        SetCharmerGuid(pet, default);
        UnitControl.State(pet).Possessor = default;
        if ((pet.UnitFlags & (UnitFlags.Stunned | UnitFlags.Fleeing | UnitFlags.Confused)) == 0)
        {
            pet.System?.StopMoving(pet);
        }

        Untrack(pet);
    }

    /// <summary>
    /// vmangos WorldSession::HandleSetActiveMoverOpcode (MovementHandler.cpp:851-891): a mismatch with the server's mover is logged and
    /// the client's mover is taken as the server's; when the client leaves the pet it moved with Eyes of the Beast, the pet loses the
    /// possessed state and flag (PetAI then brings it back), and a pet beyond the grid activation distance (here the visibility distance)
    /// is saved and dismissed (RemovePet(PET_SAVE_REAGENTS), stored as PET_SAVE_NOT_IN_SLOT). Not ported: vmangos re-sends the root
    /// movement flag to the new controller of a rooted creature mover (docs/areas/unit-control.md, Limits). Returns false on a mismatch.
    /// </summary>
    public bool HandleSetActiveMover(Player player, ObjectGuid guid, SummonService? summons = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        summons ??= Summons;
        UnitControlState state = UnitControl.State(player);
        if (!guid.IsEmpty)
        {
            Unit mover = player.GetMover();
            if (mover.Guid != guid)
            {
                state.ClientMover = mover.Guid;
                state.ClientMoverKnown = true;
                return false;
            }

            // mover swap after Eyes of the Beast, PetAI::UpdateAI handles the pet's return
            ObjectGuid previous = state.ClientMoverKnown ? state.ClientMover : player.Guid;
            if (!player.PetGuid.IsEmpty && player.PetGuid == previous && player.GetPet() is { } pet)
            {
                UnitControl.State(pet).PossessedState = false;
                pet.UnitFlags &= ~UnitFlags.Possessed;
                if (!WithinDistance(pet, player, Maps.Map.VisibilityRange))
                {
                    summons?.QueueCurrentPetSave(player);
                    summons?.Unsummon(pet);
                }
            }
        }

        state.ClientMover = guid;
        state.ClientMoverKnown = true;
        return true;
    }

    /// <summary>
    /// vmangos WorldSession::HandleMoveNotActiveMoverOpcode (MovementHandler.cpp:893-913, build &gt; 1.9.4): the client gives up a mover; it
    /// must be the one it last named, and not the server's current mover unless that is the player itself. The client mover is then
    /// cleared, and a moved player that is being teleported is left to its teleport acknowledgement (vmangos checks the moved player,
    /// pPlayerMover, not the controlling one). Returns the unit the final movement block belongs to (the caller relocates and relays it),
    /// or null when the packet is refused.
    /// </summary>
    /// <param name="isBeingTeleported">vmangos Player::IsBeingTeleported (the world's teleport service); null: nobody is.</param>
    public static Unit? HandleMoveNotActiveMover(Player player, ObjectGuid oldMover, Func<Player, bool>? isBeingTeleported = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        UnitControlState state = UnitControl.State(player);
        ObjectGuid clientMover = state.ClientMoverKnown ? state.ClientMover : player.Guid;
        if (oldMover != clientMover)
        {
            return null;
        }

        if (player.Guid != oldMover && player.GetMover().Guid == oldMover)
        {
            return null;
        }

        state.ClientMover = default;
        state.ClientMoverKnown = true;
        if (player.Map?.FindObject(oldMover) is not Unit moved)
        {
            return null;
        }

        return moved is Player movedPlayer && isBeingTeleported?.Invoke(movedPlayer) == true ? null : moved;
    }

    /// <summary>
    /// vmangos Pet::Unsummon (Pet.cpp:1118-1136): a charmed pet loses its charm auras; a pet a player possesses (Eyes of the Beast) gives
    /// the player its control back before it goes.
    /// </summary>
    internal void OnUnsummon(Creature pet)
    {
        if (_spells is not { } spells)
        {
            return;
        }

        if (!pet.CharmerGuid.IsEmpty)
        {
            RemoveCharmAuras(pet);
        }

        if (!pet.PossessorGuid.IsEmpty)
        {
            spells.RemoveAurasByType(pet, AuraType.ModPossessPet);
            if (pet.Map?.FindObject(pet.PossessorGuid) is Player possessor && possessor.CharmGuid == pet.Guid)
            {
                ModPossessPet(possessor, pet, apply: false);
            }
        }
    }

    private static bool WithinDistance(WorldObject a, WorldObject b, float distance)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        float limit = distance + a.BoundingRadius + b.BoundingRadius;
        return ReferenceEquals(a.Map, b.Map) && (dx * dx) + (dy * dy) + (dz * dz) <= limit * limit;
    }
}

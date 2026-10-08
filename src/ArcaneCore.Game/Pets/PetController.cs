using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// What the client can ask of its pets (vmangos PetHandler.cpp, <c>Unit::HandlePetCommand</c>,
/// <c>Player::PetSpellInitialize</c>): commands, react states, the action bar, autocast, pet spell
/// casts, name queries, dismissal. Every method takes the requesting player and the already parsed
/// packet (<see cref="PetPackets"/>) and answers through the player's session, so the world
/// handlers stay thin and these rules run (and are tested) without a socket.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class PetController
{
    private readonly SummonService _summons;
    private readonly Func<SpellSystem?> _spells;
    private readonly Random _random;

    /// <summary>A successful rename on the world thread; group observers may mark pet-name stats dirty.</summary>
    public event Action<Player>? PetNameChanged;

    /// <summary>World-owned name normalization/validation; null keeps the game seam permissive for isolated hosts.</summary>
    public Func<string, string?>? PetNameNormalizer { get; set; }

    public PetController(SummonService summons, Func<SpellSystem?>? spells = null, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(summons);
        _summons = summons;
        _spells = spells ?? (static () => null);
        _random = random ?? new Random();
    }

    // --- lookups ----------------------------------------------------------------------------------

    /// <summary>The unit <paramref name="guid"/> in the player's map when it is a pet-like creature the player owns, else null.</summary>
    private static (Creature Pet, CharmInfo Charm)? OwnedPet(Player player, ObjectGuid guid)
    {
        if (guid.IsEmpty || player.Map?.FindObject(guid) is not Creature { Summon.Charm: { } charm } pet || pet.CharmerOrOwnerGuid != player.Guid)
        {
            return null;
        }

        return (pet, charm);
    }

    /// <summary>The player's current pet (UNIT_FIELD_SUMMON): what the spell-bearing requests require (vmangos <c>packet.guid != GetPlayer()->GetPetGuid()</c>).</summary>
    private static (Creature Pet, CharmInfo Charm)? CurrentPet(Player player, ObjectGuid guid)
        => !guid.IsEmpty && guid == player.PetGuid ? OwnedPet(player, guid) : null;

    // --- SMSG_PET_SPELLS --------------------------------------------------------------------------

    /// <summary>vmangos Player::PetSpellInitialize: the bar, the spells (a summoned pet lists them, a guardian does not) and the cooldowns.</summary>
    public void SendPetSpells(Player player, Creature pet)
    {
        if (pet.Summon is not { Charm: { } charm } links)
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgPetSpells, PetPackets.BuildPetSpells(pet, charm, links.Kind == SummonKind.Pet, _spells()?.GetActiveCooldowns(pet) ?? []));
    }

    /// <summary>vmangos HandleRequestPetInfoOpcode: resend the bar of the current pet.</summary>
    public void HandleRequestPetInfo(Player player)
    {
        if (player.Map?.FindObject(player.PetGuid) is Creature pet)
        {
            SendPetSpells(player, pet);
        }
    }

    // --- CMSG_PET_ACTION ----------------------------------------------------------------------------

    /// <summary>vmangos WorldSession::HandlePetAction (PetHandler.cpp:35-165).</summary>
    public void HandleAction(Player player, PetActionRequest request)
    {
        if (OwnedPet(player, request.Pet) is not var (pet, charm) || !pet.IsAlive)
        {
            return;
        }

        // pet can have action bar disabled
        if (pet.IsPet && !charm.Enabled)
        {
            return;
        }

        switch ((ActionType)request.Type)
        {
            case ActionType.Command:
            {
                Unit? target = request.Target.IsEmpty ? null : player.Map?.FindObject(request.Target) as Unit;
                HandleCommand(pet, (CommandState)request.Action, target);
                break;
            }

            case ActionType.Reaction:
                switch (request.Action)
                {
                    case (uint)ReactState.Passive:
                        InterruptNonMeleeSpells(pet);
                        pet.Map?.Combat.AttackStop(pet);
                        charm.ReactState = ReactState.Passive;
                        break;
                    case (uint)ReactState.Defensive:
                    case (uint)ReactState.Aggressive:
                        charm.ReactState = (ReactState)request.Action;
                        break;
                }

                break;

            case ActionType.Disabled:
            case ActionType.Passive:
            case ActionType.Enabled:
                CastFromBar(player, pet, request);
                break;
        }
    }

    /// <summary>vmangos Unit::HandlePetCommand (Unit.cpp:8646-8764).</summary>
    public void HandleCommand(Creature pet, CommandState command, Unit? target)
    {
        if (pet.Summon?.Charm is not { } charm || pet.GetOwner() is not { } owner)
        {
            return;
        }

        CreatureMapSystem? system = pet.System;
        switch (command)
        {
            case CommandState.Stay:
                system?.StopMoving(pet);
                pet.Motion.Clear();
                charm.IsAtStay = true;
                charm.CommandState = CommandState.Stay;
                charm.IsCommandAttack = false;
                charm.IsCommandFollow = false;
                charm.IsFollowing = false;
                charm.IsReturning = false;
                charm.StayPosition = pet.Spline is { } spline ? (spline.EndX, spline.EndY, spline.EndZ) : (pet.X, pet.Y, pet.Z);
                break;

            case CommandState.Follow:
                pet.Map?.Combat.AttackStop(pet);
                InterruptNonMeleeSpells(pet);
                pet.Motion.MoveFollow(owner, PetConstants.FollowDistance, PetConstants.FollowAngle);
                charm.CommandState = CommandState.Follow;
                charm.IsCommandAttack = false;
                charm.IsAtStay = false;
                charm.IsReturning = true;
                charm.IsCommandFollow = true;
                charm.IsFollowing = false;
                break;

            case CommandState.Attack:
                CommandAttack(pet, charm, owner, target);
                break;

            case CommandState.Dismiss:
                // vmangos: dismissing a summoned pet is like killing it; a mini pet or guardian just goes. "Hunter pets are
                // dismissed with a spell with a cast time" (Dismiss Pet, SPELL_EFFECT_DISMISS_PET), so the command leaves them.
                // A charmed unit would be released (pCharmer->Uncharm()); charm is not modelled in this build.
                if (pet.Summon?.Kind == SummonKind.Pet && owner is Player { Class: Class.Hunter })
                {
                    break;
                }

                _summons.Unsummon(pet);
                break;
        }
    }

    private void CommandAttack(Creature pet, CharmInfo charm, Unit owner, Unit? target)
    {
        if (target is null)
        {
            SendFeedback(owner, PetFeedback.NothingToAttack);
            return;
        }

        // !pCharmer->IsValidAttackTarget(pTarget) || pCharmer->HasAuraType(SPELL_AURA_MOD_PACIFY)
        if (pet.Map is not { } map || !map.Combat.Hooks.CanAttack(owner, target) || _spells()?.HasLiveAura(owner, AuraType.ModPacify) == true)
        {
            SendFeedback(owner, PetFeedback.CantAttackTarget);
            return;
        }

        // This is true if pet has no target or has target but targets differs.
        if (pet.Combat.Victim == target && charm.IsCommandAttack)
        {
            return;
        }

        if (pet.Combat.Victim is not null)
        {
            map.Combat.AttackStop(pet);
        }

        charm.IsCommandAttack = true;
        charm.IsAtStay = false;
        charm.IsFollowing = false;
        charm.IsCommandFollow = false;
        charm.IsReturning = false;
        if (pet.AI is PetAI petAi)
        {
            petAi.AttackTarget(target);
        }
        else
        {
            pet.AI?.AttackStart(target);
        }

        // 10% chance to play special pet attack talk, else growl
        if (pet.Summon?.Kind == SummonKind.Pet && !ReferenceEquals(pet, target) && _random.Next(0, 101) < 10)
        {
            SendTalk(owner, pet, PetTalk.Attack);
        }
        else
        {
            SendAiReaction(owner, pet);
        }
    }

    /// <summary>The spell buttons of CMSG_PET_ACTION (PetHandler.cpp:96-163): ACT_DISABLED, ACT_PASSIVE and ACT_ENABLED.</summary>
    private void CastFromBar(Player player, Creature pet, PetActionRequest request)
    {
        if (_spells() is not { } spells || spells.Store.Get(request.Action) is not { } spell)
        {
            return; // do not cast unknown spells
        }

        if (!spells.IsSpellReady(pet, spell))
        {
            SendCastFailed(player, spell.Id, SpellCastResult.NotReady);
            return;
        }

        // do not cast not learned spells
        if (pet.Summon?.Charm?.HasSpell(spell.Id) != true || spell.IsPassive)
        {
            SendCastFailed(player, spell.Id, SpellCastResult.NotKnown);
            return;
        }

        Unit? target = request.Target.IsEmpty ? null : player.Map?.FindObject(request.Target) as Unit;
        bool explicitTarget = IsExplicitlySelectedUnitTarget(spell.Effects[0].TargetA);
        if (target is null && explicitTarget)
        {
            SendCastFailed(player, spell.Id, SpellCastResult.BadImplicitTargets);
            return;
        }

        if (ReferenceEquals(target, pet)
            // Cannot cast negative spells on yourself, nor spells that exclude the caster (Fire Shield).
            && ((explicitTarget && !spell.IsPositive) || spell.HasAttribute(SpellAttributesEx.CantTargetSelf)))
        {
            SendCastFailed(player, spell.Id, SpellCastResult.BadTargets);
            return;
        }

        // remove not needed target
        if (target is not null && !ReferenceEquals(target, pet) && !explicitTarget)
        {
            target = null;
        }

        // make sure pet is facing target
        if (target is not null && !ReferenceEquals(target, pet) && !MapCombat.HasInArc(pet, target, MathF.PI))
        {
            pet.Orientation = Creature.NormalizeOrientation(MathF.Atan2(target.Y - pet.Y, target.X - pet.X));
        }

        pet.System?.StopMoving(pet);
        SpellCastTargets targets = target is null || ReferenceEquals(target, pet) ? SpellCastTargets.ForSelf() : SpellCastTargets.ForUnit(target.Guid);
        SpellCastResult result = spells.CastSpell(pet, spell.Id, targets, triggered: false);
        if (result != SpellCastResult.CastOk)
        {
            SendCastFailed(player, spell.Id, result);
        }
    }

    // --- CMSG_PET_SET_ACTION --------------------------------------------------------------------------

    /// <summary>vmangos WorldSession::HandlePetSetAction (PetHandler.cpp:198-290).</summary>
    public void HandleSetAction(Player player, PetSetActionRequest request)
    {
        if (CurrentPet(player, request.Pet) is not var (pet, charm) || (pet.IsPet && !charm.Enabled))
        {
            return;
        }

        bool moveCommand = false;
        foreach ((uint position, uint data) in request.Actions)
        {
            byte type = (byte)(data >> 24);

            // ignore invalid position
            if (position >= CharmInfo.ActionBarSize)
            {
                return;
            }

            // command and reaction buttons can only be moved, not removed
            if (type is (byte)ActionType.Command or (byte)ActionType.Reaction)
            {
                if (request.Actions.Count == 1)
                {
                    return;
                }

                moveCommand = true;
            }
        }

        // check swap (at command->spell swap the client removes the spell first in another packet)
        if (moveCommand)
        {
            ActionButton first = new(request.Actions[0].Data);
            ActionButton second = new(request.Actions[1].Data);
            if (first.Type is (byte)ActionType.Command or (byte)ActionType.Reaction)
            {
                ActionButton other = charm.GetButton((int)request.Actions[1].Position);
                if (other.Action != first.Action || other.Type != first.Type)
                {
                    return;
                }
            }

            if (second.Type is (byte)ActionType.Command or (byte)ActionType.Reaction)
            {
                ActionButton other = charm.GetButton((int)request.Actions[0].Position);
                if (other.Action != second.Action || other.Type != second.Type)
                {
                    return;
                }
            }
        }

        foreach ((uint position, uint data) in request.Actions)
        {
            var button = new ActionButton(data);
            uint spellId = button.Action;
            byte type = button.Type;

            // a spell action (enable, disable, passive) for a spell the pet does not know is dropped; 0 removes
            bool spellAction = type is (byte)ActionType.Enabled or (byte)ActionType.Disabled or (byte)ActionType.Passive;
            if (spellAction && spellId != 0 && !charm.HasSpell(spellId))
            {
                continue;
            }

            if (type == (byte)ActionType.Enabled && spellId != 0)
            {
                charm.SetSpellAutocast(spellId, true);
            }
            else if (type == (byte)ActionType.Disabled && spellId != 0)
            {
                charm.SetSpellAutocast(spellId, false);
            }

            charm.SetActionBar((int)position, spellId, type);
        }
    }

    // --- the rest ---------------------------------------------------------------------------------------

    /// <summary>vmangos HandlePetSpellAutocastOpcode (PetHandler.cpp:420-450).</summary>
    public void HandleAutocast(Player player, PetAutocastRequest request)
    {
        if (CurrentPet(player, request.Pet) is not var (_, charm) || _spells() is not { } spells
            || spells.Store.Get(request.Spell) is not { IsPassive: false })
        {
            return;
        }

        // do not add not learned spells / passive spells
        if (charm.HasSpell(request.Spell))
        {
            charm.SetSpellAutocast(request.Spell, request.Enabled);
        }
    }

    /// <summary>vmangos HandlePetStopAttack (PetHandler.cpp:365-385).</summary>
    public void HandleStopAttack(Player player, ObjectGuid petGuid)
    {
        if (OwnedPet(player, petGuid) is { Pet: var pet } && pet.IsAlive)
        {
            pet.Map?.Combat.AttackStop(pet);
        }
    }

    /// <summary>vmangos HandlePetCastSpellOpcode (PetHandler.cpp:452-512).</summary>
    public void HandleCast(Player player, PetCastRequest request)
    {
        if (CurrentPet(player, request.Pet) is not var (pet, charm) || _spells() is not { } spells || spells.Store.Get(request.Spell) is not { } spell
            || !spells.IsSpellReady(pet, spell) || !charm.HasSpell(spell.Id) || spell.IsPassive)
        {
            return;
        }

        pet.System?.StopMoving(pet);
        SpellCastResult result = spells.CastSpell(pet, spell.Id, request.Targets, triggered: false);
        if (result == SpellCastResult.CastOk)
        {
            // 10% chance to play special pet attack talk, else growl
            if (pet.Summon?.Kind == SummonKind.Pet && _random.Next(0, 101) < 10)
            {
                SendTalk(player, pet, PetTalk.SpecialSpell);
            }
            else
            {
                SendAiReaction(player, pet);
            }

            return;
        }

        SendCastFailed(player, spell.Id, result);
        if (spells.IsSpellReady(pet, spell))
        {
            player.Session.Send(WorldOpcode.SmsgClearCooldown, SpellPackets.BuildClearCooldown(spell.Id, pet.Guid));
        }
    }

    /// <summary>vmangos HandlePetCancelAuraOpcode (SpellHandler.cpp:407-432).</summary>
    public void HandleCancelAura(Player player, ObjectGuid petGuid, uint spellId)
    {
        if (_spells() is not { } spells || spells.Store.Get(spellId) is null || CurrentPet(player, petGuid) is not var (pet, _))
        {
            return;
        }

        if (!pet.IsAlive)
        {
            SendFeedback(player, PetFeedback.PetDead);
            return;
        }

        spells.RemoveAuras(pet, spellId);
    }

    /// <summary>vmangos HandlePetNameQueryOpcode / SendPetNameQuery (PetHandler.cpp:167-185): only when the pet number matches.</summary>
    public void HandleNameQuery(Player player, uint petNumber, ObjectGuid petGuid)
    {
        if (player.Map?.FindObject(petGuid) is not Creature { Summon.Charm: { } charm } pet || charm.PetNumber != petNumber)
        {
            return;
        }

        player.Session.Send(WorldOpcode.SmsgPetNameQueryResponse,
            PetPackets.BuildNameQueryResponse(petNumber, charm.Name, charm.NameTimestamp));
    }

    public void HandleRename(Player player, PetRenameRequest request)
    {
        if (!player.IsInWorld || player.IsQuestSettlementPending
            || (_spells() is { } spells && spells.IsInTransit(player)))
        {
            return;
        }

        if (player.Class != Class.Hunter || player.PetGuid != request.Pet
            || OwnedPet(player, request.Pet) is not { } owned
            || owned.Pet.Summon?.Kind != SummonKind.Pet || !owned.Charm.RenameAllowed
            || (owned.Pet.UnitFlags & UnitFlags.PetRename) == 0)
        {
            return;
        }

        string? name = PetNameNormalizer?.Invoke(request.Name);
        if (name is null)
        {
            player.Session.Send(WorldOpcode.SmsgPetNameInvalid, PetPackets.BuildNameInvalid());
            return;
        }

        owned.Charm.Name = name;
        owned.Charm.NameTimestamp = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        owned.Charm.RenameAllowed = false;
        owned.Pet.UnitFlags &= ~UnitFlags.PetRename;
        owned.Pet.SetUInt32(UpdateFields.UnitFieldPetNameTimestamp, owned.Charm.NameTimestamp);
        _summons.QueueCurrentPetSave(player);
        PetNameChanged?.Invoke(player);
    }

    /// <summary>vmangos HandlePetAbandon (PetHandler.cpp:347-368): an owned hunter pet is permanently deleted; other controlled summons are dismissed.</summary>
    public void HandleAbandon(Player player, ObjectGuid petGuid)
    {
        if (!player.IsInWorld || player.IsQuestSettlementPending
            || (_spells() is { } spells && spells.IsInTransit(player)))
        {
            return;
        }

        if (player.Map?.FindObject(petGuid) is not Creature { Summon.Charm: not null } pet
            || pet.OwnerGuid != player.Guid)
        {
            return;
        }

        if (player.Class == Class.Hunter && pet.Summon?.Kind == SummonKind.Pet
            && player.PetGuid == pet.Guid)
        {
            _summons.QueueDeletePet(player);
            _summons.ForgetPetForSpiritHealer(player); // PET_SAVE_AS_DELETED: "do not rez the pet in BG"
        }

        _summons.Unsummon(pet);
    }

    /// <summary>
    /// vmangos Pet::SetEnabled (Pet.cpp:2362-2377): grey the pet's bar out or back in (a mounted owner)
    /// and tell the owner with SMSG_PET_MODE. The mount code of the owner's lane calls it.
    /// </summary>
    public void SetEnabled(Creature pet, bool enabled)
    {
        if (pet.Summon?.Charm is not { } charm)
        {
            return;
        }

        charm.Enabled = enabled;
        if (pet.GetOwner() is Player owner)
        {
            owner.Session.Send(WorldOpcode.SmsgPetMode, PetPackets.BuildPetMode(pet, charm));
        }
    }

    // --- responses (vmangos Unit::SendPet*) ------------------------------------------------------------------

    private void InterruptNonMeleeSpells(Creature pet)
    {
        if (_spells() is { } spells && spells.GetState(pet.Guid)?.CurrentCast is { } cast)
        {
            spells.CancelCast(pet, cast.Spell.Id);
        }
    }

    private static void SendFeedback(Unit owner, PetFeedback feedback)
    {
        if (owner is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgPetActionFeedback, PetPackets.BuildActionFeedback(feedback));
        }
    }

    private static void SendCastFailed(Player player, uint spellId, SpellCastResult result)
        => player.Session.Send(WorldOpcode.SmsgPetCastFailed, PetPackets.BuildCastFailed(spellId, result));

    private static void SendTalk(Unit owner, Creature pet, PetTalk talk)
    {
        if (owner is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgPetActionSound, PetPackets.BuildActionSound(pet.Guid, talk));
        }
    }

    private static void SendAiReaction(Unit owner, Creature pet)
    {
        if (owner is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgAiReaction, PetPackets.BuildAiReaction(pet.Guid));
        }
    }

    /// <summary>
    /// vmangos Spells::IsExplicitlySelectedUnitTarget: the implicit targets that need a unit chosen on
    /// the client. The set is the one this build's own targeting resolves from the explicit target
    /// (<c>SpellSystem.SelectEffectTargets</c>).
    /// </summary>
    internal static bool IsExplicitlySelectedUnitTarget(SpellImplicitTarget target)
        => target is SpellImplicitTarget.UnitEnemy or SpellImplicitTarget.UnitFriend or SpellImplicitTarget.Unit
            or SpellImplicitTarget.UnitFriendChainHeal or SpellImplicitTarget.UnitParty or SpellImplicitTarget.UnitRaid;
}

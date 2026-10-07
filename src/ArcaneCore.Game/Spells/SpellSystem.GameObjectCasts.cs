using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Spells cast for a game object (vmangos GameObject is a SpellCaster: goober, spell-caster and trap spells, GameObject.cpp:1987-2027 and
/// 532-537), the ritual spell, and the creating spell's cooldown at a ritual's end (GameObject::FinishRitual).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>
    /// Cast <paramref name="spellId"/> at <paramref name="target"/> for the object <paramref name="source"/>, triggered, from the object's position:
    /// by <paramref name="unitCaster"/> when the object acts for a unit (a trap's owner, vmangos <c>owner->CastSpell(target, spell, true, ..., goGuid)</c>),
    /// otherwise by the object itself. This engine casts only from units, so an object casting by itself is stood in for by its target, with the
    /// object's hostility for target selection (vmangos GameObject::IsHostileTo, GameObject.cpp:2076-2120: a wild object, faction 0, is hostile to
    /// everyone; else the faction template reaction) and without starting combat for a player. Limit of the stand-in: the spell packets and the
    /// damage log name the target as the caster where vmangos names the object.
    /// </summary>
    public SpellCastResult CastForGameObject(GameObject source, uint spellId, Unit target, Unit? unitCaster)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (unitCaster is not null)
        {
            return CastFromObject(unitCaster, spellId, target, source: (source.X, source.Y, source.Z));
        }

        ISpellTargetRelations previous = Relations;
        Relations = new GameObjectCasterRelations(previous, source, target);
        try
        {
            return CastFromObject(target, spellId, target, source: (source.X, source.Y, source.Z));
        }
        finally
        {
            Relations = previous;
        }
    }

    /// <summary>
    /// The spell of a summoning ritual (GameObject::Use, GameObject.cpp:1993-2027): cast by <paramref name="caster"/>, triggered, with the ritual as
    /// its object target and <paramref name="unitTarget"/> (the summon target) as its unit target when given.
    /// </summary>
    public SpellCastResult CastRitualSpell(GameObject ritual, uint spellId, Unit caster, Unit? unitTarget)
    {
        ArgumentNullException.ThrowIfNull(ritual);
        ArgumentNullException.ThrowIfNull(caster);
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = ritual.Guid };
        if (unitTarget is not null)
        {
            targets.Mask |= SpellCastTargetFlags.Unit;
            targets.Unit = unitTarget.Guid;
        }

        _objectCastDepth++;
        try
        {
            return CastSpell(caster, spellId, targets, triggered: true);
        }
        finally
        {
            _objectCastDepth--;
        }
    }

    /// <summary>Whether <paramref name="unit"/> is channelling (vmangos <c>GetCurrentSpell(CURRENT_CHANNELED_SPELL)</c>).</summary>
    public bool IsChanneling(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return GetState(unit.Guid)?.CurrentCast is { State: SpellCastState.Casting };
    }

    /// <summary>
    /// vmangos FinishRitual (GameObject.cpp:806-809, <c>pOwner->AddCooldown(createBySpell)</c>): the creating spell's cooldown starts now. For a
    /// COOLDOWN_ON_EVENT spell this is its event, and the client is told with SMSG_COOLDOWN_EVENT (Player::AddCooldown "haveToSendEvent").
    /// </summary>
    public void StartCreatingSpellCooldown(Unit unit, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (Store.Get(spellId) is not { } spell || !unit.IsInWorld)
        {
            return;
        }

        bool onEvent = spell.HasAttribute(SpellAttributes.CooldownOnEvent);
        AddCooldown(GetOrCreateState(unit), spell, triggered: !onEvent, onEvent: onEvent);
        if (onEvent && unit is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgCooldownEvent, SpellPackets.BuildCooldownEvent(spell.Id, player.Guid));
        }
    }

    /// <summary>The relations of a cast an object makes through its stand-in: the stand-in judges others as the object would.</summary>
    private sealed class GameObjectCasterRelations(ISpellTargetRelations inner, GameObject source, Unit standIn) : ISpellTargetRelations
    {
        public bool IsHostile(Unit caster, Unit target)
            => ReferenceEquals(caster, standIn) ? target.IsAlive && ObjectIsHostileTo(target) : inner.IsHostile(caster, target);

        public bool IsFriendly(Unit caster, Unit target)
            => ReferenceEquals(caster, standIn) ? !ObjectIsHostileTo(target) : inner.IsFriendly(caster, target);

        public bool CanAssist(Unit caster, Unit target)
            => ReferenceEquals(caster, standIn) ? !ObjectIsHostileTo(target) : inner.CanAssist(caster, target);

        private bool ObjectIsHostileTo(Unit target)
        {
            if (target is Player { IsGameMaster: true })
            {
                return false;
            }

            uint faction = source.GetUInt32(UpdateFields.GameobjectFaction);
            if (faction == 0)
            {
                return true;
            }

            return target.Map?.FindUpdater<MapCombat>()?.Hooks is FactionCombatHooks hooks
                && hooks.Factions.Find(faction) is { } tester && hooks.Factions.Find(target.FactionTemplate) is { } other
                && tester.IsHostileTo(other);
        }
    }
}

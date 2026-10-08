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
    /// <summary>vmangos PLAYER_MAX_LEVEL: the level of an object that has none (GameObject::GetLevel, GameObject.cpp:2506-2509).</summary>
    private const byte ObjectDefaultLevel = 60;

    /// <summary>The unit standing in for a game object in the cast under way (<see cref="CastForGameObject"/>), or null.</summary>
    private Unit? _objectStandIn;

    /// <summary>The level of the object <see cref="_objectStandIn"/> stands in for.</summary>
    private byte _objectStandInLevel;

    /// <summary>
    /// Cast <paramref name="spellId"/> at <paramref name="target"/> for the object <paramref name="source"/>, triggered, from the object's position:
    /// by <paramref name="unitCaster"/> when the object acts for a unit (a trap's owner, vmangos <c>owner->CastSpell(target, spell, true, ..., goGuid)</c>),
    /// otherwise by the object itself. This engine casts only from units, so an object casting by itself is stood in for by its target, which
    /// takes the object's place where the object differs from a unit:
    /// <list type="bullet">
    /// <item>relations: the object's (<see cref="GameObjectReactions"/>, vmangos GameObject::IsHostileTo / IsFriendlyTo);</item>
    /// <item>no caster side: a game object has no auras, spell mods or crit (SpellCaster::IsSpellCrit returns false, SpellCaster.h:320; the done
    /// bonuses read unit auras only, SpellCaster.cpp:1457-1700), so the stand-in's spell power, healing power, damage-done percentages, spell
    /// mods and crit chance are left out (<see cref="IsGameObjectStandIn"/>);</item>
    /// <item>level: effect values scale with the object's level (GameObject::GetLevel: chest.level, trap.level, GAMEOBJECT_LEVEL, else 60);</item>
    /// <item>no combat for a player target (Spell.cpp:1650), no range check, no living caster needed (<see cref="CastFromObject"/>).</item>
    /// </list>
    /// Limits of the stand-in: the spell packets and the damage and heal logs name the target as the caster where vmangos names the object; the
    /// hit, resist and armor rolls use the stand-in's level and skills (its own, so no level difference) instead of the object's level; the
    /// damage-taken hooks (aura breaks, pushback) see the stand-in as the attacker, and an aura the cast applies remembers the stand-in as its
    /// caster (its ticks run the target side only, as for any caster).
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
        Unit? previousStandIn = _objectStandIn;
        byte previousLevel = _objectStandInLevel;
        Relations = new GameObjectCasterRelations(previous, source, target);
        _objectStandIn = target;
        _objectStandInLevel = ObjectLevel(source);
        try
        {
            return CastFromObject(target, spellId, target, source: (source.X, source.Y, source.Z));
        }
        finally
        {
            Relations = previous;
            _objectStandIn = previousStandIn;
            _objectStandInLevel = previousLevel;
        }
    }

    /// <summary>
    /// Whether <paramref name="caster"/> is standing in for a game object in the cast under way: it then has no caster side of its own (no spell
    /// power, healing power, done percentages, spell mods or crit), like the object it stands in for.
    /// </summary>
    internal bool IsGameObjectStandIn(Unit caster) => _objectStandIn is not null && ReferenceEquals(caster, _objectStandIn);

    /// <summary>The level a cast of <paramref name="caster"/> scales with: the object's level for a stand-in, else the caster's.</summary>
    internal byte CasterLevelOf(Unit caster) => IsGameObjectStandIn(caster) ? _objectStandInLevel : caster.Level;

    /// <summary>GameObject::GetLevel (GameObject.cpp:2492-2510): chest.level (data9), trap.level (data1), else GAMEOBJECT_LEVEL, else 60.</summary>
    private static byte ObjectLevel(GameObject go)
    {
        uint level = go.Type switch
        {
            GameObjectType.Chest => go.Template.GetData(9),
            GameObjectType.Trap => go.Template.GetData(1),
            _ => 0,
        };
        if (level == 0)
        {
            level = go.GetUInt32(UpdateFields.GameobjectLevel);
        }

        return level == 0 ? ObjectDefaultLevel : (byte)Math.Min(level, byte.MaxValue);
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

    /// <summary>
    /// The relations of a cast an object makes through its stand-in: the stand-in judges others as the object would (<see cref="GameObjectReactions"/>,
    /// with the map's combat hooks for the faction templates and reputations).
    /// </summary>
    private sealed class GameObjectCasterRelations(ISpellTargetRelations inner, GameObject source, Unit standIn) : ISpellTargetRelations
    {
        public bool IsHostile(Unit caster, Unit target)
            => ReferenceEquals(caster, standIn) ? target.IsAlive && GameObjectReactions.IsHostileTo(source, target, HooksOf(target)) : inner.IsHostile(caster, target);

        public bool IsFriendly(Unit caster, Unit target)
            => ReferenceEquals(caster, standIn) ? GameObjectReactions.IsFriendlyTo(source, target, HooksOf(target)) : inner.IsFriendly(caster, target);

        public bool CanAssist(Unit caster, Unit target)
            => ReferenceEquals(caster, standIn) ? GameObjectReactions.CanHelp(source, target, HooksOf(target)) : inner.CanAssist(caster, target);

        private CombatHooks? HooksOf(Unit target) => (source.Map ?? target.Map)?.FindUpdater<MapCombat>()?.Hooks;
    }
}

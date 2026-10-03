using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Spell-created objects (hunter lane): SPELL_EFFECT_SUMMON_OBJECT_SLOT1-4 and the ownership
/// bookkeeping of vmangos Unit::AddGameObject / RemoveGameObject / RemoveAllGameObjects
/// (Unit.cpp:4075-4173) and Spell::EffectSummonObject (SpellEffects.cpp:5151-5226).
/// </summary>
public sealed partial class SpellSystem
{
    /// <summary>vmangos DEFAULT_WORLD_OBJECT_SIZE, the bounding radius of the summoned object (ObjectDefines.h:42).</summary>
    private const float SummonedObjectSize = 0.388999998569489f;

    /// <summary>The objects spells created and their owners.</summary>
    public SpellObjectRegistry SpellObjects { get; } = new();

    /// <summary>Depth of casts made on behalf of a game object (trap): they skip the range check and the caster-alive check.</summary>
    private int _objectCastDepth;

    /// <summary>
    /// A trap's spell (vmangos GameObject::Update: <c>owner->CastSpell(target, spellId, true, ..., trapGuid)</c>, a triggered
    /// cast, which neither checks range nor needs a living caster). Runs synchronously: a triggered cast has no cast time.
    /// The original-caster marker (no combat for a player a trap hits, Spell.cpp:1650) is not modelled.
    /// </summary>
    internal SpellCastResult CastFromObject(Unit owner, uint spellId, Unit target)
    {
        _objectCastDepth++;
        try
        {
            return CastSpell(owner, spellId, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        }
        finally
        {
            _objectCastDepth--;
        }
    }

    /// <summary>SPELL_EFFECT_SUMMON_OBJECT_SLOT1-4 (the slot is the effect: 0x68-0x6B).</summary>
    internal void EffectSummonObject(SpellEffectContext context)
    {
        int slot = (int)context.Effect.Effect - (int)SpellEffectName.SummonObjectSlot1;
        if (slot is < 0 or >= SpellObjectRegistry.SlotCount)
        {
            return;
        }

        Unit caster = context.Caster;
        uint entry = (uint)context.Effect.MiscValue;
        if (caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
        {
            ReportUnsupported("summon object (no game object system)", entry, context.Spell.Id);
            return;
        }

        // The slot's old object goes (vmangos: GO_JUST_DEACTIVATED, the creating spell's event cooldown starts).
        if (SpellObjects.InSlot(caster, slot) is { } old)
        {
            RemoveSpellObject(old, objects, startEventCooldown: true);
        }

        SpellCastTargets targets = context.Cast.Targets;
        float x;
        float y;
        float z;
        if (targets.HasDest)
        {
            (x, y, z) = (targets.Dest.X, targets.Dest.Y, targets.Dest.Z);
        }
        else
        {
            // WorldObject::GetClosePoint(DEFAULT_WORLD_OBJECT_SIZE): the first candidate, one caster radius plus the object's in front.
            float distance = caster.BoundingRadius + SummonedObjectSize;
            (x, y, z) = (caster.X + (MathF.Cos(caster.Orientation) * distance), caster.Y + (MathF.Sin(caster.Orientation) * distance), caster.Z);
        }

        int duration = context.Spell.GetDuration();
        GameObject? go = objects.Summon(entry, x, y, z, caster.Orientation, duration > 0 ? (uint)(duration / 1000) : 0);
        if (go is null)
        {
            ReportUnsupported("summon object (unknown template)", entry, context.Spell.Id);
            return;
        }

        go.SetUInt32(UpdateFields.GameobjectLevel, caster.Level);
        SpellObjects.Add(go, caster, context.Spell.Id, slot);
    }

    /// <summary>
    /// vmangos Unit::RemoveGameObject(del = true): the object leaves the world and its owner's
    /// slot; the creating spell's cooldown, held back while the object lived, starts now when
    /// <paramref name="startEventCooldown"/> is set (SPELL_ATTR_COOLDOWN_ON_EVENT).
    /// </summary>
    internal void RemoveSpellObject(SpellCreatedObject entry, GameObjectMapSystem? objects, bool startEventCooldown)
    {
        if (!SpellObjects.Remove(entry))
        {
            return;
        }

        objects?.Remove(entry.Object);
        if (startEventCooldown && Store.Get(entry.SpellId) is { } spell && spell.HasAttribute(SpellAttributes.CooldownOnEvent)
            && entry.Owner.IsInWorld)
        {
            UnitSpellState state = GetOrCreateState(entry.Owner);
            AddCooldown(state, spell, triggered: false, onEvent: true);
            if (entry.Owner is Player player)
            {
                // The client starts its own timer from this event (vmangos Player::AddCooldown "haveToSendEvent").
                player.Session.Send(WorldOpcode.SmsgCooldownEvent, SpellPackets.BuildCooldownEvent(spell.Id, player.Guid));
            }
        }
    }

    /// <summary>
    /// vmangos Unit::RemoveAllGameObjects (the owner leaves the world): every object goes without
    /// starting any cooldown.
    /// </summary>
    public void RemoveOwnedObjects(Unit owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        GameObjectMapSystem? objects = owner.Map?.FindUpdater<GameObjectMapSystem>();
        foreach (SpellCreatedObject entry in SpellObjects.OwnedBy(owner))
        {
            RemoveSpellObject(entry, objects ?? entry.Object.System, startEventCooldown: false);
        }
    }
}

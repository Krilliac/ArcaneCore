using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Ranged;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_CREATE_HOUSE (81), the "Create House (TEST)" spell 6757, after vmangos <c>Spell::EffectCreateHouse</c> (SpellEffects.cpp:4633):
/// a player caster removes the house it made with this spell before, then summons the game object named by the effect's misc value at the
/// spell's destination, remembering the spell on it. Without a destination there is nowhere to put it (vmangos would read an unset
/// destination), so nothing is summoned.
/// </summary>
public sealed class CreateHouseEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.CreateHouse, static context => context.System.EffectCreateHouse(context));
    }
}

public sealed partial class SpellSystem
{
    internal void EffectCreateHouse(SpellEffectContext context)
    {
        if (context.Caster is not Player player || context.Effect.MiscValue <= 0)
        {
            return;
        }

        uint entry = (uint)context.Effect.MiscValue;
        GameObjectMapSystem? objects = player.Map?.FindUpdater<GameObjectMapSystem>();
        if (objects is null)
        {
            ReportUnsupported("create house (no game object system)", entry, context.Spell.Id);
            return;
        }

        // Unit::RemoveGameObject(spellId, del = true): every object this spell made for the player.
        foreach (SpellCreatedObject old in SpellObjects.OwnedBy(player).Where(o => o.SpellId == context.Spell.Id))
        {
            RemoveSpellObject(old, objects, startEventCooldown: false);
        }

        SpellCastTargets targets = context.Cast.Targets;
        if (!targets.HasDest)
        {
            return;
        }

        GameObject? house = objects.Summon(entry, targets.Dest.X, targets.Dest.Y, targets.Dest.Z, 0f, 0);
        if (house is null)
        {
            ReportUnsupported("create house (unknown template)", entry, context.Spell.Id);
            return;
        }

        house.SetOwner(player.Guid);
        house.SpellId = context.Spell.Id;
        SpellObjects.Add(house, player, context.Spell.Id, -1);
    }
}

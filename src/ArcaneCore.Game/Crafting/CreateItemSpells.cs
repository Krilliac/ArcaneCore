using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Crafting;

/// <summary>
/// SPELL_EFFECT_CREATE_ITEM (crafting lane): the single handler of the server for the 1,159 tradeskill crafts and the
/// non-tradeskill create-item spells (conjures, quest and item spells). Installed by <see cref="Install"/> (the world feature
/// <c>CraftingFeature</c> calls it after the reagent pair, so a craft is never free).
/// <para>
/// Cast-time check (vmangos Spell::CheckItems, Spell.cpp:7311-7330): for a non-triggered cast, effect index 0 only, the target
/// (the caster when the effect's B target is the caster, otherwise the unit target) must be a player
/// (<see cref="SpellCastResult.BadTargets"/>) and <c>CanStoreNewItem</c> must take <c>max(1, value)</c> items, else the equip error
/// is sent and the cast ends with <see cref="SpellCastResult.DontReport"/> (before anything is consumed). The
/// <c>rand_dither</c> of the count is not modelled: ArcaneCore values are whole numbers.
/// </para>
/// <para>
/// Effect (Spell::DoCreateItem, SpellEffects.cpp:1885-1990): <see cref="PlayerInventory.CreateItemFromSpell"/> stores the item (stack clamp,
/// partial store, crafter signature, push result with created set), then <c>UpdateCraftSkill</c> runs when at least one item was
/// stored and the spell is not a battleground mark (<paramref name="grantSkillUp"/> false, for the battlegrounds lane).
/// </para>
/// </summary>
public static class CreateItemSpells
{
    /// <summary>
    /// Register the effect handler and its cast check. Throws unless the reagent check and taker are installed
    /// (<see cref="ReagentRules.Install"/>: otherwise every recipe would be free) or when a CREATE_ITEM handler already exists
    /// (registering it twice would silently replace the other lane's behaviour).
    /// </summary>
    public static void Install(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (!ReagentRules.IsInstalled(system))
        {
            throw new InvalidOperationException("CREATE_ITEM needs the reagent check and cost taker (ReagentRules.Install) or every craft would be free");
        }

        if (system.HasEffectHandler(SpellEffectName.CreateItem))
        {
            throw new InvalidOperationException("a CREATE_ITEM handler is already registered");
        }

        system.RegisterEffect(SpellEffectName.CreateItem, Handle);
        system.RegisterEffectCheck(SpellEffectName.CreateItem, Check);
    }

    /// <summary>The cast check (Spell.cpp:7311-7330).</summary>
    public static SpellCastResult Check(SpellEffectCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Triggered || context.EffectIndex != 0)
        {
            return SpellCastResult.CastOk;
        }

        SpellEffectInfo effect = context.Effect;
        Unit? target = effect.TargetB == SpellImplicitTarget.UnitCaster ? context.Caster : context.UnitTarget ?? SelfWhenUntargeted(context);
        if (target is null)
        {
            return SpellCastResult.CastOk;
        }

        if (target is not Player player)
        {
            return SpellCastResult.BadTargets;
        }

        uint count = (uint)Math.Max(1, context.Spell.CalculateEffectValue(0, context.Caster.Level, context.System.Random));
        InventoryResult result = player.Inventory.CanStoreNewItem(effect.ItemType, count, [], out _);
        if (result == InventoryResult.Ok)
        {
            return SpellCastResult.CastOk;
        }

        player.Inventory.SendEquipError(result, null, null, 0, effect.ItemType);
        return SpellCastResult.DontReport;
    }

    /// <summary>The effect (Spell::EffectCreateItem).</summary>
    public static void Handle(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        DoCreateItem(context.Target, context.Spell, context.Effect.ItemType, context.Value, grantSkillUp: true);
    }

    /// <summary>
    /// Spell::DoCreateItem. <paramref name="grantSkillUp"/> false is the battleground-mark case (SpellEffects.cpp:1962-1970): the
    /// battlegrounds lane calls this method for its mark spells so there is one implementation.
    /// </summary>
    public static void DoCreateItem(Unit target, SpellInfo spell, uint itemEntry, int value, bool grantSkillUp)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spell);
        if (target is not Player player)
        {
            return;
        }

        player.Inventory.CreateItemFromSpell(itemEntry, value, out _, out uint created);
        if (created > 0 && grantSkillUp)
        {
            player.Skills?.UpdateCraft(spell.Id);
        }
    }

    // vmangos fills a self cast's unit target with the caster (SpellCastTargets::read: "filled when calling PrepareForSpellSystem").
    private static Unit? SelfWhenUntargeted(SpellEffectCheckContext context)
        => (context.Targets.Mask & (SpellCastTargetFlags.Unit | SpellCastTargetFlags.UnitEnemy)) == 0 ? context.Caster : null;
}

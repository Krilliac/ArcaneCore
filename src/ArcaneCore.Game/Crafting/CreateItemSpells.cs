using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Crafting;

/// <summary>Mails the part of a battleground mark the bags could not take (vmangos BattleGround::SendRewardMarkByMail).</summary>
public delegate void BattlegroundMarkMailer(Player player, uint spellId, uint itemEntry, uint count);

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
/// stored and the spell is not a battleground mark (<c>grantSkillUp</c> false, for the battlegrounds lane).
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
        if (IsBattlegroundMarkSpell(context.Spell.Id))
        {
            DoCreateBattlegroundMark(context);
            return;
        }

        DoCreateItem(context.Target, context.Spell, context.Effect.ItemType, context.Value, grantSkillUp: true);
    }

    /// <summary>The battleground mark spells (vmangos SpellEffects.cpp:1897-1921 bgType: AV 24954/24955, WS 24950/24951, AB 24952/24953).</summary>
    public static bool IsBattlegroundMarkSpell(uint spellId) => spellId is >= 24950 and <= 24955;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpellSystem, System.Runtime.CompilerServices.StrongBox<BattlegroundMarkMailer?>> MarkMailers = new();

    /// <summary>
    /// Install (or clear with null) the mailer of the battleground mark remainder for <paramref name="system"/>. World thread, at feature
    /// attach; without one a mark that does not fit the bags is lost, as before.
    /// </summary>
    public static void SetBattlegroundMarkMailer(SpellSystem system, BattlegroundMarkMailer? mailer)
    {
        ArgumentNullException.ThrowIfNull(system);
        MarkMailers.GetOrCreateValue(system).Value = mailer;
    }

    // SpellEffects.cpp:1962-1977: a mark is pushed without the created flag, never grants a craft skill-up, and what the bags could
    // not take is mailed from the battlemaster.
    private static void DoCreateBattlegroundMark(SpellEffectContext context)
    {
        if (context.Target is not Player player)
        {
            return;
        }

        player.Inventory.CreateItemFromSpell(context.Effect.ItemType, context.Value, out _, out _, out uint noSpace, battlegroundMark: true);
        if (noSpace > 0 && MarkMailers.TryGetValue(context.System, out System.Runtime.CompilerServices.StrongBox<BattlegroundMarkMailer?>? box) && box.Value is { } mailer)
        {
            mailer(player, context.Spell.Id, context.Effect.ItemType, noSpace);
        }
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

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Crafting;

namespace ArcaneCore.Game.Crafting.Enchanting;

/// <summary>
/// The enchant spell effects (crafting lane): ENCHANT_ITEM (permanent, SpellEffects.cpp:3009-3052), ENCHANT_ITEM_TEMPORARY (:3054-3099) and ENCHANT_HELD_ITEM
/// (:5009-5057), with the cast checks of <c>Spell::CheckItems</c> (Spell.cpp:7311-7375) and the item-target fit check (<see cref="ItemTargetFitCheck"/>).
/// <para>
/// A temporary enchantment lasts the effect value in seconds (<c>damage * 1000</c> ms). Its charge count comes from vmangos' <c>spell_enchant_charges</c>
/// (<see cref="SpellSystem.SpellEnchantCharges"/>, SpellEffects.cpp:3081-3086); without a row it has none (duration only). The held-item effect takes its duration from the base points
/// (<c>EffectBasePoints + EffectBaseDice</c>, vmangos <c>CalculateSimpleValue</c>), else the spell duration, else 10 s.
/// </para>
/// </summary>
/// <param name="catalog">The enchantment catalog.</param>
/// <param name="gmAllowTrades">vmangos <c>GM.AllowTrades</c> (default true): false keeps a game master's enchant from landing (SpellEffects.cpp:3029, :3079).</param>
/// <param name="tradeItems">Finds the item a trade-slot target names; null: trade items are never found.</param>
public sealed class EnchantItemSpells(EnchantCatalog catalog, Func<bool>? gmAllowTrades = null, Func<Player, SpellCastTargets, Item?>? tradeItems = null)
{
    /// <summary>The held-item fallback duration (SpellEffects.cpp:5033-5035: "10 seconds for enchants which don't have listed duration").</summary>
    public const uint HeldItemFallbackMs = 10_000;

    /// <summary>
    /// Register the item-target fit check, the three effects and the two effect checks on <paramref name="system"/>. Throws when one of the effects already
    /// has a handler (installing a second would replace it) or the fit check is already there.
    /// </summary>
    public void Install(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        foreach (SpellEffectName effect in new[] { SpellEffectName.EnchantItem, SpellEffectName.EnchantItemTemporary, SpellEffectName.EnchantHeldItem })
        {
            if (system.HasEffectHandler(effect))
            {
                throw new InvalidOperationException($"the {effect} effect already has a handler");
            }
        }

        if (system.CastChecks.OfType<ItemTargetFitCheck>().Any())
        {
            throw new InvalidOperationException("the item target fit check is already installed");
        }

        system.RegisterCastCheck(new ItemTargetFitCheck(tradeItems));
        system.RegisterEffectCheck(SpellEffectName.EnchantItem, CheckPermanent);
        system.RegisterEffectCheck(SpellEffectName.EnchantItemTemporary, CheckTemporary);
        system.RegisterEffect(SpellEffectName.EnchantItem, Permanent);
        system.RegisterEffect(SpellEffectName.EnchantItemTemporary, Temporary);
        system.RegisterEffect(SpellEffectName.EnchantHeldItem, Held);
    }

    /// <summary>
    /// Fail closed while enchanting is unavailable (no <c>SpellItemEnchantment.dbc</c>, or <c>Enchanting:Enabled</c> false): every enchant effect refuses the cast
    /// with <see cref="SpellCastResult.Unknown"/> before the reagents are taken, so a player never loses rods, dust or essences for an enchant that
    /// would apply nothing. This is an ArcaneCore guard with no vmangos counterpart (vmangos always has the DBC). Installing the real effects afterwards
    /// replaces these checks.
    /// </summary>
    public static void InstallUnavailable(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        foreach (SpellEffectName effect in new[] { SpellEffectName.EnchantItem, SpellEffectName.EnchantItemTemporary, SpellEffectName.EnchantHeldItem })
        {
            system.RegisterEffectCheck(effect, static _ => SpellCastResult.Unknown);
        }
    }

    // --- cast checks (Spell.cpp:7311-7375) ----------------------------------------------------------------------------

    /// <summary>ENCHANT_ITEM: the item must exist, be of a high enough level and, in a trade window, be an enchant that may be traded.</summary>
    private SpellCastResult CheckPermanent(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player player || ItemTargetRules.Resolve(player, context.Targets, tradeItems) is not { } item)
        {
            return SpellCastResult.ItemGone;
        }

        if (item.Template.ItemLevel < context.Spell.BaseLevel)
        {
            return SpellCastResult.Lowlevel;
        }

        return TradeRules(player, item, context);
    }

    /// <summary>
    /// ENCHANT_ITEM_TEMPORARY: as the permanent check, without the item level. A duration that cannot be held in milliseconds (an effect value
    /// above uint.MaxValue / 1000 seconds) is refused before the reagents are taken (an ArcaneCore guard: the reference's damage * 1000 would wrap).
    /// </summary>
    private SpellCastResult CheckTemporary(SpellEffectCheckContext context)
    {
        if ((long)context.Effect.BasePoints + 1 > uint.MaxValue / 1000L)
        {
            return SpellCastResult.ItemNotReady;
        }

        return context.Caster is Player player && ItemTargetRules.Resolve(player, context.Targets, tradeItems) is { } item
            ? TradeRules(player, item, context)
            : SpellCastResult.ItemGone;
    }

    /// <summary>"Not allow enchant in trade slot for some enchant type" (Spell.cpp:7326-7340, :7354-7368).</summary>
    private SpellCastResult TradeRules(Player caster, Item item, SpellEffectCheckContext context)
    {
        if (item.OwnerGuid == caster.Guid)
        {
            return SpellCastResult.CastOk;
        }

        if ((((uint)context.Spell.AttributesEx2) & ItemTargetRules.EnchantOwnItemOnly) != 0)
        {
            return SpellCastResult.NotTradeable;
        }

        if (catalog.Find((uint)context.Effect.MiscValue) is not { } enchant)
        {
            return SpellCastResult.Error;
        }

        return (enchant.Flags & EnchantCatalog.CanSoulboundFlag) != 0 ? SpellCastResult.NotTradeable : SpellCastResult.CastOk;
    }

    // --- effects --------------------------------------------------------------------------------------------------------

    /// <summary>The item owner: a trade-window item belongs to the other player (vmangos <c>itemTarget-&gt;GetOwner()</c>).</summary>
    private static Player? OwnerOf(Item item) => item.Inventory?.Player;

    private bool GmRefused(Player caster) => gmAllowTrades?.Invoke() == false && caster.Security > AccountSecurity.Player;

    /// <summary>
    /// EffectEnchantItemPerm. The craft skill-up comes first, before the enchant is looked up (so it happens even for an enchant the catalog lacks,
    /// SpellEffects.cpp:3018-3019); the old enchantment comes off, the new one is set with the caster logged and goes on when the item is worn.
    /// </summary>
    private void Permanent(SpellEffectContext context)
    {
        if (context.Caster is not Player caster || ItemTargetRules.Resolve(caster, context.Cast.Targets, tradeItems) is not { } item)
        {
            return;
        }

        caster.Skills?.UpdateCraft(context.Spell.Id);   // "not grow at item use at item case"
        uint enchantId = (uint)context.Effect.MiscValue;
        if (enchantId == 0 || catalog.Find(enchantId) is null || OwnerOf(item) is not { } owner || GmRefused(caster))
        {
            return;
        }

        owner.Enchantments?.Apply(item, EnchantSlots.Permanent, apply: false);
        ItemEnchantments.Set(item, EnchantSlots.Permanent, enchantId, 0, 0, caster.Guid);
        owner.Enchantments?.Apply(item, EnchantSlots.Permanent, apply: true);
    }

    /// <summary>EffectEnchantItemTmp: the temporary slot for <c>value * 1000</c> ms with the spell's spell_enchant_charges (see the class remarks).</summary>
    private void Temporary(SpellEffectContext context)
    {
        if (context.Caster is not Player caster || ItemTargetRules.Resolve(caster, context.Cast.Targets, tradeItems) is not { } item)
        {
            return;
        }

        uint enchantId = (uint)context.Effect.MiscValue;
        if (enchantId == 0 || catalog.Find(enchantId) is null || OwnerOf(item) is not { } owner || GmRefused(caster))
        {
            return;
        }

        long duration = (long)Math.Max(context.Value, 0) * 1000L;
        if (duration > uint.MaxValue)
        {
            return;
        }

        uint charges = context.System.SpellEnchantCharges.Find(context.Spell.Id) ?? 0;
        owner.Enchantments?.Apply(item, EnchantSlots.Temporary, apply: false);
        ItemEnchantments.Set(item, EnchantSlots.Temporary, enchantId, (uint)duration, charges, caster.Guid);
        owner.Enchantments?.Apply(item, EnchantSlots.Temporary, apply: true);
    }

    /// <summary>
    /// EffectEnchantHeldItem: the main-hand item of the target player, only while worn; the temporary slot; a different enchantment already on it keeps
    /// its place (the effect does nothing).
    /// </summary>
    private void Held(SpellEffectContext context)
    {
        if (context.Target is not Player owner || owner.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand) is not { } item)
        {
            return;
        }

        uint enchantId = (uint)context.Effect.MiscValue;
        if (enchantId == 0 || catalog.Find(enchantId) is null)
        {
            return;
        }

        SpellEffectInfo effect = context.Effect;
        int seconds = effect.BasePoints + effect.BaseDice;   // vmangos m_currentBasePoints: CalculateSimpleValue
        uint duration = seconds > 0 ? (uint)seconds * 1000 : 0;
        if (duration == 0)
        {
            duration = (uint)Math.Max(context.Spell.Duration.Base, 0);
        }

        if (duration == 0)
        {
            duration = HeldItemFallbackMs;
        }

        uint existing = ItemEnchantments.Id(item, EnchantSlots.Temporary);
        if (existing != 0 && existing != enchantId)
        {
            return;
        }

        uint charges = context.System.SpellEnchantCharges.Find(context.Spell.Id) ?? 0;   // SpellEffects.cpp:5044
        ItemEnchantments.Set(item, EnchantSlots.Temporary, enchantId, duration, charges, context.Caster.Guid);
        owner.Enchantments?.Apply(item, EnchantSlots.Temporary, apply: true);
    }
}

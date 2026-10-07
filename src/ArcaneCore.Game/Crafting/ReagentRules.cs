using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Crafting;

/// <summary>
/// Reagent and tool rules of a cast (crafting lane), after vmangos <c>Spell::CheckItems</c> (Spells/Spell.cpp:7249-7306),
/// <c>Spell::TakeReagents</c> (:5082-5128) and <c>Spell::IgnoreItemRequirements</c> (:7069-7083).
/// <para>
/// The reagents themselves are checked and taken by the cast (<c>SpellSystem.CheckReagents</c> / <c>StageCastReagents</c>: every cast of a
/// player, with vmangos' triggered-cast rule on the triggering spell's first reagent, Spell.cpp:7069-7083). What this adds is the tool check
/// (Totem[2]) and the count that skips items offered in an open trade; a triggered cast skips both here.
/// </para>
/// </summary>
public static class ReagentRules
{
    /// <summary>
    /// Register the check (<see cref="ReagentCastCheck"/>) on <paramref name="system"/> and give the cast's reagent step the trade filter
    /// (<see cref="SpellSystem.ReagentTradeFilter"/>). A second call throws.
    /// </summary>
    /// <param name="system">The spell system.</param>
    /// <param name="isInTrade">
    /// Whether a player has offered an item in an open trade (the economy feature owns trades); null means never. Offered items neither count
    /// as reagents nor are destroyed (vmangos <c>Player::HasItemCount</c> and <c>DestroyItemCount</c> skip <c>IsInTrade</c>, Player.cpp:8681-8734).
    /// </param>
    public static void Install(SpellSystem system, Func<Player, Item, bool>? isInTrade = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (IsInstalled(system))
        {
            throw new InvalidOperationException("the reagent check is already installed");
        }

        system.RegisterCastCheck(new ReagentCastCheck(isInTrade));
        system.ReagentTradeFilter = isInTrade;
    }

    /// <summary>Whether <see cref="Install"/> already ran on <paramref name="system"/>.</summary>
    public static bool IsInstalled(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        return system.CastChecks.OfType<ReagentCastCheck>().Any();
    }

    /// <summary>vmangos <c>IgnoreItemRequirements</c>: only a player pays reagents, and a triggered cast never does (see the class remarks).</summary>
    public static bool IgnoresItemRequirements(Unit caster, bool triggered) => caster is not Player || triggered;

    /// <summary>The predicate that hides a player's traded items from the count and the destruction (null: nothing is hidden).</summary>
    internal static Func<Item, bool>? TradeFilter(Player player, Func<Player, Item, bool>? isInTrade)
        => isInTrade is null ? null : item => isInTrade(player, item);

    /// <summary>
    /// The count of <paramref name="reagent"/> a cast really needs. When the cast item is itself the reagent, is used up by the
    /// cast (a negative <c>spellcharges</c> with fewer than two charges left) and the recipe needs more than one, the item does
    /// not count as a reagent, so the count grows by one (Spell.cpp:7269-7280 check, :5101-5113 take).
    /// </summary>
    public static uint EffectiveCount(Item? castItem, SpellReagent reagent)
    {
        uint count = reagent.Count;
        if (castItem is null || castItem.Entry != reagent.ItemId)
        {
            return count;
        }

        for (int s = 0; s < Item.SpellChargeSlots && s < castItem.Template.Spells.Count; s++)
        {
            int charges = castItem.GetInt32(UpdateFields.ItemFieldSpellCharges + s);
            if (castItem.Template.Spells[s].Charges < 0 && Math.Abs(charges) < 2 && count > 1)
            {
                return count + 1;
            }
        }

        return count;
    }
}

/// <summary>
/// Spell::CheckItems reagents then totems (Spell.cpp:7249-7306): after the equipment checks and the spell focus
/// (<see cref="SpellFocusCastCheck.FocusOrder"/>, :7230), before the effect checks (:7311). A missing reagent is
/// <see cref="SpellCastResult.ItemNotReady"/>, a missing tool <see cref="SpellCastResult.ItemGone"/>.
/// </summary>
public sealed class ReagentCastCheck(Func<Player, Item, bool>? isInTrade = null) : ISpellCastCheck
{
    /// <summary>After the focus check (Equipment + 50), before the item-target fit (Equipment + 70).</summary>
    public const int ReagentOrder = SpellCastCheckOrder.Equipment + 60;

    public SpellCheckPhase Phase => SpellCheckPhase.Items;

    public int Order => ReagentOrder;

    public SpellCastResult Check(in SpellCastCheckContext context)
    {
        SpellInfo spell = context.Spell;
        if (ReagentRules.IgnoresItemRequirements(context.Caster, context.Triggered) || context.Caster is not Player player)
        {
            return SpellCastResult.CastOk;
        }

        Func<Item, bool>? traded = ReagentRules.TradeFilter(player, isInTrade);
        foreach (SpellReagent reagent in spell.Reagents)
        {
            if (!reagent.IsPresent)
            {
                continue;
            }

            if (player.Inventory.GetItemCount(reagent.ItemId, exclude: traded) < ReagentRules.EffectiveCount(context.CastItem, reagent))
            {
                return SpellCastResult.ItemNotReady;
            }
        }

        foreach (uint tool in spell.Totems)
        {
            if (player.Inventory.GetItemCount(tool, exclude: traded) < 1)
            {
                return SpellCastResult.ItemGone;
            }
        }

        return SpellCastResult.CastOk;
    }
}

using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_SUMMON_CHANGE_ITEM (34; vmangos Spell::EffectSummonChangeItem): the item the spell was cast from turns into the effect's item type
/// in the same place (<see cref="Items.PlayerInventory.ChangeItem"/>), once per cast. The cast then no longer has its item, so no charge is taken
/// from the removed one (vmangos ClearCastItem).
/// </summary>
public sealed partial class SpellSystem
{
    private void InstallSummonChangeItem() => RegisterEffect(SpellEffectName.SummonChangeItem, context =>
    {
        if (context.Caster is not Player player || context.Cast.CastItem is not { } item || context.Effect.ItemType == 0
            || item.OwnerGuid != player.Guid)
        {
            return;
        }

        if (player.Inventory.ChangeItem(item, context.Effect.ItemType) is not null)
        {
            context.Cast.CastItem = null;
        }
    });
}

using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Create Healthstone ranks and Improved Healthstone variants: vmangos WarlockCreateHealthstoneScript,
/// src/scripts/spells/spell_warlock.cpp:260-365. The SCRIPT_EFFECT in build 5875 carries no item id,
/// so the script chooses the item by spell rank and the caster's talent aura.
/// </summary>
[SpellScript(5699, 6201, 6202, 11729, 11730)]
public sealed class CreateHealthstoneScript : ISpellScript
{
    private const uint ImprovedRank1 = 18692;
    private const uint ImprovedRank2 = 18693;

    private static readonly uint[][] s_items =
    [
        [5512, 19004, 19005],
        [5511, 19006, 19007],
        [5509, 19008, 19009],
        [5510, 19010, 19011],
        [9421, 19012, 19013],
    ];

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Caster is not Player player)
        {
            return SpellCastResult.CastOk;
        }

        uint item = ItemFor(context.System, player, context.Spell.Id);
        InventoryResult result = player.Inventory.CanStoreNewItem(item, 1, [], out _);
        if (result == InventoryResult.Ok)
        {
            return SpellCastResult.CastOk;
        }

        player.Inventory.SendEquipError(result, null, null, 0, item);
        return SpellCastResult.DontReport;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Target is not Player)
        {
            return;
        }

        uint item = ItemFor(context.System, context.Target, context.Spell.Id);
        CreateItemSpells.DoCreateItem(context.Target, context.Spell, item, 1, grantSkillUp: true);
    }

    private static uint ItemFor(SpellSystem system, Unit unit, uint spellId)
    {
        int rank = 0;
        foreach (SpellAuraHolder holder in system.GetAuras(unit))
        {
            if (holder.IsRemoved || !holder.HasAura(AuraType.Dummy))
            {
                continue;
            }

            if (holder.Spell.Id == ImprovedRank1)
            {
                rank = 1;
                break;
            }

            if (holder.Spell.Id == ImprovedRank2)
            {
                rank = 2;
                break;
            }
        }

        int row = spellId switch
        {
            6201 => 0,
            6202 => 1,
            5699 => 2,
            11729 => 3,
            11730 => 4,
            _ => -1,
        };
        return row < 0 ? 0 : s_items[row][rank];
    }
}

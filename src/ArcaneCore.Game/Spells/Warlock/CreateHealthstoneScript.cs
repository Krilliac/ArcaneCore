using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Create Healthstone, all five ranks (vmangos scripts/spells/spell_warlock.cpp:260-368, <c>WarlockCreateHealthstoneScript</c>, bound to
/// 5699, 6201, 6202, 11729 and 11730 by sql/migrations/20250520041627_world.sql). The build 5875 rows carry one SPELL_EFFECT_SCRIPT_EFFECT and a Soul
/// Shard reagent, no item: the script picks the item from the rank and the caster's Improved Healthstone talent.
/// <list type="bullet">
/// <item>Item (<c>GetItemId</c>): the first SPELL_AURA_DUMMY aura of Improved Healthstone rank 1 (18692) or rank 2 (18693) on the caster selects
/// column 1 or 2 of the rank's row (Minor 5512/19004/19005, Lesser 5511/19006/19007, Healthstone 5509/19008/19009, Greater 5510/19010/19011,
/// Major 9421/19012/19013).</item>
/// <item>Cast check (<c>OnCheckCast</c>, the last check of the cast, before the reagent is taken): a player caster who cannot store one of the item
/// (full bags, or the unique count of 1 reached) gets the equip error and the cast ends with <see cref="SpellCastResult.DontReport"/>.</item>
/// <item>Effect (<c>OnEffectExecute</c>, effect 0 only): <c>Spell::DoCreateItem</c> of that item on the unit target (the caster).</item>
/// </list>
/// The Soul Shard is the spell's reagent and is taken by the normal reagent path (ReagentRules), not by the script.
/// </summary>
[SpellScript(CreateHealthstone, CreateHealthstoneMinor, CreateHealthstoneLesser, CreateHealthstoneGreater, CreateHealthstoneMajor)]
public sealed class CreateHealthstoneScript : ISpellScript
{
    public const uint CreateHealthstone = 5699;
    public const uint CreateHealthstoneMinor = 6201;
    public const uint CreateHealthstoneLesser = 6202;
    public const uint CreateHealthstoneGreater = 11729;
    public const uint CreateHealthstoneMajor = 11730;

    /// <summary>Improved Healthstone talent rank 1 (dummy aura).</summary>
    public const uint ImprovedHealthstoneRank1 = 18692;

    /// <summary>Improved Healthstone talent rank 2 (dummy aura).</summary>
    public const uint ImprovedHealthstoneRank2 = 18693;

    // spell_warlock.cpp:292-299: { plain, improved rank 1, improved rank 2 } per rank.
    private static readonly uint[] Minor = [5512, 19004, 19005];
    private static readonly uint[] Lesser = [5511, 19006, 19007];
    private static readonly uint[] Plain = [5509, 19008, 19009];
    private static readonly uint[] Greater = [5510, 19010, 19011];
    private static readonly uint[] Major = [9421, 19012, 19013];

    /// <summary>vmangos <c>GetItemId</c>: the healthstone <paramref name="caster"/> creates with <paramref name="spellId"/>, or 0 for an unknown rank.</summary>
    public static uint ItemFor(SpellSystem system, Unit caster, uint spellId)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(caster);
        uint[]? row = spellId switch
        {
            CreateHealthstoneMinor => Minor,
            CreateHealthstoneLesser => Lesser,
            CreateHealthstone => Plain,
            CreateHealthstoneGreater => Greater,
            CreateHealthstoneMajor => Major,
            _ => null,
        };
        return row is null ? 0 : row[TalentRank(system, caster)];
    }

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Caster is not Player player)
        {
            return SpellCastResult.CastOk;
        }

        uint itemId = ItemFor(context.System, player, context.Spell.Id);
        if (itemId == 0)
        {
            return SpellCastResult.CastOk;
        }

        InventoryResult result = player.Inventory.CanStoreNewItem(itemId, 1, [], out _);
        if (result == InventoryResult.Ok)
        {
            return SpellCastResult.CastOk;
        }

        player.Inventory.SendEquipError(result, null, null, 0, itemId);
        return SpellCastResult.DontReport;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0)
        {
            return;
        }

        uint itemId = ItemFor(context.System, context.Target, context.Spell.Id);
        if (itemId == 0)
        {
            return;
        }

        CreateItemSpells.DoCreateItem(context.Target, context.Spell, itemId, context.Value, grantSkillUp: true);
    }

    // The first Improved Healthstone dummy aura of the unit decides (vmangos walks GetAurasByType(SPELL_AURA_DUMMY) and breaks on the first match).
    private static int TalentRank(SpellSystem system, Unit unit)
    {
        foreach (SpellAuraHolder holder in system.GetAuras(unit))
        {
            if (holder.IsRemoved || holder.Spell.Id is not (ImprovedHealthstoneRank1 or ImprovedHealthstoneRank2))
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is { Type: AuraType.Dummy })
                {
                    return holder.Spell.Id == ImprovedHealthstoneRank1 ? 1 : 2;
                }
            }
        }

        return 0;
    }
}

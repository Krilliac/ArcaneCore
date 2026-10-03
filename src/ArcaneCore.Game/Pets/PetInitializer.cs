using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Pets;

namespace ArcaneCore.Game.Pets;

/// <summary>
/// The data-driven start of a summoned pet: <c>Pet::InitStatsForLevel</c> (Pet.cpp:1274-1480) for the
/// SUMMON_PET branch from <c>pet_levelstats</c>, and <c>Pet::InitPetCreateSpells</c>
/// (Pet.cpp:2051-2104) from <c>petcreateinfo_spell</c>. What needs data this build does not have (the
/// <c>creature_classlevelstats</c> fallback) or another area's primitives (owner stat inheritance,
/// <c>UpdateAllStats</c>, the skill-line passives, the teach spells) keeps the template values and is
/// listed under the limits of docs/integration/pets.md.
/// </summary>
internal static class PetInitializer
{
    /// <summary>
    /// vmangos <c>Pet::InitStatsForLevel</c> for a SUMMON_PET: the unit flags are cleared, the level set,
    /// then, when <c>pet_levelstats</c> has a row for the creature at that level (levels above 60 use level
    /// 60, a missing level repeats the one below, <see cref="PetContent"/>): melee damage when both bounds
    /// are non-zero (<c>SetBaseWeaponDamage</c>), armor when non-zero, health, mana and the five stats
    /// (<c>SetCreateHealth</c>, <c>SetCreateMana</c>, <c>SetCreateStat</c>). The rank health and damage
    /// rates of a non-player owner default to 1 in vmangos (<c>_GetHealthMod</c>, <c>_GetDamageMod</c>)
    /// and are not configurable here. The owner's player-controlled and PvP flags are copied last.
    /// Health and mana end full.
    /// </summary>
    public static void InitStatsForLevel(Creature pet, Unit owner, int level, PetContent content)
    {
        pet.UnitFlags = UnitFlags.None;
        pet.Level = (byte)Math.Clamp(level, 1, byte.MaxValue);

        if (content.FindLevelStats(pet.Template.Entry, level) is { } row)
        {
            if (row.MinDamage != 0 && row.MaxDamage != 0)
            {
                pet.SetFloat(UpdateFields.UnitFieldMindamage, row.MinDamage);
                pet.SetFloat(UpdateFields.UnitFieldMaxdamage, row.MaxDamage);
            }

            if (row.Armor != 0)
            {
                pet.SetUInt32(UpdateFields.UnitFieldResistances, row.Armor);
            }

            pet.MaxHealth = row.Health;
            pet.SetUInt32(UpdateFields.UnitFieldBaseHealth, row.Health);
            pet.SetUInt32(UpdateFields.UnitFieldBaseMana, row.Mana);
            if (row.Mana > 0)
            {
                pet.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
                pet.SetUInt32(UpdateFields.UnitFieldMaxpower1, row.Mana);
            }

            pet.SetUInt32(UpdateFields.UnitFieldStat0, row.Strength);
            pet.SetUInt32(UpdateFields.UnitFieldStat0 + 1, row.Agility);
            pet.SetUInt32(UpdateFields.UnitFieldStat0 + 2, row.Stamina);
            pet.SetUInt32(UpdateFields.UnitFieldStat0 + 3, row.Intellect);
            pet.SetUInt32(UpdateFields.UnitFieldStat0 + 4, row.Spirit);
        }

        if ((owner.UnitFlags & UnitFlags.PlayerControlled) != 0)
        {
            pet.UnitFlags |= UnitFlags.PlayerControlled;
        }

        if ((owner.UnitFlags & UnitFlags.Pvp) != 0)
        {
            pet.UnitFlags |= UnitFlags.Pvp;
        }

        pet.Health = pet.MaxHealth;
        if (pet.PowerType == PowerType.Mana)
        {
            pet.SetUInt32(UpdateFields.UnitFieldPower1, pet.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        }
    }

    /// <summary>
    /// vmangos <c>Pet::InitPetCreateSpells</c>: every spell of the creature's <c>petcreateinfo_spell</c> row
    /// that exists is learned; a learn spell (SPELL_EFFECT_LEARN_SPELL / LEARN_PET_SPELL) stands for the
    /// spell it triggers (<c>EffectTriggerSpell[0]</c>); a passive spell is cast on the pet at once and kept
    /// off the bar, any other starts with autocast off (<c>ACT_DECIDE</c>, Pet.cpp:1887-1975). The bar is
    /// first reset to its default. A pet with no spell list or without a spell store learns nothing.
    /// </summary>
    public static void InitCreateSpells(Creature pet, PetContent content, SpellSystem? spells)
    {
        if (pet.Summon?.Charm is not { } charm || spells is null)
        {
            return;
        }

        charm.InitPetActionBar();
        foreach (uint spellId in content.GetCreateSpells(pet.Template.Entry))
        {
            if (spells.Store.Get(spellId) is not { } learn)
            {
                continue;
            }

            uint petSpell = learn.Effects[0].Effect is SpellEffectName.LearnSpell or SpellEffectName.LearnPetSpell
                ? learn.Effects[0].TriggerSpell
                : learn.Id;
            if (spells.Store.Get(petSpell) is not { } spell)
            {
                continue;
            }

            if (charm.LearnSpell(spell.Id, spell.IsPassive ? ActionType.Passive : ActionType.Disabled) && spell.IsPassive)
            {
                spells.CastSpell(pet, spell.Id, SpellCastTargets.ForSelf(), triggered: true);
            }
        }
    }
}

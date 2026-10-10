using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Spells;
using SpellFeature = ArcaneCore.World.Spells.SpellFeature;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// The spell half of <c>.character boost</c>: what the class trainers teach at the character's (new) level, and the spells the
/// character's class and race would have earned from class quests up to that level. Every spell goes through
/// <see cref="SpellSystem.LearnSpell"/>, so the client gets SMSG_LEARNED_SPELL and a managed bot rebuilds its ability cache from
/// that packet. World thread; keeps no state.
/// </summary>
internal static class BoostSpellLearner
{
    /// <summary>A rank chain is capped at 30 deep (vmangos SpellMgr.cpp:1440), so thirty passes learn every chain.</summary>
    private const int MaxPasses = 30;

    /// <summary>
    /// Learns every class-trainer spell that is available to <paramref name="player"/> now. This repeats the loop of
    /// <c>.learn all_trainer</c> (SpellVariants.LearnAllTrainer): rows of the player's class trainers only (pet, mount and
    /// tradeskill trainers and other classes' trainers are excluded), each row through the trainer window's GREEN check
    /// (<see cref="QuestNpcServices.AvailableTrainerSpell"/>: level, prerequisite and skill gates), repeated until a pass
    /// teaches nothing because a learned rank unlocks the next. Returns the number of spells learned.
    /// </summary>
    public static int LearnClassTrainerSpells(
        Player player, SpellFeature spells, QuestNpcServices quests, CreatureContent creatures, SkillCatalog skills)
    {
        SpellSystem system = spells.System;
        TrainerSpell[] offers =
        [
            .. quests.Npcs.Content.TrainerSpells.Where(row => creatures.FindTemplate(row.Entry) is { } template
                && (template.NpcFlags & (uint)NpcFlags.Trainer) != 0
                && (TrainerType)template.TrainerType == TrainerType.Class
                && template.TrainerClass == (byte)player.Class),
        ];

        int learned = 0;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool learnedAny = false;
            foreach (TrainerSpell row in offers)
            {
                uint spellId = quests.AvailableTrainerSpell(player, row);
                if (spellId == 0 || system.Store.Get(spellId) is null || skills.IsPrimaryProfessionFirstRankSpell(spellId))
                {
                    continue;
                }

                if (system.LearnSpell(player, spellId))
                {
                    learnedAny = true;
                    learned++;
                }
            }

            if (!learnedAny)
            {
                break;
            }
        }

        return learned;
    }

    /// <summary>
    /// Learns the spells that class quests up to <paramref name="level"/> teach: every quest whose required classes include the
    /// player's class, whose required races are empty or include the player's race, and whose minimum level is within the level;
    /// the teaching spell is <c>RewSpellCast</c>, else <c>RewSpell</c> (vmangos Player.cpp:13196). A spell
    /// it teaches is learned when it belongs to the class's spell family (vmangos SpellDefines.h:34-43), fits the class and race,
    /// is not above the level and is valid (<see cref="SpellVariants.IsSpellValid"/>). Returns the number learned.
    /// </summary>
    public static int LearnClassQuestSpells(
        Player player, byte level, SpellFeature spells, QuestNpcServices quests, IItemTemplateStore? items)
    {
        uint family = ClassFamily(player.Class);
        if (family == 0)
        {
            return 0;
        }

        SpellSystem system = spells.System;
        ISpellLearner? learner = quests.Deps.Spells;
        uint classBit = 1u << ((int)player.Class - 1);
        uint raceBit = 1u << ((int)player.Race - 1);
        int learned = 0;
        foreach (QuestTemplate quest in quests.Quests.Templates.OrderBy(q => q.Entry))
        {
            if ((quest.RequiredClasses & classBit) == 0 || (quest.RequiredRaces != 0 && (quest.RequiredRaces & raceBit) == 0)
                || quest.MinLevel > level)
            {
                continue;
            }

            uint teaching = quest.RewSpellCast != 0 ? quest.RewSpellCast : quest.RewSpell;
            if (teaching == 0 || system.Store.Get(teaching) is not { } teachingSpell)
            {
                continue;
            }

            foreach (SpellEffectInfo effect in teachingSpell.Effects)
            {
                uint id = effect.TriggerSpell;
                if (effect.Effect != SpellEffectName.LearnSpell || system.Store.Get(id) is not { } spell
                    || spell.SpellFamilyName != family || spell.SpellLevel > level
                    || spells.Spellbook.HasSpell(player, id)
                    || !(learner?.IsSpellFitByClassAndRace(player, id) ?? false)
                    || !SpellVariants.IsSpellValid(system, items, id))
                {
                    continue;
                }

                if (system.LearnSpell(player, id))
                {
                    learned++;
                }
            }
        }

        return learned;
    }

    /// <summary>The spell family of a class (vmangos SpellDefines.h:34-43 SpellFamily, ChrClasses ids of build 5875); 0 for none.</summary>
    internal static uint ClassFamily(Class playerClass) => (uint)playerClass switch
    {
        1 => 4, 2 => 10, 3 => 9, 4 => 8, 5 => 6, 7 => 11, 8 => 3, 9 => 5, 11 => 7, _ => 0,
    };
}

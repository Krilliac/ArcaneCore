using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Items;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>Subcommands of vmangos' learnCommandTable (Chat.cpp:492-507, CharacterCommands.cpp:2572-3160).</summary>
public sealed class LearnVariants : ICommandExtension
{
    public string Path => "learn";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("all", AccountSecurity.Administrator, "Learn all eligible class spells of the selected player.", SpellVariants.LearnAll, RetailLevel: 6),
        new ChatCommand("all_gm", AccountSecurity.GameMaster, "Learn the reference GM spell list.", SpellVariants.LearnAllGm, RetailLevel: 3),
        new ChatCommand("all_crafts", AccountSecurity.GameMaster, "Learn every profession and secondary recipe.", SpellVariants.LearnAllCrafts, RetailLevel: 3),
        new ChatCommand("all_default", AccountSecurity.Moderator, "Learn the selected player's race/class and rewarded-quest spells.", SpellVariants.LearnAllDefault, RetailLevel: 2),
        new ChatCommand("all_lang", AccountSecurity.Moderator, "Learn all loaded language spells.", SpellVariants.LearnAllLanguages, RetailLevel: 1),
        new ChatCommand("all_myclass", AccountSecurity.Administrator, "Learn your class spells and talents.", SpellVariants.LearnAllMyClass, RetailLevel: 5),
        new ChatCommand("all_myspells", AccountSecurity.Administrator, "Learn your class spells.", SpellVariants.LearnAllMySpells, RetailLevel: 5),
        new ChatCommand("all_mytalents", AccountSecurity.Administrator, "Learn your class talents at their highest rank.", SpellVariants.LearnAllMyTalents, RetailLevel: 5),
        new ChatCommand("all_mytaxis", AccountSecurity.Moderator, "Discover taxi nodes near known flightmasters.", SpellVariants.LearnAllMyTaxis, RetailLevel: 2),
        new ChatCommand("all_recipes", AccountSecurity.GameMaster, "Syntax: .learn all_recipes $profession", SpellVariants.LearnAllRecipes, RetailLevel: 3),
        new ChatCommand("all_trainer", AccountSecurity.GameMaster, "Syntax: .learn all_trainer [#trainerTemplate]", SpellVariants.LearnAllTrainer, RetailLevel: 3),
        new ChatCommand("all_items", AccountSecurity.GameMaster, "Learn usable recipes taught by loaded items.", SpellVariants.LearnAllItems, RetailLevel: 3),
    ];
}

/// <summary>Subcommands of vmangos' unlearnCommandTable (Chat.cpp:511-516).</summary>
public sealed class UnlearnVariants : ICommandExtension
{
    public string Path => "unlearn";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("all_gm", AccountSecurity.GameMaster, "Forget the reference GM spell list.", SpellVariants.UnlearnAllGm, RetailLevel: 3),
        new ChatCommand("all_crafts", AccountSecurity.GameMaster, "Forget profession and secondary recipes.", SpellVariants.UnlearnAllCrafts, RetailLevel: 3),
        new ChatCommand("all_recipes", AccountSecurity.GameMaster, "Syntax: .unlearn all_recipes $profession", SpellVariants.UnlearnAllRecipes, RetailLevel: 3),
    ];
}

internal static class SpellVariants
{
    // vmangos CharacterCommands.cpp:2627-2667 (gmSpellList); ids are source facts, no source code copied.
    private static readonly uint[] GmSpells =
    [
        5, 265, 30879, 7482, 8295, 10073, 11821, 18389, 18390, 19901, 27254, 27255, 27258, 27261,
        25059, 26666, 24341, 26687, 29313, 1302, 9454, 31366, 1908, 30839, 8358, 23965,
        456, 2765, 1509, 18139, 6147, 2763, 20114, 20115,
    ];

    // vmangos ObjectMgr.cpp:84-101 lang_description for build 5875; Addon/Universal have no spell or skill.
    private static readonly (uint Language, uint Spell, uint Skill)[] Languages =
    [
        ((uint)Language.Orcish, 669, SkillIds.LanguageOrcish),
        ((uint)Language.Darnassian, 671, SkillIds.LanguageDarnassian),
        ((uint)Language.Taurahe, 670, SkillIds.LanguageTaurahe),
        ((uint)Language.Dwarvish, 672, SkillIds.LanguageDwarven),
        ((uint)Language.Common, 668, SkillIds.LanguageCommon),
        ((uint)Language.Demonic, 815, SkillIds.LanguageDemonTongue),
        ((uint)Language.Titan, 816, SkillIds.LanguageTitan),
        ((uint)Language.Thalassian, 813, SkillIds.LanguageThalassian),
        ((uint)Language.Draconic, 814, SkillIds.LanguageDraconic),
        ((uint)Language.Kalimag, 817, SkillIds.LanguageOldTongue),
        ((uint)Language.Gnomish, 7340, SkillIds.LanguageGnomish),
        ((uint)Language.Troll, 7341, SkillIds.LanguageTroll),
        ((uint)Language.Gutterspeak, 17737, SkillIds.LanguageGutterspeak),
    ];

    private static SpellFeature Spells(CommandContext context) => context.Session.Services.GetRequiredService<SpellFeature>();

    private static bool Ready(CommandContext context, Player player)
    {
        if (player.CanMutateQuestSettlementState)
        {
            return true;
        }

        context.Reply($"{player.Name} is settling a quest reward; try again in a moment.");
        return false;
    }

    private static Player? Selected(CommandContext context)
    {
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
        }

        return target is not null && context.CanActOn(target) ? target : null;
    }

    private static bool NoArgs(string args) => args.Trim().Length == 0;

    private static void Learn(SpellSystem system, Player player, IEnumerable<uint> spells)
    {
        foreach (uint id in spells.Distinct().Order())
        {
            if (system.Store.Get(id) is not null)
            {
                system.LearnSpell(player, id);
            }
        }
    }

    // Player::LearnDefaultSpells/LearnQuestRewardedSpells (Player.cpp:19278-19369).
    internal static void LearnDefaults(CommandContext context, Player target)
    {
        SpellSystem system = Spells(context).System;
        Learn(system, target, system.Store.GetCreateSpells((byte)target.Race, (byte)target.Class));
        var quests = context.Session.Services.GetRequiredService<QuestNpcFeature>().Services;
        SpellRankChains ranks = Skills(context).Ranks;
        if (quests.StateOf(target) is not { Loaded: true } state)
        {
            return;
        }

        foreach (var (id, status) in state.Quests.Statuses)
        {
            if (!status.Rewarded || quests.Quests.Get(id) is not { } quest
                || system.Store.Get(quest.Template.RewSpellCast) is not { } reward)
            {
                continue;
            }

            // Player::LearnQuestRewardedSpells (Player.cpp:19300-19364): do not revive a higher
            // profession rank after its first rank was unlearned, or add a conflicting specialization.
            uint firstLearned = reward.Effects[0].TriggerSpell;
            if (ranks.Rank(firstLearned) > 1 && !Spells(context).Spellbook.HasSpell(target, firstLearned))
            {
                uint firstRank = ranks.First(firstLearned);
                if (!Spells(context).Spellbook.HasSpell(target, firstRank))
                {
                    continue;
                }

                if (system.Store.Get(firstLearned) is { } learnedInfo
                    && learnedInfo.Effects[0].Effect == SpellEffectName.TradeSkill
                    && learnedInfo.Effects[1].Effect == SpellEffectName.None
                    && Spells(context).Spellbook.GetSpells(target).Any(known => known != firstLearned
                        && ranks.First(known) == firstRank && system.Store.Get(known) is { } knownInfo
                        && knownInfo.Effects[0].Effect == SpellEffectName.TradeSkill
                        && knownInfo.Effects[1].Effect == SpellEffectName.None
                        && !ranks.IsHighRankOf(firstLearned, known)))
                {
                    continue;
                }
            }

            Learn(system, target, reward.Effects.Where(e => e.Effect == SpellEffectName.LearnSpell).Select(e => e.TriggerSpell));
        }
    }

    public static bool LearnAllGm(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        if (!Ready(context, context.Player)) return true;
        Learn(Spells(context).System, context.Player, GmSpells);
        context.Reply("GM spells learned.");
        return true;
    }

    public static bool UnlearnAllGm(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        if (!Ready(context, context.Player)) return true;
        foreach (uint id in GmSpells) Spells(context).System.RemoveSpell(context.Player, id);

        context.Reply("You have forgotten all GM spells.");
        return true;
    }

    public static bool LearnAllDefault(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!GmTargets.TryPlayer(context, parsed, out Player target) || !context.CanActOn(target)) return true;
        if (!parsed.IsEmpty) return false;
        if (!Ready(context, target)) return true;
        LearnDefaults(context, target);
        context.Reply($"Default and rewarded-quest spells learned for {target.Name}.");
        return true;
    }

    public static bool LearnAllLanguages(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        Player player = context.Player;
        if (!Ready(context, player)) return true;
        SpellSystem system = Spells(context).System;
        // vmangos HandleLearnAllLangCommand (CharacterCommands.cpp:2966-2981): skip Universal; grant spell and 300/300 skill.
        foreach ((uint language, uint spell, uint skill) in Languages)
        {
            if (system.Store.Get(spell) is null) continue;
            system.LearnSpell(player, spell);
            player.Skills?.LearnLanguage(language);
            player.Skills?.Set(skill, 300, 300);
        }

        context.Reply("All loaded languages learned.");
        return true;
    }

    private static IEnumerable<uint> RecipeSpells(SkillCatalog catalog, SpellSystem system, Player player, uint skill, bool unlearn)
    {
        uint classBit = 1u << ((int)player.Class - 1);
        foreach (SkillLineAbilityRecord ability in catalog.AbilitiesOfSkill(skill))
        {
            if ((!unlearn && ability.ForwardSpellId != 0) || (!unlearn && ability.RaceMask != 0)
                || (!unlearn && ability.ClassMask != 0 && (ability.ClassMask & classBit) == 0))
            {
                continue;
            }

            if (system.Store.Get(ability.SpellId) is not null)
            {
                yield return ability.SpellId;
            }
        }
    }

    private static SkillCatalog Skills(CommandContext context) => context.Session.Services.GetRequiredService<SkillsFeature>().Catalog;

    private static IEnumerable<SkillLineRecord> Crafts(SkillCatalog catalog)
        => catalog.Lines.Where(line => line.Category is SkillCategories.Profession or SkillCategories.Secondary);

    public static bool LearnAllCrafts(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        Player player = context.Player;
        if (!Ready(context, player)) return true;
        SpellSystem system = Spells(context).System;
        SkillCatalog catalog = Skills(context);
        Learn(system, player, Crafts(catalog).SelectMany(line => RecipeSpells(catalog, system, player, line.Id, unlearn: false)));
        context.Reply("All loaded crafts learned.");
        return true;
    }

    public static bool UnlearnAllCrafts(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        Player player = context.Player;
        if (!Ready(context, player)) return true;
        SpellSystem system = Spells(context).System;
        SkillCatalog catalog = Skills(context);
        foreach (uint id in Crafts(catalog).SelectMany(line => RecipeSpells(catalog, system, player, line.Id, unlearn: true)).Distinct())
        {
            system.RemoveSpell(player, id);
        }

        LearnDefaults(context, player); // vmangos restores race/class defaults after the sweep
        context.Reply("You have forgotten all crafts.");
        return true;
    }

    private static SkillLineRecord? Profession(SkillCatalog catalog, string name)
        => Crafts(catalog).OrderBy(line => line.Id).FirstOrDefault(line => line.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static bool Recipes(CommandContext context, string args, bool unlearn)
    {
        string name = args.Trim();
        if (name.Length == 0) return false;
        Player? target = Selected(context);
        if (target is null || !Ready(context, target)) return true;
        SkillCatalog catalog = Skills(context);
        SkillLineRecord? line = Profession(catalog, name);
        if (line is null)
        {
            context.Reply($"Profession '{name}' not found.");
            return true;
        }

        SpellSystem system = Spells(context).System;
        uint[] spells = [.. RecipeSpells(catalog, system, target, line.Id, unlearn)];
        if (unlearn)
        {
            foreach (uint id in spells) system.RemoveSpell(target, id);
            context.Reply($"{target.Name} has forgotten all {line.Name} recipes.");
        }
        else
        {
            Learn(system, target, spells);
            if (target.Skills?.GetMaxPure(line.Id) is > 0 and var max) target.Skills.Set(line.Id, max, max);
            context.Reply($"All {line.Name} recipes learned for {target.Name}.");
        }

        return true;
    }

    public static bool LearnAllRecipes(CommandContext context, string args) => Recipes(context, args, unlearn: false);

    public static bool UnlearnAllRecipes(CommandContext context, string args) => Recipes(context, args, unlearn: true);

    private static uint ClassFamily(Player player) => (uint)player.Class switch
    {
        // vmangos SpellDefines.h:34-43 (spell families) and ChrClasses.dbc class ids, build 5875.
        1 => 4, 2 => 10, 3 => 9, 4 => 8, 5 => 6, 7 => 11, 8 => 3, 9 => 5, 11 => 7, _ => 0,
    };

    private static void LearnMySpells(CommandContext context, Player player)
    {
        SpellSystem system = Spells(context).System;
        SkillCatalog catalog = Skills(context);
        TalentCatalog? talents = context.Session.Services.GetService<TalentFeature>()?.Catalog;
        uint family = ClassFamily(player);
        ISpellLearner? learner = context.Session.Services.GetRequiredService<QuestNpcFeature>().Services.Deps.Spells;
        Learn(system, player, catalog.Lines.SelectMany(line => catalog.AbilitiesOfSkill(line.Id))
            .Select(a => a.SpellId).Where(id => system.Store.Get(id) is { } spell && spell.SpellLevel != 0
                && spell.SpellFamilyName == family && (learner?.IsSpellFitByClassAndRace(player, id) ?? false)
                && (talents is null || !talents.TryGetRankPosition(id, out _))));
    }

    private static void LearnMyTalents(CommandContext context, Player player)
    {
        TalentCatalog? catalog = context.Session.Services.GetService<TalentFeature>()?.Catalog;
        if (catalog is null) return;
        uint classBit = 1u << ((int)player.Class - 1);
        Learn(Spells(context).System, player, catalog.Talents.Where(t => (catalog.Tab(t.TabId)?.ClassMask & classBit) != 0)
            .Select(t => t.RankSpells.LastOrDefault(id => id != 0)));
    }

    public static bool LearnAllMySpells(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        if (!Ready(context, context.Player)) return true;
        LearnMySpells(context, context.Player);
        context.Reply("Class spells learned.");
        return true;
    }

    public static bool LearnAllMyTalents(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        if (!Ready(context, context.Player)) return true;
        LearnMyTalents(context, context.Player);
        context.Reply("Class talents learned.");
        return true;
    }

    public static bool LearnAllMyClass(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        if (!Ready(context, context.Player)) return true;
        LearnMySpells(context, context.Player);
        LearnMyTalents(context, context.Player);

        context.Reply("Class spells and talents learned.");
        return true;
    }

    public static bool LearnAll(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        Player? target = Selected(context);
        if (target is null || !Ready(context, target)) return true;
        SpellSystem system = Spells(context).System;
        TalentCatalog? talents = context.Session.Services.GetService<TalentFeature>()?.Catalog;
        Learn(system, target, system.Store.All.SelectMany(source => source.Effects
            .Where(e => e.Effect == SpellEffectName.LearnSpell && e.TargetA == SpellImplicitTarget.None)
            .Select(e => e.TriggerSpell))
            .Where(id => system.Store.Get(id) is { } spell && (talents is null || !talents.TryGetRankPosition(id, out _))
                && (spell.HasEffect(SpellEffectName.Proficiency) || spell.SpellFamilyName != 0
                    && !spell.IsPassive && !spell.HasAttribute(SpellAttributes.DoNotDisplay)
                    && (spell.AttributesEx2 & (SpellAttributesEx2)0x10) == 0)));
        context.Reply("Eligible class spells learned.");
        return true;
    }

    public static bool LearnAllMyTaxis(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        Player player = context.Player;
        QuestNpcFeature feature = context.Session.Services.GetRequiredService<QuestNpcFeature>();
        var state = feature.Services.StateOf(player);
        if (state is not { Loaded: true })
        {
            context.Reply("Taxi data is not ready.");
            return true;
        }

        CreatureContent creatures = context.Session.Services.GetRequiredService<CreatureWorldFeature>().Content;
        NpcStore npcs = feature.Services.Npcs;
        int learned = 0;
        foreach (CreatureTemplate template in creatures.Templates.Where(t => (t.NpcFlags & (uint)NpcFlags.FlightMaster) != 0))
        {
            // ObjectMgr::FindCreatureData (ObjectMgr.cpp:11348-11387): nearest spawn on the caller's map, else any spawn.
            CreatureSpawn[] spawns = [.. creatures.MapsWithSpawns.Order().SelectMany(map => creatures.GetSpawns(map, template.Entry))];
            CreatureSpawn? spawn = spawns.Where(s => s.MapId == player.MapId)
                .OrderBy(s => MathF.Pow(s.X - player.X, 2) + MathF.Pow(s.Y - player.Y, 2)).FirstOrDefault()
                ?? spawns.OrderBy(s => s.Guid).FirstOrDefault();
            if (spawn is null) continue;
            TaxiNode? node = npcs.Nodes.Where(n => n.MapId == spawn.MapId
                    && (player.Team == Team.Alliance ? n.MountAlliance : n.MountHorde) != 0)
                .OrderBy(n => MathF.Pow(n.X - spawn.X, 2) + MathF.Pow(n.Y - spawn.Y, 2) + MathF.Pow(n.Z - spawn.Z, 2))
                .FirstOrDefault();
            if (node is null) continue;
            int word = (int)((node.Id - 1) / 32);
            uint bit = 1u << (int)((node.Id - 1) % 32);
            if (word >= state.TaxiMask.Length || (state.TaxiMask[word] & bit) != 0) continue;
            state.TaxiMask[word] |= bit;
            player.Session.Send(WorldOpcode.SmsgNewTaxiPath, []);
            learned++;
        }

        if (learned > 0) feature.Persistence.SaveTaxiMask((int)player.Guid.Low, state.TaxiMask);
        context.Reply($"Discovered {learned} taxi nodes.");
        return true;
    }

    public static bool LearnAllTrainer(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        uint trainerId = 0;
        if (!parsed.IsEmpty && (!parsed.ExtractUInt32(out trainerId) || !parsed.IsEmpty)) return false;
        Player player = context.Player;
        if (!Ready(context, player)) return true;
        SpellSystem system = Spells(context).System;
        var feature = context.Session.Services.GetRequiredService<QuestNpcFeature>();
        CreatureContent creatures = context.Session.Services.GetRequiredService<CreatureWorldFeature>().Content;
        SkillCatalog skills = Skills(context);
        IEnumerable<TrainerSpell> rows = feature.Services.Npcs.Content.TrainerSpells;
        if (trainerId != 0) rows = rows.Where(row => row.Entry == trainerId);
        else rows = rows.Where(row => creatures.FindTemplate(row.Entry) is { } template
            && (template.NpcFlags & (uint)NpcFlags.Trainer) != 0
            && ((TrainerType)template.TrainerType != TrainerType.Class || template.TrainerClass == (byte)player.Class)
            && ((TrainerType)template.TrainerType != TrainerType.Pets || (byte)player.Class == 3));
        if (trainerId != 0 && !rows.Any())
        {
            context.Reply("Trainer template not found!");
            return true;
        }

        TrainerSpell[] offers = [.. rows];
        for (int pass = 0; pass < 30; pass++) // vmangos repeats until a pass teaches nothing; a rank chain is capped at 30.
        {
            bool learnedAny = false;
            foreach (TrainerSpell row in offers)
            {
                uint spellId = feature.Services.AvailableTrainerSpell(player, row);
                if (spellId == 0 || system.Store.Get(spellId) is null || skills.IsPrimaryProfessionFirstRankSpell(spellId)) continue;
                learnedAny |= system.LearnSpell(player, spellId);
            }

            if (!learnedAny) break;
        }

        context.Reply("Learned all available spells from trainers.");
        return true;
    }

    public static bool LearnAllItems(CommandContext context, string args)
    {
        if (!NoArgs(args)) return false;
        Player player = context.Player;
        if (!Ready(context, player)) return true;
        SpellSystem system = Spells(context).System;
        SkillCatalog skills = Skills(context);
        if (context.Session.Services.GetRequiredService<ItemsFeature>().LoadedStore is not ItemTemplateStore items)
        {
            context.Reply("Item templates are unavailable.");
            return true;
        }

        // vmangos HandleLearnAllItemsCommand (CharacterCommands.cpp:2890-2940), ITEM_EXTRA_NOT_OBTAINABLE=0x04,
        // ITEM_SPELLTRIGGER_ON_USE=0 (ItemPrototype.h:42,398).
        foreach (var item in items.All)
        {
            if ((item.ExtraFlags & 0x04) != 0 || player.Inventory.CanUseItem(item) != ArcaneCore.Game.Items.InventoryResult.Ok) continue;
            uint teachingId = item.Spells.FirstOrDefault(s => s.SpellId != 0).SpellId;
            if (item.Spells.FirstOrDefault(s => s.SpellId != 0).Trigger != 0
                || system.Store.Get(teachingId) is not { } teaching || !teaching.HasEffect(SpellEffectName.LearnSpell)) continue;
            Learn(system, player, teaching.Effects.Where(e => e.Effect == SpellEffectName.LearnSpell
                && skills.AbilitiesOfSpell(e.TriggerSpell).Count != 0).Select(e => e.TriggerSpell));
        }

        context.Reply("Learned all available spells from items.");
        return true;
    }
}

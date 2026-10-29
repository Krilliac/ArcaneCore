using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using InventoryResult = ArcaneCore.Game.Items.InventoryResult;
using Item = ArcaneCore.Game.Items.Item;
using SpellFeature = ArcaneCore.World.Spells.SpellFeature;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>The content and services <c>.character boost</c> reads, resolved once per command.</summary>
internal sealed record BoostEnvironment(
    ProgressionFeature Progression,
    SpellFeature Spells,
    QuestNpcServices Quests,
    SkillCatalog Skills,
    CreatureContent Creatures,
    ItemTemplateStore Items,
    HashSet<uint> Sources,
    HashSet<uint> QuestOnly,
    CharacterBoostOptions Options,
    BoostSuffixes? Suffixes)
{
    /// <summary>
    /// Resolves the environment, or returns null with the first thing that is missing in <paramref name="missing"/>. Item
    /// templates, and at least one vendor, quest or loot source for them, must be loaded: with none, no kit can be chosen honestly.
    /// </summary>
    public static BoostEnvironment? Resolve(IServiceProvider services, out string missing)
    {
        missing = string.Empty;
        ProgressionFeature? progression = services.GetService<ProgressionFeature>();
        SpellFeature? spells = services.GetService<SpellFeature>();
        QuestNpcFeature? quests = services.GetService<QuestNpcFeature>();
        ArcaneCore.World.Skills.SkillsFeature? skills = services.GetService<ArcaneCore.World.Skills.SkillsFeature>();
        ArcaneCore.World.Creatures.CreatureWorldFeature? creatures = services.GetService<ArcaneCore.World.Creatures.CreatureWorldFeature>();
        ArcaneCore.World.Items.ItemsFeature? items = services.GetService<ArcaneCore.World.Items.ItemsFeature>();
        if (progression is null) { missing = "progression is not available"; return null; }
        if (spells is null) { missing = "spells are not available"; return null; }
        if (quests is null) { missing = "quest and NPC content is not available"; return null; }
        if (skills is null) { missing = "skills are not available"; return null; }
        if (creatures is null) { missing = "creature content is not available"; return null; }
        if (items?.LoadedStore is not ItemTemplateStore { Count: > 0 } store) { missing = "item templates are not loaded"; return null; }

        // Event-bound quests and vendors must not count as obtainable (holiday cosmetics). A world that has no event feature has no
        // events; one that has the feature but not yet its service cannot say, so the boost refuses rather than guess.
        GameEventFeature? eventFeature = services.GetService<GameEventFeature>();
        if (eventFeature is not null && eventFeature.Service is null) { missing = "game events are not loaded yet, so holiday items cannot be told from permanent ones"; return null; }
        GameEventRows eventRows = eventFeature?.Service?.Rows ?? GameEventRows.Empty;

        LootContent loot = services.GetService<GameObjectLootFeature>()?.LootContent ?? LootContent.Empty;
        (HashSet<uint> sources, HashSet<uint> questOnly) = CachedSources(quests.Services.Npcs, quests.Services.Quests, loot, eventRows, creatures.Content);
        if (sources.Count == 0)
        {
            missing = "no vendor, quest or loot data is loaded to choose obtainable items from";
            return null;
        }

        // Random-property items ("of the Bear") need both ItemRandomProperties.dbc with item_enchantment_template and SpellItemEnchantment.dbc
        // to know their stats; without them such items are left out of the kit rather than equipped statless.
        BoostSuffixes? suffixes = BoostSuffixes.TryCreate(
            services.GetService<ArcaneCore.World.Items.ItemRandomPropertyFeature>()?.Properties?.Catalog,
            services.GetService<ArcaneCore.World.Crafting.EnchantingFeature>()?.Catalog,
            spells.System.Store.Get);
        return new BoostEnvironment(progression, spells, quests.Services, skills.Catalog, creatures.Content, store, sources, questOnly,
            CharacterBoostOptions.Bind(services.GetService<IConfiguration>()), suffixes);
    }

    /// <summary>The last sources set with the content objects it was built from. The NPC, quest, loot and event objects are replaced on a reload, so a hit on those is current; the creature content is not (see <see cref="CachedSources"/>).</summary>
    private sealed record SourcesCache(NpcStore Npcs, QuestStore Quests, LootContent Loot, GameEventRows Events, CreatureContent Creatures, HashSet<uint> Sources, HashSet<uint> QuestOnly);

    private static SourcesCache? _sourcesCache;

    /// <summary>
    /// The obtainable-item set, built once per content load instead of once per command (it walks every vendor row, quest and loot
    /// row). The cached set is shared and never mutated after <see cref="BoostItemSources.Build"/> returns. Keyed by reference
    /// identity of the five content objects. The NPC, quest, loot and event objects are replaced on a reload. The creature content is mutated in place (<c>.reload creature_template</c> goes through <c>CreatureContent.SwapDefinitions</c> and bumps <c>DefinitionsVersion</c>), so its reference never changes and a template-only reload does not rebuild this set; that is correct today because <c>BoostEventBinding.From</c> reads only spawns, which that reload leaves alone. A use of template fields in the binding must add <c>DefinitionsVersion</c> to <see cref="SourcesCache"/>. The swap of the single immutable record is atomic.
    /// </summary>
    private static (HashSet<uint> Sources, HashSet<uint> QuestOnly) CachedSources(NpcStore npcs, QuestStore quests, LootContent loot, GameEventRows events, CreatureContent creatures)
    {
        SourcesCache? cached = _sourcesCache;
        if (cached is not null && ReferenceEquals(cached.Npcs, npcs) && ReferenceEquals(cached.Quests, quests) && ReferenceEquals(cached.Loot, loot)
            && ReferenceEquals(cached.Events, events) && ReferenceEquals(cached.Creatures, creatures))
        {
            return (cached.Sources, cached.QuestOnly);
        }

        BoostEventBinding binding = BoostEventBinding.From(events, creatures);
        HashSet<uint> built = BoostItemSources.Build(npcs, quests, loot, binding);
        HashSet<uint> questOnly = BoostItemSources.QuestOnly(npcs, quests, loot, binding);
        _sourcesCache = new SourcesCache(npcs, quests, loot, events, creatures, built, questOnly);
        return (built, questOnly);
    }
}

/// <summary>What a boost did, for the reply.</summary>
internal sealed class BoostReport
{
    public int OldLevel { get; init; }
    public int NewLevel { get; init; }
    public int SpellsLearned { get; set; }
    public int Equipped { get; set; }
    public int Kept { get; set; }
    public int MovedToBags { get; set; }
    public int ButtonsSet { get; set; }

    /// <summary>Incomplete quests of the log that the new level made gray (see <see cref="BoostQuestPruner"/>) and the boost abandoned.</summary>
    public int QuestsDropped { get; set; }
    public uint Money { get; set; }
    public uint AmmoEntry { get; set; }
    public List<byte> EmptySlots { get; } = [];
    public List<(byte Slot, InventoryResult Result)> SlotFailures { get; } = [];
}

/// <summary>
/// The mutation steps of <c>.character boost</c>, in order: level, class-trainer spells, class-quest spells, skills to their
/// maximum, gear, ammo, action bars, money, save (and, right after the level, the incomplete quests it made gray are abandoned). Every step goes through the server's own paths (level via
/// <see cref="LevelCommands.ApplyLevel"/>, spells via <see cref="SpellSystem.LearnSpell"/>, items via
/// <see cref="PlayerInventory.AddItemAt"/> and <see cref="PlayerInventory.SwapItem"/>, buttons via
/// <see cref="Player.SetActionButton"/> and SMSG_ACTION_BUTTONS), so the client and a managed bot's caches see what a normal
/// level-up, learn and equip would show. The caller has validated the target and the level. World thread only.
/// </summary>
internal static class CharacterBoost
{
    /// <summary>Copper per level squared (an ArcaneCore choice: 1g 96s at level 14).</summary>
    public const uint MoneyPerLevelSquared = 100;

    private const uint WeaponBow = 2;
    private const uint WeaponGun = 3;
    private const uint WeaponCrossbow = 18;
    private const uint QualityUncommon = 2;   // ammo stays green or lower

    /// <summary>The gear weights: a managed playerbot's own build (so the kit matches its talents and rotation), else a fixed table by class.</summary>
    public static PlayerbotStatWeights WeightsFor(Player target)
        => target.Session is WorldSession { IsManaged: true }
            ? PlayerbotTalentBuilds.Choose(target.Class, target.Guid.Low).Weights
            : target.Class switch
            {
                Class.Warrior => PlayerbotStatWeights.ShieldTank,
                Class.Paladin => PlayerbotStatWeights.TwoHandStrength,
                Class.Hunter => PlayerbotStatWeights.Hunter,
                Class.Rogue => PlayerbotStatWeights.DualWieldAgility,
                Class.Priest => PlayerbotStatWeights.Healer,
                Class.Shaman => PlayerbotStatWeights.TwoHandShaman,
                Class.Mage or Class.Warlock => PlayerbotStatWeights.Caster,
                Class.Druid => PlayerbotStatWeights.Feral,
                _ => PlayerbotStatWeights.TwoHandStrength,
            };

    /// <summary>Boosts <paramref name="target"/> to <paramref name="level"/> (never lower than its level; validated by the caller).</summary>
    public static BoostReport Run(CommandContext context, Player target, byte level, BoostEnvironment env)
    {
        var report = new BoostReport { OldLevel = target.Level, NewLevel = level };

        // A same-level re-boost keeps the experience: ApplyLevel clears PlayerXp and messages the target.
        if (level != target.Level)
        {
            LevelCommands.ApplyLevel(context, target, env.Progression.Progression, level);
        }

        // Quests the new level made gray are dropped here and only here (a level-up by play keeps them): a boosted character would
        // otherwise carry its starting-zone log into a level where it pays nothing.
        report.QuestsDropped = BoostQuestPruner.DropGrayQuests(target, env.Quests, level);

        report.SpellsLearned += BoostSpellLearner.LearnClassTrainerSpells(target, env.Spells, env.Quests, env.Creatures, env.Skills);
        report.SpellsLearned += BoostSpellLearner.LearnClassQuestSpells(target, level, env.Spells, env.Quests, env.Items);

        // vmangos .maxskill (UpdateSkillsToMaxSkillsForLevel): after the spells, so a weapon skill a trainer just taught is raised too.
        target.Skills?.UpdateSkillsToMax();

        EquipKit(target, level, env, report);
        FillAmmo(target, level, env, report);
        FillTotemTools(target, env);
        FillActionBars(target, env, report);

        uint wanted = MoneyPerLevelSquared * level * level;
        if (target.Money < wanted)
        {
            target.Money = wanted;
        }

        report.Money = target.Money;
        context.World.SavePlayer(target);
        return report;
    }

    private static void EquipKit(Player target, byte level, BoostEnvironment env, BoostReport report)
    {
        PlayerInventory inventory = target.Inventory;
        PlayerbotStatWeights weights = WeightsFor(target);
        IReadOnlyList<ItemTemplate> pool = Candidates(env, level).Gear;
        BoostGearPlan plan = BoostGearPlanner.Plan(pool, weights, target.Class, inventory.Requirements.CanDualWield(inventory),
            template => inventory.CanUseItem(template) == InventoryResult.Ok, env.QuestOnly, env.Suffixes is { } suffixes ? suffixes.For : null);
        report.EmptySlots.AddRange(plan.EmptySlots);

        foreach ((byte slot, ItemTemplate template) in plan.Picks)
        {
            int? property = plan.Suffixes.TryGetValue(slot, out BoostSuffix? suffix) ? suffix.PropertyId : null;
            Item? worn = inventory.GetItem(InventorySlots.Bag0, slot);
            if (worn?.Template.Entry == template.Entry && (property is null || worn.RandomPropertyId == property))
            {
                report.Kept++;
                continue;
            }

            if (slot == InventorySlots.MainHand && template.GetInventoryType() == InventoryType.TwoHandWeapon
                && inventory.GetItem(InventorySlots.Bag0, InventorySlots.OffHand) is not null
                && !MoveToBags(inventory, InventorySlots.OffHand, report))
            {
                continue;
            }

            if (worn is not null && !MoveToBags(inventory, slot, report))
            {
                continue;
            }

            InventoryResult result = inventory.AddItemAt(InventorySlots.Bag0, slot, template.Entry, 1, out _, randomPropertyId: property);
            if (result == InventoryResult.Ok)
            {
                report.Equipped++;
            }
            else
            {
                report.SlotFailures.Add((slot, result));
            }
        }
    }

    /// <summary>Moves what is worn in <paramref name="slot"/> to the first free bag position; records a refusal in the report.</summary>
    private static bool MoveToBags(PlayerInventory inventory, byte slot, BoostReport report)
    {
        Item? worn = inventory.GetItem(InventorySlots.Bag0, slot);
        if (worn is null)
        {
            return true;
        }

        InventoryResult result = inventory.CanUnequipItem(InventorySlots.Bag0, slot, false);
        var dest = new List<ItemPosCount>();
        if (result == InventoryResult.Ok)
        {
            result = inventory.CanStoreItem(InventorySlots.NullBag, InventorySlots.NullSlot, dest, worn, false, out _);
        }

        if (result != InventoryResult.Ok || dest.Count == 0)
        {
            report.SlotFailures.Add((slot, result == InventoryResult.Ok ? InventoryResult.InventoryFull : result));
            return false;
        }

        inventory.SwapItem(InventorySlots.Bag0, slot, dest[0].Bag, dest[0].Slot);
        if (inventory.GetItem(InventorySlots.Bag0, slot) is not null)
        {
            report.SlotFailures.Add((slot, InventoryResult.CantDoRightNow));
            return false;
        }

        report.MovedToBags++;
        return true;
    }

    /// <summary>
    /// A character wearing a bow, gun or crossbow gets one stack of the best obtainable ammo that fits it (vmangos
    /// Player::CheckAmmoCompatibility, Player.cpp:7538-7570) and selects it (Player::SetAmmo, Player.cpp:10125-10148).
    /// </summary>
    private static void FillAmmo(Player target, byte level, BoostEnvironment env, BoostReport report)
    {
        PlayerInventory inventory = target.Inventory;
        if (inventory.GetItem(InventorySlots.Bag0, InventorySlots.Ranged) is not { } ranged
            || (ItemClass)ranged.Template.Class != ItemClass.Weapon
            || ranged.Template.SubClass is not (WeaponBow or WeaponGun or WeaponCrossbow))
        {
            return;
        }

        ItemTemplate? ammo = Candidates(env, level).Ammo
            .Where(t => inventory.CheckAmmoCompatibility(t) && inventory.CanUseAmmo(t.Entry) == InventoryResult.Ok)
            .OrderByDescending(t => t.ItemLevel)
            .ThenByDescending(t => AverageDamage(t))
            .ThenBy(t => t.Entry)
            .FirstOrDefault();
        if (ammo is null)
        {
            return;
        }

        uint have = inventory.GetItemCount(ammo.Entry);
        if (have < ammo.Stackable && inventory.AddItem(ammo.Entry, ammo.Stackable - have, out _, received: true) != InventoryResult.Ok)
        {
            return;
        }

        inventory.SetAmmo(ammo.Entry);
        report.AmmoEntry = ammo.Entry;
    }

    /// <summary>The character-independent candidates for one level: gear pool items and ammo by item level, best first.</summary>
    private sealed record CandidateCache(ItemTemplateStore Items, HashSet<uint> Sources, byte Level, uint MaxQuality, IReadOnlyList<ItemTemplate> Gear, IReadOnlyList<ItemTemplate> Ammo);

    private static CandidateCache? _candidates;

    /// <summary>
    /// The gear pool and ammo list for <paramref name="level"/>, filtered once per (item store, sources set, level, quality cap) instead of once per
    /// command: a 200-bot provision would otherwise scan every item template twice per bot. A content reload replaces the store or the
    /// sources set, so the reference check below is a valid freshness test. World thread only.
    /// </summary>
    private static CandidateCache Candidates(BoostEnvironment env, byte level)
    {
        CandidateCache? cached = _candidates;
        uint maxQuality = env.Options.EffectiveMaxQuality;
        if (cached is not null && cached.Level == level && cached.MaxQuality == maxQuality
            && ReferenceEquals(cached.Items, env.Items) && ReferenceEquals(cached.Sources, env.Sources))
        {
            return cached;
        }

        ItemTemplate[] gear = [.. env.Items.All.Where(t => BoostGearPlanner.IsPoolItem(t, level, env.Sources, maxQuality))];
        ItemTemplate[] ammo = [.. env.Items.All
            .Where(t => (ItemClass)t.Class == ItemClass.Projectile && t.GetInventoryType() == InventoryType.Ammo
                && t.Quality <= QualityUncommon && t.RequiredLevel <= level && t.RandomProperty == 0
                && !t.HasFlag(ItemTemplateFlags.Deprecated) && (t.ExtraFlags & 0x04) == 0 && env.Sources.Contains(t.Entry))];
        var built = new CandidateCache(env.Items, env.Sources, level, maxQuality, gear, ammo);
        _candidates = built;
        return built;
    }

    private static float AverageDamage(ItemTemplate template)
        => template.Damages.Count == 0 ? 0f : (template.Damages[0].Min + template.Damages[0].Max) / 2f;

    private static void FillTotemTools(Player target, BoostEnvironment env)
    {
        PlayerInventory inventory = target.Inventory;
        foreach (uint spellId in env.Spells.Spellbook.GetSpells(target))
        {
            if (env.Spells.System.Store.Get(spellId) is not { Totems.Count: > 0 } spell)
            {
                continue;
            }

            foreach (uint tool in spell.Totems)
            {
                if (tool != 0 && env.Items.Find(tool) is not null && inventory.GetItemCount(tool) < 1)
                {
                    inventory.AddItem(tool, 1, out _, received: true);
                }
            }
        }
    }

    private static void FillActionBars(Player target, BoostEnvironment env, BoostReport report)
    {
        IReadOnlyList<BoostActionButtonWrite> writes = BoostActionBarPlanner.Plan(
            env.Spells.Spellbook.GetSpells(target),
            env.Spells.System.Store.Get,
            env.Skills.Ranks,
            BoostSpellLearner.ClassFamily(target.Class),
            BoostActionBarPlanner.PrimaryStart(target.Class),
            target.ActionButtons);
        foreach (BoostActionButtonWrite write in writes)
        {
            if (target.SetActionButton(write.Button, write.Packed))
            {
                report.ButtonsSet++;
            }
        }

        // The full 120 values, as at login (vmangos MasterPlayer::SendInitialActionButtons). Sending it again in a session is
        // UNVERIFIED to redraw the bars; the buttons are saved with the character either way.
        target.Session.Send(WorldOpcode.SmsgActionButtons, LoginPackets.BuildActionButtons(target.ActionButtons));
    }
}

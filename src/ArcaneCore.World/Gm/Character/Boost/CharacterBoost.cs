using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
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
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using InventoryResult = ArcaneCore.Game.Items.InventoryResult;
using Item = ArcaneCore.Game.Items.Item;
using SpellFeature = ArcaneCore.World.Spells.SpellFeature;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>The content and services <c>.character boost</c> reads, resolved once per command.</summary>
internal sealed record BoostEnvironment(
    ProgressionFeature Progression,
    SpellFeature Spells,
    QuestNpcServices Quests,
    SkillCatalog Skills,
    CreatureContent Creatures,
    ItemTemplateStore Items,
    HashSet<uint> Sources)
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

        LootContent loot = services.GetService<GameObjectLootFeature>()?.LootContent ?? LootContent.Empty;
        HashSet<uint> sources = CachedSources(quests.Services.Npcs, quests.Services.Quests, loot);
        if (sources.Count == 0)
        {
            missing = "no vendor, quest or loot data is loaded to choose obtainable items from";
            return null;
        }

        return new BoostEnvironment(progression, spells, quests.Services, skills.Catalog, creatures.Content, store, sources);
    }

    /// <summary>The last sources set with the content objects it was built from; a content reload replaces those objects, so a hit is always current.</summary>
    private sealed record SourcesCache(NpcStore Npcs, QuestStore Quests, LootContent Loot, HashSet<uint> Sources);

    private static SourcesCache? _sourcesCache;

    /// <summary>
    /// The obtainable-item set, built once per content load instead of once per command (it walks every vendor row, quest and loot
    /// row). The cached set is shared and never mutated after <see cref="BoostItemSources.Build"/> returns. Keyed by reference
    /// identity of the three immutable content objects; the swap of the single immutable record is atomic.
    /// </summary>
    private static HashSet<uint> CachedSources(NpcStore npcs, QuestStore quests, LootContent loot)
    {
        SourcesCache? cached = _sourcesCache;
        if (cached is not null && ReferenceEquals(cached.Npcs, npcs) && ReferenceEquals(cached.Quests, quests) && ReferenceEquals(cached.Loot, loot))
        {
            return cached.Sources;
        }

        HashSet<uint> built = BoostItemSources.Build(npcs, quests, loot);
        _sourcesCache = new SourcesCache(npcs, quests, loot, built);
        return built;
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
    public uint Money { get; set; }
    public uint AmmoEntry { get; set; }
    public List<byte> EmptySlots { get; } = [];
    public List<(byte Slot, InventoryResult Result)> SlotFailures { get; } = [];
}

/// <summary>
/// The mutation steps of <c>.character boost</c>, in order: level, class-trainer spells, class-quest spells, skills to their
/// maximum, gear, ammo, action bars, money, save. Every step goes through the server's own paths (level via
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
    private const uint QualityUncommon = 2;

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

        report.SpellsLearned += BoostSpellLearner.LearnClassTrainerSpells(target, env.Spells, env.Quests, env.Creatures, env.Skills);
        report.SpellsLearned += BoostSpellLearner.LearnClassQuestSpells(target, level, env.Spells, env.Quests, env.Items);

        // vmangos .maxskill (UpdateSkillsToMaxSkillsForLevel): after the spells, so a weapon skill a trainer just taught is raised too.
        target.Skills?.UpdateSkillsToMax();

        EquipKit(target, level, env, report);
        FillAmmo(target, level, env, report);
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
            template => inventory.CanUseItem(template) == InventoryResult.Ok);
        report.EmptySlots.AddRange(plan.EmptySlots);

        foreach ((byte slot, ItemTemplate template) in plan.Picks)
        {
            Item? worn = inventory.GetItem(InventorySlots.Bag0, slot);
            if (worn?.Template.Entry == template.Entry)
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

            InventoryResult result = inventory.AddItemAt(InventorySlots.Bag0, slot, template.Entry, 1, out _);
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
    private sealed record CandidateCache(ItemTemplateStore Items, HashSet<uint> Sources, byte Level, IReadOnlyList<ItemTemplate> Gear, IReadOnlyList<ItemTemplate> Ammo);

    private static CandidateCache? _candidates;

    /// <summary>
    /// The gear pool and ammo list for <paramref name="level"/>, filtered once per (item store, sources set, level) instead of once per
    /// command: a 200-bot provision would otherwise scan every item template twice per bot. A content reload replaces the store or the
    /// sources set, so the reference check below is a valid freshness test. World thread only.
    /// </summary>
    private static CandidateCache Candidates(BoostEnvironment env, byte level)
    {
        CandidateCache? cached = _candidates;
        if (cached is not null && cached.Level == level && ReferenceEquals(cached.Items, env.Items) && ReferenceEquals(cached.Sources, env.Sources))
        {
            return cached;
        }

        ItemTemplate[] gear = [.. env.Items.All.Where(t => BoostGearPlanner.IsPoolItem(t, level, env.Sources))];
        ItemTemplate[] ammo = [.. env.Items.All
            .Where(t => (ItemClass)t.Class == ItemClass.Projectile && t.GetInventoryType() == InventoryType.Ammo
                && t.Quality <= QualityUncommon && t.RequiredLevel <= level && t.RandomProperty == 0
                && !t.HasFlag(ItemTemplateFlags.Deprecated) && (t.ExtraFlags & 0x04) == 0 && env.Sources.Contains(t.Entry))];
        var built = new CandidateCache(env.Items, env.Sources, level, gear, ammo);
        _candidates = built;
        return built;
    }

    private static float AverageDamage(ItemTemplate template)
        => template.Damages.Count == 0 ? 0f : (template.Damages[0].Min + template.Damages[0].Max) / 2f;

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

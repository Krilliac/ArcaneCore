using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Progression;

/// <summary>
/// Out-of-combat crafting and material gathering for one managed playerbot:
/// <list type="bullet">
/// <item><description>Selects class-fitted trade-skill professions and recipes at <c>TrainerType.TradeSkills</c> trainers (<see cref="WantsTradeSkillSpell"/>).</description></item>
/// <item><description>Crafts equipment upgrades, bags, ammunition, consumables and intermediate reagents from known recipes when reagents and tools are in the bags (walking to a nearby <c>SpellFocus</c> object such as a forge or anvil when required).</description></item>
/// <item><description>Actively seeks out crafting materials by harvesting visible herb/mining nodes (<c>SPELL_EFFECT_OPEN_LOCK</c>), skinning looted corpses (<c>SPELL_EFFECT_SKINNING</c>), buying crafting tools and vendor reagents in town (<see cref="NeedsVendorSupply"/>), and hunting creatures that drop missing reagents (<see cref="PreferredMaterialDropperEntry"/>).</description></item>
/// </list>
/// </summary>
internal sealed class PlayerbotCrafting(WorldSession session, PlayerbotOptions options)
{
    private const uint RefusalBackoffMs = 60_000;
    private const float GatherInteractYards = 4.5f;
    private const float MaxGatherRangeYards = 65f;
    private const float MaxSpellFocusRangeYards = 85f;

    /// <summary>A recipe (or a campfire) that cannot be done for want of a spell focus is set aside this long: the fire is not going to appear in a minute.</summary>
    private const uint FocusBackoffMs = 600_000;

    /// <summary>SpellFocusObject.dbc id 4, the cooking fire (gameobject_template 29784 Basic Campfire carries data0 = 4, data1 = 10 yards).</summary>
    private const uint CookingFireFocus = 4;

    /// <summary>Basic Campfire (a Cooking spell, Spell.dbc: effect TRANS_DOOR of gameobject 29784, tool Flint and Tinder 4471, reagent Simple Wood 4470).</summary>
    private const uint BasicCampfireSpell = 818;

    private const uint SimpleWoodItem = 4470;
    private const int MaxDropperCandidates = 32;

    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Dictionary<ulong, uint> _refusedUntil = [];
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _pendingLoot = new();

    private TileWait _tileWait;
    private PlayerbotRoute? _route;
    private ObjectGuid _routeTarget;
    private ObjectGuid _gatheringTarget;
    private bool _waitingForGatherLoot;
    private uint _gatherWaitUntilMs;

    internal PlayerbotGoalKind Goal { get; private set; } = PlayerbotGoalKind.Vendor;
    internal uint TargetEntry { get; private set; }
    internal PlayerbotSuspensions? Suspensions { get; set; }
    internal PlayerbotHomeArea? Home { get; set; }

    /// <summary>
    /// The risk side of gathering (<see cref="PlayerbotOpportunities.NodeVeto"/>): asked of each node the bot could gather before it walks
    /// there; true leaves the node alone (guards the bot would not fight). Null: every reachable node is gathered.
    /// </summary>
    internal Func<GameObject, bool>? GatherVeto { get; set; }

    /// <summary>
    /// Whether the bot wants to learn <paramref name="learnedSpellId"/> at a <c>TrainerType.TradeSkills</c> trainer:
    /// secondary skills (First Aid, Cooking), class-fitted gathering and crafting professions, or any recipe that crafts
    /// a wearable upgrade, bag upgrade, ammunition, reagent, or permanent equipment enchantment.
    /// </summary>
    internal static bool WantsTradeSkillSpell(Player player, SpellStore? store, SpellbookCache? spellbook, uint learnedSpellId)
    {
        if (learnedSpellId == 0)
            return false;
        if (spellbook is not null && spellbook.HasSpell(player, learnedSpellId))
            return false;
        if (store?.Get(learnedSpellId) is not { } spell)
            return true;

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo eff = spell.Effects[i];
            if (eff.Effect is SpellEffectName.Skill or SpellEffectName.SkillStep)
            {
                uint skillId = (uint)Math.Max(0, eff.MiscValue);
                if (skillId != 0 && !IsPreferredSkillForClass(player.Class, skillId)
                    && (player.Skills?.GetValueBase(skillId) ?? 0) == 0)
                {
                    return false;
                }
            }
        }

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo eff = spell.Effects[i];
            if (eff.Effect is SpellEffectName.Skill or SpellEffectName.SkillStep)
                return true;

            if (eff.Effect is SpellEffectName.OpenLock or SpellEffectName.Skinning or SpellEffectName.EnchantItem or SpellEffectName.Disenchant)
                return true;

            if (eff.Effect == SpellEffectName.CreateItem && eff.ItemType != 0)
                return true;
        }

        return true;
    }

    private static bool IsPreferredSkillForClass(Class playerClass, uint skillId)
    {
        if (skillId is SkillIds.FirstAid or SkillIds.Cooking or SkillIds.Fishing)
            return true;

        return playerClass switch
        {
            Class.Warrior or Class.Paladin => skillId is SkillIds.Mining or SkillIds.Blacksmithing or SkillIds.Engineering,
            Class.Hunter or Class.Rogue or Class.Shaman or Class.Druid => skillId is SkillIds.Skinning or SkillIds.Leatherworking or SkillIds.Herbalism or SkillIds.Alchemy,
            Class.Mage or Class.Priest or Class.Warlock => skillId is SkillIds.Tailoring or SkillIds.Herbalism or SkillIds.Alchemy or SkillIds.Enchanting,
            _ => true,
        };
    }

    /// <summary>
    /// Whether the bot needs <paramref name="itemId"/> from a vendor to craft or gather: a profession tool (<c>SpellInfo.Totems</c>,
    /// Mining Pick 2901, Blacksmith Hammer 5956, Skinning Knife 7005) not yet in the bags, or a reagent required by a known recipe
    /// that would craft an equipment upgrade, bag upgrade, or needed supply.
    /// </summary>
    internal static bool NeedsVendorSupply(Player player, uint itemId, SpellStore? store, SpellbookCache? spellbook)
    {
        if (itemId == 0)
            return false;

        uint have = player.Inventory.GetItemCount(itemId);
        // Common gathering/crafting tools (Mining Pick 2901, Blacksmith Hammer 5956, Skinning Knife 7005, Arclight Spanner 6219).
        if (itemId is 2901 or 5956 or 7005 or 6219)
        {
            if (have > 0)
                return false;
            if (player.Skills is { } skills
                && ((itemId == 2901 && skills.GetValueBase(SkillIds.Mining) > 0)
                    || (itemId == 5956 && skills.GetValueBase(SkillIds.Blacksmithing) > 0)
                    || (itemId == 7005 && skills.GetValueBase(SkillIds.Skinning) > 0)
                    || (itemId == 6219 && skills.GetValueBase(SkillIds.Engineering) > 0)))
            {
                return true;
            }
        }

        if (store is null || spellbook is null)
            return false;

        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (store.Get(spellId) is not { } spell)
                continue;

            if (have == 0
                && ((spell.Totems.Count > 0 && spell.Totems[0] == itemId)
                    || (spell.Totems.Count > 1 && spell.Totems[1] == itemId)))
            {
                return true;
            }

            if (!WantsToCraftSpell(player, spell, weights, store, spellbook)
                && !WantsToEnchantSpell(player, spell))
                continue;

            for (int r = 0; r < spell.Reagents.Count; r++)
            {
                SpellReagent reagent = spell.Reagents[r];
                if (reagent.ItemId == itemId && reagent.Count > 0 && have < reagent.Count)
                    return true;
            }
        }

        return false;
    }

    internal bool Update(Player player, uint elapsedMs)
    {
        if (elapsedMs == 0 || !player.IsInWorld || !player.IsAlive || player.Combat.IsInCombat
            || _session.ManagedBudget is not { Remaining: > 0 })
            return false;

        SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
        if (spellFeature?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting })
        {
            return true;
        }

        uint now = _session.World.NowMs;
        ForgetExpiredRefusals(now);

        if (DrainGatherLoot(player, now))
            return true;

        if (spellFeature is not null && TryCraft(player, spellFeature.System.Store, spellFeature.Spellbook, elapsedMs, now))
            return true;

        if (spellFeature is not null && TryEnchantEquipment(player, spellFeature.System.Store, spellFeature.Spellbook, now))
            return true;

        if (spellFeature is not null && TryDisenchantItem(player, spellFeature.System.Store, spellFeature.Spellbook, now))
            return true;

        if (spellFeature is not null && TryPickLockItem(player, spellFeature.System.Store, spellFeature.Spellbook, now))
            return true;

        if (spellFeature is not null && TryGatherNode(player, spellFeature.System.Store, spellFeature.Spellbook, elapsedMs, now))
            return true;

        if (spellFeature is not null && TrySkinCorpse(player, spellFeature.System.Store, spellFeature.Spellbook, elapsedMs, now))
            return true;

        _route = null;
        _routeTarget = default;
        return false;
    }

    /// <summary>
    /// Returns a creature entry on <paramref name="player"/>'s map that drops a missing reagent for a recipe the bot knows and
    /// wants to craft (an equipment upgrade, bag upgrade, or intermediate reagent), or 0 when none applies.
    /// </summary>
    internal uint PreferredMaterialDropperEntry(Player player)
    {
        SpellFeature? spellFeature = _session.Services.GetService<SpellFeature>();
        CreatureContent? content = _session.Services.GetService<CreatureWorldFeature>()?.Content;
        LootContent? loot = _session.Services.GetService<GameObjectLootFeature>()?.LootContent;
        if (spellFeature is null || content is null || loot is null)
            return 0;

        SpellStore store = spellFeature.System.Store;
        SpellbookCache spellbook = spellFeature.Spellbook;
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        uint now = _session.World.NowMs;

        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (store.Get(spellId) is not { } spell)
                continue;
            if (!WantsToCraftSpell(player, spell, weights, store, spellbook))
                continue;

            for (int r = 0; r < spell.Reagents.Count; r++)
            {
                SpellReagent reagent = spell.Reagents[r];
                if (reagent.ItemId == 0 || reagent.Count == 0)
                    continue;
                if (player.Inventory.GetItemCount(reagent.ItemId) >= reagent.Count)
                    continue;

                IReadOnlyList<uint> droppers = PlayerbotDropIndex.DroppersOf(loot, reagent.ItemId);
                int limit = Math.Min(droppers.Count, MaxDropperCandidates);
                for (int i = 0; i < limit; i++)
                {
                    uint dropper = droppers[i];
                    if (Suspensions?.IsEntrySuspended(dropper, now) == true)
                        continue;
                    if (content.GetSpawns(player.MapId, dropper).Count == 0
                        || content.FindTemplate(dropper) is not { Rank: 0 } template
                        || template.MinLevel > player.Level + 2
                        || template.MaxLevel + 8 < player.Level)
                        continue;
                    if (Home is { } home && (player.Map?.MapId ?? player.MapId) == home.MapId && !home.HasSpawnInside(content, dropper))
                        continue;
                    return dropper;
                }
            }
        }

        return 0;
    }

    private bool TryCraft(Player player, SpellStore store, SpellbookCache spellbook, uint elapsedMs, uint now)
    {
        if (PlayerbotTownGoals.FreeBagSlots(player) <= 0)
            return false;

        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        SpellInfo? bestSpell = null;
        float bestPriority = 0f;

        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (_refusedUntil.ContainsKey(SpellKey(spellId))
                || store.Get(spellId) is not { } spell)
                continue;

            if (!HasReagentsAndTools(player, spell))
                continue;

            float priority = CraftPriority(player, spell, weights, store, spellbook);
            if (priority <= bestPriority)
                continue;

            bestSpell = spell;
            bestPriority = priority;
        }

        if (bestSpell is null)
            return false;

        if (bestSpell.RequiresSpellFocus != 0 && !HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, out GameObject? distantFocus))
        {
            if (distantFocus is null && bestSpell.RequiresSpellFocus == CookingFireFocus
                && TryLightCampfire(player, store, spellbook, now, out bool campfireHandled))
            {
                return campfireHandled;
            }

            if (distantFocus is not null && player.Map is not null)
            {
                Goal = PlayerbotGoalKind.Vendor;
                TargetEntry = distantFocus.Entry;
                if (_route is null || _routeTarget != distantFocus.Guid)
                {
                    PlanOutcome planned = PlayerbotNavigation.PlanExact(
                        player,
                        new Vector3(distantFocus.X, distantFocus.Y, distantFocus.Z),
                        _options,
                        ref _tileWait,
                        now,
                        out _route);
                    if (planned == PlanOutcome.Pending)
                        return true;
                    if (planned == PlanOutcome.Refused || _route is null)
                    {
                        _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + FocusBackoffMs);
                        if (!_session.IsInChaosProbe)
                            PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"craft spell {bestSpell.Id} spell focus {bestSpell.RequiresSpellFocus} unreachable");
                        return false;
                    }
                    _routeTarget = distantFocus.Guid;
                }

                if (PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now))
                    return true;

                // The walk is over: when it ended in reach of the focus the recipe is cast now, else the bot gives up on it for a while.
                if (!HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, out _))
                {
                    // A route that ran to its end and still leaves the bot outside the focus's reach is a planner or range fault; a hazard
                    // abort or a stale route ends the walk early and says nothing about the fire.
                    bool walkedToTheEnd = _route?.Complete == true;
                    _route = null;
                    _routeTarget = default;
                    _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + FocusBackoffMs);
                    if (walkedToTheEnd && !_session.IsInChaosProbe)
                        PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"craft spell {bestSpell.Id} walked to spell focus {bestSpell.RequiresSpellFocus} but is still out of its range");
                    return false;
                }
            }
            else
            {
                // No focus object around (and no campfire to light): a retail player simply cannot cook here, which is no anomaly. The
                // recipe is set aside for the backoff and the bot carries on with something else.
                _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + FocusBackoffMs);
                return false;
            }
        }

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        uint reagentId = 0, reagentBefore = 0;
        for (int r = 0; r < bestSpell.Reagents.Count; r++)
        {
            if (bestSpell.Reagents[r].ItemId != 0 && bestSpell.Reagents[r].Count > 0)
            {
                reagentId = bestSpell.Reagents[r].ItemId;
                reagentBefore = player.Inventory.GetItemCount(reagentId);
                break;
            }
        }

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(6);
        writer.WriteUInt32(bestSpell.Id);
        writer.WriteUInt16(0);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        // Back off briefly if the cast was immediately rejected (not preparing/casting and reagents unchanged).
        if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is null
            && (reagentId == 0 || player.Inventory.GetItemCount(reagentId) >= reagentBefore))
        {
            _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
            if (!_session.IsInChaosProbe)
                PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"craft spell {bestSpell.Id} rejected with reagents present");
        }
        return true;
    }

    private static bool WantsToCraftSpell(Player player, SpellInfo spell, PlayerbotStatWeights weights, SpellStore store, SpellbookCache spellbook)
        => CraftPriority(player, spell, weights, store, spellbook) > 0f;

    private static float CraftPriority(Player player, SpellInfo spell, PlayerbotStatWeights weights, SpellStore store, SpellbookCache spellbook)
    {
        bool hasReagent = false;
        for (int r = 0; r < spell.Reagents.Count; r++)
        {
            if (spell.Reagents[r].ItemId != 0 && spell.Reagents[r].Count > 0)
            {
                hasReagent = true;
                break;
            }
        }
        if (!hasReagent)
            return 0f;

        uint createdId = 0;
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo eff = spell.Effects[i];
            if (eff.Effect == SpellEffectName.CreateItem && eff.ItemType != 0)
            {
                createdId = eff.ItemType;
                break;
            }
        }
        if (createdId == 0 || player.Inventory.Templates.Find(createdId) is not { } template)
            return 0f;

        uint alreadyCarried = player.Inventory.GetItemCount(createdId);

        // 1. Wearable equipment upgrade (highest priority).
        if (alreadyCarried == 0
            && player.Inventory.CanUseItem(template) == InventoryResult.Ok
            && PlayerbotItemScore.UpgradeGain(player, template, weights) is { } gain && gain > PlayerbotItemScore.MinimumGain)
        {
            return 1000f + gain;
        }

        // 2. Bag upgrade.
        if (alreadyCarried == 0 && PlayerbotEquipment.IsBagUpgrade(player, template))
            return 800f + template.ContainerSlots;

        // 3. Needed ammunition for a ranged weapon.
        if (template.GetInventoryType() == InventoryType.Ammo
            && player.Inventory.CheckAmmoCompatibility(template)
            && player.Inventory.CanUseAmmo(template.Entry) == InventoryResult.Ok
            && alreadyCarried < PlayerbotTownGoals.AmmoLowWater)
        {
            return 500f + template.ItemLevel;
        }

        // 4. Intermediate reagent needed by another known recipe (e.g. smelting bars, bolts of cloth, cured hides).
        if (alreadyCarried < 20 && IsReagentForKnownRecipe(player, createdId, store, spellbook))
            return 300f;

        // 5. Consumables / bandages / potions when running low.
        if ((ItemClass)template.Class == ItemClass.Consumable
            && player.Inventory.CanUseItem(template) == InventoryResult.Ok
            && alreadyCarried < 5)
        {
            return 200f + template.ItemLevel;
        }

        return 0f;
    }

    private static bool IsReagentForKnownRecipe(Player player, uint reagentItemId, SpellStore store, SpellbookCache spellbook)
    {
        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (store.Get(spellId) is not { } spell)
                continue;
            for (int r = 0; r < spell.Reagents.Count; r++)
            {
                if (spell.Reagents[r].ItemId == reagentItemId && spell.Reagents[r].Count > 0)
                    return true;
            }
        }
        return false;
    }

    private static bool HasReagentsAndTools(Player player, SpellInfo spell)
    {
        for (int t = 0; t < spell.Totems.Count; t++)
        {
            uint totem = spell.Totems[t];
            if (totem != 0 && player.Inventory.GetItemCount(totem) < 1)
                return false;
        }

        bool anyReagent = false;
        for (int r = 0; r < spell.Reagents.Count; r++)
        {
            SpellReagent reagent = spell.Reagents[r];
            if (reagent.ItemId == 0 || reagent.Count == 0)
                continue;
            anyReagent = true;
            if (player.Inventory.GetItemCount(reagent.ItemId) < reagent.Count)
                return false;
        }

        return anyReagent;
    }

    private static bool WantsToEnchantSpell(Player player, SpellInfo spell)
    {
        uint enchantId = 0;
        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo eff = spell.Effects[i];
            if (eff.Effect == SpellEffectName.EnchantItem && eff.MiscValue > 0)
            {
                enchantId = (uint)eff.MiscValue;
                break;
            }
        }
        if (enchantId == 0)
            return false;

        for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is { } worn
                && worn.GetUInt32(UpdateFields.ItemFieldEnchantment) == 0
                && worn.Template.ItemLevel >= spell.BaseLevel
                && ArcaneCore.Game.Crafting.Enchanting.ItemTargetRules.IsFit(spell, worn.Template))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryEnchantEquipment(Player player, SpellStore store, SpellbookCache spellbook, uint now)
    {
        SpellInfo? bestSpell = null;
        Item? bestTarget = null;
        uint bestRank = 0;

        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (_refusedUntil.ContainsKey(SpellKey(spellId))
                || store.Get(spellId) is not { } spell
                || !HasReagentsAndTools(player, spell))
                continue;

            uint enchantId = 0;
            for (int i = 0; i < spell.Effects.Count; i++)
            {
                SpellEffectInfo eff = spell.Effects[i];
                if (eff.Effect == SpellEffectName.EnchantItem && eff.MiscValue > 0)
                {
                    enchantId = (uint)eff.MiscValue;
                    break;
                }
            }
            if (enchantId == 0)
                continue;

            for (byte slot = 0; slot < InventorySlots.EquipmentEnd; slot++)
            {
                if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is not { } worn)
                    continue;
                if (_refusedUntil.ContainsKey(worn.Guid.Value)
                    || worn.GetUInt32(UpdateFields.ItemFieldEnchantment) != 0
                    || worn.Template.ItemLevel < spell.BaseLevel
                    || !ArcaneCore.Game.Crafting.Enchanting.ItemTargetRules.IsFit(spell, worn.Template))
                    continue;

                uint rank = Math.Max(spell.SpellLevel, spell.BaseLevel) * 100_000u + spell.Id;
                if (bestSpell is null || rank > bestRank)
                {
                    bestSpell = spell;
                    bestTarget = worn;
                    bestRank = rank;
                }
            }
        }

        if (bestSpell is null || bestTarget is null)
            return false;

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(16);
        writer.WriteUInt32(bestSpell.Id);
        new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = bestTarget.Guid }.Write(writer);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is null
            && bestTarget.GetUInt32(UpdateFields.ItemFieldEnchantment) == 0)
        {
            _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
            _refusedUntil[bestTarget.Guid.Value] = unchecked(now + RefusalBackoffMs);
            if (!_session.IsInChaosProbe)
                PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"enchant spell {bestSpell.Id} rejected on item {bestTarget.Entry}");
        }
        return true;
    }

    private bool TryDisenchantItem(Player player, SpellStore store, SpellbookCache spellbook, uint now)
    {
        if (PlayerbotTownGoals.FreeBagSlots(player) <= 0)
            return false;

        const uint DisenchantSpellId = 13262;
        SpellInfo? disenchantSpell = null;
        if (spellbook.HasSpell(player, DisenchantSpellId))
        {
            disenchantSpell = store.Get(DisenchantSpellId);
        }
        else
        {
            foreach (uint spellId in spellbook.GetSpells(player))
            {
                if (store.Get(spellId) is { } s && s.Effects.Any(e => e.Effect == SpellEffectName.Disenchant))
                {
                    disenchantSpell = s;
                    break;
                }
            }
        }

        if (disenchantSpell is null || _refusedUntil.ContainsKey(SpellKey(disenchantSpell.Id)))
            return false;

        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        Item? candidate = null;
        foreach (Item item in player.Inventory.AllItems.Take(128))
        {
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)
                || InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot)
                || item.Count == 0
                || _refusedUntil.ContainsKey(item.Guid.Value))
                continue;

            ItemTemplate template = item.Template;
            if (!DisenchantLoot.HasDisenchantLoot(template))
                continue;

            // Never disenchant an item that is an upgrade for this bot.
            if (PlayerbotItemScore.BestUpgradeSlot(player, template, out _, out _, weights, spells: store))
                continue;

            candidate = item;
            break;
        }

        if (candidate is null)
            return false;

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(16);
        writer.WriteUInt32(disenchantSpell.Id);
        new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = candidate.Guid }.Write(writer);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        _gatheringTarget = candidate.Guid;
        _waitingForGatherLoot = true;
        _gatherWaitUntilMs = unchecked(now + 6_000);
        _refusedUntil[candidate.Guid.Value] = unchecked(now + RefusalBackoffMs);
        return true;
    }

    private bool TryPickLockItem(Player player, SpellStore store, SpellbookCache spellbook, uint now)
    {
        SpellInfo? pickLockSpell = null;
        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (store.Get(spellId) is not { } s)
                continue;
            if (s.Id == 1804 || s.Effects.Any(e => e.Effect == SpellEffectName.OpenLock && e.MiscValue == (int)LockType.PickLock))
            {
                pickLockSpell = s;
                break;
            }
        }

        if (pickLockSpell is null || _refusedUntil.ContainsKey(SpellKey(pickLockSpell.Id)))
            return false;

        GameObjectLootFeature? objects = _session.Services.GetService<GameObjectLootFeature>();
        Item? lockedItem = null;
        foreach (Item item in player.Inventory.AllItems.Take(128))
        {
            if (!InventorySlots.IsInventoryPos(item.BagSlot, item.Slot)
                || InventorySlots.IsEquipmentPos(item.BagSlot, item.Slot)
                || item.Count == 0
                || _refusedUntil.ContainsKey(item.Guid.Value))
                continue;

            uint lockId = item.Template.LockId;
            if (lockId == 0 || (item.GetUInt32(UpdateFields.ItemFieldFlags) & 4u) != 0)
                continue;

            if (objects?.Content.FindLock(lockId) is { } lockEntry)
            {
                SpellEffectInfo eff = pickLockSpell.Effects[0];
                OpenLockCheck check = GatheringRules.CanOpenLock(
                    lockId, lockEntry, (uint)LockType.PickLock, 0, false, eff.BasePoints + eff.BaseDice, true,
                    id => player.Skills?.GetValue(id) ?? 0);
                if (check.Result != SpellCastResult.CastOk)
                    continue;
            }

            lockedItem = item;
            break;
        }

        if (lockedItem is null)
            return false;

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(16);
        writer.WriteUInt32(pickLockSpell.Id);
        new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = lockedItem.Guid }.Write(writer);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        _refusedUntil[lockedItem.Guid.Value] = unchecked(now + RefusalBackoffMs);
        return true;
    }

    private static bool HasNearbySpellFocus(Player player, uint spellFocusType, out GameObject? nearestOutOfRange)
    {
        nearestOutOfRange = null;
        if (player.Map is not { } map)
            return false;

        // The server's own check (vmangos GameObjectFocusCheck, GridNotifiers.h:586-606) decides "in reach", so the bot never casts into a
        // RequiresSpellFocus rejection that its own, different, radius estimate let through.
        GameObjectMapSystem? objects = map.FindUpdater<GameObjectMapSystem>();
        if (objects?.FindSpellFocus(player, spellFocusType) is not null)
            return true;

        float bestDistSq = MaxSpellFocusRangeYards * MaxSpellFocusRangeYards;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not GameObject { IsSpawned: true, Type: GameObjectType.SpellFocus } go
                || go.Template.GetData(0) != spellFocusType)
                continue;

            float dist = go.DistanceTo(player);
            float distSq = dist * dist;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                nearestOutOfRange = go;
            }
        }

        return false;
    }

    /// <summary>
    /// Lights a Basic Campfire (spell 818) for a cooking recipe when no cooking fire is around, as a retail player does: the bot must know
    /// the spell (Cooking teaches it) and carry its reagent (Simple Wood 4470) and its tool (the totem slot: Flint and Tinder 4471), and the spell must really put down a cooking fire (<see cref="ProvidesSpellFocus"/>).
    /// Returns false when the bot cannot light one (the caller then sets the recipe aside); otherwise <paramref name="handled"/> is
    /// this think's result. A cast the server rejects with the wood still in the bags sets the campfire aside for <see cref="FocusBackoffMs"/>.
    /// </summary>
    private bool TryLightCampfire(Player player, SpellStore store, SpellbookCache spellbook, uint now, out bool handled)
    {
        handled = false;
        if (_refusedUntil.ContainsKey(SpellKey(BasicCampfireSpell))
            || !spellbook.HasSpell(player, BasicCampfireSpell)
            || store.Get(BasicCampfireSpell) is not { } campfire
            || !ProvidesSpellFocus(player, campfire, CookingFireFocus)
            || !HasReagentsAndTools(player, campfire))
            return false;

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
        {
            handled = true;
            return true;
        }

        uint woodBefore = player.Inventory.GetItemCount(SimpleWoodItem);
        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(6);
        writer.WriteUInt32(BasicCampfireSpell);
        writer.WriteUInt16(0);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
        {
            handled = false;
            return true;
        }

        handled = true;
        if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is null
            && player.Inventory.GetItemCount(SimpleWoodItem) >= woodBefore)
        {
            // Retail refuses a campfire in several ordinary places (indoors, in a city, on a transport, swimming): not a fault, so the
            // bot backs off in silence.
            _refusedUntil[SpellKey(BasicCampfireSpell)] = unchecked(now + FocusBackoffMs);
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="spell"/> puts down a spell focus object of <paramref name="focusId"/>: some effect summons (vmangos
    /// Spell::EffectTransmitted: SPELL_EFFECT_TRANS_DOOR, the effect Basic Campfire uses, or SummonObjectWild) a game object of the map that is a
    /// <see cref="GameObjectType.SpellFocus"/> with that focus id. A fishing node or an unknown template does not count.
    /// </summary>
    internal static bool ProvidesSpellFocus(Player player, SpellInfo spell, uint focusId)
    {
        GameObjectMapSystem? objects = player.Map?.FindUpdater<GameObjectMapSystem>();
        if (objects is null)
            return false;

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo eff = spell.Effects[i];
            if (eff.Effect is not (SpellEffectName.TransDoor or SpellEffectName.SummonObjectWild) || eff.MiscValue <= 0)
                continue;
            if (objects.FindTemplate((uint)eff.MiscValue) is { } template
                && (GameObjectType)template.Type == GameObjectType.SpellFocus
                && template.GetData(0) == focusId)
                return true;
        }

        return false;
    }
    private bool TryGatherNode(Player player, SpellStore store, SpellbookCache spellbook, uint elapsedMs, uint now)
    {
        if (player.Map is not { } map || PlayerbotTownGoals.FreeBagSlots(player) <= 0)
            return false;

        GameObjectLootFeature? objects = _session.Services.GetService<GameObjectLootFeature>();
        if (objects is null)
            return false;

        // Find the player's known OpenLock gathering spells (Herbalism / Mining).
        SpellInfo? herbSpell = null;
        SpellInfo? mineSpell = null;
        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (store.Get(spellId) is not { } s)
                continue;
            SpellEffectInfo eff = s.Effects[0];
            if (eff.Effect != SpellEffectName.OpenLock)
                continue;
            if (eff.MiscValue == (int)LockType.Herbalism)
                herbSpell = s;
            else if (eff.MiscValue == (int)LockType.Mining
                && (s.Totems[0] == 0 || player.Inventory.GetItemCount(s.Totems[0]) >= 1))
                mineSpell = s;
        }

        if (herbSpell is null && mineSpell is null)
            return false;

        GameObject? bestNode = null;
        SpellInfo? bestGatherSpell = null;
        float bestDistSq = MaxGatherRangeYards * MaxGatherRangeYards;

        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (_refusedUntil.ContainsKey(guid.Value)
                || map.FindObject(guid) is not GameObject { IsSpawned: true, Type: GameObjectType.Chest } go
                || (go.Flags & GameObjectFlags.InUse) != 0)
                continue;

            if (Home is { } home && go.MapId == home.MapId && !home.Contains(go.MapId, go.X, go.Y, go.Z))
                continue;

            uint lockId = GameObjectLocks.LockIdOf(go.Template);
            if (lockId == 0)
                continue;

            LockEntry? lockEntry = objects.Content.FindLock(lockId);
            SpellInfo? matchSpell = null;
            if (herbSpell is not null)
            {
                SpellEffectInfo eff = herbSpell.Effects[0];
                OpenLockCheck check = GatheringRules.CanOpenLock(
                    lockId, lockEntry, (uint)eff.MiscValue, 0, false, eff.BasePoints + eff.BaseDice, true,
                    id => player.Skills?.GetValue(id) ?? 0);
                if (check.Result == SpellCastResult.CastOk)
                    matchSpell = herbSpell;
            }
            if (matchSpell is null && mineSpell is not null)
            {
                SpellEffectInfo eff = mineSpell.Effects[0];
                OpenLockCheck check = GatheringRules.CanOpenLock(
                    lockId, lockEntry, (uint)eff.MiscValue, 0, false, eff.BasePoints + eff.BaseDice, true,
                    id => player.Skills?.GetValue(id) ?? 0);
                if (check.Result == SpellCastResult.CastOk)
                    matchSpell = mineSpell;
            }
            if (matchSpell is null)
                continue;

            // The guards beside the node are not worth it as the bot is now: leave it (PlayerbotOpportunities).
            if (GatherVeto?.Invoke(go) == true)
                continue;

            float dx = go.X - player.X, dy = go.Y - player.Y, dz = go.Z - player.Z;
            float distSq = (dx * dx) + (dy * dy) + (dz * dz);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestNode = go;
                bestGatherSpell = matchSpell;
            }
        }

        if (bestNode is null || bestGatherSpell is null)
            return false;

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = bestNode.Entry;

        if (bestNode.DistanceTo(player) > GatherInteractYards)
        {
            if (_route is null || _routeTarget != bestNode.Guid)
            {
                PlanOutcome planned = PlayerbotNavigation.PlanExact(
                    player,
                    new Vector3(bestNode.X, bestNode.Y, bestNode.Z),
                    _options,
                    ref _tileWait,
                    now,
                    out _route);
                if (planned == PlanOutcome.Pending)
                    return true;
                if (planned == PlanOutcome.Refused || _route is null)
                {
                    _refusedUntil[bestNode.Guid.Value] = unchecked(now + RefusalBackoffMs);
                    return false;
                }
                _routeTarget = bestNode.Guid;
            }

            if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now))
            {
                _refusedUntil[bestNode.Guid.Value] = unchecked(now + RefusalBackoffMs);
                _route = null;
                return false;
            }
            return true;
        }

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        var writer = new PacketWriter(15);
        writer.WriteUInt32(bestGatherSpell.Id);
        writer.WriteUInt16((ushort)SpellCastTargetFlags.GameObject);
        writer.WritePackedGuid(bestNode.Guid.Value);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        _gatheringTarget = bestNode.Guid;
        _waitingForGatherLoot = true;
        _gatherWaitUntilMs = unchecked(now + 6_000);
        _refusedUntil[bestNode.Guid.Value] = unchecked(now + RefusalBackoffMs);
        return true;
    }

    private bool TrySkinCorpse(Player player, SpellStore store, SpellbookCache spellbook, uint elapsedMs, uint now)
    {
        if (player.Map is not { } map || PlayerbotTownGoals.FreeBagSlots(player) <= 0)
            return false;

        // Require Skinning Knife (7005) and a known Skinning spell (SPELL_EFFECT_SKINNING).
        if (player.Inventory.GetItemCount(7005) < 1)
            return false;

        SpellInfo? skinSpell = null;
        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (store.Get(spellId) is not { } s)
                continue;
            if (s.Effects[0].Effect == SpellEffectName.Skinning)
            {
                skinSpell = s;
                break;
            }
        }
        if (skinSpell is null)
            return false;

        int skillValue = (int)(player.Skills?.GetValue(SkillIds.Skinning) ?? 0);
        if (skillValue <= 0)
            return false;

        LootService? loot = _session.Services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot;
        Creature? bestCorpse = null;
        float bestDistSq = MaxGatherRangeYards * MaxGatherRangeYards;

        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (_refusedUntil.ContainsKey(guid.Value)
                || map.FindObject(guid) is not Creature { IsAlive: false, IsInWorld: true } corpse
                || (corpse.UnitFlags & UnitFlags.Skinnable) == 0
                || corpse.LootedForSkin
                || (corpse.GetUInt32(UpdateFields.UnitDynamicFlags) & 1u) != 0)
                continue;

            if (GatheringRules.SkinningRequiredSkill(skillValue, (int)corpse.Level) > skillValue)
                continue;

            if (loot is not null && !loot.IsSkinnableBy(player, corpse))
                continue;

            float dx = corpse.X - player.X, dy = corpse.Y - player.Y, dz = corpse.Z - player.Z;
            float distSq = (dx * dx) + (dy * dy) + (dz * dz);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestCorpse = corpse;
            }
        }

        if (bestCorpse is null)
            return false;

        Goal = PlayerbotGoalKind.Grind;
        TargetEntry = bestCorpse.Entry;

        float dist = MathF.Sqrt(bestDistSq);
        if (dist > GatherInteractYards)
        {
            if (_route is null || _routeTarget != bestCorpse.Guid)
            {
                PlanOutcome planned = PlayerbotNavigation.PlanExact(
                    player,
                    new Vector3(bestCorpse.X, bestCorpse.Y, bestCorpse.Z),
                    _options,
                    ref _tileWait,
                    now,
                    out _route);
                if (planned == PlanOutcome.Pending)
                    return true;
                if (planned == PlanOutcome.Refused || _route is null)
                {
                    _refusedUntil[bestCorpse.Guid.Value] = unchecked(now + RefusalBackoffMs);
                    return false;
                }
                _routeTarget = bestCorpse.Guid;
            }

            if (!PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now))
            {
                _refusedUntil[bestCorpse.Guid.Value] = unchecked(now + RefusalBackoffMs);
                _route = null;
                return false;
            }
            return true;
        }

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        var writer = new PacketWriter(15);
        writer.WriteUInt32(skinSpell.Id);
        writer.WriteUInt16((ushort)SpellCastTargetFlags.Unit);
        writer.WritePackedGuid(bestCorpse.Guid.Value);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        _gatheringTarget = bestCorpse.Guid;
        _waitingForGatherLoot = true;
        _gatherWaitUntilMs = unchecked(now + 6_000);
        _refusedUntil[bestCorpse.Guid.Value] = unchecked(now + RefusalBackoffMs);
        return true;
    }

    private bool DrainGatherLoot(Player player, uint now)
    {
        if (_pendingLoot.TryPeek(out (WorldOpcode Opcode, byte[] Payload) next))
        {
            if (!PlayerbotMovementControl.Stop(_session, player))
                return true;
            if (_session.TryManagedAction(next.Opcode, next.Payload))
                _pendingLoot.Dequeue();
            return true;
        }

        if (!_waitingForGatherLoot)
            return false;

        if (unchecked((int)(_gatherWaitUntilMs - now)) <= 0)
        {
            ObjectGuid timedOutTarget = _gatheringTarget;
            _waitingForGatherLoot = false;
            _gatheringTarget = default;
            if (!_session.IsInChaosProbe)
                PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"gather or disenchant loot timed out for target {timedOutTarget}");
            return false;
        }

        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Payload.Length < 9)
                continue;
            var reader = new PacketReader(packet.Payload);
            ulong sourceGuid = reader.ReadUInt64();
            if (sourceGuid != _gatheringTarget.Value)
                continue;

            _waitingForGatherLoot = false;
            if (reader.ReadByte() != 1 || packet.Payload.Length < 14)
                continue;
            uint coins = reader.ReadUInt32();
            byte count = reader.ReadByte();
            bool hasSpace = PlayerbotTownGoals.FreeBagSlots(player) > 0;
            for (int i = 0; i < count && reader.Remaining >= 22; i++)
            {
                byte slot = reader.ReadByte();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadInt32();
                byte slotType = reader.ReadByte();
                if (slotType == 0 && hasSpace)
                    _pendingLoot.Enqueue((WorldOpcode.CmsgAutostoreLootItem, [slot]));
            }
            if (coins > 0)
                _pendingLoot.Enqueue((WorldOpcode.CmsgLootMoney, []));
            _pendingLoot.Enqueue((WorldOpcode.CmsgLootRelease, PlayerbotNavigation.GuidPayload(sourceGuid)));
            return _pendingLoot.Count > 0;
        }

        return false;
    }

    private void ForgetExpiredRefusals(uint now)
    {
        foreach ((ulong key, uint until) in _refusedUntil.ToArray())
        {
            if (unchecked((int)(until - now)) <= 0)
                _refusedUntil.Remove(key);
        }
    }

    private static ulong SpellKey(uint spellId) => (1UL << 62) | spellId;
}

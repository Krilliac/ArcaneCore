using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
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
    /// Whether the bot wants to learn <paramref name="learnedSpellId"/> at a <c>TrainerType.TradeSkills</c> trainer:
    /// secondary skills (First Aid, Cooking), class-fitted gathering and crafting professions, or any recipe that crafts
    /// a wearable upgrade, bag upgrade, ammunition, or reagent.
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
                if (skillId == 0 || IsPreferredSkillForClass(player.Class, skillId)
                    || (player.Skills?.GetValueBase(skillId) ?? 0) > 0)
                {
                    return true;
                }
            }

            if (eff.Effect is SpellEffectName.OpenLock or SpellEffectName.Skinning)
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

            if (!WantsToCraftSpell(player, spell, weights, store, spellbook))
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
                        _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
                        return false;
                    }
                    _routeTarget = distantFocus.Guid;
                }

                if (PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now))
                    return true;
            }

            _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
            return false;
        }

        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(6);
        writer.WriteUInt32(bestSpell.Id);
        writer.WriteUInt16(0);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        // Back off briefly if the cast was immediately rejected (not preparing/casting and reagents unchanged).
        if (_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is null)
            _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
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

    private static bool HasNearbySpellFocus(Player player, uint spellFocusType, out GameObject? nearestOutOfRange)
    {
        nearestOutOfRange = null;
        if (player.Map is not { } map)
            return false;

        float bestDistSq = MaxSpellFocusRangeYards * MaxSpellFocusRangeYards;
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not GameObject { IsSpawned: true, Type: GameObjectType.SpellFocus } go
                || go.Template.GetData(0) != spellFocusType)
                continue;

            float radius = MathF.Max(4.5f, go.Template.GetData(1));
            float dist = go.DistanceTo(player);
            if (dist <= radius)
                return true;

            float distSq = dist * dist;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                nearestOutOfRange = go;
            }
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
            _waitingForGatherLoot = false;
            _gatheringTarget = default;
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

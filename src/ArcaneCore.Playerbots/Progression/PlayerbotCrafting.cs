using System.Numerics;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;


using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps.Collision;
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
using ArcaneCore.World.Bots.Progression;

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

    /// <summary>The longest a recipe or fire is set aside after repeated failures to reach a spell focus (<see cref="SetFocusAside"/>).</summary>
    private const uint MaxFocusBackoffMs = 4 * 3_600_000;

    /// <summary>SpellFocusObject.dbc id 4, the cooking fire (gameobject_template 29784 Basic Campfire carries data0 = 4, data1 = 10 yards).</summary>
    internal const uint CookingFireFocus = 4;

    /// <summary>Basic Campfire (a Cooking spell, Spell.dbc: effect TRANS_DOOR of gameobject 29784, tool Flint and Tinder 4471, reagent Simple Wood 4470).</summary>
    internal const uint BasicCampfireSpell = 818;

    private const uint SimpleWoodItem = 4470;

    /// <summary>
    /// How long past its cast bar a gather, skin or disenchant may go without its loot window before the wait counts as failed. The
    /// window opens in the tick the cast lands (vmangos Spell::EffectOpenLock / EffectSkinning / EffectDisenchant all end in
    /// Player::SendLoot), so this only covers the reply's queueing; the deadline used to be a fixed 6 s from the cast packet, shorter
    /// than a 5 s Herb Gathering (Spell.dbc 2366, CastingTimeIndex 6) plus one think.
    /// </summary>
    internal const uint LootReplyGraceMs = 2_000;

    /// <summary>
    /// Casts on one node, corpse or item that may fail in a row (no loot window: the orange-skill TRY_AGAIN roll, an interrupt, a refusal)
    /// before the bot leaves it for <see cref="RefusalBackoffMs"/> and reports it. A player clicks the node again after "Try again"; at the
    /// lowest skill the roll fails about 40% of the time (vmangos Spell::CheckCast, Spell.cpp:6050-6057, <see cref="GatheringRules.OrangeGatherFails"/>).
    /// </summary>
    internal const int MaxGatherCasts = 5;

    private const int MaxDropperCandidates = 32;

    private readonly WorldSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly PlayerbotOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly Dictionary<ulong, uint> _refusedUntil = [];

    /// <summary>Failures in a row to reach a spell focus of this kind (<see cref="SetFocusAside"/>); it sets how long the next one backs off.</summary>
    private readonly Dictionary<uint, int> _focusFailures = [];
    private readonly Queue<(WorldOpcode Opcode, byte[] Payload)> _pendingLoot = new();

    private TileWait _tileWait;
    private PlayerbotRoute? _route;
    private ObjectGuid _routeTarget;
    private ObjectGuid _gatheringTarget;
    private bool _waitingForGatherLoot;
    private uint _gatherWaitUntilMs;
    private SpellCast? _lootCast;
    private uint _lootSpellId;
    private readonly Dictionary<ulong, int> _failedLootCasts = [];

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
    /// The common gathering and crafting tools (Mining Pick 2901, Blacksmith Hammer 5956, Skinning Knife 7005, Arclight Spanner 6219):
    /// false when one is carried (<paramref name="have"/>), true when it is missing and its skill is known, null for any other item or a
    /// missing tool of an unknown skill (the recipes decide). Shared by <see cref="NeedsVendorSupply"/> and <see cref="PlayerbotVendorSupplyNeeds"/>.
    /// </summary>
    internal static bool? CommonToolNeed(Player player, uint itemId, uint have)
    {
        if (itemId is not (2901 or 5956 or 7005 or 6219))
            return null;
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

        return null;
    }

    /// <summary>
    /// Whether the bot needs <paramref name="itemId"/> from a vendor to craft or gather: a profession tool (<c>SpellInfo.Totems</c>,
    /// Mining Pick 2901, Blacksmith Hammer 5956, Skinning Knife 7005) not yet in the bags, or a reagent required by a known recipe
    /// that would craft an equipment upgrade, bag upgrade, or needed supply. Walks the whole spellbook per call; for many items at
    /// once use <see cref="PlayerbotVendorSupplyNeeds"/>, which gives the same answers.
    /// </summary>
    internal static bool NeedsVendorSupply(Player player, uint itemId, SpellStore? store, SpellbookCache? spellbook)
    {
        if (itemId == 0)
            return false;

        uint have = player.Inventory.GetItemCount(itemId);
        if (CommonToolNeed(player, itemId, have) is { } tool)
            return tool;

        if (store is null || spellbook is null)
            return false;

        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        bool knowsCampfire = false, wantsFireRecipe = false;
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

            if (spellId == BasicCampfireSpell) knowsCampfire = true;
            if (!WantsToCraftSpell(player, spell, weights, store, spellbook)
                && !WantsToEnchantSpell(player, spell))
                continue;

            if (spell.RequiresSpellFocus == CookingFireFocus) wantsFireRecipe = true;
            for (int r = 0; r < spell.Reagents.Count; r++)
            {
                SpellReagent reagent = spell.Reagents[r];
                if (reagent.ItemId == itemId && reagent.Count > 0 && have < reagent.Count)
                    return true;
            }
        }

        return knowsCampfire && wantsFireRecipe && CampfireReagentShort(store, itemId, have);
    }

    /// <summary>
    /// Whether <paramref name="itemId"/> is a reagent of Basic Campfire (<see cref="BasicCampfireSpell"/>: Simple Wood 4470) the bot carries
    /// fewer of than one fire uses. A cook that knows the campfire and wants a recipe that needs a cooking fire keeps the wood for one:
    /// the fallback for a fire it cannot reach (<see cref="TryLightCampfire"/>) needs it, and no recipe asked the vendor for wood, so the
    /// fallback almost never fired (bot QA #774: ~90/h craft-failed 'spell focus 4 unreachable' on live 2026-10-17 after it landed).
    /// </summary>
    internal static bool CampfireReagentShort(SpellStore store, uint itemId, uint have)
    {
        if (store.Get(BasicCampfireSpell) is not { } campfire) return false;
        for (int r = 0; r < campfire.Reagents.Count; r++)
        {
            SpellReagent reagent = campfire.Reagents[r];
            if (reagent.ItemId == itemId && reagent.Count > 0 && have < reagent.Count) return true;
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
                    // A dropper whose every spawn lies where no walk reaches from the bot (another landmass, an island) is a hunt that cannot start
                    // (GitHub #1441); the next dropper of the reagent is tried instead.
                    if (AllSpawnsUnreachable(content.GetSpawns(player.MapId, dropper), at => PlayerbotNavigation.IsUnreachableOnFoot(player, at, out _, out _)))
                        continue;
                    return dropper;
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether there is at least one spawn and <paramref name="unreachable"/> holds for every one of them (stops at the first reachable one).
    /// False for no spawn: the caller has its own test for a creature that is not spawned. Pure; world thread when the predicate reads a map.
    /// </summary>
    internal static bool AllSpawnsUnreachable(IReadOnlyList<CreatureSpawn> spawns, Func<Vector3, bool> unreachable)
    {
        if (spawns.Count == 0)
            return false;
        for (int i = 0; i < spawns.Count; i++)
        {
            CreatureSpawn spawn = spawns[i];
            if (!unreachable(new Vector3(spawn.X, spawn.Y, spawn.Z)))
                return false;
        }

        return true;
    }

    private bool TryCraft(Player player, SpellStore store, SpellbookCache spellbook, uint elapsedMs, uint now)
    {
        if (PlayerbotTownGoals.FreeBagSlots(player) <= 0)
            return false;

        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        SpellSystem? spellSystem = _session.Services.GetService<SpellFeature>()?.System;
        SpellInfo? bestSpell = null;
        float bestPriority = 0f;

        foreach (uint spellId in spellbook.GetSpells(player))
        {
            if (_refusedUntil.ContainsKey(SpellKey(spellId))
                || store.Get(spellId) is not { } spell
                || (spell.RequiresSpellFocus != 0 && _refusedUntil.ContainsKey(FocusKey(spell.RequiresSpellFocus))))
                continue;

            if (!HasReagentsAndTools(player, spell, spellSystem)
                || spellSystem?.IsSpellReadyIgnoringGlobalCooldown(player, spell) == false)
                continue;

            float priority = CraftPriority(player, spell, weights, store, spellbook);
            if (priority <= bestPriority)
                continue;

            bestSpell = spell;
            bestPriority = priority;
        }

        if (bestSpell is null)
            return false;

        if (bestSpell.RequiresSpellFocus != 0 && !HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, _refusedUntil, out GameObject? distantFocus))
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
                    while (distantFocus is not null)
                    {
                        TargetEntry = distantFocus.Entry;
                        PlanOutcome planned = PlanToSpellFocus(
                            player,
                            distantFocus,
                            _options,
                            ref _tileWait,
                            now,
                            out _route);
                        if (planned == PlanOutcome.Pending)
                            return true;
                        if (planned != PlanOutcome.Refused && _route is not null)
                            break;

                        _refusedUntil[distantFocus.Guid.Value] = unchecked(now + FocusBackoffMs);
                        if (HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, _refusedUntil, out GameObject? nextFocus) || nextFocus is null)
                        {
                            // A fire in sight that no route reaches (inside a closed building, up a cliff) is no reason to skip the recipe: a cook
                            // with Basic Campfire, wood and a flint lights one here instead (GitHub #774, ~60/h 'spell focus 4 unreachable' on live).
                            if (bestSpell.RequiresSpellFocus == CookingFireFocus && TryLightCampfire(player, store, spellbook, now, out bool fallbackHandled))
                            {
                                if (_refusedUntil.ContainsKey(SpellKey(BasicCampfireSpell)))
                                {
                                    _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + FocusBackoffMs);
                                    _refusedUntil[FocusKey(bestSpell.RequiresSpellFocus)] = unchecked(now + FocusBackoffMs);
                                }
                                return fallbackHandled;
                            }
                            SetFocusAside(bestSpell, distantFocus, now);
                            Vector3 focusCoords = new(distantFocus.X, distantFocus.Y, distantFocus.Z);
                            bool terrainBlocked = player.Map is { } focusMap && focusMap.Collision.Pathfinder.Enabled
                                && (!PlayerbotNavigation.OnWalkableMesh(focusMap, focusCoords)
                                    || !PlayerbotNavigation.OnWalkableMesh(focusMap, PlayerbotMotion.CurrentPosition(player))
                                    || PlayerbotNavigation.RequiresSteepAscent(player, focusCoords, _options, out _)
                                    || PlayerbotNavigation.IsOnMeshFragment(player)
                                    || PlayerbotNavigation.IsOnMeshIsland(player, _options));
                            if (!_session.IsInChaosProbe && !terrainBlocked)
                                PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"craft spell {bestSpell.Id} spell focus {bestSpell.RequiresSpellFocus} unreachable");
                            return false;
                        }
                        distantFocus = nextFocus;
                    }
                    _routeTarget = distantFocus!.Guid;
                }

                if (PlayerbotNavigation.TryAdvance(_session, _route!, _options, elapsedMs, now)
                    && !HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, null, out _))
                    return true;

                // The walk is over: when it ended in reach of the focus the recipe is cast now, else the bot gives up on it for a while.
                if (!HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, null, out _))
                {
                    if (_route?.Complete == true
                        && distantFocus.IsSpawned
                        && player.Map?.FindObject(distantFocus.Guid) is not null)
                    {
                        var focusAt = new Vector3(distantFocus.X, distantFocus.Y, distantFocus.Z);
                        float priorRemaining = Vector3.Distance(PlayerbotMotion.CurrentPosition(player), focusAt);
                        PlanOutcome onward = PlanToSpellFocus(player, distantFocus, _options, ref _tileWait, now, out PlayerbotRoute? onwardRoute);
                        if (onward == PlanOutcome.Pending)
                            return true;
                        if (onward == PlanOutcome.Route
                            && onwardRoute is { Points.Count: > 0 }
                            && Vector3.Distance(onwardRoute.Points[^1], focusAt) < priorRemaining - 0.5f)
                        {
                            _route = onwardRoute;
                            if (PlayerbotNavigation.TryAdvance(_session, _route, _options, elapsedMs, now)
                                && !HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, null, out _))
                                return true;
                        }
                    }

                    if (!HasNearbySpellFocus(player, bestSpell.RequiresSpellFocus, null, out _))
                    {
                        if (bestSpell.RequiresSpellFocus == CookingFireFocus
                            && TryLightCampfire(player, store, spellbook, now, out bool fallbackHandled))
                        {
                            _route = null;
                            _routeTarget = default;
                            if (_refusedUntil.ContainsKey(SpellKey(BasicCampfireSpell)))
                                SetFocusAside(bestSpell, distantFocus, now);
                            else
                                _refusedUntil[distantFocus.Guid.Value] = unchecked(now + FocusBackoffMs);
                            return fallbackHandled;
                        }

                        bool focusStillPresent = distantFocus.IsSpawned && player.Map?.FindObject(distantFocus.Guid) is not null;
                        bool walkedToTheEnd = _route is { Complete: true, Points.Count: > 0 }
                            && focusStillPresent
                            && Vector3.Distance(PlayerbotMotion.CurrentPosition(player), _route.Points[^1]) <= 1.5f;
                        _route = null;
                        _routeTarget = default;
                        SetFocusAside(bestSpell, distantFocus, now);
                        if (walkedToTheEnd && !_session.IsInChaosProbe)
                            PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"craft spell {bestSpell.Id} walked to spell focus {bestSpell.RequiresSpellFocus} but is still out of its range");
                        return false;
                    }
                }
            }
            else
            {
                _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + FocusBackoffMs);
                return false;
            }
        }

        if (bestSpell.RequiresSpellFocus != 0)
            _focusFailures.Remove(bestSpell.RequiresSpellFocus);
        _route = null;
        _routeTarget = default;
        if (!PlayerbotMovementControl.Stop(_session, player))
            return true;
        EnsureReadyForCraftCast(player, bestSpell);
        if (ShouldDeferCraftCast(player, bestSpell, spellSystem))
        {
            Goal = PlayerbotGoalKind.Vendor;
            TargetEntry = 0;
            return true;
        }
        if (spellSystem is not null && spellSystem.CheckCasterState(player, bestSpell) != SpellCastResult.CastOk)
        {
            _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
            return false;
        }

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
        if (CastNotUnderway(player)
            && (reagentId == 0 || player.Inventory.GetItemCount(reagentId) >= reagentBefore))
        {
            _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
            if (!_session.IsInChaosProbe)
                PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"craft spell {bestSpell.Id} rejected with reagents present");
        }
        return true;
    }

    internal static bool WantsToCraftSpell(Player player, SpellInfo spell, PlayerbotStatWeights weights, SpellStore store, SpellbookCache spellbook)
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

    /// <summary>Spellbook walks for <see cref="IsReagentForKnownRecipe"/> on this thread: the test seam of its index.</summary>
    [ThreadStatic] private static int t_reagentIndexBuilds;

    internal static int ReagentIndexBuilds => t_reagentIndexBuilds;

    /// <summary>
    /// Per known-spell snapshot (<see cref="SpellbookCache.GetSpells"/> hands out one instance per bot until its book changes, so a learned or
    /// forgotten spell is a new key): the items any of those spells uses as a reagent, with the spell store it was read from.
    /// </summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<uint>, KnownRecipeReagents> ReagentIndex = new();

    private sealed record KnownRecipeReagents(SpellStore Store, HashSet<uint> Items);

    /// <summary>
    /// Whether a known spell uses <paramref name="reagentItemId"/> as a reagent (count above 0). The spellbook is walked once per book
    /// snapshot and spell store into a set: <see cref="CraftPriority"/> asked this of every craftable recipe on every think, a spellbook walk
    /// per recipe (live 2026-10-27, H1: CraftPriority/IsReagentForKnownRecipe 6.5% of think time).
    /// </summary>
    private static bool IsReagentForKnownRecipe(Player player, uint reagentItemId, SpellStore store, SpellbookCache spellbook)
    {
        IReadOnlyList<uint> spells = spellbook.GetSpells(player);
        if (!ReagentIndex.TryGetValue(spells, out KnownRecipeReagents? index) || !ReferenceEquals(index.Store, store))
        {
            t_reagentIndexBuilds++;
            var items = new HashSet<uint>();
            foreach (uint spellId in spells)
            {
                if (store.Get(spellId) is not { } spell)
                    continue;
                for (int r = 0; r < spell.Reagents.Count; r++)
                {
                    if (spell.Reagents[r].Count > 0)
                        items.Add(spell.Reagents[r].ItemId);
                }
            }

            index = new KnownRecipeReagents(store, items);
            ReagentIndex.AddOrUpdate(spells, index);
        }

        return index.Items.Contains(reagentItemId);
    }

    /// <summary>
    /// Right after the bot sent a cast: true when nothing of it is underway, neither a cast in the slot nor (spell batching on) a go waiting
    /// in the batch. Only then does an unchanged inventory mean the server refused the cast.
    /// </summary>
    private bool CastNotUnderway(Player player)
        => _session.Services.GetService<SpellFeature>()?.System is not { } spells
            || (spells.GetState(player.Guid)?.CurrentCast is null && !spells.HasPendingGo(player));

    private static bool ShouldDeferCraftCast(Player player, SpellInfo spell, SpellSystem? spellSystem)
    {
        if (player.IsQuestSettlementPending)
            return true;
        if (spellSystem is null)
            return false;
        if (!spellSystem.IsSpellReady(player, spell))
            return true;
        // With spell batching on, an instant the bot just cast (the campfire it cooks at) waits for its go out of the cast slot.
        return spellSystem.HasPendingGo(player) || spellSystem.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting };
    }

    private void EnsureReadyForCraftCast(Player player, SpellInfo spell)
    {
        if (player.StandState != StandState.Stand)
        {
            player.StandState = StandState.Stand;
        }

        SpellSystem? spellSystem = _session.Services.GetService<SpellFeature>()?.System;
        if (spellSystem is not null
            && ArcaneCore.Game.Locomotion.MountService.IsMounted(player)
            && (player.UnitFlags & UnitFlags.TaxiFlight) == 0)
        {
            ArcaneCore.Game.Locomotion.MountService.Unmount(spellSystem, player);
            spellSystem.RemoveSpellsCausingAura(player, AuraType.Mounted);
        }

        byte form = player.GetByte(UpdateFields.UnitFieldBytes1, 2);
        if (form != 0)
        {
            var stanceService = _session.Services.GetService<StanceFeature>()?.Service;
            uint? formFlags = stanceService is not null
                ? stanceService.GetFormFlags((ShapeshiftForm)form)
                : (ArcaneCore.Kernel.WorldData.ShapeshiftFormCatalog.Retail.TryGet(form, out ArcaneCore.Kernel.WorldData.ShapeshiftFormInfo info) ? info.Flags1 : null);
            if (spell.GetErrorAtShapeshiftedCast(form, formFlags) != SpellCastResult.CastOk
                || (stanceService is not null && stanceService.CheckCast(player, spell) != SpellCastResult.CastOk)
                || spellSystem?.CheckCasterState(player, spell) == SpellCastResult.NotShapeshift)
            {
                spellSystem?.RemoveSpellsCausingAura(player, AuraType.ModShapeshift);
                spellSystem?.RemoveSpellsCausingAura(player, AuraType.ModStealth);
                player.SetByte(UpdateFields.UnitFieldBytes1, 2, 0);
            }
        }
    }

    private static bool HasReagentsAndTools(Player player, SpellInfo spell, SpellSystem? spellSystem = null)
    {
        for (int t = 0; t < spell.Totems.Count; t++)
        {
            uint totem = spell.Totems[t];
            if (totem != 0 && player.Inventory.GetItemCount(totem) < 1)
                return false;
        }

        if (spell.EquippedItemClass >= 0 && spellSystem is not null)
        {
            var ctx = new SpellCastCheckContext(spellSystem, player, spell, SpellCastTargets.ForSelf(), player, false, true);
            if (new EquippedItemCastCheck().Check(ctx) != SpellCastResult.CastOk)
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

        if (!anyReagent)
            return false;

        if (spellSystem is not null)
        {
            var ctx = new SpellCastCheckContext(spellSystem, player, spell, SpellCastTargets.ForSelf(), player, false, true);
            for (int i = 0; i < spellSystem.CastChecks.Count; i++)
            {
                ISpellCastCheck check = spellSystem.CastChecks[i];
                if (check is ArcaneCore.Game.Crafting.ReagentCastCheck or CombatRestrictionCastCheck or LocationCastCheck
                    && check.Check(ctx) != SpellCastResult.CastOk)
                    return false;
            }
        }

        for (int i = 0; i < spell.Effects.Count; i++)
        {
            SpellEffectInfo eff = spell.Effects[i];
            if (eff.Effect == SpellEffectName.CreateItem && eff.ItemType != 0)
            {
                uint maxCount = (uint)Math.Max(1, eff.BasePoints + 1 + eff.DieSides);
                if (player.Inventory.CanStoreNewItem(eff.ItemType, maxCount, [], out _) != InventoryResult.Ok)
                    return false;
            }
        }

        return true;
    }

    internal static bool WantsToEnchantSpell(Player player, SpellInfo spell)
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
        EnsureReadyForCraftCast(player, bestSpell);
        SpellSystem? spellSystem = _session.Services.GetService<SpellFeature>()?.System;
        if (ShouldDeferCraftCast(player, bestSpell, spellSystem))
        {
            Goal = PlayerbotGoalKind.Vendor;
            TargetEntry = 0;
            return true;
        }
        if (spellSystem is not null && spellSystem.CheckCasterState(player, bestSpell) != SpellCastResult.CastOk)
        {
            _refusedUntil[SpellKey(bestSpell.Id)] = unchecked(now + RefusalBackoffMs);
            return false;
        }

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(16);
        writer.WriteUInt32(bestSpell.Id);
        new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = bestTarget.Guid }.Write(writer);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        if (CastNotUnderway(player)
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
        EnsureReadyForCraftCast(player, disenchantSpell);
        SpellSystem? spellSystem = _session.Services.GetService<SpellFeature>()?.System;
        if (ShouldDeferCraftCast(player, disenchantSpell, spellSystem))
        {
            Goal = PlayerbotGoalKind.Vendor;
            TargetEntry = 0;
            return true;
        }
        if (spellSystem is not null && spellSystem.CheckCasterState(player, disenchantSpell) != SpellCastResult.CastOk)
        {
            _refusedUntil[SpellKey(disenchantSpell.Id)] = unchecked(now + RefusalBackoffMs);
            return false;
        }

        Goal = PlayerbotGoalKind.Vendor;
        TargetEntry = 0;
        var writer = new PacketWriter(16);
        writer.WriteUInt32(disenchantSpell.Id);
        new SpellCastTargets { Mask = SpellCastTargetFlags.Item, Item = candidate.Guid }.Write(writer);
        if (!_session.TryManagedAction(WorldOpcode.CmsgCastSpell, writer.ToArray()))
            return false;

        BeginLootWait(player, candidate.Guid, disenchantSpell, now);
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
        EnsureReadyForCraftCast(player, pickLockSpell);
        SpellSystem? spellSystem = _session.Services.GetService<SpellFeature>()?.System;
        if (ShouldDeferCraftCast(player, pickLockSpell, spellSystem))
        {
            Goal = PlayerbotGoalKind.Vendor;
            TargetEntry = 0;
            return true;
        }
        if (spellSystem is not null && spellSystem.CheckCasterState(player, pickLockSpell) != SpellCastResult.CastOk)
        {
            _refusedUntil[SpellKey(pickLockSpell.Id)] = unchecked(now + RefusalBackoffMs);
            return false;
        }

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

    /// <summary>
    /// Sets aside a recipe whose spell focus <paramref name="focus"/> the bot could not get in reach, and that focus object with it, for a
    /// backoff that doubles with every failure in a row for this kind of focus (<see cref="FocusBackoffMs"/> up to
    /// <see cref="MaxFocusBackoffMs"/>): the live server showed 181 bots retrying the same unreachable fire every ten minutes for days
    /// (14k craft-failed reports). A bot that stands in reach of a focus again starts over (<see cref="_focusFailures"/>); the next
    /// think of another recipe walks to the next nearest fire instead (<see cref="HasNearbySpellFocus"/> skips the set-aside one).
    /// </summary>
    private void SetFocusAside(SpellInfo recipe, GameObject focus, uint now)
    {
        _focusFailures.TryGetValue(recipe.RequiresSpellFocus, out int failures);
        _focusFailures[recipe.RequiresSpellFocus] = Math.Min(failures + 1, 16);
        uint backoff = Math.Min(FocusBackoffMs << Math.Min(failures, 8), MaxFocusBackoffMs);
        _refusedUntil[SpellKey(recipe.Id)] = unchecked(now + backoff);
        _refusedUntil[FocusKey(recipe.RequiresSpellFocus)] = unchecked(now + backoff);
        _refusedUntil[focus.Guid.Value] = unchecked(now + backoff);
    }

    /// <summary>
    /// Plans a route that ends within <paramref name="focus"/>'s server-accepted reach
    /// (<see cref="GameObjectMapSystem.FindSpellFocus"/>: <c>data1 + go.BoundingRadius + player.BoundingRadius</c>). Tries the exact
    /// centre first; when the centre lies inside an unwalkable firepit or tent (e.g. Camp Taurajo), accepts a nearest-end partial path or
    /// one of eight evenly-spaced ring points inside the focus radius whose route end lands within reach.
    /// </summary>
    private static PlanOutcome PlanToSpellFocus(
        Player player,
        GameObject focus,
        PlayerbotOptions options,
        ref TileWait tileWait,
        uint now,
        out PlayerbotRoute? route)
    {
        Vector3 focusPos = new(focus.X, focus.Y, focus.Z);
        float focusReach = MathF.Max(focus.Template.GetData(1), 1f) + focus.BoundingRadius + player.BoundingRadius;

        PlanOutcome exact = PlayerbotNavigation.PlanExact(player, focusPos, options, ref tileWait, now, out route);
        if (exact == PlanOutcome.Pending)
            return PlanOutcome.Pending;
        if (exact == PlanOutcome.Route)
        {
            if (route is { Points.Count: > 0 } && Vector3.Distance(route.Points[^1], focusPos) <= focusReach - 0.25f)
                return PlanOutcome.Route;
            route = null;
            exact = PlanOutcome.Refused;
        }

        PlanOutcome nearOutcome = PlayerbotNavigation.PlanExact(
            player, focusPos, options, ref tileWait, now, out PlayerbotRoute? nearRoute, trial: false, nearestEnd: true);
        if (nearOutcome == PlanOutcome.Pending)
        {
            route = null;
            return PlanOutcome.Pending;
        }
        if (nearOutcome == PlanOutcome.Route
            && nearRoute is { Points.Count: > 0 }
            && Vector3.Distance(nearRoute.Points[^1], focusPos) <= focusReach - 0.25f)
        {
            route = nearRoute;
            return PlanOutcome.Route;
        }

        float ringRadius = MathF.Min(focusReach * 0.65f, focusReach - 0.5f);
        if (ringRadius > 0.5f)
        {
            var collision = player.Map?.Collision;
            ReadOnlySpan<float> ringRadii = [MathF.Min(3.5f, focusReach - 0.5f), ringRadius, MathF.Max(ringRadius, focusReach - 1.0f)];
            foreach (float radius in ringRadii)
            {
                if (radius <= 0.5f) continue;
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * (MathF.Tau / 8f);
                    float rx = focusPos.X + (MathF.Cos(angle) * radius);
                    float ry = focusPos.Y + (MathF.Sin(angle) * radius);
                    float rz = focusPos.Z;
                    if (collision is not null)
                    {
                        float h = collision.GetHeight(rx, ry, focusPos.Z);
                        if (!PlayerbotNavigation.InvalidHeight(h))
                            rz = h;
                    }

                    var ringPoint = new Vector3(rx, ry, rz);
                    PlanOutcome ringOutcome = PlayerbotNavigation.PlanExact(
                        player, ringPoint, options, ref tileWait, now, out PlayerbotRoute? ringRoute, trial: false);
                    if (ringOutcome == PlanOutcome.Pending)
                    {
                        route = null;
                        return PlanOutcome.Pending;
                    }
                    if (ringOutcome == PlanOutcome.Route
                        && ringRoute is { Points.Count: > 0 }
                        && Vector3.Distance(ringRoute.Points[^1], focusPos) <= focusReach - 0.25f)
                    {
                        route = ringRoute;
                        return PlanOutcome.Route;
                    }
                }
            }
        }

        return exact;
    }

    private static bool HasNearbySpellFocus(Player player, uint spellFocusType, Dictionary<ulong, uint>? refused, out GameObject? nearestOutOfRange)
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
        PlayerbotNavigation.Guards.TryGetValue(player, out PlayerbotRisk? risk);
        var playerPos = new Vector3(player.X, player.Y, player.Z);
        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (map.FindObject(guid) is not GameObject { IsSpawned: true, Type: GameObjectType.SpellFocus } go
                || go.Template.GetData(0) != spellFocusType
                || refused?.ContainsKey(go.Guid.Value) == true)
                continue;

            var goPos = new Vector3(go.X, go.Y, go.Z);
            if (MathF.Abs(go.Z - player.Z) > 12f
                || PlayerbotNavigation.IsUnreachableOnFoot(player, goPos, out _, out _)
                || risk?.Blocking(player, [playerPos, goPos], completedReturn: false) is not null)
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
        EnsureReadyForCraftCast(player, campfire);
        SpellSystem? spellSystem = _session.Services.GetService<SpellFeature>()?.System;
        if (ShouldDeferCraftCast(player, campfire, spellSystem))
        {
            Goal = PlayerbotGoalKind.Vendor;
            TargetEntry = 0;
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
        if (CastNotUnderway(player)
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
        GameObjectMapSystem? goSystem = objects.FindSystem(map);

        foreach (ObjectGuid guid in player.VisibleObjects)
        {
            if (_refusedUntil.ContainsKey(guid.Value)
                || map.FindObject(guid) is not GameObject { IsSpawned: true, Type: GameObjectType.Chest } go)
                continue;

            uint reqQuest = go.Template.GetData(8);
            if ((go.Flags & (GameObjectFlags.InUse | GameObjectFlags.NoInteract)) != 0
                || go.LootState is GameObjectLootState.JustDeactivated or GameObjectLootState.NotReady
                || (!go.User.IsEmpty && go.User != player.Guid)
                || go.Loot is { IsEmpty: true }
                || HeldByAnotherLooter(go, player) // another looter holds the window: the cast is refused with CHEST_IN_USE (GatheringSpells)
                || go.Template.GetData(GameObjectMapSystem.ChestLevelData) > player.Level + GameObjectMapSystem.ChestLevelLeeway
                || (reqQuest != 0
                    && (goSystem?.Quests is { } goQuests
                        ? !goQuests.IsQuestIncomplete(player, reqQuest)
                        : !(_session.Services.GetService<ArcaneCore.World.Npc.QuestNpcFeature>()?.Services.StateOf(player) is { Loaded: true } qState && qState.Quests.GetStatus(reqQuest) == ArcaneCore.Game.Quests.QuestStatus.Incomplete))))
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

        // Its own kind: TargetEntry is a gameobject entry, which a Vendor goal (a creature entry) made bot QA read as "creature N has no spawn" (GitHub #1487).
        Goal = PlayerbotGoalKind.Gather;
        TargetEntry = bestNode.Entry;

        if (bestDistSq > GatherInteractYards * GatherInteractYards)
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

        BeginLootWait(player, bestNode.Guid, bestGatherSpell, now);
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

        BeginLootWait(player, bestCorpse.Guid, skinSpell, now);
        _refusedUntil[bestCorpse.Guid.Value] = unchecked(now + RefusalBackoffMs);
        return true;
    }

    /// <summary>
    /// Waits for the loot window of <paramref name="target"/>: the cast bar of <paramref name="spell"/> plus <see cref="LootReplyGraceMs"/>.
    /// The cast the server started for it (null when it refused the cast outright) tells a failed cast from a missing window.
    /// </summary>
    private void BeginLootWait(Player player, ObjectGuid target, SpellInfo spell, uint now)
    {
        _gatheringTarget = target;
        _waitingForGatherLoot = true;
        _lootSpellId = spell.Id;
        _lootCast = _session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is { } cast && cast.Spell.Id == spell.Id
            ? cast
            : null;
        uint castMs = (uint)Math.Max(0, Math.Max(spell.CastTime.Base, spell.CastTime.Minimum));
        _gatherWaitUntilMs = unchecked(now + castMs + LootReplyGraceMs);
    }

    private void EndLootWait()
    {
        _waitingForGatherLoot = false;
        _gatheringTarget = default;
        _lootCast = null;
        _lootSpellId = 0;
    }

    /// <summary>Targets whose current failure streak holds a refusal other than the TRY_AGAIN skill roll.</summary>
    private readonly HashSet<ulong> _failedLootNonDice = [];

    /// <summary>
    /// The cast waited on is over without reaching its effects (<see cref="SpellCast.Completed"/> false), or the server never started it:
    /// no loot window is coming.
    /// </summary>
    private bool LootCastFailed(Player player)
    {
        if (_lootCast is not { } cast)
            return true;
        return !cast.Completed
            && !ReferenceEquals(_session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast, cast);
    }

    /// <summary>
    /// A cast with no window: the target is free again for the next think (a player casts again after "Try again"), up to
    /// <see cref="MaxGatherCasts"/> failures in a row; then it is left for <see cref="RefusalBackoffMs"/> and reported.
    /// </summary>
    private void OnLootCastFailed(Player player)
    {
        ObjectGuid target = _gatheringTarget;
        uint spellId = _lootSpellId;
        EndLootWait();
        // Why the cast went nowhere (SMSG_CAST_RESULT: u32 spell, u8 status, u8 SpellCastResult), for the report (GitHub #774). A herb or ore
        // node in the orange band fails with TRY_AGAIN by the skill roll (vmangos Spell::EffectOpenLock / Player::UpdateGatherSkill): a player
        // clicks again, so a streak of those is the dice, not a fault, and is not reported.
        string? result = null;
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgCastResult))
        {
            if (packet.Payload.Length >= 6 && BitConverter.ToUInt32(packet.Payload, 0) == spellId && packet.Payload[4] != 0)
                result = ((SpellCastResult)packet.Payload[5]).ToString();
        }
        if (result == nameof(SpellCastResult.ChestInUse))
        {
            // Someone else holds the node's window: not a fault of the cast and not a streak to count. Leave it at once for the full back-off
            // (the cast set it; it is not cleared for a retry) and let the holder finish or the server's loot audit close a stale window.
            _failedLootCasts.Remove(target.Value);
            _failedLootNonDice.Remove(target.Value);
            _refusedUntil[target.Value] = unchecked(_session.World.NowMs + RefusalBackoffMs);
            return;
        }
        if (result is not null && result != nameof(SpellCastResult.TryAgain))
            _failedLootNonDice.Add(target.Value);
        int failures = _failedLootCasts.GetValueOrDefault(target.Value) + 1;
        if (failures < MaxGatherCasts)
        {
            if (_failedLootCasts.Count >= 64)
            {
                _failedLootCasts.Clear();
                _failedLootNonDice.Clear();
            }
            _failedLootCasts[target.Value] = failures;
            _refusedUntil.Remove(target.Value);
            return;
        }

        _failedLootCasts.Remove(target.Value);
        bool onlyDice = !_failedLootNonDice.Remove(target.Value) && result == nameof(SpellCastResult.TryAgain);
        if (!_session.IsInChaosProbe && !onlyDice)
            PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"gather or disenchant cast {spellId} on {target} failed {failures} times in a row (no loot window, last result {result ?? "none sent"})");
    }

    /// <summary>
    /// The wait for a gather, skin or disenchant window on every brain update, between thinks too (GitHub #774). The window opens in the
    /// tick the cast lands, while the crafting rung reads it only on a later think that reaches the rung: seconds later, after any higher
    /// rung (the policy, the quests, a fight and its corpse loot, which discards every other SMSG_LOOT_RESPONSE) and after the bounded
    /// received-packet ring may have dropped it under the casts around the bot. Taken here, the reply is read the tick after it came, and
    /// its takes and release go out as a corpse's do (<c>PlayerbotBrain</c>'s loot actions). True while it sends them.
    /// </summary>
    internal bool UpdateGatherLoot(Player player)
    {
        if ((!_waitingForGatherLoot && _pendingLoot.Count == 0) || !player.IsInWorld || !player.IsAlive)
            return false;

        if (_waitingForGatherLoot)
            TakeGatherLoot(player);

        // Still on its cast bar (a pushback can stretch it past the wait's deadline): no window is due yet.
        if (_pendingLoot.Count == 0
            && _session.Services.GetService<SpellFeature>()?.System.GetState(player.Guid)?.CurrentCast is
            { State: SpellCastState.Preparing or SpellCastState.Casting })
        {
            return false;
        }

        return DrainGatherLoot(player, _session.World.NowMs);
    }

    /// <summary>True when a looter other than <paramref name="player"/> holds the node's loot window open (the cast is refused with CHEST_IN_USE, GatheringSpells).</summary>
    private static bool HeldByAnotherLooter(GameObject go, Player player)
    {
        if (go.Loot is not { Viewers.Count: > 0 } looted)
            return false;
        foreach (Player viewer in looted.Viewers)
        {
            if (!ReferenceEquals(viewer, player))
                return true;
        }

        return false;
    }

    /// <summary>The source of the loot window <paramref name="player"/> has open, or the empty guid.</summary>
    private ObjectGuid OpenWindowSourceOf(Player player)
        => player.Map is { } map
            && _session.Services.GetService<ArcaneCore.World.GameObjects.GameObjectLootFeature>()?.FindSystem(map)?.Loot?.OpenLootOf(player) is { } bag
                ? bag.Source
                : default;

    private bool DrainGatherLoot(Player player, uint now)
    {
        if (_waitingForGatherLoot)
            TakeGatherLoot(player);

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

        // No window and none coming: the cast failed (TRY_AGAIN, interrupted, refused), which is not a loot timeout.
        if (LootCastFailed(player))
        {
            OnLootCastFailed(player);
            return false;
        }

        if (unchecked((int)(_gatherWaitUntilMs - now)) <= 0)
        {
            ObjectGuid timedOutTarget = _gatheringTarget;
            EndLootWait();
            // The window may be open server side with its reply lost: close it, or the node stays "in use" and is never gatherable, despawned
            // or respawned. Released server side through the cast path's release (no managed action: a spent budget cannot drop it); a
            // release when no window is held does nothing.
            // Only the gather's own window, and not while a quest settlement freezes the character (the client path drops requests then).
            if (!timedOutTarget.IsEmpty && !player.IsQuestSettlementPending && OpenWindowSourceOf(player) == timedOutTarget)
                _session.Services.GetService<SpellFeature>()?.System.ReleaseLoot?.Invoke(player);
            if (timedOutTarget.High == HighGuid.GameObject)
            {
                _refusedUntil[timedOutTarget.Value] = unchecked(now + RefusalBackoffMs);
                if (player.Map?.FindObject(timedOutTarget) is not GameObject { IsSpawned: true } go
                    || (go.Flags & GameObjectFlags.NoInteract) != 0
                    || ((go.Flags & GameObjectFlags.InUse) != 0 && go.User != player.Guid)
                    || go.LootState is GameObjectLootState.JustDeactivated or GameObjectLootState.NotReady
                    || (!go.User.IsEmpty && go.User != player.Guid))
                {
                    return false;
                }
            }

            if (!_session.IsInChaosProbe)
                PlayerbotAnomalies.Report(player, "progression", "craft-failed", $"gather or disenchant loot timed out for target {timedOutTarget}");
        }

        return false;
    }

    /// <summary>Reads the SMSG_LOOT_RESPONSE of the target waited on, if it came, and queues the takes and the release; true once it came.</summary>
    private bool TakeGatherLoot(Player player)
    {
        foreach (ManagedSessionPacket packet in _session.DrainManagedPackets(WorldOpcode.SmsgLootResponse))
        {
            if (packet.Payload.Length < 9)
                continue;
            var reader = new PacketReader(packet.Payload);
            ulong sourceGuid = reader.ReadUInt64();
            if (sourceGuid != _gatheringTarget.Value)
                continue;

            _failedLootCasts.Remove(sourceGuid);
            _failedLootNonDice.Remove(sourceGuid);
            EndLootWait();
            // Any loot type is a window: herb and mining nodes go out as 1, skinning as 2, disenchanting as 4 (LootTypes.ToWire, vmangos
            // Player::SendLoot, Player.cpp:7980-7995). Only 0 (LOOT_NONE) is the refusal reply, followed by its LootError byte.
            if (reader.ReadByte() == 0 || packet.Payload.Length < 14)
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
            return true;
        }

        return !_waitingForGatherLoot;
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
    private static ulong FocusKey(uint focusId) => (1UL << 61) | focusId;
}

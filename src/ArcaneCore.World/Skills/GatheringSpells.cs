using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Skills;

/// <summary>
/// The spells that open locks and gather (herb gathering, mining, lock picking, skinning): the OPEN_LOCK (33),
/// OPEN_LOCK_ITEM (59) and SKINNING (95) cast checks and effects, after vmangos Spell::CheckCast (Spell.cpp:5948-6060)
/// and Spell::EffectOpenLock / EffectSkinning (SpellEffects.cpp:2100-2213, 5371-5390). Opening reuses the game object
/// and loot systems (<see cref="GameObjectMapSystem.OpenLock"/>, <see cref="Game.Loot.LootService"/>); this class owns only
/// the spell side: target resolution, the lock rules, the orange-failure roll and the skill-ups.
/// </summary>
/// <remarks>
/// A key opens its lock when it is the cast item (Spell::CanOpenLock, Spell.cpp:7885-7888: the item case matches <c>m_CastItem</c>), and a
/// cast from an item never gives a skill-up or adds the caster's skill (Spell.cpp:7906-7907, SpellEffects.cpp:2191-2192); the key is then
/// used up by its own spell charges (Spell::TakeCastItem). Not modelled: the per-object use
/// requirement table, battleground flags, the play-time flag, the SPELL_FAILED_DAMAGE_IMMUNE cast check (the effect itself refuses an
/// immune caster, <see cref="GameObjectMapSystem.OpenLock"/>). Multi-use veins are the object system's (an emptied vein may stay for another open,
/// LootHandler.cpp:435-487, in <see cref="GameObjectMapSystem"/>). Skinning follows Spell.cpp:5940-5969 including the tapper's head start; the
/// tap list is approximated by the corpse loot's recipients (see <see cref="LootService.IsSkinnableBy"/>).
/// </remarks>
internal sealed class GatheringSpells(IServiceProvider services, SkillsFeature skills)
{
    /// <summary>CREATURE_TYPE_CRITTER (vmangos SharedDefines.h): the creature type that may be skinned without looting.</summary>
    private const uint CreatureTypeCritter = 8;

    /// <summary>vmangos Spell::CheckRange leeway for players (Spell.cpp:6908): 1.25 yd when the cast starts, 6.25 yd when it lands.</summary>
    private const float StrictLeeway = 1.25f;

    private const float LandingLeeway = 6.25f;

    public void Register(SpellSystem system)
    {
        system.RegisterEffectCheck(SpellEffectName.OpenLock, CheckOpenLock);
        system.RegisterEffectCheck(SpellEffectName.OpenLockItem, CheckOpenLock);
        system.RegisterEffectCheck(SpellEffectName.Skinning, CheckSkinning);
        system.RegisterEffect(SpellEffectName.OpenLock, EffectOpenLock);
        system.RegisterEffect(SpellEffectName.OpenLockItem, EffectOpenLock);
        system.RegisterEffect(SpellEffectName.Skinning, EffectSkinning);
    }

    private GameObjectLootFeature? Objects => services.GetService<GameObjectLootFeature>();

    private ILogger? _logger;

    /// <summary>
    /// Refusals that reach the effect (after the cast went through) show the client nothing - no loot window, no error text - so the realm log
    /// is the only place their cause appears.
    /// </summary>
    private ILogger Logger => _logger ??= services.GetService<ILoggerFactory>()?.CreateLogger<GatheringSpells>() ?? (ILogger)NullLogger.Instance;

    private ushort ConfigMaxSkill => SkillRules.ConfigMaxSkillValue(skills.SkillOptions.MaxPlayerLevel);

    /// <summary>The spawned game object named by the cast's target block, in the caster's map.</summary>
    private GameObject? FindObject(Player player, SpellCastTargets targets)
    {
        if ((targets.Mask & (SpellCastTargetFlags.GameObject | SpellCastTargetFlags.Locked)) == 0 || targets.GameObject.IsEmpty
            || player.Map is not { } map)
        {
            return null;
        }

        GameObject? go = Objects?.FindSystem(map)?.Find(targets.GameObject);
        return go is { IsSpawned: true } && ReferenceEquals(go.Map, map) ? go : null;
    }

    private static Item? FindItem(Player player, SpellCastTargets targets)
        => (targets.Mask & (SpellCastTargetFlags.Item | SpellCastTargetFlags.TradeItem)) != 0 && !targets.Item.IsEmpty
            ? player.Inventory.GetItemByGuid(targets.Item)
            : null;

    /// <summary>vmangos CalculateSimpleValue (SpellEntry.h:1232): the effect's base points plus base dice, the skill bonus of the spell.</summary>
    private static int SimpleValue(SpellEffectInfo effect) => effect.BasePoints + effect.BaseDice;

    private SpellCastResult CheckOpenLock(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player { Skills: { } playerSkills } player)
        {
            return SpellCastResult.BadTargets; // only players open locks and gather
        }

        SpellEffectInfo effect = context.Effect;
        GameObject? go = FindObject(player, context.Targets);
        if (effect.TargetA == SpellImplicitTarget.GameObject && go is null)
        {
            return SpellCastResult.BadTargets;
        }

        uint lockId;
        if (go is not null)
        {
            lockId = GameObjectLocks.LockIdOf(go.Template);
            if (lockId == 0)
            {
                return SpellCastResult.AlreadyOpen;
            }

            // Range to the object's centre (vmangos GameObject::IsAtInteractDistance without display bounds).
            float leeway = context.Strict ? StrictLeeway : LandingLeeway;
            if (go.DistanceTo(player) + go.BoundingRadius + player.BoundingRadius > context.Spell.Range.Max + leeway)
            {
                return SpellCastResult.OutOfRange;
            }

            if (!context.Strict && ((go.Type == GameObjectType.Chest && go.Loot is { Viewers.Count: > 0 }) || (go.Flags & GameObjectFlags.InUse) != 0))
            {
                return SpellCastResult.ChestInUse;
            }
        }
        else if (FindItem(player, context.Targets) is { } item)
        {
            lockId = item.Template.LockId;
            if (lockId == 0 || (item.DynamicFlags & ItemDynFlags.Unlocked) != 0)
            {
                return SpellCastResult.AlreadyOpen;
            }
        }
        else
        {
            return SpellCastResult.BadTargets;
        }

        OpenLockCheck check = GatheringRules.CanOpenLock(
            lockId, Objects?.Content.FindLock(lockId), (uint)effect.MiscValue, context.CastItem?.Entry ?? 0, context.CastItem is not null,
            SimpleValue(effect), true, id => playerSkills.GetValue(id));
        if (check.Result != SpellCastResult.CastOk)
        {
            return check.Result;
        }

        // The orange roll belongs to the landing check: vmangos tests it only while the spell is its caster's current
        // spell, which a cast is only once prepare() has finished (Spell.cpp:3403 before :3482).
        return !context.Strict && check.SkillId is SkillIds.Herbalism or SkillIds.Mining or SkillIds.Lockpicking
            && GatheringRules.OrangeGatherFails(check.SkillId, check.SkillValue, check.RequiredSkill, ConfigMaxSkill, context.System.Random)
            ? SpellCastResult.TryAgain
            : SpellCastResult.CastOk;
    }

    private void EffectOpenLock(SpellEffectContext context)
    {
        if (context.Caster is not Player { Skills: { } playerSkills } player)
        {
            return;
        }

        SpellEffectInfo effect = context.Effect;
        GameObject? go = FindObject(player, context.Cast.Targets);
        Item? item = go is null ? FindItem(player, context.Cast.Targets) : null;
        uint lockId = go is not null ? GameObjectLocks.LockIdOf(go.Template) : item?.Template.LockId ?? 0;
        if (go is null && item is null)
        {
            Logger.LogInformation("Spell {Spell} ({Name}) by {Player}: its open-lock target ({Mask}, object {Object}, item {Item}) is gone or out of this map; nothing opened",
                context.Spell.Id, context.Spell.Name, player.Name, context.Cast.Targets.Mask, context.Cast.Targets.GameObject, context.Cast.Targets.Item);
            return;
        }

        Item? key = context.Cast.CastItem;
        OpenLockCheck check = GatheringRules.CanOpenLock(
            lockId, Objects?.Content.FindLock(lockId), (uint)effect.MiscValue, key?.Entry ?? 0, key is not null, SimpleValue(effect), true,
            id => playerSkills.GetValue(id));
        if (check.Result != SpellCastResult.CastOk)
        {
            player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(context.Spell.Id, check.Result));
            return;
        }

        // SpellEffects.cpp:2191-2192: no skill-up for an open from an item (a key, a skeleton key).
        bool rollsSkill = check.SkillId != 0 && key is null;

        // vmangos Spell::EffectOpenLock (SpellEffects.cpp:2191-2207): one Player::UpdateGatherSkill per player and object until
        // it respawns, when the skill is really known.
        void RollFor(GameObject target)
        {
            uint pureSkill = playerSkills.GetValuePure(check.SkillId);
            if (pureSkill != 0 && !target.SkillupSet.Contains(player.Guid)
                && playerSkills.UpdateGather(check.SkillId, pureSkill, (uint)check.RequiredSkill))
            {
                target.SkillupSet.Add(player.Guid);
            }
        }

        if (go is not null)
        {
            // Only an object that really opened gives a skill-up: a refusal (an immune caster, the chest quest gate, a chest
            // being despawned) leaves the node closed and the skill as it was (vmangos returns before UpdateGatherSkill for an
            // immune caster, SpellEffects.cpp:2117-2118). A chest rolls from its open: a dungeon chest accepts the open now
            // and opens its window only once the generation committed, so a generation refused later raises nothing. A door
            // or button rolls once it activated.
            bool scriptTookOpen = false; // set only when the object's script took the open over: such an Ok leaves no loot window on purpose
            GameObjectUseResult opened = Objects?.FindSystem(player.Map!)?.OpenLock(player, go.Guid, (LockType)effect.MiscValue, key?.Entry ?? 0,
                (uint)Math.Max(0, SimpleValue(effect)), target =>
                {
                    if (rollsSkill)
                    {
                        RollFor(target);
                    }
                }, _ => scriptTookOpen = true)
                ?? GameObjectUseResult.Unsupported;
            if (opened == GameObjectUseResult.Ok && go.Type == GameObjectType.Chest && !scriptTookOpen && player.Map is { } openMap
                && Objects?.FindSystem(openMap)?.Loot is { } openLoot && openLoot.OpenLootOf(player)?.Source != go.Guid
                && !(openMap.InstanceId != 0 && go.Spawn is { } openSpawn
                    && openLoot.Durable?.IsPending(new LootStateKey(openMap.InstanceId, openSpawn.Guid)) == true))
            {
                // A script that took the open is legitimate and leaves no window, so it is exempt (scriptTookOpen). A dungeon chest whose fresh
                // generation was accepted (any settlement pending on its durable key) says Ok now and opens the window once the generation committed, so only
                // that is exempt; a live bag opens synchronously and is checked like any other chest.
                ReportOpenNoEffect(context, player, go, "the open returned Ok for a chest but the player has no loot window on it");
            }

            if (opened != GameObjectUseResult.Ok)
            {
                Logger.LogInformation(
                    "Spell {Spell} ({Name}) by {Player}: {Object} '{ObjectName}' (type {Type}, lock {Lock}, lock type {LockType}, distance {Distance:0.00}) refused the open: {Result}",
                    context.Spell.Id, context.Spell.Name, player.Name, go.Guid, go.Template.Name, go.Type, lockId, effect.MiscValue, go.DistanceTo(player), opened);
            }

            if (opened == GameObjectUseResult.Ok && rollsSkill && go.Type != GameObjectType.Chest)
            {
                RollFor(go);
            }

            return;
        }

        if (item is not null)
        {
            // vmangos marks the item ITEM_DYNFLAG_UNLOCKED, then sends its loot.
            item.DynamicFlags |= ItemDynFlags.Unlocked;
            Objects?.FindSystem(player.Map!)?.Loot?.OpenItem(player, item);
        }

        // An item gets one roll per open.
        uint pure = rollsSkill ? playerSkills.GetValuePure(check.SkillId) : 0u;
        if (pure != 0)
        {
            playerSkills.UpdateGather(check.SkillId, pure, (uint)check.RequiredSkill);
        }
    }

    /// <summary>Anomaly code of an open-lock effect on a game object that left the player without any visible result.</summary>
    internal const string OpenNoEffectCode = "open-no-effect";

    /// <summary>
    /// Report (Error, subsystem <c>gameobject</c>) an open-lock cast on a game object that ended with neither a loot window nor a client-visible
    /// refusal. Called from failure branches only, so the strings are built only then. Ids and guid low only, no names.
    /// </summary>
    private static void ReportOpenNoEffect(SpellEffectContext context, Player player, GameObject go, string why)
    {
        AnomalyLedger.Shared.Report(
            "gameobject",
            OpenNoEffectCode,
            $"gameobject entry {go.Entry} spell {context.Spell.Id}",
            $"{why}; type {go.Type}, spawn {go.Spawn?.Guid.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, guid {go.Guid.Low}, lock {GameObjectLocks.LockIdOf(go.Template)}, map {go.MapId}, caster guid {player.Guid.Low}",
            AnomalySeverity.Error);
    }

    /// <summary>
    /// Spell::CheckCast for SPELL_EFFECT_SKINNING (Spell.cpp:5940-5969), in its order: BAD_TARGETS, TARGET_UNSKINNABLE (no skinnable flag),
    /// TARGET_NOT_LOOTED for a non-tapper inside the tapper's 5 s head start, LOW_CASTLEVEL, TARGET_NOT_LOOTED again unless the creature is a
    /// critter and the corpse is looted out and not yet skinned, then the orange TRY_AGAIN roll.
    /// </summary>
    private SpellCastResult CheckSkinning(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player { Skills: { } playerSkills } player || context.UnitTarget is not Creature creature)
        {
            return SpellCastResult.BadTargets;
        }

        if ((creature.UnitFlags & UnitFlags.Skinnable) == 0)
        {
            return SpellCastResult.TargetUnskinnable;
        }

        LootService? loot = player.Map is { } map ? Objects?.FindSystem(map)?.Loot : null;
        if (loot is not null && !loot.IsSkinnableBy(player, creature))
        {
            return SpellCastResult.TargetNotLooted;
        }

        int skillValue = playerSkills.GetValue(SkillIds.Skinning);
        int required = GatheringRules.SkinningRequiredSkill(skillValue, creature.Level);
        if (required > skillValue)
        {
            return SpellCastResult.LowCastlevel;
        }

        // CREATURE_TYPE_CRITTER (8) can be skinned without looting; anything else needs the corpse looted out and not skinned yet.
        if (creature.Template.CreatureType != CreatureTypeCritter && (creature.LootedForSkin || (loot is not null && !loot.IsCorpseLooted(creature))))
        {
            return SpellCastResult.TargetNotLooted;
        }

        return !context.Strict
            && GatheringRules.OrangeGatherFails(SkillIds.Skinning, skillValue, Math.Max(required, 0), ConfigMaxSkill, context.System.Random)
            ? SpellCastResult.TryAgain
            : SpellCastResult.CastOk;
    }

    private void EffectSkinning(SpellEffectContext context)
    {
        if (context.Caster is not Player { Skills: { } playerSkills } player || context.Target is not Creature creature || !creature.IsInWorld)
        {
            return;
        }

        Objects?.FindSystem(player.Map!)?.Loot?.OpenSkinning(player, creature);
        // SpellEffects.cpp:5378 removes UNIT_FLAG_SKINNABLE after SendLoot, whether or not skinning loot was
        // generated. OpenSkinning only clears the flag when a skinning table exists, so a corpse with no
        // table would stay skinnable and every later cast would grant another skill-up.
        creature.UnitFlags &= ~UnitFlags.Skinnable;
        int reqValue = GatheringRules.SkinningSkillUpLevel(creature.Level);
        uint multiplicator = IsElite(creature) ? 2u : 1u;
        playerSkills.UpdateGather(SkillIds.Skinning, playerSkills.GetValuePure(SkillIds.Skinning), (uint)reqValue, multiplicator);
    }

    /// <summary>vmangos Creature::IsElite: any rank but normal and rare.</summary>
    private static bool IsElite(Creature creature) => (CreatureRank)creature.Template.Rank is not (CreatureRank.Normal or CreatureRank.Rare);
}

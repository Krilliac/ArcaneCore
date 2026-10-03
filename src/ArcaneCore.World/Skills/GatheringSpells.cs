using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.GameObjects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Skills;

/// <summary>
/// The spells that open locks and gather (herb gathering, mining, lock picking, skinning): the OPEN_LOCK (33),
/// OPEN_LOCK_ITEM (59) and SKINNING (95) cast checks and effects, after vmangos Spell::CheckCast (Spell.cpp:5948-6060)
/// and Spell::EffectOpenLock / EffectSkinning (SpellEffects.cpp:2100-2213, 5371-5390). Opening reuses the game object
/// and loot systems (<see cref="GameObjectMapSystem.OpenLock"/>, <see cref="Game.Loot.LootService"/>); this class owns only
/// the spell side: target resolution, the lock rules, the orange-failure roll and the skill-ups.
/// </summary>
/// <remarks>
/// Not modelled: opening with a key item (no item-cast path exists, so key locks never open), the per-object use
/// requirement table, battleground flags, the play-time flag, immune users, the "target needs to be looted first"
/// message (a corpse only becomes skinnable once it is looted out, so an unlooted one reads as unskinnable) and
/// multi-use veins (the object system despawns an emptied chest).
/// </remarks>
internal sealed class GatheringSpells(IServiceProvider services, SkillsFeature skills)
{
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
            lockId, Objects?.Content.FindLock(lockId), (uint)effect.MiscValue, 0, false, SimpleValue(effect), true, id => playerSkills.GetValue(id));
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
            return;
        }

        OpenLockCheck check = GatheringRules.CanOpenLock(
            lockId, Objects?.Content.FindLock(lockId), (uint)effect.MiscValue, 0, false, SimpleValue(effect), true, id => playerSkills.GetValue(id));
        if (check.Result != SpellCastResult.CastOk)
        {
            player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(context.Spell.Id, check.Result));
            return;
        }

        if (go is not null)
        {
            Objects?.FindSystem(player.Map!)?.OpenLock(player, go.Guid, (LockType)effect.MiscValue, 0, (uint)Math.Max(0, SimpleValue(effect)));
        }
        else if (item is not null)
        {
            // vmangos marks the item ITEM_DYNFLAG_UNLOCKED, then sends its loot.
            item.DynamicFlags |= ItemDynFlags.Unlocked;
            Objects?.FindSystem(player.Map!)?.Loot?.OpenItem(player, item);
        }

        uint pure = check.SkillId == 0 ? 0u : playerSkills.GetValuePure(check.SkillId);
        if (pure == 0)
        {
            return;
        }

        // One skill-up per player and node until the node respawns; an item always gets its roll.
        if (go is null)
        {
            playerSkills.UpdateGather(check.SkillId, pure, (uint)check.RequiredSkill);
        }
        else if (!go.SkillupSet.Contains(player.Guid) && playerSkills.UpdateGather(check.SkillId, pure, (uint)check.RequiredSkill))
        {
            go.SkillupSet.Add(player.Guid);
        }
    }

    private SpellCastResult CheckSkinning(SpellEffectCheckContext context)
    {
        if (context.Caster is not Player { Skills: { } playerSkills } || context.UnitTarget is not Creature creature)
        {
            return SpellCastResult.BadTargets;
        }

        if ((creature.UnitFlags & UnitFlags.Skinnable) == 0)
        {
            return SpellCastResult.TargetUnskinnable;
        }

        int skillValue = playerSkills.GetValue(SkillIds.Skinning);
        int required = GatheringRules.SkinningRequiredSkill(skillValue, creature.Level);
        if (required > skillValue)
        {
            return SpellCastResult.LowCastlevel;
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
        int reqValue = GatheringRules.SkinningSkillUpLevel(creature.Level);
        uint multiplicator = IsElite(creature) ? 2u : 1u;
        playerSkills.UpdateGather(SkillIds.Skinning, playerSkills.GetValuePure(SkillIds.Skinning), (uint)reqValue, multiplicator);
    }

    /// <summary>vmangos Creature::IsElite: any rank but normal and rare.</summary>
    private static bool IsElite(Creature creature) => (CreatureRank)creature.Template.Rank is not (CreatureRank.Normal or CreatureRank.Rare);
}

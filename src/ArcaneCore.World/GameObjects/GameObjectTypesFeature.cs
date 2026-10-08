using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The game object type behaviours that need other areas (a discovered <see cref="IWorldFeature"/>): every map's
/// <see cref="GameObjectMapSystem"/> gets the spell system for object spells (goober and spell-caster spells, traps, rituals), the quest feature
/// for goober gossip and the groups for party-only spell casters and grouped rituals; the spell system gets the TRANS_DOOR effect of
/// non-fishing objects (portals, rituals), SUMMON_PLAYER and the ritual channel ends (<see cref="GameObjectSpellEffects"/>). Everything is
/// resolved at use time, so a missing collaborator turns its behaviour off (no spell feature: objects cast nothing). Flag stands stay without a
/// battleground side until the battleground area is wired into the world (<see cref="GameObjectMapSystem.FlagStands"/>).
/// </summary>
public sealed class GameObjectTypesFeature(IServiceProvider services, ILogger<GameObjectTypesFeature> logger) : IWorldFeature
{
    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        // Features attach in type-name order: this post runs after GameObjectLootFeature's install (its map systems exist) and before
        // SpecialLootFeature's, whose fishing TRANS_DOOR handler then wraps this one and hands it every object that is not a bobber.
        world.Post(Install);
    }

    private void Install()
    {
        WorldRuntime world = _world!;
        if (services.GetService<GameObjectLootFeature>() is not { } objects)
        {
            logger.LogInformation("Game object types: no game object feature, the type behaviours are off");
            return;
        }

        if (services.GetService<SpellFeature>() is { } spells)
        {
            new GameObjectSpellEffects(map => objects.FindSystem(map)).Register(spells.System);
        }

        world.MapCreated += map => Configure(objects, map);
        foreach (Map map in world.Maps.ToArray())
        {
            Configure(objects, map);
        }
    }

    private void Configure(GameObjectLootFeature objects, Map map)
    {
        GameObjectMapSystem system;
        try
        {
            system = objects.GetOrCreateSystem(map);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "no game object system for map {Map}", map.MapId);
            return;
        }

        system.Spells ??= new WorldObjectSpells(services, _world!);
        system.Gossip ??= new WorldObjectGossip(services);
        system.SameRaid ??= (a, b) => Groups()?.AreInSameGroup(a.Guid, b.Guid) == true;
        system.GroupIdOf ??= player => Groups()?.GetGroup(player.Guid)?.Id ?? 0;
    }

    private GroupManager? Groups()
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Groups;
        }
        catch (InvalidOperationException)
        {
            return null; // the social feature is not attached
        }
    }

    /// <summary>The spell system behind <see cref="IGameObjectSpells"/>, looked up at use time.</summary>
    private sealed class WorldObjectSpells(IServiceProvider services, WorldRuntime world) : IGameObjectSpells
    {
        private SpellSystem? System => services.GetService<SpellFeature>()?.System;

        public bool Cast(GameObject source, uint spellId, Unit target, Unit? unitCaster)
            => System?.CastForGameObject(source, spellId, target, unitCaster) == SpellCastResult.CastOk;

        public float? MaxRange(uint spellId) => System?.Store.Get(spellId)?.Range.Max;

        public bool IsChanneling(Unit unit) => System?.IsChanneling(unit) == true;

        public void StartRitualAnimation(Player helper, uint animSpellId, GameObject ritual)
            => System?.CastSpell(helper, animSpellId, new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = ritual.Guid }, triggered: true);

        /// <summary>
        /// The summon target may be anywhere in the world (vmangos <c>sObjectMgr.GetPlayer</c>), while a cast resolves its unit target in the
        /// caster's map: a summon spell aimed at a player elsewhere sends its summon request directly (Spell::EffectSummonPlayer).
        /// </summary>
        public bool CastRitualSpell(GameObject ritual, uint spellId, Unit caster, ObjectGuid summonTarget)
        {
            if (System is not { } spells || spells.Store.Get(spellId) is not { } spell)
            {
                return false;
            }

            Player? target = summonTarget.IsEmpty ? null : world.FindOnlinePlayer(summonTarget);
            if (target is not null && !ReferenceEquals(target.Map, caster.Map)
                && spell.Effects.Any(e => e.Effect == SpellEffectName.SummonPlayer))
            {
                GameObjectSpellEffects.Offer(target, caster, ritual, spells.NowMs);
                return true;
            }

            Unit? unitTarget = target is not null && ReferenceEquals(target.Map, caster.Map) ? target : null;
            return spells.CastRitualSpell(ritual, spellId, caster, unitTarget) == SpellCastResult.CastOk;
        }

        public void StartCreatingSpellCooldown(Player owner, uint spellId) => System?.StartCreatingSpellCooldown(owner, spellId);
    }

    /// <summary>Goober gossip through the quest and gossip feature.</summary>
    private sealed class WorldObjectGossip(IServiceProvider services) : IGameObjectGossip
    {
        public bool OpenGossip(Player player, GameObject go, uint menuId)
            => services.GetService<QuestNpcFeature>()?.Services.OpenGameObjectGossip(player, go.Guid, menuId) ?? false;
    }
}

using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Spells;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>The "Conditions" configuration section.</summary>
public sealed class ConditionOptions
{
    public const string SectionName = "Conditions";

    /// <summary>
    /// Game event ids reported active to CONDITION_ACTIVE_GAME_EVENT (cmangos Conditions.cpp:245-248) IN ADDITION to the events the
    /// game-event service says are running (<c>GameEventFeature</c>, docs/areas/game-events-weather.md). Default none: an
    /// operator override for a world that runs no event service or wants an event forced on for its conditions only.
    /// </summary>
    public uint[] ActiveGameEvents { get; set; } = [];

    /// <summary>Holiday ids reported active to CONDITION_ACTIVE_HOLIDAY (Conditions.cpp:318-321) in addition to the running events' holidays; default none, same override.</summary>
    public uint[] ActiveHolidays { get; set; } = [];
}

/// <summary>
/// Evaluates the <c>conditions</c> table for the NPC services (gossip options and menu texts, vendor
/// rows, quest <c>RequiredCondition</c>): the <see cref="IConditionEvaluator"/> the quest and NPC
/// features pick up through <see cref="NpcServicesFeature.Extend"/>. The table comes from the optional
/// <see cref="IConditionContentStore"/> (the Data module supplies it; without stored rows every
/// conditioned option stays hidden, as a missing row does in cmangos). The evaluator forwards to the current inner evaluator, so a services
/// rebuild by another feature never holds a stale one.
/// </summary>
public sealed class ConditionFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<ConditionFeature> logger)
    : IWorldFeature, IConditionEvaluator, IConditionTableEvaluator
{
    private ConditionEvaluator _current = new(ConditionTable.Empty, new ConditionContext());
    private WorldRuntime? _world;
    private ConditionTable? _table;

    public ConditionOptions Options { get; } = new();

    /// <summary>The evaluator over the loaded table.</summary>
    public ConditionEvaluator Current => Volatile.Read(ref _current);

    public bool IsSatisfied(uint conditionId, Player player, NpcInfo? source) => Current.IsSatisfied(conditionId, player, source);

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        services.GetService<IConfiguration>()?.GetSection(ConditionOptions.SectionName).Bind(Options);
        IReadOnlyList<ConditionRecord> records;
        using (IServiceScope scope = scopes.CreateScope())
        {
            records = scope.ServiceProvider.GetService<IConditionContentStore>() is { } store
                ? store.LoadAsync().GetAwaiter().GetResult()
                : [];
        }

        ConditionTable table = ConditionTable.Build(records);
        _table = table;
        Install(world, table);
        foreach (ConditionRejection rejection in table.Rejected.Take(20))
        {
            logger.LogWarning("Condition {Entry} (type {Type}) skipped: {Reason}", rejection.Entry, rejection.Type, rejection.Reason);
        }

        LogSummary(table);
        world.WorldTick += _ => InstallSpawnGroupConditions(world);
    }

    /// <summary>
    /// cmangos spawn groups ask their <c>spawn_group.WorldState</c> condition with a map but no player
    /// (SpawnGroup::IsWorldstateConditionSatisfied). Each map system gets the current evaluator with its owning map
    /// (systems attach at different times, so it is installed from the world tick). Undecidable conditions keep their group out.
    /// </summary>
    private void InstallSpawnGroupConditions(WorldRuntime world)
    {
        foreach (Map map in world.Maps)
        {
            if (map.FindUpdater<Game.Creatures.CreatureMapSystem>() is { SpawnGroupCondition: null } creatures)
            {
                creatures.SpawnGroupCondition = group => Current.EvaluateOnMap(group.WorldStateCondition, map);
            }

            if (map.FindUpdater<Game.GameObjects.GameObjectMapSystem>() is { SpawnGroupCondition: null } objects)
            {
                objects.SpawnGroupCondition = group => Current.EvaluateOnMap(group.WorldStateCondition, map);
            }
        }
    }

    /// <summary>
    /// Build the evaluator over <paramref name="table"/> with the collaborators the daemon has right now. A feature that attaches
    /// after this one and becomes a collaborator (the skills feature, which needs its content loaded first) calls
    /// <see cref="RefreshCollaborators"/>.
    /// </summary>
    private void Install(WorldRuntime world, ConditionTable table)
        => Volatile.Write(ref _current, new ConditionEvaluator(table, BuildContext(world)));

    /// <summary>Rebuild the evaluator over the loaded table once a collaborator became available after <see cref="Attach"/>.</summary>
    public void RefreshCollaborators()
    {
        if (_world is null || _table is null)
        {
            return;
        }

        Install(_world, _table);
        LogSummary(_table);
    }

    private void LogSummary(ConditionTable table)
    {
        ConditionSummary summary = Current.Summarize();
        logger.LogInformation(
            "Conditions: {Rows} rows loaded, {Rejected} rejected, {Evaluable} decidable; undecidable leaf rows by type (they fail closed): {Unavailable}",
            table.Count, table.Rejected.Count, summary.Evaluable,
            summary.UnavailableByType.Count == 0 ? "none" : string.Join(", ", summary.UnavailableByType.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}")));
    }

    private ConditionContext BuildContext(WorldRuntime world)
    {
        HashSet<uint> events = [.. Options.ActiveGameEvents];
        HashSet<uint> holidays = [.. Options.ActiveHolidays];
        ReputationService? reputation = services.GetService<ReputationFeature>()?.Service;
        ReputationService? ranked = reputation is { Factions.Count: > 0 } ? reputation : null;
        ConditionRuntimeState runtimeConditions = ConditionRuntimeState.For(world);
        int? MapVariable(Map map, uint id)
        {
            if (map.FindUpdater<InstanceData>() is { } instance)
            {
                int? encounter = (instance as IInstanceConditionFacts)?.MapVariable(id);
                if (encounter.HasValue) return encounter;
                if (instance.TryGetVariable(id, out int explicitValue)) return explicitValue;
            }

            return runtimeConditions.GetMapVariable(map, id);
        }
        return new ConditionContext
        {
            ItemCount = (player, item, bank) => player.Inventory.GetItemCount(item, bank),
            HasSpell = (player, spell) => services.GetService<SpellFeature>()?.System.Spellbook?.HasSpell(player, spell) ?? false,

            // Player::GetSkillValueBase from the skills feature, once it is active (it attaches after this feature and then
            // calls RefreshCollaborators). Without active skills (Legacy mode or no skill content) SkillValueBase stays unset,
            // so SKILL and SKILL_BELOW fail closed and are counted, instead of reading "no skill" for everyone.
            SkillValueBase = services.GetService<SkillsFeature>() is { IsActive: true }
                ? (player, skill) => player.Skills?.GetValueBase(skill) ?? (ushort)0
                : null,
            FactionExists = ranked is null ? null : faction => ranked.Factions.Find(faction) is not null,
            ReputationRank = ranked is null ? null : (player, faction) => (byte)ranked.GetRank(player, faction),
            ZoneAndArea = (mapId, x, y, z) => world.Maps.FirstOrDefault(m => m.MapId == mapId)?.GetZoneAndAreaId(x, y, z) ?? (0u, 0u),
            AreaFlags = area => WorldMaps.Of(world).Areas.GetById(area)?.Flags,
            HasAura = (player, spell, effect) => services.GetService<SpellFeature>()?.System.GetAuras(player)
                .Any(h => !h.IsRemoved && h.Spell.Id == spell && effect < h.Auras.Count && h.Auras[effect] is not null) ?? false,
            HasAdCommissionAura = player => services.GetService<SpellFeature>()?.System.GetAuras(player).Any(h => !h.IsRemoved
                && (h.Spell.HasAttribute(AllowWhileMounted) || h.Spell.HasAttribute(SpellAttributes.IsAbility)) && h.Spell.SpellVisual == AdCommissionVisual) ?? false,
            // The live game-event state is read at every evaluation (the feature may attach after this one, and its service is replaced by a reload).
            IsGameEventActive = id => events.Contains(id) || (id is > 0 and <= ushort.MaxValue && services.GetService<GameEventFeature>()?.IsActiveEvent((ushort)id) == true),
            IsHolidayActive = id => holidays.Contains(id) || (services.GetService<GameEventFeature>()?.IsActiveHoliday(id) ?? false),
            Quests = () => services.GetService<QuestNpcFeature>()?.Services,
            InstanceScript = (player, conditionId) => player.Map?.FindUpdater<InstanceData>() is { } script
                ? script.CheckConditionCriteriaMeet(player, conditionId) : null,
            // Raid scripts expose their saved encounter state directly; the mutable runtime facts remain
            // available for scripts that publish an encounter without an instance-state mapping.
            CompletedEncounter = (player, first, second) => player.Map is { } map
                ? (map.FindUpdater<InstanceData>() is IInstanceConditionFacts facts
                    && (facts.HasCompletedEncounter(first) == true || (second != 0 && facts.HasCompletedEncounter(second) == true)))
                    || runtimeConditions.HasCompletedEncounter(map, first, second) : null,
            LastWaypoint = (player, npc) => player.Map?.FindUpdater<CreatureMapSystem>()
                ?.FindCreature(npc.Guid)?.Motion.LastReachedWaypoint,
            CreatureInRange = (player, entry, range) => player.Map?.FindUpdater<CreatureMapSystem>() is { } creatures
                ? creatures.CreaturesOfEntryInRange(player, entry, range).Any(c => c.IsAlive) : null,
            SpawnCount = (player, entry) => player.Map?.FindUpdater<CreatureMapSystem>() is { } spawned
                // mangos-classic Creature::AddToWorld/RemoveFromWorld count only
                // CREATURE_EXTRA_FLAG_COUNT_SPAWNS (0x00200000), while the creature is in the world.
                ? (uint)spawned.Creatures.Count(c => c.Template.Entry == entry && c.IsInWorld
                    && c.Template.ExtraFlagsDialect == CreatureExtraFlagsDialect.CMangos
                    && (c.Template.ExtraFlags & 0x00200000u) != 0) : null,
            WorldScript = (id, state) => services.GetService<WorldState.WarEffortFeature>()?.WorldScriptCondition(id, state)
                ?? runtimeConditions.WorldScriptCondition(id, state),
            // AQ20 boss variables follow the saved encounter slots, including immediately after Load.
            WorldState = (player, id) => player.Map is { } map ? MapVariable(map, id) : null,
            MapWorldState = MapVariable,

            // GetHonorRankInfo().rank (the PvP_RANK condition, classic-db/mangos-classic type 11). Without honor the condition stays
            // undecidable and fails closed, as before.
            HonorRank = services.GetService<Honor.HonorFeature>()?.ActiveService is { } honor
                ? player => ((Game.Honor.IPlayerHonor)honor).CurrentRank(player)
                : null,
        };
    }

    /// <summary>SPELL_ATTR_ALLOW_WHILE_MOUNTED (mangos-classic SpellDefines.h:54).</summary>
    private const SpellAttributes AllowWhileMounted = (SpellAttributes)0x01000000;

    /// <summary>The AD commission aura spell visual (Conditions.cpp:228-235).</summary>
    private const uint AdCommissionVisual = 3580;
}

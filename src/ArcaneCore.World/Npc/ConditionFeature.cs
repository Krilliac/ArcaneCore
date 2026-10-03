using ArcaneCore.Game.Conditions;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
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
/// <see cref="IConditionContentStore"/> (no implementation ships yet: until the content importer
/// registers one the table is empty and every conditioned option stays hidden, which is also what a
/// missing row means in cmangos). The evaluator forwards to the current inner evaluator, so a services
/// rebuild by another feature never holds a stale one.
/// </summary>
public sealed class ConditionFeature(IServiceProvider services, IServiceScopeFactory scopes, ILogger<ConditionFeature> logger)
    : IWorldFeature, IConditionEvaluator
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
        };
    }

    /// <summary>SPELL_ATTR_ALLOW_WHILE_MOUNTED (mangos-classic SpellDefines.h:54).</summary>
    private const SpellAttributes AllowWhileMounted = (SpellAttributes)0x01000000;

    /// <summary>The AD commission aura spell visual (Conditions.cpp:228-235).</summary>
    private const uint AdCommissionVisual = 3580;
}

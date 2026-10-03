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
using ArcaneCore.World.Spells;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

/// <summary>The "Conditions" configuration section.</summary>
public sealed class ConditionOptions
{
    public const string SectionName = "Conditions";

    /// <summary>
    /// Game event ids reported active to CONDITION_ACTIVE_GAME_EVENT (cmangos Conditions.cpp:245-248). Retail
    /// events are date driven (game_event + the calendar); that scheduler does not exist yet, so by default
    /// no event is active. This is a documented placeholder, not retail behaviour.
    /// </summary>
    public uint[] ActiveGameEvents { get; set; } = [];

    /// <summary>Holiday ids reported active to CONDITION_ACTIVE_HOLIDAY (Conditions.cpp:318-321); default none, same placeholder.</summary>
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
        var evaluator = new ConditionEvaluator(table, BuildContext(world));
        Volatile.Write(ref _current, evaluator);
        foreach (ConditionRejection rejection in table.Rejected.Take(20))
        {
            logger.LogWarning("Condition {Entry} (type {Type}) skipped: {Reason}", rejection.Entry, rejection.Type, rejection.Reason);
        }

        ConditionSummary summary = evaluator.Summarize();
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

            // No skills owner exists (docs/integration/npc-services.md): SkillValueBase stays unset so the
            // skill conditions fail closed and are counted, instead of reading "no skill" for everyone.
            FactionExists = ranked is null ? null : faction => ranked.Factions.Find(faction) is not null,
            ReputationRank = ranked is null ? null : (player, faction) => (byte)ranked.GetRank(player, faction),
            ZoneAndArea = (mapId, x, y, z) => world.Maps.FirstOrDefault(m => m.MapId == mapId)?.GetZoneAndAreaId(x, y, z) ?? (0u, 0u),
            AreaFlags = area => WorldMaps.Of(world).Areas.GetById(area)?.Flags,
            HasAura = (player, spell, effect) => services.GetService<SpellFeature>()?.System.GetAuras(player)
                .Any(h => !h.IsRemoved && h.Spell.Id == spell && effect < h.Auras.Count && h.Auras[effect] is not null) ?? false,
            HasAdCommissionAura = player => services.GetService<SpellFeature>()?.System.GetAuras(player).Any(h => !h.IsRemoved
                && (h.Spell.HasAttribute(AllowWhileMounted) || h.Spell.HasAttribute(SpellAttributes.IsAbility)) && h.Spell.SpellVisual == AdCommissionVisual) ?? false,
            IsGameEventActive = events.Contains,
            IsHolidayActive = holidays.Contains,
            Quests = () => services.GetService<QuestNpcFeature>()?.Services,
        };
    }

    /// <summary>SPELL_ATTR_ALLOW_WHILE_MOUNTED (mangos-classic SpellDefines.h:54).</summary>
    private const SpellAttributes AllowWhileMounted = (SpellAttributes)0x01000000;

    /// <summary>The AD commission aura spell visual (Conditions.cpp:228-235).</summary>
    private const uint AdCommissionVisual = 3580;
}

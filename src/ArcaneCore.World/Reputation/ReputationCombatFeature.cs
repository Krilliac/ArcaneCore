using ArcaneCore.Data.Npc;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Features;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Reputation;

/// <summary>
/// Installs reputation-aware combat and aggro: <see cref="ReputationCombatHooks"/> over the template-only
/// <see cref="FactionCombatHooks"/> the combat feature registered, and, as the world's <see cref="ICreatureHostility"/>,
/// <see cref="ReputationCreatureHostility"/> over the template-only hostility. It is inert (and says so in one log line)
/// when <c>Reputation:CombatReactions</c> is false, when no Faction.dbc is loaded, or when no FactionTemplate catalog is
/// loaded (<c>Creatures:FactionTemplateDbcPath</c>): the half-configured world then keeps today's template-only behaviour.
/// The combat hooks are installed only over <see cref="FactionCombatHooks"/>; if another feature registered different
/// hooks they are left alone and a warning is logged.
/// <para>
/// It attaches after <c>ArcaneCore.World.Combat.WorldCombatHooksFeature</c> (features attach in full type-name order) and
/// registers with <see cref="CombatHooks.Register"/> only after checking what is registered. The creature system resolves
/// <see cref="ICreatureHostility"/> from this feature at its own attach, before this one attaches, so the hostility answers
/// lazily and delegates until <see cref="Attach"/> has built the resolver.
/// </para>
/// </summary>
public sealed class ReputationCombatFeature(IServiceProvider services, ILogger<ReputationCombatFeature> logger) : IWorldFeature, ICreatureHostility
{
    private volatile ICreatureHostility? _hostility;
    private ICreatureHostility? _fallback;

    /// <summary>The reaction resolver of the installed rules, or null while inactive (shared with the spell handlers).</summary>
    public ReputationReactionResolver? Resolver { get; private set; }

    /// <summary>Whether the reputation combat rules are installed.</summary>
    public bool IsActive => _hostility is not null;

    /// <summary>The reason the feature is inactive, or null when active (also logged at attach).</summary>
    public string? InactiveReason { get; private set; } = "not attached";

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        ReputationFeature reputation = services.GetRequiredService<ReputationFeature>();
        if (!reputation.Options.CombatReactions)
        {
            Inactive("Reputation:CombatReactions is off");
            return;
        }

        ReputationService service = reputation.Service;
        if (service.Factions.Count == 0)
        {
            Inactive("no Faction.dbc is loaded (set Reputation:FactionDbcPath)");
            return;
        }

        var creatureOptions = new CreatureOptions();
        services.GetService<IConfiguration>()?.GetSection(CreatureOptions.SectionName).Bind(creatureOptions);
        FactionTemplateCatalog? templates = services.GetService<FactionTemplateCatalog>()
            ?? (string.IsNullOrWhiteSpace(creatureOptions.FactionTemplateDbcPath) ? null : FactionTemplateDbcReader.Load(creatureOptions.FactionTemplateDbcPath));
        if (templates is null || templates.Count == 0)
        {
            logger.LogWarning("Faction.dbc is loaded but no faction templates are: reputation combat reactions stay off (set Creatures:FactionTemplateDbcPath)");
            Inactive("Faction.dbc is loaded but no FactionTemplate.dbc is (set Creatures:FactionTemplateDbcPath)");
            return;
        }

        var resolver = new ReputationReactionResolver(templates, service.Factions, service.For, (a, b) => SameRaid(a, b));
        Resolver = resolver;
        _fallback = new FactionCreatureHostility(templates);
        _hostility = new ReputationCreatureHostility(_fallback, resolver);

        CombatHooks current = CombatHooks.For(world);
        if (current is FactionCombatHooks)
        {
            CombatHooks.Register(world, new ReputationCombatHooks(current, resolver));
            InactiveReason = null;
            logger.LogInformation("Reputation combat reactions ACTIVE: combat hooks and creature aggro follow player reputation");
        }
        else
        {
            logger.LogWarning(
                "Reputation combat reactions: creature aggro follows reputation, but the combat hooks were not replaced because {Hooks} is registered instead of the faction hooks",
                current.GetType().Name);
            InactiveReason = null;
        }
    }

    public bool IsHostile(Creature creature, Unit target) => _hostility is { } hostility ? hostility.IsHostile(creature, target) : DefaultHostility(creature, target);

    public bool CanAssist(Creature helper, Creature caller) => helper.FactionTemplate == caller.FactionTemplate;

    // Hostility to players as such is a faction-template fact (guards attacking mobs), so it never goes through the reputation resolver.
    public bool IsHostileToPlayers(Unit unit) => Fallback().IsHostileToPlayers(unit);

    public bool IsFriendly(Creature creature, Unit other) => Fallback().IsFriendly(creature, other);

    // Until attached (or when inactive) behave exactly like the creature feature's own default: template-only hostility.
    private bool DefaultHostility(Creature creature, Unit target) => Fallback().IsHostile(creature, target);

    private ICreatureHostility Fallback()
    {
        if (_fallback is null)
        {
            var options = new CreatureOptions();
            services.GetService<IConfiguration>()?.GetSection(CreatureOptions.SectionName).Bind(options);
            _fallback = new FactionCreatureHostility(services.GetService<FactionTemplateCatalog>()
                ?? (string.IsNullOrWhiteSpace(options.FactionTemplateDbcPath) ? FactionTemplateCatalog.Empty : FactionTemplateDbcReader.Load(options.FactionTemplateDbcPath)));
        }

        return _fallback;
    }

    private void Inactive(string reason)
    {
        InactiveReason = reason;
        logger.LogInformation("Reputation combat reactions INACTIVE: {Reason}; combat and aggro keep the template-only rules", reason);
    }

    private bool SameRaid(Player a, Player b)
    {
        try
        {
            return services.GetService<SocialFeature>()?.Context.Groups.AreInSameGroup(a.Guid, b.Guid) ?? false;
        }
        catch (InvalidOperationException)
        {
            return false; // the social feature is not attached yet: a unit is alone
        }
    }
}

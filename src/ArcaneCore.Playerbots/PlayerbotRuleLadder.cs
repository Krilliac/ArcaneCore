using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.Bot;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// One playerbot as a rule subject: its facts for subject filters, its money and free bag slots for the <c>bot.*</c> events, and the
/// player conditions are checked against. One per brain; world thread only. <see cref="Bind"/> refreshes it at the start of each pass.
/// </summary>
internal sealed class PlayerbotRuleSubject : BotRuleSubject
{
    private Player? _player;
    private ulong _guid;
    private RuleSubjectFacts _facts;

    public override ulong Guid => _guid;

    public override bool IsInCombat => _player?.Combat.IsInCombat ?? false;

    public override RuleSubjectFacts Facts => _facts;

    public override Player? Player => _player;

    public override long Money => _player?.Money ?? 0;

    public override int FreeBagSlots => _player is null ? 0 : CountFreeBagSlots(_player);

    /// <summary>Point the subject at <paramref name="player"/> and refresh the facts (team: Alliance 1, Horde 2; the area is not tracked, 0).</summary>
    public void Bind(Player player)
    {
        _player = player;
        _guid = player.Guid.Value;
        _facts = new RuleSubjectFacts((byte)player.Class, (byte)player.Race, player.Level, player.Team == Team.Alliance ? (byte)1 : (byte)2,
            player.MapId, player.ZoneId, 0, RuleSubjectFlags.Player | RuleSubjectFlags.Bot);
    }

    public void Unbind() => _player = null;

    /// <summary>Free slots in the backpack and the equipped bags.</summary>
    internal static int CountFreeBagSlots(Player player)
    {
        int free = 0;
        for (byte slot = InventorySlots.ItemStart; slot < InventorySlots.ItemEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is null) free++;
        }

        for (byte slot = InventorySlots.BagStart; slot < InventorySlots.BagEnd; slot++)
        {
            if (player.Inventory.GetItem(InventorySlots.Bag0, slot) is Container bag) free += bag.FreeSlots;
        }

        return free;
    }
}

/// <summary>
/// The bot host's shared, read-mostly state for a world: which catalog the ladder runs and the counters. One per <see cref="RuleFeature"/>,
/// or one per service provider when a build has no rule feature (brain tests). It holds NO evaluator: a <see cref="RuleEvaluator"/> keeps
/// per-firing state and a random sequence, so every brain gets its own through <see cref="CreateLadder"/> (a pass on one brain can never
/// disturb another). World thread only; the shared counters are updated with <c>Interlocked</c>. The cached host assumes <see cref="RuleFeature"/> is a singleton of the provider (world features are registered as singletons by <c>WorldFeatures</c>).
/// <para>
/// The catalog is the loaded rule store's when it carries the shipped ladder (the rung rows of <c>playerbot_goal</c> AND at least one ladder
/// rule 10001-10016, enabled or deliberately disabled), else the shipped content (<see cref="PlayerbotLadderContent"/>, the rows the world schema step seeds), so a
/// database whose rows are missing still runs today's ladder; a one-time warning says so.
/// </para>
/// </summary>
internal sealed class PlayerbotRuleLadder
{
    private static readonly ConditionalWeakTable<RuleFeature, PlayerbotRuleLadder> PerFeature = new();
    private static readonly ConditionalWeakTable<IServiceProvider, PlayerbotRuleLadder> PerProvider = new();

    private static readonly Lazy<RuleCatalog> Shipped = new(() => RuleCatalog.Build(PlayerbotLadderContent.Content, RuleVocabulary.Default, new RuleBuildOptions()));

    // Resolved once at construction: the host outlives the provider it was built from (a bot's per-session provider is disposed when the bot stops), so it must not keep the provider.
    private readonly ConditionFeature? _conditions;
    private readonly RuleFeature? _feature;
    private readonly ILogger? _logger;
    private volatile CatalogChoice? _choice;
    private int _warnedFallback;

    private PlayerbotRuleLadder(IServiceProvider services, RuleFeature? feature)
    {
        _conditions = services.GetService<ConditionFeature>();
        _feature = feature;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotRuleLadder>();
        feature?.Counters.Register(RuleHostKind.Bot, "bot ladder", Counters);
    }

    /// <summary>What the host's evaluators counted (<c>.rules stats</c>); one shared instance, so the lines add up over the bots.</summary>
    public RuleEvaluatorCounters Counters { get; } = new();

    /// <summary>A host over <paramref name="services"/> and an optional rule feature (tests build their own; the brain goes through <see cref="For"/>).</summary>
    internal static PlayerbotRuleLadder Create(IServiceProvider services, RuleFeature? feature) => new(services, feature);

    /// <summary>The host for the services of a brain's session. A brain calls this once and keeps the result.</summary>
    public static PlayerbotRuleLadder For(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<RuleFeature>() is { } feature)
        {
            return PerFeature.TryGetValue(feature, out PlayerbotRuleLadder? found) ? found : PerFeature.GetValue(feature, f => Create(services, f));
        }

        return PerProvider.TryGetValue(services, out PlayerbotRuleLadder? known) ? known : PerProvider.GetValue(services, s => Create(s, null));
    }

    /// <summary>
    /// A new ladder with its own evaluator for one brain; world thread only. The evaluator's random sequence (chance and cooldown rolls of
    /// custom rules) is seeded from <paramref name="guid"/>, so bots roll differently from each other but a given bot rolls the same way
    /// every run; the brain's own random stream is not touched.
    /// </summary>
    public BotGoalLadder CreateLadder(ulong guid)
    {
        ConditionFeature? conditions = _conditions;
        return new BotGoalLadder(new RuleEvaluator(new Random(SeedOf(guid)))
        {
            Conditions = conditions is null ? null : new BotConditionGate((id, player) => conditions.IsSatisfied(id, player, null)),
            Tracer = _feature?.Tracer,
            TraceSink = _feature?.TraceSink,
            Logger = _logger,
            FaultSite = "rules.bot",
            Counters = Counters,
        });
    }

    /// <summary>The evaluator seed of a bot: the guid mixed with a constant through two SplitMix64 finalisations.</summary>
    internal static int SeedOf(ulong guid)
    {
        ulong z = (guid + 0x9E3779B97F4A7C15UL) ^ 0x0B07_1ADD_5EED_0001UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (int)(z & 0x7FFF_FFFF);
    }

    /// <summary>The catalog this pass runs against (see the class remarks); logs once when the shipped content stands in for the database's.</summary>
    public RuleCatalog Catalog()
    {
        RuleCatalog loaded = _feature?.Catalog ?? RuleCatalog.Empty;
        CatalogChoice? choice = _choice;
        if (choice is null || !ReferenceEquals(choice.Loaded, loaded))
        {
            _choice = choice = new CatalogChoice(loaded, HasGoalRows(loaded), HasLadderRules(loaded));
        }

        if (choice.UseLoaded)
        {
            return loaded;
        }

        if (Interlocked.Exchange(ref _warnedFallback, 1) == 0)
        {
            _logger?.LogWarning("Playerbot goal ladder: the loaded rule tables are missing ladder rows ({Missing}); running the shipped ladder from the code. Apply the world schema step, or insert the rows (playerbot_goal {First}..{Last}, rules {FirstRule}..{LastRule}).",
                choice.Describe(),
                PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.HelpErrand), PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.QuestDestinations),
                PlayerbotLadderContent.FirstRuleId, PlayerbotLadderContent.FirstRuleId + PlayerbotLadderContent.Rungs.Count - 1);
        }

        return Shipped.Value;
    }

    private static bool HasGoalRows(RuleCatalog catalog)
        => catalog.Goals.ContainsKey(PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.HelpErrand));

    /// <summary>Whether any shipped ladder rule row exists, loaded or disabled (a server that disabled them chose to; that is not a missing row).</summary>
    private static bool HasLadderRules(RuleCatalog catalog)
    {
        foreach (PlayerbotLadderRung rung in PlayerbotLadderContent.Rungs)
        {
            int id = PlayerbotLadderContent.RuleIdOf(rung);
            if (catalog.FindRule(id) is not null || catalog.DisabledRuleIds.Contains(id))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record CatalogChoice(RuleCatalog Loaded, bool GoalRows, bool LadderRules)
    {
        public bool UseLoaded => GoalRows && LadderRules;

        public string Describe() => !GoalRows && !LadderRules ? "no playerbot_goal rung rows and no ladder rules" : !GoalRows ? "no playerbot_goal rung rows" : "no ladder rules";
    }
}

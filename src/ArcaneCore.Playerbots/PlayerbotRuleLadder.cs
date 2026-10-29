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
/// The bot host's evaluator and catalog for a world (one per <see cref="RuleFeature"/>; one shared default when a build has none, as in
/// brain tests). The catalog is the loaded rule store's when it carries the shipped ladder (the <c>playerbot_goal</c> rung rows), else the
/// shipped content (<see cref="PlayerbotLadderContent"/>, the same rows the world schema step seeds), so a database whose
/// <c>playerbot_goal</c> table is empty or missing still runs today's ladder; a one-time warning says which. World thread only.
/// </summary>
internal sealed class PlayerbotRuleLadder
{
    private static readonly ConditionalWeakTable<RuleFeature, PlayerbotRuleLadder> PerFeature = new();
    private static readonly Lock DefaultLock = new();
    private static PlayerbotRuleLadder? s_default;

    private static readonly Lazy<RuleCatalog> Shipped = new(() => RuleCatalog.Build(PlayerbotLadderContent.Content, RuleVocabulary.Default, new RuleBuildOptions()));

    private readonly RuleFeature? _feature;
    private readonly ILogger? _logger;
    private bool _warnedFallback;

    private PlayerbotRuleLadder(IServiceProvider services, RuleFeature? feature)
    {
        _feature = feature;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger<PlayerbotRuleLadder>();
        ConditionFeature? conditions = services.GetService<ConditionFeature>();
        var evaluator = new RuleEvaluator(new Random(0x0B07_1ADD))
        {
            Conditions = conditions is null ? null : new BotConditionGate((id, player) => conditions.IsSatisfied(id, player, null)),
            Tracer = feature?.Tracer,
            TraceSink = feature?.TraceSink,
            Logger = _logger,
            FaultSite = "rules.bot",
            Counters = Counters,
        };
        Ladder = new BotGoalLadder(evaluator);
        feature?.Counters.Register(RuleHostKind.Bot, "bot ladder", Counters);
    }

    /// <summary>What the host's evaluator counted (<c>.rules stats</c>).</summary>
    public RuleEvaluatorCounters Counters { get; } = new();

    public BotGoalLadder Ladder { get; }

    /// <summary>A host over <paramref name="services"/> and an optional rule feature (tests build their own; the brain goes through <see cref="For"/>).</summary>
    internal static PlayerbotRuleLadder Create(IServiceProvider services, RuleFeature? feature) => new(services, feature);

    /// <summary>The host for the services of a brain's session.</summary>
    public static PlayerbotRuleLadder For(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<RuleFeature>() is { } feature)
        {
            return PerFeature.GetValue(feature, f => Create(services, f));
        }

        lock (DefaultLock)
        {
            return s_default ??= Create(services, null);
        }
    }

    /// <summary>The catalog this pass runs against (see the class remarks); logs once when the shipped content stands in for the database's.</summary>
    public RuleCatalog Catalog()
    {
        RuleCatalog loaded = _feature?.Catalog ?? RuleCatalog.Empty;
        if (loaded.Goals.ContainsKey(PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.HelpErrand)))
        {
            return loaded;
        }

        if (!_warnedFallback)
        {
            _warnedFallback = true;
            _logger?.LogWarning("Playerbot goal ladder: the loaded rule tables hold no ladder rungs (playerbot_goal {First}..{Last}); running the shipped ladder from the code. Apply the world schema step to seed the rows.",
                PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.HelpErrand), PlayerbotLadderContent.GoalIdOf(PlayerbotLadderRung.QuestDestinations));
        }

        return Shipped.Value;
    }
}

using System.Collections.Concurrent;
using System.Globalization;
using ArcaneCore.Game.Conditions;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Rules;

namespace ArcaneCore.Game.Rules;

/// <summary>What <see cref="RuleCatalog.Build"/> checks references against, beyond the rule tables themselves.</summary>
public sealed record RuleBuildOptions
{
    /// <summary>The loaded <c>conditions</c> table; a rule or parameter naming a condition it lacks is rejected.</summary>
    public ConditionTable Conditions { get; init; } = ConditionTable.Empty;

    /// <summary>Whether a spell exists; null = spell ids are only checked to be positive.</summary>
    public Func<int, bool>? SpellExists { get; init; }

    /// <summary>Whether a creature entry exists; null = entries are only checked to be positive.</summary>
    public Func<int, bool>? CreatureEntryExists { get; init; }

    /// <summary>The largest <c>cooldown_max_ms</c> a rule may have (default <see cref="RuleCatalog.MaxCooldownMs"/>); larger is rejected.</summary>
    public int MaxCooldownMs { get; init; } = RuleCatalog.MaxCooldownMs;

    /// <summary>The largest <c>delay_ms</c> an action may have (default <see cref="RuleCatalog.MaxDelayMs"/>); larger is rejected.</summary>
    public int MaxDelayMs { get; init; } = RuleCatalog.MaxDelayMs;
}

/// <summary>
/// The loaded rule engine content (design 5.1): every rule table validated against the vocabulary and compiled into an immutable form,
/// indexed per host, owner and event type. A bad row is never thrown: it lands in <see cref="Rejected"/> as (table, id, reason) and is not
/// loaded; a rule with one bad action is rejected whole (half a rule would change behaviour). Disabled rules are validated (so their errors
/// show) but not indexed. Immutable apart from the binding cache; safe to share between threads.
/// <para>
/// Rule lists are compiled for the host of each rule that starts them, so a list may use that host's own actions. A list no loaded rule
/// starts, an action list that starts itself again with no delay in between (directly or through other lists), and a list that starts a
/// rejected list are rejected too.
/// </para>
/// </summary>
public sealed class RuleCatalog
{
    /// <summary>Goal ids from here up are server-defined goals; below it a goal id must be a <see cref="PlayerbotGoalKind"/> value or a ladder rung (<see cref="RungGoalIdStart"/>..<see cref="RungGoalIdEnd"/>).</summary>
    public const short CustomGoalIdStart = 1000;

    /// <summary>The first and last <c>playerbot_goal</c> id of the shipped ladder rungs (100 + the rung number): rungs that have no <see cref="PlayerbotGoalKind"/> of their own.</summary>
    public const short RungGoalIdStart = 101;

    /// <summary>See <see cref="RungGoalIdStart"/>.</summary>
    public const short RungGoalIdEnd = 116;

    /// <summary>Rule ids from here up are server-authored (custom), up to <see cref="ConvertedIdStart"/> (exclusive); shipped content stays below.</summary>
    public const int CustomIdStart = RuleIdBands.CustomStart;

    /// <summary>Rule and text ids from here up belong to the converters, one sub-band each (<see cref="RuleIdBands"/>); an id past the named sub-bands is rejected at load.</summary>
    public const int ConvertedIdStart = RuleIdBands.ConvertedStart;

    /// <summary>The default cap on <c>cooldown_max_ms</c>: 24 hours. A longer cooldown is a data error (a rule meant to fire once a day or less
    /// is a once-only rule), and the cap keeps the drawn cooldown and the subject's clock arithmetic far from overflow.</summary>
    public const int MaxCooldownMs = 86_400_000;

    /// <summary>The default cap on an action's <c>delay_ms</c>: 24 hours. A longer delay is a data error (a timed
    /// event belongs to its host), and the cap keeps a pending run's due time far from the subject clock's limits.</summary>
    public const int MaxDelayMs = 86_400_000;

    private const RuleFlags KnownFlags = RuleFlags.Repeatable | RuleFlags.CombatOnly | RuleFlags.OutOfCombatOnly | RuleFlags.DebugTrace | RuleFlags.StableShare;
    private const RuleSubjectFlags KnownSubjectFlags = RuleSubjectFlags.Player | RuleSubjectFlags.Bot | RuleSubjectFlags.Creature | RuleSubjectFlags.Elite | RuleSubjectFlags.GameObject;

    // RACEMASK_ALL_PLAYABLE / CLASSMASK_ALL_PLAYABLE (mangos-classic SharedDefines.h:52-57, 91-96), as ConditionTable uses them.
    private const int RaceMaskAllPlayable = 0xFF;
    private const int ClassMaskAllPlayable = 0x5DF;

    private readonly Dictionary<int, CompiledRule> _rules;
    private readonly Dictionary<(RuleHostKind, RuleOwner), CompiledRule[]> _byOwner;
    private readonly Dictionary<int, CompiledRuleList> _lists;
    private readonly ConcurrentDictionary<(RuleHostKind, RuleOwner, RuleOwner), RuleBinding> _bindings = new();

    private RuleCatalog(RuleVocabulary vocabulary, Dictionary<int, CompiledRule> rules, int disabled, HashSet<int> disabledIds, Dictionary<int, CompiledRuleList> lists,
        Dictionary<int, CompiledFilter> filters, Dictionary<int, RuleText> texts, Dictionary<short, PlayerbotGoalRecord> goals,
        HashSet<uint> conditions, List<RuleRejection> rejected)
    {
        Vocabulary = vocabulary;
        _rules = rules;
        DisabledCount = disabled;
        DisabledRuleIds = disabledIds;
        _lists = lists;
        Filters = filters;
        Texts = texts;
        Goals = goals;
        ReferencedConditions = conditions;
        Rejected = rejected;
        _byOwner = rules.Values.GroupBy(r => (r.Host, r.Owner)).ToDictionary(g => g.Key, g => g.ToArray());
    }

    /// <summary>No rules (before the content is loaded).</summary>
    public static RuleCatalog Empty { get; } = Build(RuleContent.Empty, RuleVocabulary.Default);

    public RuleVocabulary Vocabulary { get; }

    /// <summary>The rows that were not loaded, in table order (texts, filters, goals, rules, lists), each with its reason.</summary>
    public IReadOnlyList<RuleRejection> Rejected { get; }

    /// <summary>Enabled, loaded rules.</summary>
    public int RuleCount => _rules.Count;

    /// <summary>Valid rules with <c>enabled = 0</c> (validated, not loaded).</summary>
    public int DisabledCount { get; }

    /// <summary>The ids of those disabled rules (the rows exist; the server turned them off).</summary>
    public IReadOnlySet<int> DisabledRuleIds { get; }

    /// <summary>Loaded rule lists (by list id; a list started by rules of two hosts counts once).</summary>
    public int ListCount => _lists.Count;

    public IReadOnlyDictionary<int, CompiledFilter> Filters { get; }

    public IReadOnlyDictionary<int, RuleText> Texts { get; }

    public IReadOnlyDictionary<short, PlayerbotGoalRecord> Goals { get; }

    /// <summary>Every <c>conditions</c> entry a valid rule (enabled or not) or action parameter names; a conditions reload must keep them.</summary>
    public IReadOnlySet<uint> ReferencedConditions { get; }

    /// <summary>The loaded rules, by id.</summary>
    public IEnumerable<CompiledRule> Rules => _rules.Values.OrderBy(r => r.Id);

    public CompiledRule? FindRule(int id) => _rules.GetValueOrDefault(id);

    /// <summary>One loaded list (the compilation of the first host that started it).</summary>
    public CompiledRuleList? FindList(int id) => _lists.GetValueOrDefault(id);

    /// <summary>The one-line description of what is loaded, for logs and <c>.reload</c>.</summary>
    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{RuleCount} rules ({DisabledCount} disabled), {ListCount} lists, {Filters.Count} filters, {Texts.Count} texts, {Goals.Count} bot goals, {Rejected.Count} rejected rows");

    /// <summary>
    /// The binding for subjects of <paramref name="host"/> owned by <paramref name="primary"/> and <paramref name="secondary"/> (each may
    /// be <see cref="RuleOwner.Global"/> for none): the host's global rules plus each owner's rules. Cached; call at spawn or bind time.
    /// </summary>
    public RuleBinding Bind(RuleHostKind host, RuleOwner primary = default, RuleOwner secondary = default)
        => _bindings.GetOrAdd((host, primary, secondary), key => new RuleBinding(this, key.Item1, key.Item2, key.Item3,
            RulesOf(key.Item1, RuleOwner.Global).Concat(RulesOf(key.Item1, key.Item2)).Concat(RulesOf(key.Item1, key.Item3))));

    private IEnumerable<CompiledRule> RulesOf(RuleHostKind host, RuleOwner owner)
        => _byOwner.TryGetValue((host, owner), out CompiledRule[]? rules) ? rules : [];

    /// <summary>Validate and compile <paramref name="content"/> against <paramref name="vocabulary"/>. Never throws for a bad row.</summary>
    public static RuleCatalog Build(RuleContent content, RuleVocabulary vocabulary, RuleBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(vocabulary);
        return new Builder(content, vocabulary, options ?? new RuleBuildOptions()).Build();
    }

    private sealed class Builder(RuleContent content, RuleVocabulary vocabulary, RuleBuildOptions options)
    {
        private readonly List<RuleRejection> _rejected = [];
        private readonly Dictionary<int, RuleText> _texts = [];
        private readonly Dictionary<int, CompiledFilter> _filters = [];
        private readonly HashSet<int> _disabledIds = [];
        private readonly Dictionary<short, PlayerbotGoalRecord> _goals = [];
        private readonly Dictionary<int, RuleListRecord> _listRows = [];
        private readonly Dictionary<int, RuleActionRecord[]> _listActions = [];
        private readonly Dictionary<(RuleHostKind, int), ListResult> _compiledLists = [];
        private readonly HashSet<uint> _conditions = [];
        private readonly List<ListFrame> _stack = [];

        public RuleCatalog Build()
        {
            LoadTexts();
            LoadFilters();
            LoadGoals();
            LoadListRows();
            (Dictionary<int, CompiledRule> enabled, int disabled, Dictionary<CompiledRule, HashSet<int>> ruleLists) = LoadRules();

            RejectDeepChains();

            // A list that starts a rejected list (possible only through a delayed cycle) is rejected, and so is a rule that starts one.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (((RuleHostKind host, int id), ListResult result) in _compiledLists)
                {
                    if (result.List is not null && result.Starts.FirstOrDefault(s => IsRejected(host, s)) is var bad && bad != 0)
                    {
                        result.List = null;
                        result.Reason = $"starts rule_list {bad}, which was rejected";
                        changed = true;
                    }
                }
            }

            foreach (((RuleHostKind host, int id), ListResult result) in _compiledLists.OrderBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item1))
            {
                if (result.List is null)
                {
                    _rejected.Add(new RuleRejection(RuleDataTables.List, id, $"for host {HostName(host)}: {result.Reason}"));
                }
            }

            foreach ((CompiledRule rule, HashSet<int> started) in ruleLists.OrderBy(kv => kv.Key.Id))
            {
                if (started.FirstOrDefault(l => IsRejected(rule.Host, l)) is var bad && bad != 0)
                {
                    enabled.Remove(rule.Id);
                    _rejected.Add(Reject(RuleDataTables.Rule, rule.Id, rule.Host, rule.Owner, $"starts rule_list {bad}, which was rejected"));
                }
            }

            foreach (int id in _listRows.Keys.Order())
            {
                if (!_compiledLists.Keys.Any(k => k.Item2 == id))
                {
                    _rejected.Add(new RuleRejection(RuleDataTables.List, id, "no loaded rule starts this list (a list that is never started does nothing)"));
                }
            }

            var lists = new Dictionary<int, CompiledRuleList>();
            foreach (((RuleHostKind _, int id), ListResult result) in _compiledLists.OrderBy(kv => kv.Key.Item1))
            {
                if (result.List is not null)
                {
                    lists.TryAdd(id, result.List);
                }
            }

            return new RuleCatalog(vocabulary, enabled, disabled, _disabledIds, lists, _filters, _texts, _goals, _conditions, _rejected);
        }

        /// <summary>
        /// Rejects a list whose zero-delay chain of started lists is longer than <see cref="RuleEvaluator.MaxListDepth"/>: at run time the ninth
        /// list would be cut (<see cref="RuleEvaluatorCounters.DepthLimited"/>) and its actions silently skipped, which a loaded row must never do.
        /// A rule's chain is its first list's chain, so checking lists covers rules too.
        /// </summary>
        private void RejectDeepChains()
        {
            var depths = new Dictionary<CompiledRuleList, int>(ReferenceEqualityComparer.Instance);
            foreach (((RuleHostKind _, int id), ListResult result) in _compiledLists.OrderBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item1))
            {
                if (result.List is { } list && ChainDepth(list, depths, []) > RuleEvaluator.MaxListDepth)
                {
                    result.List = null;
                    result.Reason = $"it starts lists {depths[list]} deep with no delay in between; the limit is {RuleEvaluator.MaxListDepth} (put a delay in the chain or shorten it)";
                }
            }
        }

        /// <summary>The number of lists in the longest chain that starts from <paramref name="list"/> without a delay (the list itself counts).</summary>
        private static int ChainDepth(CompiledRuleList list, Dictionary<CompiledRuleList, int> depths, HashSet<CompiledRuleList> visiting)
        {
            if (depths.TryGetValue(list, out int known))
            {
                return known;
            }

            if (!visiting.Add(list))
            {
                return int.MaxValue / 2;
            }

            int deepest = 0;
            foreach (CompiledAction action in list.Actions)
            {
                if (action.DelayMs > 0)
                {
                    break;
                }

                if (action.Action is StartRuleListAction && action.Reference is CompiledRuleList child)
                {
                    deepest = Math.Max(deepest, ChainDepth(child, depths, visiting));
                }
            }

            visiting.Remove(list);
            int depth = 1 + deepest;
            depths[list] = depth;
            return depth;
        }

        private bool IsRejected(RuleHostKind host, int list) => !_compiledLists.TryGetValue((host, list), out ListResult? result) || result.List is null;

        private static string IdPastBands(int id)
            => $"id {id} is in the converter range (2^30 and up) but past the named converter sub-bands (ends at {RuleIdBands.End}); authored ids are {RuleIdBands.CustomStart} up to {RuleIdBands.ConvertedStart - 1}";

        private void LoadTexts()
        {
            foreach (RuleTextRecord row in content.Texts.OrderBy(t => t.Id))
            {
                string? problem = row.Id <= 0 ? "id must be 1 or more"
                    : !RuleIdBands.IsAcceptable(row.Id) ? IdPastBands(row.Id)
                    : _texts.ContainsKey(row.Id) ? "duplicate id (the first row is kept)"
                    : string.IsNullOrWhiteSpace(row.ContentDefault) ? "content_default is empty"
                    : null;
                if (problem is not null)
                {
                    _rejected.Add(new RuleRejection(RuleDataTables.Text, row.Id, problem));
                    continue;
                }

                _texts[row.Id] = new RuleText(row.Id, row.ContentDefault, row.Sound, row.Type, row.Language, row.Emote, row.BroadcastTextId);
            }
        }

        private void LoadFilters()
        {
            foreach (RuleSubjectFilterRecord row in content.Filters.OrderBy(f => f.Id))
            {
                string? problem = row.Id <= 0 ? "id must be 1 or more"
                    : _filters.ContainsKey(row.Id) ? "duplicate id (the first row is kept)"
                    : (row.ClassMask & ~ClassMaskAllPlayable) != 0 ? $"class_mask {row.ClassMask} has bits outside the playable classes (0x{ClassMaskAllPlayable:X})"
                    : (row.RaceMask & ~RaceMaskAllPlayable) != 0 ? $"race_mask {row.RaceMask} has bits outside the playable races (0x{RaceMaskAllPlayable:X})"
                    : row.LevelMax != 0 && row.LevelMin > row.LevelMax ? $"level_min {row.LevelMin} is above level_max {row.LevelMax}"
                    : row.Team > 2 ? $"team {row.Team} must be 0 (any), 1 (Alliance) or 2 (Horde)"
                    : row.MapId < 0 || row.ZoneId < 0 || row.AreaId < 0 ? "map_id, zone_id and area_id must not be negative"
                    : (row.SubjectFlags & ~(int)KnownSubjectFlags) != 0 ? $"subject_flags {row.SubjectFlags} has unknown bits (known: 0x{(int)KnownSubjectFlags:X})"
                    : row.SharePct is 0 or > 100 ? $"share_pct {row.SharePct} must be 1..100 (0 would select no subject)"
                    : null;
                if (problem is not null)
                {
                    _rejected.Add(new RuleRejection(RuleDataTables.Filter, row.Id, problem));
                    continue;
                }

                _filters[row.Id] = new CompiledFilter(row.Id, (uint)row.ClassMask, (uint)row.RaceMask, row.LevelMin, row.LevelMax, row.Team,
                    (uint)row.MapId, (uint)row.ZoneId, (uint)row.AreaId, (RuleSubjectFlags)row.SubjectFlags, row.SharePct, row.ShareSeed);
            }
        }

        private static bool IsRungGoalId(short goalId)
            => goalId is >= RungGoalIdStart and <= RungGoalIdEnd && Enum.IsDefined((PlayerbotLadderRung)(goalId - RungGoalIdStart + 1));

        private void LoadGoals()
        {
            foreach (PlayerbotGoalRecord row in content.Goals.OrderBy(g => g.GoalId))
            {
                string? problem = row.GoalId < 0 ? "goal_id must not be negative"
                    : _goals.ContainsKey(row.GoalId) ? "duplicate goal_id (the first row is kept)"
                    : row.GoalId < CustomGoalIdStart && !Enum.IsDefined((PlayerbotGoalKind)row.GoalId) && !IsRungGoalId(row.GoalId)
                        ? $"goal_id {row.GoalId} is not a PlayerbotGoalKind value or a ladder rung ({RungGoalIdStart}..{RungGoalIdEnd}); custom goals start at {CustomGoalIdStart}"
                    : row.Driver != 0 && !(row.Driver > 0 && Enum.IsDefined((PlayerbotLadderRung)row.Driver))
                        ? $"driver {row.Driver} is not a ladder rung (0 for none, or 1..{(short)Enum.GetValues<PlayerbotLadderRung>().Max()})"
                    : string.IsNullOrWhiteSpace(row.Name) ? "name is empty"
                    : null;
                if (problem is not null)
                {
                    _rejected.Add(new RuleRejection(RuleDataTables.Goal, row.GoalId, problem));
                    continue;
                }

                _goals[row.GoalId] = row;
            }
        }

        private void LoadListRows()
        {
            foreach (RuleListRecord row in content.Lists.OrderBy(l => l.Id))
            {
                string? problem = row.Id <= 0 ? "id must be 1 or more" : _listRows.ContainsKey(row.Id) ? "duplicate id (the first row is kept)" : null;
                if (problem is not null)
                {
                    _rejected.Add(new RuleRejection(RuleDataTables.List, row.Id, problem));
                    continue;
                }

                _listRows[row.Id] = row;
            }

            foreach (IGrouping<int, RuleActionRecord> group in content.ListActions.GroupBy(a => a.OwnerId).OrderBy(g => g.Key))
            {
                if (!_listRows.ContainsKey(group.Key))
                {
                    foreach (RuleActionRecord orphan in group.OrderBy(a => a.Seq))
                    {
                        _rejected.Add(new RuleRejection(RuleDataTables.ListAction, group.Key, $"seq {orphan.Seq}: rule_list {group.Key} does not exist or was rejected"));
                    }

                    continue;
                }

                _listActions[group.Key] = Sequence(group, RuleDataTables.ListAction, null, null);
            }
        }

        /// <summary>The rows of one owner in seq order; a repeated seq is rejected (the first is kept).</summary>
        private RuleActionRecord[] Sequence(IEnumerable<RuleActionRecord> rows, string table, RuleHostKind? host, RuleOwner? owner)
        {
            var kept = new List<RuleActionRecord>();
            foreach (RuleActionRecord row in rows.OrderBy(a => a.Seq))
            {
                if (kept.Count > 0 && kept[^1].Seq == row.Seq)
                {
                    _rejected.Add(new RuleRejection(table, row.OwnerId, $"seq {row.Seq}: duplicate seq (the first row is kept)") { Host = host, Owner = owner });
                    continue;
                }

                kept.Add(row);
            }

            return [.. kept];
        }

        private (Dictionary<int, CompiledRule> Enabled, int Disabled, Dictionary<CompiledRule, HashSet<int>> Lists) LoadRules()
        {
            var enabled = new Dictionary<int, CompiledRule>();
            var seen = new HashSet<int>();
            var ruleLists = new Dictionary<CompiledRule, HashSet<int>>();
            int disabled = 0;
            Dictionary<int, IGrouping<int, RuleActionRecord>> actionsByRule = content.Actions.GroupBy(a => a.OwnerId).ToDictionary(g => g.Key);
            foreach (RuleRecord row in content.Rules.OrderBy(r => r.Id))
            {
                var host = (RuleHostKind)row.Host;
                var owner = new RuleOwner((RuleOwnerKind)row.OwnerKind, row.OwnerId);
                if (row.Id > 0 && !RuleIdBands.IsAcceptable(row.Id))
                {
                    _rejected.Add(Reject(RuleDataTables.Rule, row.Id, host, owner, IdPastBands(row.Id)));
                    continue;
                }

                if (row.Id <= 0 || !seen.Add(row.Id))
                {
                    _rejected.Add(Reject(RuleDataTables.Rule, row.Id, host, owner, row.Id <= 0 ? "id must be 1 or more" : "duplicate id (the first row is kept)"));
                    continue;
                }

                RuleActionRecord[] actionRows = actionsByRule.TryGetValue(row.Id, out IGrouping<int, RuleActionRecord>? group)
                    ? Sequence(group, RuleDataTables.Action, host, owner) : [];
                var lists = new HashSet<int>();
                CompiledRule? rule = CompileRule(row, host, owner, actionRows, lists, out string? table, out string? problem);
                if (rule is null)
                {
                    _rejected.Add(Reject(table!, row.Id, host, owner, problem!));
                    continue;
                }

                AddConditions(rule);
                if (row.Enabled)
                {
                    enabled[rule.Id] = rule;
                    ruleLists[rule] = lists;
                }
                else
                {
                    disabled++;
                    _disabledIds.Add(rule.Id);
                }
            }

            foreach (IGrouping<int, RuleActionRecord> group in actionsByRule.Values.Where(g => !seen.Contains(g.Key)).OrderBy(g => g.Key))
            {
                foreach (RuleActionRecord orphan in group.OrderBy(a => a.Seq))
                {
                    _rejected.Add(new RuleRejection(RuleDataTables.Action, group.Key, $"seq {orphan.Seq}: rule {group.Key} does not exist"));
                }
            }

            return (enabled, disabled, ruleLists);
        }

        private void AddConditions(CompiledRule rule)
        {
            if (rule.ConditionId != 0)
            {
                _conditions.Add(rule.ConditionId);
            }
        }

        private CompiledRule? CompileRule(RuleRecord row, RuleHostKind host, RuleOwner owner, RuleActionRecord[] actionRows, HashSet<int> lists,
            out string? table, out string? problem)
        {
            table = RuleDataTables.Rule;
            if (vocabulary.FindHost(host) is not { } definition)
            {
                problem = $"host {row.Host} is not registered (registered: {RegisteredHosts()})";
                return null;
            }

            if (!Enum.IsDefined(owner.Kind) || !definition.Owners.Contains(owner.Kind))
            {
                problem = $"owner_kind {row.OwnerKind} is not one host {definition.Name} accepts ({string.Join(", ", definition.Owners.Select(o => $"{(byte)o} {o}"))})";
                return null;
            }

            if ((owner.Kind == RuleOwnerKind.Global) != (owner.Id == 0) || owner.Id < 0)
            {
                problem = owner.Kind == RuleOwnerKind.Global ? $"a global rule needs owner_id 0, not {owner.Id}" : $"owner_id {owner.Id} must be 1 or more";
                return null;
            }

            if (vocabulary.FindEvent(host, row.EventType) is not { } eventPrimitive)
            {
                problem = $"event_type {row.EventType} is not an event of host {definition.Name}";
                return null;
            }

            int[] eventParams = [row.EventParam1, row.EventParam2, row.EventParam3, row.EventParam4];
            if (CheckParams(eventPrimitive, definition, eventParams, "event_param") is { } eventProblem)
            {
                problem = $"event {eventPrimitive.Info.Name}: {eventProblem}";
                return null;
            }

            CompiledFilter? filter = null;
            if (row.FilterId != 0 && !_filters.TryGetValue(row.FilterId, out filter))
            {
                problem = $"filter_id {row.FilterId} does not exist or was rejected";
                return null;
            }

            var flags = (RuleFlags)row.Flags;
            problem = row.ChancePct is 0 or > 100 ? $"chance_pct {row.ChancePct} must be 1..100 (0 never fires; disable the rule instead)"
                : row.ConditionId < 0 ? $"condition_id {row.ConditionId} must not be negative"
                : row.ConditionId != 0 && options.Conditions.Find((uint)row.ConditionId) is null ? $"condition_id {row.ConditionId} is not a loaded conditions row"
                : row.CooldownMinMs < 0 || row.CooldownMaxMs < row.CooldownMinMs ? $"cooldown {row.CooldownMinMs}..{row.CooldownMaxMs} ms must satisfy 0 <= min <= max"
                : row.CooldownMaxMs > options.MaxCooldownMs ? $"cooldown {row.CooldownMinMs}..{row.CooldownMaxMs} ms must be at most {options.MaxCooldownMs} ms"
                : (flags & ~KnownFlags) != 0 ? $"flags 0x{row.Flags:X} has unknown bits (known: 0x{(int)KnownFlags:X})"
                : (flags & (RuleFlags.CombatOnly | RuleFlags.OutOfCombatOnly)) == (RuleFlags.CombatOnly | RuleFlags.OutOfCombatOnly) ? "combat-only and out-of-combat-only together never fire"
                : actionRows.Length == 0 ? "the rule has no rule_action rows (it would fire and do nothing)"
                : null;
            if (problem is not null)
            {
                return null;
            }

            var actions = new CompiledAction[actionRows.Length];
            for (int i = 0; i < actionRows.Length; i++)
            {
                CompiledAction? action = CompileAction(definition, actionRows[i], lists, out string? actionProblem);
                if (action is null)
                {
                    table = RuleDataTables.Action;
                    problem = $"seq {actionRows[i].Seq}: {actionProblem}";
                    return null;
                }

                actions[i] = action;
            }

            return new CompiledRule(row.Id, host, owner, eventPrimitive, eventParams, (uint)row.PhaseMask, filter, row.ChancePct,
                (uint)row.ConditionId, row.CooldownMinMs, row.CooldownMaxMs, row.Priority, flags, actions, row.Comment);
        }

        private CompiledAction? CompileAction(RuleHostDefinition host, RuleActionRecord row, HashSet<int> lists, out string? problem)
        {
            if (vocabulary.FindAction(host.Kind, row.ActionType) is not { } action)
            {
                problem = $"action_type {row.ActionType} is not an action of host {host.Name}";
                return null;
            }

            if ((host.Capabilities & action.Requires) != action.Requires)
            {
                problem = $"action {action.Info.Name} needs subjects that can {action.Requires}; host {host.Name} subjects cannot";
                return null;
            }

            int[] parameters = [row.Param1, row.Param2, row.Param3, row.Param4, row.Param5, row.Param6];
            if (CheckParams(action, host, parameters, "param") is { } paramProblem)
            {
                problem = $"action {action.Info.Name}: {paramProblem}";
                return null;
            }

            if (vocabulary.FindTarget(host.Kind, row.TargetType) is not { } target)
            {
                problem = $"target_type {row.TargetType} is not a target of host {host.Name}";
                return null;
            }

            int[] targetParams = [row.TargetParam1, row.TargetParam2, row.TargetParam3];
            if (CheckParams(target, host, targetParams, "target_param") is { } targetProblem)
            {
                problem = $"target {target.Info.Name}: {targetProblem}";
                return null;
            }

            if (row.DelayMs < 0)
            {
                problem = $"delay_ms {row.DelayMs} must not be negative";
                return null;
            }

            if (row.DelayMs > 0 && !host.AllowsDelays)
            {
                problem = $"delay_ms {row.DelayMs}: the {host.Name} host runs its rules in one synchronous pass and never resumes a delayed action";
                return null;
            }

            if (row.DelayMs > options.MaxDelayMs)
            {
                problem = $"delay_ms {row.DelayMs} must be at most {options.MaxDelayMs} ms";
                return null;
            }

            if (row.Flags != 0)
            {
                problem = $"flags {row.Flags} are not defined for actions (0 only)";
                return null;
            }

            var compiled = new CompiledAction(row.Seq, action, parameters, target, targetParams, row.DelayMs, row.Flags);
            for (int i = 0; i < action.Info.Parameters.Count; i++)
            {
                RuleParamInfo info = action.Info.Parameters[i];
                int value = parameters[i];
                if (info.Kind == RuleParamKind.ConditionId)
                {
                    _conditions.Add((uint)value);
                }
                else if (info.Kind == RuleParamKind.TextId && compiled.Reference is null)
                {
                    compiled.Reference = _texts[value];
                }
                else if (info.Kind == RuleParamKind.RuleListId && compiled.Reference is null)
                {
                    lists.Add(value);
                    if (ResolveList(host, value, out problem) is not { } list)
                    {
                        problem = $"action {action.Info.Name}: rule_list {value}: {problem}";
                        return null;
                    }

                    compiled.Reference = list;
                }
            }

            problem = null;
            return compiled;
        }

        /// <summary>
        /// The list <paramref name="id"/> compiled for <paramref name="host"/>. A reference to a list still being compiled (a cycle) is allowed
        /// only when time passes along the cycle; a zero-delay cycle would loop forever within one firing.
        /// </summary>
        private CompiledRuleList? ResolveList(RuleHostDefinition host, int id, out string? problem)
        {
            int frame = _stack.FindIndex(f => f.Host == host.Kind && f.ListId == id);
            if (frame >= 0)
            {
                long delay = 0;
                for (int i = frame; i < _stack.Count; i++)
                {
                    delay += _stack[i].Elapsed;
                }

                if (delay == 0)
                {
                    problem = "it starts itself again with no delay in between (an endless loop)";
                    return null;
                }

                _stack[^1].Starts.Add(id);
                problem = null;
                return _compiledLists[(host.Kind, id)].Shell;
            }

            if (_stack.Count > 0)
            {
                _stack[^1].Starts.Add(id);
            }

            if (_compiledLists.TryGetValue((host.Kind, id), out ListResult? done))
            {
                problem = done.Reason;
                return done.List;
            }

            if (!_listRows.TryGetValue(id, out RuleListRecord? row))
            {
                problem = "it does not exist or was rejected";
                return null;
            }

            var shell = new CompiledRuleList(id, row.Comment);
            var result = new ListResult(shell);
            _compiledLists[(host.Kind, id)] = result;
            RuleActionRecord[] rows = _listActions.GetValueOrDefault(id) ?? [];
            if (rows.Length == 0)
            {
                result.Reason = "it has no rule_list_action rows";
                problem = result.Reason;
                return null;
            }

            var current = new ListFrame(host.Kind, id, result.Starts);
            _stack.Add(current);
            try
            {
                var actions = new CompiledAction[rows.Length];
                var nested = new HashSet<int>();
                for (int i = 0; i < rows.Length; i++)
                {
                    current.Elapsed += rows[i].DelayMs > 0 ? rows[i].DelayMs : 0;
                    CompiledAction? action = CompileAction(host, rows[i], nested, out string? actionProblem);
                    if (action is null)
                    {
                        result.Reason = $"seq {rows[i].Seq}: {actionProblem}";
                        problem = result.Reason;
                        return null;
                    }

                    actions[i] = action;
                }

                shell.Actions = actions;
                result.List = shell;
                problem = null;
                return shell;
            }
            finally
            {
                _stack.RemoveAt(_stack.Count - 1);
            }
        }

        private string? CheckParams(RulePrimitive primitive, RuleHostDefinition host, int[] values, string column)
        {
            IReadOnlyList<RuleParamInfo> declared = primitive.Info.Parameters;
            for (int i = declared.Count; i < values.Length; i++)
            {
                if (values[i] != 0)
                {
                    return $"{column}{i + 1} is {values[i]}, but {primitive.Info.Name} takes {declared.Count} parameter{(declared.Count == 1 ? "" : "s")}";
                }
            }

            foreach (RuleParamInfo info in declared)
            {
                int value = values[info.Index - 1];
                string? problem = info.Kind switch
                {
                    RuleParamKind.Bool when value is not (0 or 1) => "must be 0 or 1",
                    RuleParamKind.Percent when value is < 0 or > 100 => "must be 0..100",
                    RuleParamKind.Milliseconds or RuleParamKind.NonNegative when value < 0 => "must not be negative",
                    RuleParamKind.Phase when value is < 0 or > 31 => "must be a phase 0..31",
                    RuleParamKind.SpellId when value <= 0 || options.SpellExists?.Invoke(value) == false => "is not a known spell",
                    RuleParamKind.CreatureEntry when value <= 0 || options.CreatureEntryExists?.Invoke(value) == false => "is not a known creature entry",
                    RuleParamKind.TextId when !_texts.ContainsKey(value) => "is not a loaded rule_text id",
                    RuleParamKind.ConditionId when value <= 0 || options.Conditions.Find((uint)value) is null => "is not a loaded conditions row",
                    RuleParamKind.RuleListId when !_listRows.ContainsKey(value) => "is not a rule_list id",
                    RuleParamKind.GoalId when value is < short.MinValue or > short.MaxValue || !_goals.ContainsKey((short)value) => "is not a loaded playerbot_goal id",
                    RuleParamKind.GoalId when _goals[(short)value].Driver == 0 => "is a playerbot_goal without a driver (nothing would run it)",
                    _ => null,
                };
                if (problem is not null)
                {
                    return $"{column}{info.Index} ({info.Name}) = {value} {problem}";
                }
            }

            return primitive.Validate(new RuleValidationContext(host, values));
        }

        private string RegisteredHosts()
        {
            IReadOnlyList<RuleHostDefinition> hosts = vocabulary.Hosts;
            return hosts.Count == 0 ? "none" : string.Join(", ", hosts.Select(h => $"{(byte)h.Kind} {h.Name}"));
        }

        private string HostName(RuleHostKind kind) => vocabulary.FindHost(kind)?.Name ?? ((byte)kind).ToString(CultureInfo.InvariantCulture);
    }

    private static RuleRejection Reject(string table, int id, RuleHostKind host, RuleOwner owner, string reason)
        => new(table, id, reason) { Host = host, Owner = owner };

    private sealed class ListResult(CompiledRuleList shell)
    {
        public CompiledRuleList Shell { get; } = shell;

        /// <summary>The compiled list, or null when it was rejected (then <see cref="Reason"/> says why).</summary>
        public CompiledRuleList? List { get; set; }

        public string? Reason { get; set; }

        /// <summary>The lists this list's actions start.</summary>
        public HashSet<int> Starts { get; } = [];
    }

    private sealed class ListFrame(RuleHostKind host, int listId, HashSet<int> starts)
    {
        public RuleHostKind Host { get; } = host;

        public int ListId { get; } = listId;

        public HashSet<int> Starts { get; } = starts;

        /// <summary>The delay accumulated in this list up to the action being compiled.</summary>
        public long Elapsed { get; set; }
    }
}

/// <summary>The rule engine's table names, as rejections and GM output name them.</summary>
public static class RuleDataTables
{
    public const string Rule = "rule";
    public const string Action = "rule_action";
    public const string List = "rule_list";
    public const string ListAction = "rule_list_action";
    public const string Filter = "rule_subject_filter";
    public const string Text = "rule_text";
    public const string Goal = "playerbot_goal";
}

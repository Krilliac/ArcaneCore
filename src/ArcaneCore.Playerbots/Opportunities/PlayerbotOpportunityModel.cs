using System.Globalization;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.World.Playerbots;

/// <summary>What an opportunity is (<see cref="PlayerbotOpportunities"/>).</summary>
internal enum OpportunityKind : byte
{
    /// <summary>A chest that opens with a click (a treasure chest, a battered or solid chest, a footlocker).</summary>
    Chest,

    /// <summary>A chest behind a lock the bot can open: Pick Lock at the skill the lock needs, or a key in the bags.</summary>
    LockedChest,

    /// <summary>A herb or mining node (the gathering itself is <see cref="Progression.PlayerbotCrafting"/>'s; only the risk side is here).</summary>
    Node,

    /// <summary>A rare creature (rank rare or rare elite) with loot.</summary>
    Rare,
}

/// <summary>What the bot does about an opportunity.</summary>
internal enum OpportunityDecision : byte
{
    /// <summary>Go for it.</summary>
    Engage,

    /// <summary>Worth it, but not as the bot is now (hurt, drained, recovering): look again later, no backoff.</summary>
    Wait,

    /// <summary>Not worth it, or too dangerous.</summary>
    Refuse,
}

/// <summary>The verdict on one opportunity: the decision, why, and the numbers behind it (BOTINSPECT and tests).</summary>
/// <param name="Risk">The predicted damage taken as a share of the bot's health (<see cref="PlayerbotRiskModel.Risk"/>); 0 unguarded.</param>
/// <param name="Net">The reward less the time cost, in same-level kills.</param>
/// <param name="DeathProbability">The chance of dying (<see cref="PlayerbotOpportunityModel.DeathProbability"/>).</param>
internal readonly record struct OpportunityVerdict(OpportunityDecision Decision, string Reason, float Risk, float Net, float DeathProbability)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"opportunity={Decision.ToString().ToLowerInvariant()} reason={Reason} risk={Risk:F2} net={Net:F2} death={DeathProbability:F2}");
}

/// <summary>Everything one opportunity decision reads (a plain snapshot; unit tests build it directly).</summary>
internal sealed record OpportunityFacts
{
    /// <summary>
    /// The bot, its guards (<see cref="PlayerbotEngagementFacts.Enemies"/>, the first one is the first pull) and its group. Its
    /// <see cref="PlayerbotEngagementFacts.LootValue"/> is ignored: <see cref="LootUnits"/> stands in.
    /// </summary>
    public required PlayerbotEngagementFacts Engagement { get; init; }

    /// <summary>The expected loot in same-level kills (<see cref="PlayerbotOpportunityModel.LootUnits"/>).</summary>
    public float LootUnits { get; init; }

    /// <summary>Seconds to walk to the find.</summary>
    public float TravelSeconds { get; init; }

    /// <summary>The bot is waiting out a retreat or recovering (<see cref="PlayerbotRisk.Recovered"/>).</summary>
    public bool Recovering { get; init; }

    /// <summary>The bot would drop a quest or grind goal for this: it asks more (<see cref="PlayerbotOpportunityModel.InterruptValueFactor"/>).</summary>
    public bool Interrupting { get; init; }

    /// <summary>The bot has already begun this find (it cleared a guard): the worth is not asked again, only the risk (no sunk-cost walk-away).</summary>
    public bool Committed { get; init; }

    /// <summary>
    /// Judge the guards only (a gathering node: the bot gathers it anyway, <see cref="PlayerbotOpportunities.NodeVeto"/> asks whether the
    /// guards are safe to face): the net worth is never asked, so nothing is refused as <c>low-value</c>, and the guards are weighed
    /// against the find's loot without the time cost.
    /// </summary>
    public bool RiskOnly { get; init; }

    /// <summary>The find is itself a creature to kill (a rare): <see cref="PlayerbotEngagementFacts.Enemies"/> starts with it and it is no guard.</summary>
    public bool FindIsEnemy { get; init; }

    /// <summary>The bot's temperament (<see cref="PlayerbotOpportunityModel.Personality"/>), 1 for none.</summary>
    public float Personality { get; init; } = 1f;
}

/// <summary>
/// The opportunity arithmetic (pure). A find is weighed the way a pull is (<see cref="PlayerbotRiskModel"/>), with a loot reward and a
/// time cost added:
/// <list type="bullet">
/// <item><b>Net reward</b> = loot + the guards' experience (each relative to a same-level kill, <see cref="PlayerbotRiskModel.Reward"/>)
/// minus the time to walk there and fight the guards at <see cref="KillUnitsPerSecond"/>: a grinding bot earns about a kill (1 plus
/// its loot) every 25 seconds, so a second spent here is a second not spent there.</item>
/// <item><b>Risk</b> = the whole guard pack scored as one pull (<see cref="PlayerbotRiskModel.Assess"/>: pack size, levels, elites, the
/// bot's health, mana, ready escapes, group, remembered danger).</item>
/// <item>Engage when the net reward reaches <see cref="PlayerbotOpportunityOptions.MinValue"/> and the risk is at most
/// <c>Tolerance x RiskAppetite x Personality x min(0.9, 0.4 + 0.2 x net)</c>, and it passes the hard refusals: a pack larger than
/// <see cref="PlayerbotOpportunityOptions.MaxGuards"/>, a creature that kills outright, an elite <see cref="PlayerbotRiskModel.EliteLevelMargin"/>
/// levels above the bot, any guard <see cref="OverLevelMargin"/> or more levels above it, a death probability above
/// <see cref="PlayerbotOpportunityOptions.MaxDeathProbability"/>.</item>
/// </list>
/// </summary>
internal static class PlayerbotOpportunityModel
{
    /// <summary>The kills a second of grinding earns (one kill with its loot in about 25 seconds).</summary>
    internal const float KillUnitsPerSecond = 0.04f;

    /// <summary>A bot asks this many times <see cref="PlayerbotOpportunityOptions.MinValue"/> to drop a quest or grind goal for a find.</summary>
    internal const float InterruptValueFactor = 1.5f;

    /// <summary>... and accepts this share of the risk it would accept otherwise.</summary>
    internal const float InterruptRiskFactor = 0.8f;

    /// <summary>A guard this many levels above the bot (elite or not) is refused.</summary>
    internal const int OverLevelMargin = 5;

    /// <summary>What an upgrade is worth at least, in same-level kills.</summary>
    internal const float UpgradeBase = 1.5f;

    /// <summary>What a large upgrade (the new item twice as good as the old) adds to <see cref="UpgradeBase"/>.</summary>
    internal const float UpgradeScale = 2.5f;

    /// <summary>Copper a same-level kill is worth (its coin and vendor trash): about 75 at level 10, 515 at 30, 2000 at 60.</summary>
    internal static float KillCopper(byte level) => 20f + (0.55f * level * level);

    /// <summary>What an item of <paramref name="quality"/> is worth whatever the vendor pays (poor 0 .. legendary 4 kills): a blue is never trash.</summary>
    internal static float QualityUnits(uint quality) => quality switch
    {
        0 => 0f,
        1 => 0.05f,
        2 => 0.25f,
        3 => 0.8f,
        4 => 2f,
        _ => 4f,
    };

    /// <summary>
    /// The chance of dying in a fight of this <paramref name="risk"/>: a logistic curve centred on 0.95 (the bot dies as the last enemy
    /// does at about 1), 4% at 0.5, 15% at 0.7, 50% at 0.95, 92% at 1.3.
    /// </summary>
    internal static float DeathProbability(float risk)
        => float.IsPositiveInfinity(risk) ? 1f : float.IsNaN(risk) ? 1f : 1f / (1f + MathF.Exp(-(risk - 0.95f) * 7f));

    /// <summary>The bot's temperament: a stable factor from its guid between 1 - <paramref name="variance"/> and 1 + <paramref name="variance"/>.</summary>
    internal static float Personality(ulong botGuid, float variance)
    {
        double h = (PlayerbotContention.Mix(botGuid, 0x0DD5C0DEUL) >> 11) * (1.0 / 9007199254740992.0);
        return 1f + (variance * (float)((2.0 * h) - 1.0));
    }

    /// <summary>
    /// The items a loot table is expected to give: item to expected count, one level of references followed (and their references,
    /// bounded). A quest row (negative chance) is left out; rows of one group yield one item between them, the zero-chance rows
    /// sharing what the explicit chances leave.
    /// </summary>
    internal static Dictionary<uint, float> ExpectedItems(LootContent content, LootTableKind kind, uint entry)
    {
        ArgumentNullException.ThrowIfNull(content);
        var items = new Dictionary<uint, float>();
        if (entry != 0) Expect(content, kind, entry, 1f, items, 0);
        return items;
    }

    private const int MaxReferenceDepth = 3;

    // Mirrors LootGenerator.Process: ungrouped rows (and reference rows, whatever their group) roll alone and a chance of 0 never
    // drops; grouped rows (group > 0 and a positive count) yield one item between them; a reference row's group picks the group of
    // the referenced template; maxcount is the reference's repeat count.
    private static void Expect(LootContent content, LootTableKind kind, uint entry, float weight, Dictionary<uint, float> items, int depth, byte groupFilter = 0)
    {
        IReadOnlyList<LootStoreRow> rows = content.GetRows(kind, entry);
        if (groupFilter != 0)
        {
            ExpectGroup([.. rows.Where(r => r.GroupId == groupFilter && r.MinCountOrRef > 0)], weight, items);
            return;
        }

        foreach (LootStoreRow row in rows)
        {
            if (row.GroupId > 0 && row.MinCountOrRef > 0) continue;
            float chance = row.ChanceOrQuestChance;
            if (chance <= 0f) continue; // never drops, or a quest item: no use to a bot looking for loot
            float p = Math.Min(1f, chance / 100f) * weight;
            if (row.MinCountOrRef < 0)
            {
                if (depth < MaxReferenceDepth)
                    Expect(content, LootTableKind.Reference, (uint)-row.MinCountOrRef, p * row.MaxCount, items, depth + 1, row.GroupId);
                continue;
            }

            Add(items, row, p);
        }

        foreach (IGrouping<byte, LootStoreRow> group in rows.Where(r => r.GroupId > 0 && r.MinCountOrRef > 0).GroupBy(r => r.GroupId))
            ExpectGroup([.. group], weight, items);
    }

    // Mirrors LootGenerator.PickFromGroup (vmangos LootGroup::Roll): one roll against the explicit chances in row order (a quest row
    // takes its share too), a chance of 100 or more takes whatever roll is left, and the zero-chance rows share the rest equally.
    private static void ExpectGroup(IReadOnlyList<LootStoreRow> members, float weight, Dictionary<uint, float> items)
    {
        float remaining = 1f;
        int equal = 0;
        foreach (LootStoreRow row in members)
        {
            float chance = Math.Abs(row.ChanceOrQuestChance);
            if (chance == 0f) { equal++; continue; }
            float share = chance >= 100f ? remaining : Math.Min(chance / 100f, remaining);
            if (row.ChanceOrQuestChance > 0) Add(items, row, share * weight); // a quest item takes its share of the roll but is worth nothing here
            remaining = Math.Max(0f, remaining - share);
        }

        if (equal == 0 || remaining <= 0f) return;
        foreach (LootStoreRow row in members)
            if (row.ChanceOrQuestChance == 0f) Add(items, row, remaining / equal * weight);
    }

    private static void Add(Dictionary<uint, float> items, LootStoreRow row, float probability)
    {
        if (probability <= 0f) return;
        float count = (row.MinCountOrRef + Math.Max((uint)row.MinCountOrRef, row.MaxCount)) / 2f;
        items[row.Item] = items.GetValueOrDefault(row.Item) + (probability * Math.Max(1f, count));
    }

    /// <summary>
    /// What <paramref name="items"/> and <paramref name="gold"/> are worth to a bot of <paramref name="level"/>, in same-level kills: per
    /// item the larger of its vendor price and its quality floor (<see cref="QualityUnits"/>), plus, for an upgrade, <see cref="UpgradeBase"/>
    /// + <see cref="UpgradeScale"/> x the share of the item's score it gains. <paramref name="upgrade"/> gives that share (0..1) or null for
    /// an item the bot cannot use or does not gain from.
    /// </summary>
    internal static float LootUnits(IReadOnlyDictionary<uint, float> items, uint gold, byte level, Func<uint, ItemTemplate?> templates,
        Func<ItemTemplate, float?> upgrade)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(upgrade);
        float kill = KillCopper(level);
        float units = gold / kill;
        foreach ((uint entry, float count) in items)
        {
            if (templates(entry) is not { } template) continue;
            float vendor = template.SellPrice * count / kill;
            units += Math.Max(vendor, QualityUnits(template.Quality) * count);
            if (upgrade(template) is { } share) units += count * (UpgradeBase + (UpgradeScale * Math.Clamp(share, 0f, 1f)));
        }

        return units;
    }

    /// <summary>The seconds the bot needs to kill every guard, one at a time (their health over the bot's damage per second).</summary>
    internal static float FightSeconds(PlayerbotEngagementFacts facts)
    {
        float dps = Math.Max(0.1f, facts.BotDps * PlayerbotRiskModel.ManaFactor(facts.ManaDependence, facts.BotManaPct)) * Math.Max(1, facts.GroupSize);
        float seconds = 0f;
        foreach (RiskEnemy enemy in facts.Enemies) seconds += enemy.Health / dps;
        return seconds;
    }

    /// <summary>The verdict on one opportunity (see the type summary for the formula).</summary>
    internal static OpportunityVerdict Decide(OpportunityFacts facts, PlayerbotOpportunityOptions options, PlayerbotRiskOptions riskOptions)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(riskOptions);
        PlayerbotEngagementFacts engagement = facts.Engagement;
        IReadOnlyList<RiskEnemy> guards = engagement.Enemies;
        float experience = guards.Count == 0 ? 0f : PlayerbotRiskModel.Reward(engagement with { LootValue = 0f, QuestObjective = false });
        float timeCost = (facts.TravelSeconds + FightSeconds(engagement)) * KillUnitsPerSecond;
        float net = facts.LootUnits + experience - timeCost;
        OpportunityVerdict Verdict(OpportunityDecision decision, string reason, float risk = 0f)
            => new(decision, reason, risk, net, DeathProbability(risk));

        if (!options.Enabled) return Verdict(OpportunityDecision.Refuse, "off");
        if (facts.Recovering) return Verdict(OpportunityDecision.Wait, "recovering");
        int guardCount = guards.Count - (facts.FindIsEnemy && guards.Count > 0 ? 1 : 0);
        if (guardCount > options.MaxGuards) return Verdict(OpportunityDecision.Refuse, $"pack-of-{guardCount}");
        float minValue = options.MinValue * (facts.Interrupting ? InterruptValueFactor : 1f);
        if (net < minValue && !facts.Committed && !facts.RiskOnly) return Verdict(OpportunityDecision.Refuse, "low-value");
        if (guards.Count == 0) return Verdict(OpportunityDecision.Engage, "unguarded");

        if (engagement.Remembered) return Verdict(OpportunityDecision.Refuse, "remembered");
        if (guards.Any(guard => guard.Lethal)) return Verdict(OpportunityDecision.Refuse, "lethal", float.PositiveInfinity);
        int eliteMargin = PlayerbotRiskModel.EliteLevelMargin + (engagement.GroupSize > 1 ? PlayerbotRiskModel.GroupEliteLevelMargin : 0);
        if (guards.Any(guard => guard.Elite && guard.Level >= engagement.BotLevel + eliteMargin)) return Verdict(OpportunityDecision.Refuse, "elite-above");
        if (guards.Any(guard => guard.Level >= engagement.BotLevel + OverLevelMargin)) return Verdict(OpportunityDecision.Refuse, "over-level");

        float tolerance = riskOptions.Tolerance * options.RiskAppetite * facts.Personality * (facts.Interrupting ? InterruptRiskFactor : 1f);
        // Every key Assess reads comes along (RestManaBelowPct, with the RecoverHealthPct that caps it), so a live change reaches this path (bot QA #1480).
        var scaled = new PlayerbotRiskOptions
        {
            Tolerance = Math.Clamp(tolerance, 0.25f, 4f), RestManaBelowPct = riskOptions.RestManaBelowPct, RecoverHealthPct = riskOptions.RecoverHealthPct,
        };
        // The pack as one pull: its loot value is the find's, less the time cost; the guards' experience the model adds itself.
        PlayerbotEngagement assessed = PlayerbotRiskModel.Assess(
            engagement with { LootValue = facts.RiskOnly ? facts.LootUnits : facts.LootUnits - timeCost, QuestObjective = false }, scaled);
        float death = DeathProbability(assessed.Risk);
        switch (assessed.Decision)
        {
            case PlayerbotEngageDecision.Rest:
                return new(OpportunityDecision.Wait, assessed.Reason, assessed.Risk, net, death);
            case PlayerbotEngageDecision.Engage when death > options.MaxDeathProbability:
                return new(OpportunityDecision.Refuse, "death-risk", assessed.Risk, net, death);
            case PlayerbotEngageDecision.Engage:
                return new(OpportunityDecision.Engage, assessed.Reason, assessed.Risk, net, death);
            default:
                return new(OpportunityDecision.Refuse, assessed.Reason, assessed.Risk, net, death);
        }
    }
}

/// <summary>
/// The opportunities a bot set aside, keyed by gameobject or spawn guid (pure; clock passed in; world thread; owned by one controller).
/// A failed attempt is set aside for 60 seconds, doubling each time up to 5 minutes (<see cref="Defer"/>); a find taken is not looked at
/// again until it respawns (<see cref="Complete"/>). Bounded: the oldest entries go first.
/// </summary>
internal sealed class PlayerbotOpportunityLedger
{
    /// <summary>The first backoff.</summary>
    internal const uint MinBackoffMs = 60_000;

    /// <summary>The longest backoff.</summary>
    internal const uint MaxBackoffMs = 300_000;

    private const int MaxEntries = 128;
    private readonly Dictionary<ulong, (uint UntilMs, int Strikes)> _entries = [];

    /// <summary>Entries held, expired ones not yet dropped included (tests).</summary>
    internal int Count => _entries.Count;

    /// <summary>The backoff after <paramref name="strikes"/> failures already counted: 60 s, 120 s, 240 s, then 300 s.</summary>
    internal static uint BackoffMs(int strikes)
        => (uint)Math.Min((long)MaxBackoffMs, (long)MinBackoffMs << Math.Clamp(strikes, 0, 8));

    /// <summary>Whether <paramref name="guid"/> is set aside at <paramref name="nowMs"/>.</summary>
    internal bool IsDeferred(ulong guid, uint nowMs)
        => _entries.TryGetValue(guid, out (uint UntilMs, int Strikes) entry) && unchecked((int)(entry.UntilMs - nowMs)) > 0;

    /// <summary>When <paramref name="guid"/> comes back (0 when it is not set aside; tests and inspection).</summary>
    internal uint DeferredUntil(ulong guid, uint nowMs)
        => IsDeferred(guid, nowMs) ? _entries[guid].UntilMs : 0u;

    /// <summary>Set <paramref name="guid"/> aside for the next backoff step; the number of failures goes up. Returns the milliseconds.</summary>
    internal uint Defer(ulong guid, uint nowMs)
    {
        int strikes = _entries.TryGetValue(guid, out (uint UntilMs, int Strikes) known) ? known.Strikes : 0;
        uint backoff = BackoffMs(strikes);
        Store(guid, unchecked(nowMs + backoff), strikes + 1, nowMs);
        return backoff;
    }

    /// <summary>Leave <paramref name="guid"/> alone for <paramref name="ms"/> without counting a failure (another bot or a player is on it).</summary>
    internal void SetAside(ulong guid, uint nowMs, uint ms)
    {
        int strikes = _entries.TryGetValue(guid, out (uint UntilMs, int Strikes) known) ? known.Strikes : 0;
        Store(guid, unchecked(nowMs + ms), strikes, nowMs);
    }

    /// <summary>The find was taken: look at <paramref name="guid"/> again after <paramref name="respawnMs"/> (never sooner than the first backoff), failures forgotten.</summary>
    internal void Complete(ulong guid, uint nowMs, uint respawnMs)
        => Store(guid, unchecked(nowMs + Math.Max(MinBackoffMs, respawnMs)), 0, nowMs);

    /// <summary>Forget <paramref name="guid"/> (it is gone: someone else took it).</summary>
    internal void Forget(ulong guid) => _entries.Remove(guid);

    private void Store(ulong guid, uint untilMs, int strikes, uint nowMs)
    {
        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(guid))
        {
            foreach (ulong old in _entries.Where(e => unchecked((int)(e.Value.UntilMs - nowMs)) <= 0).Select(e => e.Key).ToArray()) _entries.Remove(old);
            if (_entries.Count >= MaxEntries) _entries.Remove(_entries.OrderBy(e => unchecked((int)(e.Value.UntilMs - nowMs))).First().Key);
        }

        _entries[guid] = (untilMs, strikes);
    }
}

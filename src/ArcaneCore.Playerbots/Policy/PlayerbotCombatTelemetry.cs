using System.Buffers;
using System.Globalization;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Spells;

namespace ArcaneCore.World.Playerbots.Policy;

/// <summary>One telemetry line (goal decisions and combat decisions share the writer).</summary>
internal interface IPlayerbotTelemetryRecord
{
    void WriteTo(IBufferWriter<byte> output);
}

/// <summary>
/// Where the risk model tells the combat telemetry what it decided (world thread, inside a think): each pull candidate it assessed,
/// with whether the choice took it, and each in-fight verdict. <see cref="PlayerbotRisk.CombatSink"/> is null unless the policy telemetry
/// is on; the risk model guards the calls, so a sink fault never changes a decision.
/// </summary>
internal interface IPlayerbotCombatSink
{
    /// <param name="taken">The choice took this candidate (an engage, or a detour with a route); false: it was turned down.</param>
    void Assessed(Player player, Creature candidate, in PlayerbotEngagement verdict, bool taken, uint nowMs);

    void Judged(Player player, in PlayerbotFightFacts facts, in PlayerbotFightVerdict verdict, uint nowMs);
}

/// <summary>One opportunity the controller weighed (<see cref="PlayerbotOpportunities"/>).</summary>
/// <param name="Key">The find's key (its object guid), to match the decision to its outcome.</param>
/// <param name="Distance">Yards from the bot to the find.</param>
/// <param name="Guards">The creatures that guard the find (a rare is not counted).</param>
/// <param name="Reward">The net reward in same-level kills (<see cref="OpportunityVerdict.Net"/>).</param>
/// <param name="Risk">The predicted damage taken as a share of the bot's health (<see cref="OpportunityVerdict.Risk"/>; infinity for a hard refusal).</param>
/// <param name="Reason">Why the verdict came out as it did.</param>
internal readonly record struct OpportunityDecisionInfo(ulong Key, OpportunityKind Kind, uint Entry, float Distance, int Guards, float Reward, float Risk, string Reason);

/// <summary>How an opportunity the bot went for came out. <paramref name="LootCopper"/> is 0 for a find given up, and null when the bot
/// succeeded but the controller did not see the loot (a rare's corpse is the brain's to loot).</summary>
internal readonly record struct OpportunityOutcome(ulong? LootCopper, bool Died, uint ElapsedMs, bool Lost = false);

/// <summary>
/// Where the opportunity controller tells the telemetry what it decided and how it came out (world thread, inside a think).
/// <see cref="PlayerbotOpportunities.Sink"/> is null unless the policy telemetry is on; the controller guards the calls.
/// </summary>
internal interface IPlayerbotOpportunitySink
{
    /// <param name="engaged">The bot went for this find (an episode begins); false: it was refused or passed over.</param>
    void Decided(Player player, in OpportunityDecisionInfo decision, bool engaged, uint nowMs);

    /// <summary>The episode of the find <paramref name="key"/> ended (looted, killed, given up, or the bot died).</summary>
    void Ended(ulong botGuid, ulong key, in OpportunityOutcome outcome, uint nowMs);
}

/// <summary>
/// Combat-adjacent decision telemetry for a later combat policy (docs/areas/playbots.md, Combat decisions): the four decisions a policy
/// may one day score (engage or skip a pull, retreat, rest after a fight, which target in a fight) with their features, the option the
/// rules chose, and what followed. Nothing here changes a decision. One <c>fight</c> record per fight (from the first think in combat to
/// the first out of it, or the bot's death) and one <c>skip</c> record per pull candidate turned down (once a minute per creature).
/// World thread.
/// </summary>
internal sealed class PlayerbotCombatTelemetry(Action<IPlayerbotTelemetryRecord> enqueue, Func<long> unixMs) : IPlayerbotCombatSink, IPlayerbotOpportunitySink
{
    /// <summary>An opportunity's features (<c>opportunity</c> records, <c>f</c>).</summary>
    internal static readonly string[] OpportunityFeatureNames = ["level", "health", "mana", "distance", "kind"];

    /// <summary>The most opportunity finds one bot remembers writing a refusal for (the oldest are forgotten all at once).</summary>
    internal const int MaxOpportunitySeen = 64;

    /// <summary>A candidate turned down is recorded again only after this long.</summary>
    internal const uint SkipRepeatMs = 60_000;

    /// <summary>The most turned-down candidates one bot holds while waiting for its next fight.</summary>
    internal const int MaxPendingSkips = 8;

    /// <summary>The most target picks one fight record keeps (the first and three switches).</summary>
    internal const int MaxTargets = 4;

    /// <summary>The most candidates one target pick lists (the chosen one always among them).</summary>
    internal const int MaxCandidates = 4;

    /// <summary>An engage verdict this old when a fight starts did not start it.</summary>
    internal const uint EngageFreshMs = 30_000;

    /// <summary>After a fight (and its looting), a rest begun within this long is the rest decision.</summary>
    internal const uint RestLookMs = 5_000;

    /// <summary>Looting after a fight pushes the rest window back by at most this long.</summary>
    internal const uint LootMaxMs = 30_000;

    /// <summary>Downtime longer than this (no next fight) is written as null.</summary>
    internal const uint DowntimeCapMs = 600_000;

    /// <summary>A pull candidate's features (engage and skip records, <c>f</c>).</summary>
    internal static readonly string[] CandidateFeatureNames =
        ["level", "health", "mana", "level_diff", "elite", "health_ratio", "distance", "risk", "reward", "adds"];

    /// <summary>An in-fight verdict's features (fight record, <c>retreat.f</c>).</summary>
    internal static readonly string[] FightFeatureNames =
        ["health", "dps_in", "dps_out", "enemies", "time_to_kill", "time_to_die", "target_health", "round_damage"];

    /// <summary>One in-fight target candidate's features (fight record, <c>targets[].c[]</c>).</summary>
    internal static readonly string[] TargetFeatureNames = ["target_health", "level_diff", "distance", "attacking_bot", "elite"];

    /// <summary>The state at a fight's end the rest decision is taken on (fight record, <c>rest.f</c>).</summary>
    internal static readonly string[] RestFeatureNames = ["health", "mana", "sick"];

    private readonly Dictionary<ulong, Bot> _bots = [];
    private readonly List<Creature> _enemies = [];

    internal long Fights { get; private set; }

    internal long Skips { get; private set; }

    /// <summary>Opportunity records queued.</summary>
    internal long Opportunities { get; private set; }

    /// <summary>
    /// A find the bot refused or passed over is written once a minute at most (as a skip is); the one it goes for is held until its episode
    /// ends and written then, with the outcome.
    /// </summary>
    public void Decided(Player player, in OpportunityDecisionInfo decision, bool engaged, uint nowMs)
    {
        if (BotOf(player) is not { } bot) return;
        bot.LastSeenMs = nowMs;
        if (!engaged)
        {
            if (bot.OpportunitySeen.TryGetValue(decision.Key, out uint seen) && unchecked(nowMs - seen) < SkipRepeatMs) return;
            if (bot.OpportunitySeen.Count >= MaxOpportunitySeen) bot.OpportunitySeen.Clear();
            bot.OpportunitySeen[decision.Key] = nowMs;
        }

        var record = new PlayerbotOpportunityRecord(unixMs(), player.Guid.Value, player.MapId, player.ZoneId, decision.Key, decision.Kind, decision.Entry,
            player.Level / 60f, player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 0f, ManaPct(player),
            MathF.Min(decision.Distance, 100f) / 50f, decision.Guards, decision.Reward, decision.Risk, decision.Reason, engaged, Outcome: null);
        if (engaged)
        {
            bot.Opportunity = record;
            return;
        }

        Opportunities++;
        enqueue(record);
    }

    public void Ended(ulong botGuid, ulong key, in OpportunityOutcome outcome, uint nowMs)
    {
        if (!_bots.TryGetValue(botGuid, out Bot? bot) || bot.Opportunity is not { } pending || pending.Key != key) return;
        bot.Opportunity = null;
        bot.LastSeenMs = nowMs;
        Opportunities++;
        enqueue(pending with { Outcome = outcome });
    }

    public void Assessed(Player player, Creature candidate, in PlayerbotEngagement verdict, bool taken, uint nowMs)
    {
        if (BotOf(player) is not { } bot) return;
        bot.LastSeenMs = nowMs;
        if (taken)
        {
            bot.Engage = new Choice(nowMs, unixMs(), candidate.Guid.Value, CandidateFeatures(player, candidate, verdict), verdict);
            return;
        }
        ulong guid = candidate.Guid.Value;
        if (bot.SkipSeen.TryGetValue(guid, out uint seen) && unchecked(nowMs - seen) < SkipRepeatMs) return;
        if (bot.Skips.Count >= MaxPendingSkips) return;
        if (bot.SkipSeen.Count >= 64) bot.SkipSeen.Clear();
        bot.SkipSeen[guid] = nowMs;
        bot.Skips.Add(new Choice(nowMs, unixMs(), guid, CandidateFeatures(player, candidate, verdict), verdict));
    }

    public void Judged(Player player, in PlayerbotFightFacts facts, in PlayerbotFightVerdict verdict, uint nowMs)
    {
        if (BotOf(player) is not { } bot) return;
        // The judgement runs inside the think, before AfterThink: the fight starts here when this is its first think in combat.
        Fight fight = bot.Fight ?? StartFight(bot, player, nowMs);
        // The retreat decision of a fight: the verdict that ran, else the stay closest to running (the least time to die per time to kill).
        if (fight.Retreat is { Retreat: true }) return;
        float margin = verdict.TimeToDie / MathF.Max(verdict.TimeToKill, 0.1f);
        if (verdict.Retreat || fight.Retreat is null || margin < fight.RetreatMargin)
        {
            fight.Retreat = verdict;
            fight.RetreatMargin = margin;
            fight.RetreatFeatures =
            [
                facts.BotMaxHealth > 0 ? (float)facts.BotHealth / facts.BotMaxHealth : 0f,
                facts.BotMaxHealth > 0 ? facts.DpsIn / facts.BotMaxHealth : 0f,
                facts.DpsOut / MathF.Max(facts.EnemyHealth, 1f),
                facts.Enemies / 5f,
                Seconds(verdict.TimeToKill),
                Seconds(verdict.TimeToDie),
                facts.TargetHealthPct / 100f,
                facts.BotMaxHealth > 0 ? facts.RoundDamage / facts.BotMaxHealth : 0f,
            ];
        }
    }

    /// <summary>After a brain think (world thread): start, follow and end fights; take the rest decision; close downtime.</summary>
    internal void AfterThink(PlayerbotBrain brain, Player player, uint nowMs)
    {
        brain.Risk.CombatSink = this;
        brain.Opportunities.Sink = this;
        if (BotOf(player) is not { } bot) return;
        bot.LastSeenMs = nowMs;
        bool alive = player.IsAlive;
        bool inCombat = player.Combat.IsInCombat;

        if (bot.Fight is null && inCombat && alive) StartFight(bot, player, nowMs);

        if (bot.Fight is { } fight)
        {
            if (player.Health < fight.LastHealth) fight.Damage += fight.LastHealth - player.Health;
            fight.LastHealth = player.Health;
            FollowTargets(brain, player, fight, nowMs);
            if (!inCombat || !alive)
            {
                bot.Fight = null;
                bot.After = new After(fight, nowMs, Died: !alive, Health: player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 0f,
                    Mana: ManaPct(player), Sick: alive && PlayerbotSickness.IsSick(brain.Session, player)) { RestFromMs = nowMs };
            }
        }
        bot.LastHealth = player.Health;

        if (bot.Fight is null && bot.Skips.Count > 0 && unchecked(nowMs - bot.Skips[0].AtMs) > DowntimeCapMs)
        {
            foreach (Choice skip in bot.Skips) Enqueue(new PlayerbotSkipRecord(skip, player, waitMs: null));
            bot.Skips.Clear();
        }

        if (bot.After is { } done && bot.Fight is null)
        {
            // The loot comes first: the rest decision is the one taken after it.
            if (brain.Goal == PlayerbotGoalKind.Loot && unchecked(nowMs - done.EndMs) <= LootMaxMs) done.RestFromMs = nowMs;
            else if (unchecked(nowMs - done.RestFromMs) <= RestLookMs && brain.Goal == PlayerbotGoalKind.Rest) done.Rested = true;
            if (unchecked(nowMs - done.EndMs) > DowntimeCapMs) Emit(player.Guid.Value, bot, done, downtimeMs: null);
        }
    }

    /// <summary>
    /// Forget bots not seen for <paramref name="goneMs"/>: a finished fight still waiting for its downtime is written (downtime null),
    /// a fight under way and its waiting skips are not. Returns how many bots were dropped.
    /// </summary>
    internal int Sweep(uint nowMs, uint goneMs)
    {
        List<ulong>? remove = null;
        foreach ((ulong guid, Bot bot) in _bots)
            if (unchecked(nowMs - bot.LastSeenMs) > goneMs) (remove ??= []).Add(guid);
        if (remove is null) return 0;
        foreach (ulong guid in remove)
        {
            if (_bots[guid].After is { } after) Emit(guid, _bots[guid], after, downtimeMs: null);
            _bots.Remove(guid);
        }
        return remove.Count;
    }

    private Fight StartFight(Bot bot, Player player, uint nowMs)
    {
        Choice? engage = bot.Engage is { } pending && unchecked(nowMs - pending.AtMs) < EngageFreshMs ? pending : null;
        bot.Engage = null;
        // The health before this think: the blow that started the fight counts.
        var fight = new Fight(nowMs, unixMs(), player.MapId, player.ZoneId, engage, bot.LastHealth is > 0 and var last ? last : player.Health,
            player.MaxHealth);
        bot.Fight = fight;
        if (bot.After is { } after) Emit(player.Guid.Value, bot, after, downtimeMs: unchecked(nowMs - after.EndMs));
        foreach (Choice skip in bot.Skips) Enqueue(new PlayerbotSkipRecord(skip, player, waitMs: unchecked(nowMs - skip.AtMs)));
        bot.Skips.Clear();
        return fight;
    }

    /// <summary>A pick is the live enemy the bot fights (its melee victim, else its own target when that is an enemy) changing.</summary>
    private void FollowTargets(PlayerbotBrain brain, Player player, Fight fight, uint nowMs)
    {
        foreach (TargetPick pick in fight.Targets)
            if (pick.KilledAtMs == 0 && !pick.Chosen.IsAlive) pick.KilledAtMs = Math.Max(1u, nowMs);

        _enemies.Clear();
        if (player.Combat.Victim is Creature { IsAlive: true } victim) _enemies.Add(victim);
        foreach (Unit unit in player.Combat.Attackers.Concat(player.Combat.ThreatenedBy))
            // As the risk model counts enemies (PlayerbotRisk.ObserveFight): an evading creature is not one.
            if (unit is Creature { IsAlive: true, IsInEvadeMode: false } creature && ReferenceEquals(creature.Map, player.Map) && !_enemies.Contains(creature))
                _enemies.Add(creature);
        Creature? chosen = player.Combat.Victim as Creature is { IsAlive: true } meleeVictim ? meleeVictim
            : brain.InspectionTarget is { IsAlive: true } own && _enemies.Contains(own) ? own : null;
        if (chosen is null || chosen.Guid.Value == fight.LastChosen) return;
        fight.LastChosen = chosen.Guid.Value;
        if (fight.Targets.Count == 0)
            // The pull: its engage verdict holds only when the fight is with the creature it chose (not a wanderer that came first).
            fight.EngageMatched = fight.Engage is { } engage && engage.Target == chosen.Guid.Value;
        if (fight.Targets.Count >= MaxTargets) return;

        int at = _enemies.IndexOf(chosen);
        if (at >= MaxCandidates) (_enemies[0], _enemies[at]) = (_enemies[at], _enemies[0]);
        int count = Math.Min(_enemies.Count, MaxCandidates);
        var candidates = new float[count][];
        int pickIndex = 0;
        for (int i = 0; i < count; i++)
        {
            candidates[i] = TargetFeatures(player, _enemies[i]);
            if (ReferenceEquals(_enemies[i], chosen)) pickIndex = i;
        }
        fight.Targets.Add(new TargetPick(nowMs, chosen, candidates, pickIndex));
        _enemies.Clear();
    }

    private void Emit(ulong botGuid, Bot bot, After after, uint? downtimeMs)
    {
        bot.After = null;
        Fights++;
        enqueue(new PlayerbotFightRecord(botGuid, after, downtimeMs));
    }

    private void Enqueue(PlayerbotSkipRecord record)
    {
        Skips++;
        enqueue(record);
    }

    /// <summary>The bot's state; null past the cap (65,536 bots): such a bot is not recorded at all.</summary>
    private Bot? BotOf(Player player)
    {
        if (_bots.TryGetValue(player.Guid.Value, out Bot? bot)) return bot;
        return _bots.Count >= 65_536 ? null : _bots[player.Guid.Value] = new Bot();
    }

    private static float[] CandidateFeatures(Player player, Creature candidate, in PlayerbotEngagement verdict) =>
    [
        player.Level / 60f,
        player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 0f,
        ManaPct(player),
        ((int)candidate.Level - (int)player.Level) / 10f,
        candidate.Template.Rank is 1 or 2 or 3 ? 1f : 0f,
        player.MaxHealth > 0 ? MathF.Min(10f, (float)candidate.MaxHealth / player.MaxHealth) / 10f : 0f,
        MathF.Min(Distance(player, candidate), 100f) / 50f,
        verdict.Risk,
        verdict.Reward,
        verdict.Adds / 5f,
    ];

    private static float[] TargetFeatures(Player player, Creature target) =>
    [
        target.MaxHealth > 0 ? (float)target.Health / target.MaxHealth : 0f,
        ((int)target.Level - (int)player.Level) / 10f,
        MathF.Min(Distance(player, target), 60f) / 30f,
        player.Combat.Attackers.Contains(target) ? 1f : 0f,
        target.Template.Rank is 1 or 2 or 3 ? 1f : 0f,
    ];

    private static float Distance(Player player, Creature creature)
    {
        float dx = player.X - creature.X, dy = player.Y - creature.Y, dz = player.Z - creature.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Mana as 0..1; 1 for a bot without mana (rage, energy), which never waits for it.</summary>
    private static float ManaPct(Player player)
    {
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        return player.PowerType == PowerType.Mana && maxMana > 0 ? Math.Min(1f, SpellSystem.GetPower(player, PowerType.Mana) / (float)maxMana) : 1f;
    }

    private static float Seconds(float value) => float.IsFinite(value) ? MathF.Min(value, 600f) / 60f : 10f;

    internal sealed record Choice(uint AtMs, long UnixMs, ulong Target, float[] Features, PlayerbotEngagement Verdict);

    internal sealed class TargetPick(uint atMs, Creature chosen, float[][] candidates, int pick)
    {
        public uint AtMs { get; } = atMs;
        public Creature Chosen { get; } = chosen;
        public float[][] Candidates { get; } = candidates;
        public int Pick { get; } = pick;
        public uint KilledAtMs;
    }

    internal sealed class Fight(uint startMs, long unixMs, uint map, uint zone, Choice? engage, uint health, uint maxHealth)
    {
        public uint StartMs { get; } = startMs;
        public long UnixMs { get; } = unixMs;
        public uint Map { get; } = map;
        public uint Zone { get; } = zone;
        public Choice? Engage { get; } = engage;
        public bool EngageMatched;
        public uint MaxHealth { get; } = maxHealth;
        public uint LastHealth = health;
        public long Damage;
        public ulong LastChosen;
        public readonly List<TargetPick> Targets = [];
        public PlayerbotFightVerdict? Retreat;
        public float RetreatMargin;
        public float[]? RetreatFeatures;
    }

    internal sealed record After(Fight Fight, uint EndMs, bool Died, float Health, float Mana, bool Sick)
    {
        public uint RestFromMs { get; set; }
        public bool Rested { get; set; }
    }

    private sealed class Bot
    {
        public uint LastSeenMs;
        public uint LastHealth;
        public Choice? Engage;
        public Fight? Fight;
        public After? After;
        public readonly List<Choice> Skips = [];
        public readonly Dictionary<ulong, uint> SkipSeen = [];
        public PlayerbotOpportunityRecord? Opportunity;
        public readonly Dictionary<ulong, uint> OpportunitySeen = [];
    }
}

/// <summary>
/// One opportunity decision (<c>"k":"opportunity"</c>): the features, the estimated reward and risk, the guard count, whether the bot
/// went for it, and for one it went for the outcome <c>o</c> (<c>loot</c> in copper, <c>died</c> 0/1, <c>seconds</c> from the decision, <c>lost</c> 1 when another player killed the rare).
/// A decision not taken has no outcome: <c>o</c> holds nulls. Loot is null too when the bot succeeded but the loot was not seen (a rare). <c>lost</c> is 1 for a rare another player tapped and killed: not a success, no loot.
/// </summary>
internal sealed record PlayerbotOpportunityRecord(
    long UnixMs, ulong Bot, uint Map, uint Zone, ulong Key, OpportunityKind Kind, uint Entry, float Level, float Health, float Mana, float Distance,
    int Guards, float Reward, float Risk, string Reason, bool Engaged, OpportunityOutcome? Outcome) : IPlayerbotTelemetryRecord
{
    public void WriteTo(IBufferWriter<byte> output)
    {
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("v", PlayerbotPolicyRecord.SchemaVersion);
            json.WriteString("k", "opportunity");
            json.WriteNumber("t", UnixMs);
            json.WriteString("bot", Bot.ToString("X", CultureInfo.InvariantCulture));
            json.WriteNumber("map", Map);
            json.WriteNumber("zone", Zone);
            json.WriteStartArray("f");
            Write(json, Level);
            Write(json, Health);
            Write(json, Mana);
            Write(json, Distance);
            Write(json, (byte)Kind / 3f);
            json.WriteEndArray();
            json.WriteString("kind", Kind.ToString().ToLowerInvariant());
            json.WriteNumber("entry", Entry);
            // A hard refusal's risk is infinite: written as the cap, still far above any risk a bot would take.
            json.WriteNumber("reward", float.IsFinite(Reward) ? MathF.Round(Reward, 4) : 0f);
            json.WriteNumber("risk", float.IsNaN(Risk) ? 0f : MathF.Round(MathF.Min(Risk, 10f), 4));
            json.WriteNumber("guards", Guards);
            json.WriteString("reason", Reason);
            json.WriteNumber("engaged", Engaged ? 1 : 0);
            json.WriteStartObject("o");
            if (Outcome is { } outcome)
            {
                if (outcome.LootCopper is { } loot) json.WriteNumber("loot", loot);
                else json.WriteNull("loot");
                json.WriteNumber("died", outcome.Died ? 1 : 0);
                json.WriteNumber("seconds", Math.Round(outcome.ElapsedMs / 1000.0, 1));
                json.WriteNumber("lost", outcome.Lost ? 1 : 0);
            }
            else
            {
                json.WriteNull("loot");
                json.WriteNull("died");
                json.WriteNull("seconds");
                json.WriteNull("lost");
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        output.Write("\n"u8);
    }

    private static void Write(Utf8JsonWriter json, float value) => json.WriteNumberValue(float.IsFinite(value) ? MathF.Round(value, 4) : 0f);
}

/// <summary>One fight (<c>"k":"fight"</c>): its four decisions and its outcome (docs/areas/playbots.md, Combat decisions).</summary>
internal sealed record PlayerbotFightRecord(ulong Bot, PlayerbotCombatTelemetry.After After, uint? DowntimeMs) : IPlayerbotTelemetryRecord
{
    public void WriteTo(IBufferWriter<byte> output)
    {
        PlayerbotCombatTelemetry.Fight fight = After.Fight;
        PlayerbotCombatTelemetry.TargetPick? first = fight.Targets.Count > 0 ? fight.Targets[0] : null;
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("v", PlayerbotPolicyRecord.SchemaVersion);
            json.WriteString("k", "fight");
            json.WriteNumber("t", fight.UnixMs);
            json.WriteString("bot", Bot.ToString("X", CultureInfo.InvariantCulture));
            json.WriteNumber("map", fight.Map);
            json.WriteNumber("zone", fight.Zone);
            if (fight is { Engage: { } engage, EngageMatched: true })
            {
                json.WriteStartObject("engage");
                PlayerbotSkipRecord.WriteChoice(json, engage);
                json.WriteEndObject();
            }
            else json.WriteNull("engage");
            json.WriteStartArray("targets");
            foreach (PlayerbotCombatTelemetry.TargetPick pick in fight.Targets)
            {
                json.WriteStartObject();
                json.WriteStartArray("c");
                foreach (float[] candidate in pick.Candidates) PlayerbotSkipRecord.WriteArray(json, candidate);
                json.WriteEndArray();
                json.WriteNumber("pick", pick.Pick);
                WriteSeconds(json, "ttk", pick.KilledAtMs, pick.AtMs);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            if (fight is { Retreat: { } retreat, RetreatFeatures: { } retreatFeatures })
            {
                json.WriteStartObject("retreat");
                PlayerbotSkipRecord.WriteFeatures(json, retreatFeatures);
                json.WriteNumber("retreat", retreat.Retreat ? 1 : 0);
                json.WriteString("reason", retreat.Reason);
                json.WriteEndObject();
            }
            else json.WriteNull("retreat");
            json.WriteStartObject("rest");
            PlayerbotSkipRecord.WriteFeatures(json, [After.Health, After.Mana, After.Sick ? 1f : 0f]);
            json.WriteNumber("rested", After.Rested ? 1 : 0);
            json.WriteEndObject();
            json.WriteStartObject("o");
            json.WriteNumber("died", After.Died ? 1 : 0);
            json.WriteNumber("killed", first is { KilledAtMs: not 0 } ? 1 : 0);
            WriteSeconds(json, "ttk", first?.KilledAtMs ?? 0, fight.StartMs);
            json.WriteNumber("fight", Math.Round(unchecked(After.EndMs - fight.StartMs) / 1000.0, 1));
            json.WriteNumber("damage", fight.Damage);
            json.WriteNumber("damage_pct", fight.MaxHealth > 0 ? Math.Round((double)fight.Damage / fight.MaxHealth, 3) : 0);
            if (DowntimeMs is { } downtime) json.WriteNumber("downtime", Math.Round(downtime / 1000.0, 1));
            else json.WriteNull("downtime");
            json.WriteEndObject();
            json.WriteEndObject();
        }
        output.Write("\n"u8);
    }

    private static void WriteSeconds(Utf8JsonWriter json, string name, uint atMs, uint fromMs)
    {
        if (atMs != 0) json.WriteNumber(name, Math.Round(unchecked(atMs - fromMs) / 1000.0, 1));
        else json.WriteNull(name);
    }
}

/// <summary>A pull candidate turned down (<c>"k":"skip"</c>) and how long the bot then went without a fight.</summary>
internal sealed record PlayerbotSkipRecord(PlayerbotCombatTelemetry.Choice Choice, ulong Bot, uint Map, uint Zone, uint? WaitMs)
    : IPlayerbotTelemetryRecord
{
    internal PlayerbotSkipRecord(PlayerbotCombatTelemetry.Choice choice, Player player, uint? waitMs)
        : this(choice, player.Guid.Value, player.MapId, player.ZoneId, waitMs) { }

    public void WriteTo(IBufferWriter<byte> output)
    {
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("v", PlayerbotPolicyRecord.SchemaVersion);
            json.WriteString("k", "skip");
            json.WriteNumber("t", Choice.UnixMs);
            json.WriteString("bot", Bot.ToString("X", CultureInfo.InvariantCulture));
            json.WriteNumber("map", Map);
            json.WriteNumber("zone", Zone);
            WriteChoice(json, Choice);
            json.WriteStartObject("o");
            if (WaitMs is { } wait) json.WriteNumber("wait", Math.Round(wait / 1000.0, 1));
            else json.WriteNull("wait");
            json.WriteEndObject();
            json.WriteEndObject();
        }
        output.Write("\n"u8);
    }

    internal static void WriteChoice(Utf8JsonWriter json, PlayerbotCombatTelemetry.Choice choice)
    {
        WriteFeatures(json, choice.Features);
        json.WriteString("decision", choice.Verdict.Decision.ToString().ToLowerInvariant());
        json.WriteString("reason", choice.Verdict.Reason);
        json.WriteNumber("entry", choice.Verdict.Entry);
    }

    internal static void WriteFeatures(Utf8JsonWriter json, float[] features)
    {
        json.WritePropertyName("f");
        WriteArray(json, features);
    }

    internal static void WriteArray(Utf8JsonWriter json, float[] values)
    {
        json.WriteStartArray();
        foreach (float value in values) json.WriteNumberValue(float.IsFinite(value) ? MathF.Round(value, 4) : 0f);
        json.WriteEndArray();
    }
}

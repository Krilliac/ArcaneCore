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
/// Where the risk model tells the combat telemetry what it decided (world thread, inside a think): each pull candidate it assessed and
/// each in-fight verdict. <see cref="PlayerbotRisk.CombatSink"/> is null unless the policy telemetry is on.
/// </summary>
internal interface IPlayerbotCombatSink
{
    void Assessed(Player player, Creature candidate, in PlayerbotEngagement verdict, uint nowMs);

    void Judged(Player player, in PlayerbotFightFacts facts, in PlayerbotFightVerdict verdict, uint nowMs);
}

/// <summary>
/// Combat-adjacent decision telemetry for a later combat policy (docs/areas/playbots.md, Goal policy, Combat decisions): the four
/// decisions a policy may one day score (engage or skip a pull, retreat, rest after a fight, which target in a fight) with their features,
/// the option the rules chose, and what followed. Nothing here changes a decision. One <c>fight</c> record per fight (from the first think
/// in combat to the first out of it, or the bot's death) and one <c>skip</c> record per pull candidate the risk model turned down
/// (once a minute per creature). World thread.
/// </summary>
internal sealed class PlayerbotCombatTelemetry(Action<IPlayerbotTelemetryRecord> enqueue, Func<long> unixMs) : IPlayerbotCombatSink
{
    /// <summary>A candidate turned down is recorded again only after this long.</summary>
    internal const uint SkipRepeatMs = 60_000;

    /// <summary>The most turned-down candidates one bot holds while waiting for its next pull.</summary>
    internal const int MaxPendingSkips = 8;

    /// <summary>The most in-fight target switches one fight record keeps.</summary>
    internal const int MaxTargets = 4;

    /// <summary>An engage verdict this old when a fight starts did not start it.</summary>
    internal const uint EngageFreshMs = 30_000;

    /// <summary>After a fight, a rest begun within this long is the rest decision.</summary>
    internal const uint RestLookMs = 5_000;

    /// <summary>Downtime longer than this (no next fight) is written as null.</summary>
    internal const uint DowntimeCapMs = 600_000;

    /// <summary>A pull candidate's features (engage and skip records, <c>f</c>).</summary>
    internal static readonly string[] CandidateFeatureNames =
        ["level", "health", "mana", "level_diff", "elite", "health_ratio", "distance", "risk", "reward", "adds"];

    /// <summary>An in-fight verdict's features (fight record, <c>retreat.f</c>).</summary>
    internal static readonly string[] FightFeatureNames =
        ["health", "dps_in", "dps_out", "enemies", "time_to_kill", "time_to_die", "target_health"];

    /// <summary>An in-fight target's features (fight record, <c>targets[].f</c>).</summary>
    internal static readonly string[] TargetFeatureNames = ["attackers", "target_health", "level_diff", "distance", "attacking_bot"];

    private readonly Dictionary<ulong, Bot> _bots = [];

    internal long Fights { get; private set; }

    internal long Skips { get; private set; }

    public void Assessed(Player player, Creature candidate, in PlayerbotEngagement verdict, uint nowMs)
    {
        Bot bot = BotOf(player);
        bot.LastSeenMs = nowMs;
        float[] features = CandidateFeatures(player, candidate, verdict);
        if (verdict.Decision is PlayerbotEngageDecision.Engage or PlayerbotEngageDecision.Detour)
        {
            bot.Engage = new Choice(nowMs, unixMs(), candidate.Guid.Value, features, verdict);
            return;
        }
        ulong guid = candidate.Guid.Value;
        if (bot.SkipSeen.TryGetValue(guid, out uint seen) && unchecked(nowMs - seen) < SkipRepeatMs) return;
        if (bot.SkipSeen.Count >= 64) bot.SkipSeen.Clear();
        bot.SkipSeen[guid] = nowMs;
        if (bot.Skips.Count >= MaxPendingSkips) return;
        bot.Skips.Add(new Choice(nowMs, unixMs(), guid, features, verdict));
    }

    public void Judged(Player player, in PlayerbotFightFacts facts, in PlayerbotFightVerdict verdict, uint nowMs)
    {
        if (BotOf(player).Fight is not { } fight) return;
        // The retreat decision of a fight: the verdict that ran, else the stay closest to running (the least time to die per time to kill).
        float margin = verdict.TimeToDie / MathF.Max(verdict.TimeToKill, 0.1f);
        if (fight.Retreat is { Retreat: true }) return;
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
            ];
        }
    }

    /// <summary>After a brain think (world thread): start, follow and end fights; take the rest decision; close downtime.</summary>
    internal void AfterThink(PlayerbotBrain brain, Player player, uint nowMs)
    {
        brain.Risk.CombatSink = this;
        Bot bot = BotOf(player);
        bot.LastSeenMs = nowMs;
        bool alive = player.IsAlive;
        bool inCombat = player.Combat.IsInCombat;

        if (bot.Fight is null && inCombat && alive)
        {
            Choice? engage = bot.Engage is { } pending && unchecked(nowMs - pending.AtMs) < EngageFreshMs ? pending : null;
            bot.Engage = null;
            bot.Fight = new Fight(nowMs, unixMs(), player.MapId, player.ZoneId, engage, player.Health, player.MaxHealth);
            if (bot.After is { } after) Emit(player, bot, after, downtimeMs: unchecked(nowMs - after.EndMs));
            foreach (Choice skip in bot.Skips) Enqueue(new PlayerbotSkipRecord(skip, player, waitMs: unchecked(nowMs - skip.AtMs)));
            bot.Skips.Clear();
        }

        if (bot.Fight is { } fight)
        {
            if (player.Health < fight.LastHealth) fight.Damage += fight.LastHealth - player.Health;
            fight.LastHealth = player.Health;
            // A caster's spell target is not its melee victim: the brain's own target stands in.
            if ((player.Combat.Victim as Creature ?? brain.InspectionTarget) is { } victim && victim.Guid.Value != fight.LastVictim)
            {
                if (fight.LastVictim != 0 && fight.Targets.Count < MaxTargets) fight.Targets.Add(TargetFeatures(player, victim));
                fight.LastVictim = victim.Guid.Value;
                fight.FirstTarget ??= victim;
            }
            if (fight.KilledAtMs == 0 && fight.FirstTarget is { IsAlive: false }) fight.KilledAtMs = Math.Max(1u, nowMs);
            if (!inCombat || !alive)
            {
                bot.Fight = null;
                bot.After = new After(fight, nowMs, Died: !alive, Health: player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 0f,
                    Mana: ManaPct(player));
            }
        }

        if (bot.Fight is null && bot.Skips.Count > 0 && unchecked(nowMs - bot.Skips[0].AtMs) > DowntimeCapMs)
        {
            foreach (Choice skip in bot.Skips) Enqueue(new PlayerbotSkipRecord(skip, player, waitMs: null));
            bot.Skips.Clear();
        }

        if (bot.After is { } done && bot.Fight is null)
        {
            uint since = unchecked(nowMs - done.EndMs);
            if (since <= RestLookMs && brain.Goal == PlayerbotGoalKind.Rest) done.Rested = true;
            if (since > DowntimeCapMs) Emit(player, bot, done, downtimeMs: null);
        }
    }

    /// <summary>Forget bots not seen for <paramref name="goneMs"/>; their unfinished fight is not written. Returns how many were dropped.</summary>
    internal int Sweep(uint nowMs, uint goneMs)
    {
        List<ulong>? remove = null;
        foreach ((ulong guid, Bot bot) in _bots)
            if (unchecked(nowMs - bot.LastSeenMs) > goneMs) (remove ??= []).Add(guid);
        if (remove is null) return 0;
        foreach (ulong guid in remove) _bots.Remove(guid);
        return remove.Count;
    }

    private void Emit(Player player, Bot bot, After after, uint? downtimeMs)
    {
        bot.After = null;
        Fights++;
        enqueue(new PlayerbotFightRecord(player.Guid.Value, after, downtimeMs));
    }

    private void Enqueue(PlayerbotSkipRecord record)
    {
        Skips++;
        enqueue(record);
    }

    private Bot BotOf(Player player)
    {
        if (!_bots.TryGetValue(player.Guid.Value, out Bot? bot) && _bots.Count < 65_536) _bots[player.Guid.Value] = bot = new Bot();
        return bot ?? new Bot(); // past the cap: a throwaway state, nothing recorded for good
    }

    private static float[] CandidateFeatures(Player player, Creature candidate, in PlayerbotEngagement verdict)
    {
        float distance = MathF.Sqrt((player.X - candidate.X) * (player.X - candidate.X) + (player.Y - candidate.Y) * (player.Y - candidate.Y)
            + (player.Z - candidate.Z) * (player.Z - candidate.Z));
        return
        [
            player.Level / 60f,
            player.MaxHealth > 0 ? (float)player.Health / player.MaxHealth : 0f,
            ManaPct(player),
            ((int)candidate.Level - (int)player.Level) / 10f,
            candidate.Template.Rank is 1 or 2 or 3 ? 1f : 0f,
            player.MaxHealth > 0 ? MathF.Min(10f, (float)candidate.MaxHealth / player.MaxHealth) / 10f : 0f,
            MathF.Min(distance, 100f) / 50f,
            verdict.Risk,
            verdict.Reward,
            verdict.Adds / 5f,
        ];
    }

    private static float[] TargetFeatures(Player player, Creature target)
    {
        float distance = MathF.Sqrt((player.X - target.X) * (player.X - target.X) + (player.Y - target.Y) * (player.Y - target.Y));
        int attackers = player.Combat.Attackers.Count();
        bool attackingBot = player.Combat.Attackers.Contains(target);
        return
        [
            attackers / 5f,
            target.MaxHealth > 0 ? (float)target.Health / target.MaxHealth : 0f,
            ((int)target.Level - (int)player.Level) / 10f,
            MathF.Min(distance, 60f) / 30f,
            attackingBot ? 1f : 0f,
        ];
    }

    /// <summary>Mana as 0..1; 1 for a bot without mana (rage, energy), which never waits for it.</summary>
    private static float ManaPct(Player player)
    {
        uint maxMana = player.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana);
        return player.PowerType == PowerType.Mana && maxMana > 0 ? Math.Min(1f, SpellSystem.GetPower(player, PowerType.Mana) / (float)maxMana) : 1f;
    }

    private static float Seconds(float value) => float.IsFinite(value) ? MathF.Min(value, 600f) / 60f : 10f;

    internal sealed record Choice(uint AtMs, long UnixMs, ulong Target, float[] Features, PlayerbotEngagement Verdict);

    internal sealed class Fight(uint startMs, long unixMs, uint map, uint zone, Choice? engage, uint health, uint maxHealth)
    {
        public uint StartMs { get; } = startMs;
        public long UnixMs { get; } = unixMs;
        public uint Map { get; } = map;
        public uint Zone { get; } = zone;
        public Choice? Engage { get; } = engage;
        public uint MaxHealth { get; } = maxHealth;
        public uint LastHealth = health;
        public long Damage;
        public ulong LastVictim;
        public Creature? FirstTarget;
        public uint KilledAtMs;
        public readonly List<float[]> Targets = [];
        public PlayerbotFightVerdict? Retreat;
        public float RetreatMargin;
        public float[]? RetreatFeatures;
    }

    internal sealed record After(Fight Fight, uint EndMs, bool Died, float Health, float Mana)
    {
        public bool Rested { get; set; }
    }

    private sealed class Bot
    {
        public uint LastSeenMs;
        public Choice? Engage;
        public Fight? Fight;
        public After? After;
        public readonly List<Choice> Skips = [];
        public readonly Dictionary<ulong, uint> SkipSeen = [];
    }
}

/// <summary>One fight (<c>"k":"fight"</c>): its four decisions and its outcome (docs/areas/playbots.md, Combat decisions).</summary>
internal sealed record PlayerbotFightRecord(ulong Bot, PlayerbotCombatTelemetry.After After, uint? DowntimeMs) : IPlayerbotTelemetryRecord
{
    public void WriteTo(IBufferWriter<byte> output)
    {
        PlayerbotCombatTelemetry.Fight fight = After.Fight;
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            json.WriteNumber("v", PlayerbotPolicyRecord.SchemaVersion);
            json.WriteString("k", "fight");
            json.WriteNumber("t", fight.UnixMs);
            json.WriteString("bot", Bot.ToString("X", CultureInfo.InvariantCulture));
            json.WriteNumber("map", fight.Map);
            json.WriteNumber("zone", fight.Zone);
            if (fight.Engage is { } engage)
            {
                json.WriteStartObject("engage");
                PlayerbotSkipRecord.WriteChoice(json, engage);
                json.WriteEndObject();
            }
            else json.WriteNull("engage");
            json.WriteStartArray("targets");
            foreach (float[] target in fight.Targets)
            {
                json.WriteStartObject();
                PlayerbotSkipRecord.WriteFeatures(json, target);
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
            PlayerbotSkipRecord.WriteFeatures(json, [After.Health, After.Mana]);
            json.WriteNumber("rested", After.Rested ? 1 : 0);
            json.WriteEndObject();
            json.WriteStartObject("o");
            json.WriteNumber("died", After.Died ? 1 : 0);
            json.WriteNumber("killed", fight.KilledAtMs != 0 ? 1 : 0);
            if (fight.KilledAtMs != 0) json.WriteNumber("ttk", Math.Round(unchecked(fight.KilledAtMs - fight.StartMs) / 1000.0, 1));
            else json.WriteNull("ttk");
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
}

/// <summary>A pull candidate the risk model turned down (<c>"k":"skip"</c>) and how long the bot then went without a fight.</summary>
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
        json.WriteStartArray("f");
        foreach (float value in features) json.WriteNumberValue(float.IsFinite(value) ? MathF.Round(value, 4) : 0f);
        json.WriteEndArray();
    }
}

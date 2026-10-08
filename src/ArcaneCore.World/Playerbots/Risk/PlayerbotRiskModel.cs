using System.Globalization;

namespace ArcaneCore.World.Playerbots;

/// <summary>What a bot does about a candidate target (<see cref="PlayerbotRiskModel.Assess"/>).</summary>
internal enum PlayerbotEngageDecision : byte
{
    /// <summary>The reward justifies the risk: pull it.</summary>
    Engage,

    /// <summary>Too dangerous for what it gives: pick another target, or set the goal aside.</summary>
    Avoid,

    /// <summary>Fine at full health and mana, too much as the bot is now: eat, drink or wait first.</summary>
    Rest,

    /// <summary>The creatures that would join stand along the way, not at the target: walk round them.</summary>
    Detour,
}

/// <summary>How a creature comes into a fight (<see cref="RiskEnemy.Join"/>).</summary>
internal enum RiskJoin : byte
{
    /// <summary>The target itself.</summary>
    Target,

    /// <summary>Called by the target when it enters combat (vmangos Creature::CallAssistance within its assistance radius).</summary>
    Assist,

    /// <summary>Its aggro radius covers where the bot will stand to fight (vmangos BasicAI::MoveInLineOfSight).</summary>
    FightSpot,

    /// <summary>Its aggro radius covers the approach route only: a detour avoids it.</summary>
    Path,
}

/// <summary>One creature of a predicted fight: its level, rank, health and damage per second against the bot.</summary>
/// <param name="Lethal">One of its spells kills the bot outright (<see cref="CreatureSpellThreat.IsLethalTo"/>).</param>
internal readonly record struct RiskEnemy(uint Entry, byte Level, uint Rank, uint Health, float Dps, RiskJoin Join, bool Lethal = false)
{
    /// <summary>vmangos CreatureEliteType: 1 elite, 2 rare elite, 3 world boss (4 rare is a normal creature).</summary>
    public bool Elite => Rank is 1 or 2 or 3;
}

/// <summary>Everything one pre-engagement estimate reads (a plain snapshot; unit tests build it directly).</summary>
internal sealed record PlayerbotEngagementFacts
{
    public byte BotLevel { get; init; } = 1;

    public uint BotHealth { get; init; } = 100;

    public uint BotMaxHealth { get; init; } = 100;

    /// <summary>Mana percentage, or null for a bot without mana (rage, energy).</summary>
    public float? BotManaPct { get; init; }

    /// <summary>How much of the bot's damage needs mana: 1 for a caster, about a third for a hybrid, 0 for rage and energy.</summary>
    public float ManaDependence { get; init; }

    /// <summary>The bot's damage per second (observed in its recent fights, else the level estimate).</summary>
    public float BotDps { get; init; } = 5f;

    /// <summary>Class escapes ready now (Frost Nova, Vanish, Feign Death, ...): a way out lowers the risk a little.</summary>
    public int ReadyEscapes { get; init; }

    /// <summary>The target first, then every creature predicted to join.</summary>
    public IReadOnlyList<RiskEnemy> Enemies { get; init; } = [];

    /// <summary>The target is an objective of a quest the bot still needs.</summary>
    public bool QuestObjective { get; init; }

    /// <summary>Loot and skinning value of the target (about 0.1 to 0.4).</summary>
    public float LootValue { get; init; }

    /// <summary>Deaths and retreats the bot remembers near the target or against its kind.</summary>
    public int DangerHits { get; init; }

    /// <summary>The bot retreated from or died to this very creature within its danger memory.</summary>
    public bool Remembered { get; init; }
}

/// <summary>The verdict on one candidate target, shown in BOTINSPECT and <c>.playerbot status</c>.</summary>
internal readonly record struct PlayerbotEngagement(uint Entry, float Risk, float Reward, PlayerbotEngageDecision Decision, string Reason, int Adds)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"risk={Risk:F2} reward={Reward:F2} decision={Decision.ToString().ToLowerInvariant()} reason={Reason} target={Entry}");
}

/// <summary>Everything one in-combat estimate reads.</summary>
internal readonly record struct PlayerbotFightFacts(uint BotHealth, uint BotMaxHealth, float DpsIn, float DpsOut, uint EnemyHealth,
    int Enemies, float TargetHealthPct, bool Lethal = false);

/// <summary>The in-combat verdict: the times to kill and to die and whether to retreat.</summary>
internal readonly record struct PlayerbotFightVerdict(bool Retreat, float TimeToKill, float TimeToDie, string Reason)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"fight ttk={Show(TimeToKill)} ttd={Show(TimeToDie)} decision={(Retreat ? "retreat" : "fight")} reason={Reason}");

    private static string Show(float seconds) => float.IsFinite(seconds) ? seconds.ToString("F1", CultureInfo.InvariantCulture) + "s" : "inf";
}

/// <summary>
/// The risk against reward arithmetic (pure). The cmangos/mangoszero playerbot module decides by fixed health thresholds
/// (FleeStrategy: "critical health", "panic" = health below 20% and low mana; FleeFromAddsStrategy: "has nearest adds") and vmangos
/// PartyBotAI never declines a target; here both sides are estimates, so a pull is weighed against what it gives:
/// <list type="bullet">
/// <item><b>Risk</b>: the damage the bot is predicted to take killing everything that would join, one at a time weakest first,
/// as a share of its health: the target's and each add's damage per second times the time until it dies. 1 means it dies as
/// the last enemy does; a lone same-level creature is about 0.4 to 0.5, a pack of three same-level creatures about 2.5.
/// Mana-dependent damage shrinks with the mana left; remembered danger raises it and a ready escape lowers it a little.</item>
/// <item><b>Reward</b>: the experience of the kills relative to a same-level kill (XP::Gain: a gray creature gives nothing, an
/// elite twice), plus 2 for a quest objective and the loot value.</item>
/// <item>A creature with a spell that kills the bot outright (an instakill, or one hit at least the bot's health) is avoided whatever
/// the reward, and fled at once in a fight.</item>
/// <item>A pull is taken when the risk is at most <c>Tolerance x min(0.9, 0.4 + 0.2 x reward)</c>: a same-level kill without a
/// quest accepts about 0.65, a quest objective 0.9. An elite three or more levels above the bot is a quest objective or nothing.</item>
/// </list>
/// </summary>
internal static class PlayerbotRiskModel
{
    /// <summary>An elite this many levels above the bot is pulled only for a quest.</summary>
    internal const int EliteLevelMargin = 3;

    /// <summary>The reward a quest objective adds.</summary>
    internal const float QuestReward = 2f;

    /// <summary>The bot's damage per second before it has fought (a rough 1.12 figure: a level 10 bot deals about 20).</summary>
    internal static float PriorBotDps(byte level) => 2f + (1.8f * Math.Max((byte)1, level));

    /// <summary>A creature's damage per second from its template, or the level estimate when the template has none.</summary>
    internal static float CreatureDps(float minDamage, float maxDamage, uint attackTimeMs, byte level, bool elite)
    {
        float average = (minDamage + maxDamage) / 2f;
        if (float.IsFinite(average) && average > 0 && attackTimeMs > 0) return average / (attackTimeMs / 1000f);
        return Math.Max((byte)1, level) * (elite ? 2.5f : 1f);
    }

    internal static PlayerbotEngagement Assess(PlayerbotEngagementFacts facts, PlayerbotRiskOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        if (facts.Enemies.Count == 0) return new(0, 0, 0, PlayerbotEngageDecision.Avoid, "no-target", 0);
        RiskEnemy target = facts.Enemies[0];
        int adds = facts.Enemies.Count - 1;
        float reward = Reward(facts);
        float risk = Risk(facts, facts.BotHealth, facts.BotManaPct, facts.Enemies);
        float accept = Acceptable(reward, options.Tolerance);
        PlayerbotEngagement Verdict(PlayerbotEngageDecision decision, string reason) => new(target.Entry, risk, reward, decision, reason, adds);

        if (facts.Remembered) return Verdict(PlayerbotEngageDecision.Avoid, "remembered");
        // A creature with a spell that kills the bot outright is never worth it, quest or not: no estimate of rates covers a one-shot.
        if (facts.Enemies.Any(enemy => enemy.Lethal))
            return new(target.Entry, float.PositiveInfinity, reward, PlayerbotEngageDecision.Avoid, "lethal", adds);
        if (target.Elite && target.Level >= facts.BotLevel + EliteLevelMargin && !facts.QuestObjective)
            return Verdict(PlayerbotEngageDecision.Avoid, "elite-above");
        if (risk <= accept) return Verdict(PlayerbotEngageDecision.Engage, adds > 0 ? $"ok-with-{adds}-adds" : "ok");

        float full = Risk(facts, facts.BotMaxHealth, facts.BotManaPct is null ? null : 100f, facts.Enemies);
        if (full <= accept)
        {
            float healthPct = facts.BotMaxHealth == 0 ? 0 : facts.BotHealth * 100f / facts.BotMaxHealth;
            bool mana = facts.BotManaPct is { } manaPct && manaPct < healthPct && facts.ManaDependence > 0;
            return Verdict(PlayerbotEngageDecision.Rest, mana ? "low-mana" : "low-health");
        }

        if (adds > 0)
        {
            RiskEnemy[] withoutPath = [.. facts.Enemies.Where(enemy => enemy.Join != RiskJoin.Path)];
            int onPath = facts.Enemies.Count - withoutPath.Length;
            if (onPath > 0 && Risk(facts, facts.BotHealth, facts.BotManaPct, withoutPath) <= accept)
                return Verdict(PlayerbotEngageDecision.Detour, $"path-adds-{onPath}");
            return Verdict(PlayerbotEngageDecision.Avoid, $"pack-of-{facts.Enemies.Count}");
        }

        return Verdict(PlayerbotEngageDecision.Avoid, target.Elite ? "elite" : "too-strong");
    }

    /// <summary>The risk a reward justifies: <c>Tolerance x min(0.9, 0.4 + 0.2 x reward)</c>.</summary>
    internal static float Acceptable(float reward, float tolerance) => tolerance * MathF.Min(0.9f, 0.4f + (0.2f * MathF.Max(0f, reward)));

    /// <summary>The kills' experience relative to a same-level kill, plus quest credit and loot.</summary>
    internal static float Reward(PlayerbotEngagementFacts facts)
    {
        float baseline = Game.Progression.ExperienceFormulas.BaseGain(facts.BotLevel, facts.BotLevel);
        float experience = 0;
        foreach (RiskEnemy enemy in facts.Enemies)
            experience += Game.Progression.ExperienceFormulas.KillGain(facts.BotLevel, enemy.Level, enemy.Elite, nonRaidDungeon: false);
        float reward = baseline > 0 ? experience / baseline : 0;
        if (facts.QuestObjective) reward += QuestReward;
        return reward + facts.LootValue;
    }

    /// <summary>
    /// The damage the bot is predicted to take, as a share of <paramref name="health"/>, killing <paramref name="enemies"/> one at
    /// a time, the quickest kill first (each one hits until it dies).
    /// </summary>
    internal static float Risk(PlayerbotEngagementFacts facts, uint health, float? manaPct, IReadOnlyList<RiskEnemy> enemies)
    {
        if (enemies.Count == 0) return 0;
        if (health == 0) return float.PositiveInfinity;
        float dps = MathF.Max(0.1f, facts.BotDps * ManaFactor(facts.ManaDependence, manaPct));
        float elapsed = 0, taken = 0;
        foreach (RiskEnemy enemy in enemies.OrderBy(e => e.Health))
        {
            elapsed += enemy.Health / dps;
            taken += enemy.Dps * elapsed;
        }

        float risk = taken / health;
        risk *= 1f + (0.5f * Math.Max(0, facts.DangerHits));
        if (facts.ReadyEscapes > 0) risk *= 0.9f;
        return risk;
    }

    /// <summary>The share of the bot's damage it still deals with <paramref name="manaPct"/> mana left (a wand or melee remains).</summary>
    internal static float ManaFactor(float dependence, float? manaPct)
    {
        if (manaPct is not { } mana || dependence <= 0) return 1f;
        float d = Math.Clamp(dependence, 0f, 1f);
        float left = Math.Clamp(mana / 100f, 0f, 1f);
        return (1f - (0.65f * d)) + (0.65f * d * left);
    }

    /// <summary>
    /// The in-combat verdict from the observed damage rates: time to kill (the enemies' health over the bot's damage per second)
    /// against time to die (the bot's health over the damage it takes). A losing bot (time to die times Tolerance below the time to
    /// kill) retreats at <see cref="PlayerbotRiskOptions.RetreatHealthPct"/> or when it would die within 4 seconds; a single enemy at
    /// <see cref="PlayerbotRiskOptions.NearlyWonHealthPct"/> or less is finished unless the bot would die in half the time.
    /// </summary>
    internal static PlayerbotFightVerdict Judge(PlayerbotFightFacts facts, PlayerbotRiskOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        float ttk = facts.EnemyHealth == 0 ? 0 : facts.EnemyHealth / MathF.Max(0.1f, facts.DpsOut);
        float ttd = facts.DpsIn <= 0.01f ? float.PositiveInfinity : facts.BotHealth / facts.DpsIn;
        if (facts.Enemies == 0 || facts.EnemyHealth == 0) return new(false, ttk, ttd, "won");
        // An enemy that can kill outright: leave unless it dies within the next two seconds.
        if (facts.Lethal) return ttk < 2f ? new(false, ttk, ttd, "nearly-won") : new(true, ttk, ttd, "lethal");
        float healthPct = facts.BotMaxHealth == 0 ? 0 : facts.BotHealth * 100f / facts.BotMaxHealth;
        if (facts.Enemies == 1 && facts.TargetHealthPct <= options.NearlyWonHealthPct && ttd * 2f >= ttk)
            return new(false, ttk, ttd, "nearly-won");
        bool losing = ttd * options.Tolerance < ttk;
        if (!losing) return new(false, ttk, ttd, "winning");
        if (healthPct <= options.RetreatHealthPct || ttd < 4f) return new(true, ttk, ttd, facts.Enemies > 1 ? $"losing-to-{facts.Enemies}" : "losing");
        return new(false, ttk, ttd, "behind");
    }
}

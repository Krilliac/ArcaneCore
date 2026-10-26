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
/// <param name="Remembered">The bot fled from or died to this very creature and still remembers it (<see cref="PlayerbotDangerMemory"/>).</param>
internal readonly record struct RiskEnemy(uint Entry, byte Level, uint Rank, uint Health, float Dps, RiskJoin Join, bool Lethal = false,
    bool Remembered = false)
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

    /// <summary>
    /// A creature of the target's kind alone beat the bot within its danger memory (<see cref="PlayerbotDangerMemory.LostAlone"/>): the
    /// estimate let it pull one before, and the fight said otherwise.
    /// </summary>
    public bool LostToKind { get; init; }

    /// <summary>
    /// The players who would take the fight together (1 alone). A group of N deals N times the damage and shares the damage taken
    /// over N players' health (the healer's heals stand in for its own damage), so the risk falls roughly with N squared.
    /// </summary>
    public int GroupSize { get; init; } = 1;
}

/// <summary>The verdict on one candidate target, shown in BOTINSPECT and <c>.playerbot status</c>.</summary>
internal readonly record struct PlayerbotEngagement(uint Entry, float Risk, float Reward, PlayerbotEngageDecision Decision, string Reason, int Adds)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"risk={Risk:F2} reward={Reward:F2} decision={Decision.ToString().ToLowerInvariant()} reason={Reason} target={Entry}");
}

/// <summary>One creature that would chase a retreating bot: its current run speed (yards a second) and its damage per second against the bot.</summary>
internal readonly record struct RiskPursuer(float RunSpeed, float Dps);

/// <summary>
/// What the escapes a retreat would use (ready now, <see cref="PlayerbotEscapes.Kit"/>) buy together: seconds the pursuers cannot hit
/// the bot (all of them, or the nearest only), the bot's speed multiplier, the nearest pursuer's (a snare), and any cast time spent
/// standing still before those escapes land.
/// </summary>
internal readonly record struct PlayerbotEscapeKit(float AllFreeSeconds, float VictimFreeSeconds, float SelfSpeed, float VictimSpeed,
    float CastSeconds = 0f)
{
    /// <summary>No escape ready.</summary>
    internal static PlayerbotEscapeKit None { get; } = new(0f, 0f, 1f, 1f);
}

/// <summary>
/// What a retreat from this fight would face (a plain snapshot): the yards to run to the retreat goal, the bot's current run speed
/// (snares and speed auras included), the chasers nearest first, and the escapes ready. See <see cref="PlayerbotRiskModel.RetreatSucceeds"/>.
/// </summary>
/// <param name="MinContactSeconds">The seconds a pursuer in contact cannot leash yet, however soon the bot is out of its threat area:
/// vmangos Creature::IsOutOfThreatArea lets a creature give up only once its leash clock (started at the fight's first check) is more
/// than 12 whole seconds old, so a bot that turns to run early in a fight is hit until then.</param>
/// <param name="FleeDamageFactor">How much more of the pursuers' blows land on a bot that shows them its back: a blow from behind
/// cannot be dodged by a player, nor parried or blocked (vmangos Unit::RollMeleeOutcomeAgainst, <c>MeleeHitTable</c>).</param>
internal sealed record PlayerbotPursuit(float EscapeYards, float BotRunSpeed, IReadOnlyList<RiskPursuer> Pursuers, PlayerbotEscapeKit Kit,
    float MinContactSeconds = 0f, float FleeDamageFactor = 1f);

/// <summary>Everything one in-combat estimate reads.</summary>
/// <param name="Pursuit">What a retreat would face; null when the caller does not know (the retreat is then never refused).</param>
/// <param name="RoundDamage">The non-crit max hit of each melee attacker added up (0 when unknown). The mean <c>DpsIn</c> lags a
/// burst of aligned blows, so against several enemies a bot just above the retreat line retreats when such a round would carry it to
/// or below the line while a retreat can still succeed.</param>
internal readonly record struct PlayerbotFightFacts(uint BotHealth, uint BotMaxHealth, float DpsIn, float DpsOut, uint EnemyHealth,
    int Enemies, float TargetHealthPct, bool Lethal = false, PlayerbotPursuit? Pursuit = null, float RoundDamage = 0f);

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

    /// <summary>A group may take an elite this many more levels above its members than a bot alone (<see cref="EliteLevelMargin"/>).</summary>
    internal const int GroupEliteLevelMargin = 3;

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

        // The target, or a creature that would join the fight itself (its assistance, one whose aggro covers the fight spot), is one
        // the bot fled from or died to: the danger memory steers the bot off the fight, not only off pulling that creature (bot QA #788).
        if (facts.Remembered || facts.Enemies.Any(enemy => enemy.Remembered && enemy.Join != RiskJoin.Path))
            return Verdict(PlayerbotEngageDecision.Avoid, "remembered");
        // A kind that beat the bot one against one is not pulled alone again while it remembers (bot QA #816: run, rest, pull the same
        // kind, run again). A group is weighed as ever (NeededGroupSize).
        if (facts.LostToKind && facts.GroupSize <= 1) return Verdict(PlayerbotEngageDecision.Avoid, "lost-to-kind");
        // A creature with a spell that kills the bot outright is never worth it, quest or not: no estimate of rates covers a one-shot.
        if (facts.Enemies.Any(enemy => enemy.Lethal))
            return new(target.Entry, float.PositiveInfinity, reward, PlayerbotEngageDecision.Avoid, "lethal", adds);
        int eliteMargin = EliteLevelMargin + (facts.GroupSize > 1 ? GroupEliteLevelMargin : 0);
        if (target.Elite && target.Level >= facts.BotLevel + eliteMargin && !facts.QuestObjective)
            return Verdict(PlayerbotEngageDecision.Avoid, "elite-above");
        // A mana user below RestManaBelowPct recovers before the next pull when the fight is fine at full health and mana, even when
        // it would also be fine at the mana left: a hybrid at 1% mana weighs an easy mob as fine (ManaDependence 0.4) and would pull it
        // with nothing to cast (bot QA #1480). Without food or drink the brain sits and regenerates (while mana cannot rise, from a mana-drain aura or a state that does not regenerate, the Rest verdict persists and the bot explores instead of grinding; the fruitless-wait guard does not end that).
        // The line is capped at RecoverHealthPct: the rest ends there, so a higher line (90 against 80) would hold the bot below it for good.
        float restManaBelow = MathF.Min(options.RestManaBelowPct, options.RecoverHealthPct);
        if (restManaBelow > 0 && facts.ManaDependence > 0 && facts.BotManaPct is { } lowMana && lowMana < restManaBelow
            && !facts.Enemies.Any(enemy => enemy.Remembered && enemy.Join == RiskJoin.Path)
            && Risk(facts, facts.BotMaxHealth, 100f, facts.Enemies) <= accept)
            return Verdict(PlayerbotEngageDecision.Rest, "low-mana");
        if (risk <= accept)
        {
            // A remembered creature only along the way: the fight may be taken, never by walking through its reach (the fight without
            // the creatures on the way is no riskier than with them).
            if (!facts.Enemies.Any(enemy => enemy.Remembered && enemy.Join == RiskJoin.Path))
                return Verdict(PlayerbotEngageDecision.Engage, adds > 0 ? $"ok-with-{adds}-adds" : "ok");
            return Verdict(PlayerbotEngageDecision.Detour, $"path-adds-{facts.Enemies.Count(enemy => enemy.Join == RiskJoin.Path)}");
        }

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

    /// <summary>
    /// The smallest group (2 to <paramref name="maxSize"/>) that would take a fight the bot alone would not
    /// (<see cref="PlayerbotEngagementFacts.GroupSize"/>): 0 when the bot can take it alone, when no group up to that size could, or
    /// when the reason is not strength (a creature that kills outright, one the bot remembers fleeing from).
    /// </summary>
    internal static int NeededGroupSize(PlayerbotEngagementFacts facts, PlayerbotRiskOptions options, int maxSize)
        => NeededGroupSize(facts, options, maxSize, out _);

    /// <summary><see cref="NeededGroupSize(PlayerbotEngagementFacts, PlayerbotRiskOptions, int)"/>, with the number of group-size estimates this call made.</summary>
    internal static int NeededGroupSize(PlayerbotEngagementFacts facts, PlayerbotRiskOptions options, int maxSize, out int probes)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        probes = 0;
        if (facts.Enemies.Count == 0 || facts.Remembered
            || facts.Enemies.Any(enemy => enemy.Lethal || (enemy.Remembered && enemy.Join != RiskJoin.Path))) return 0;
        PlayerbotEngagementFacts alone = facts with { GroupSize = 1 };
        if (Assess(alone, options).Decision is PlayerbotEngageDecision.Engage or PlayerbotEngageDecision.Rest or PlayerbotEngageDecision.Detour)
            return 0;
        // A bigger group is never weaker, so the smallest size that takes the fight is found by bisection: about log2(limit) estimates
        // (6 for a raid of 40), not one per size.
        int limit = Math.Min(maxSize, 40);
        if (limit < 2) return 0;
        probes++;
        if (!GroupTakes(facts, options, limit)) return 0;
        int low = 2, high = limit;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            probes++;
            if (GroupTakes(facts, options, mid)) high = mid;
            else low = mid + 1;
        }

        return low;
    }

    /// <summary>Whether a group of <paramref name="size"/> would take the fight, judged at full health and mana: a group rests before it pulls.</summary>
    private static bool GroupTakes(PlayerbotEngagementFacts facts, PlayerbotRiskOptions options, int size)
    {
        PlayerbotEngagementFacts group = facts with
        {
            GroupSize = size, BotHealth = facts.BotMaxHealth, BotManaPct = facts.BotManaPct is null ? null : 100f,
        };
        return Assess(group, options).Decision is PlayerbotEngageDecision.Engage or PlayerbotEngageDecision.Detour;
    }

    /// <summary>
    /// Whether a fight is beyond the bot's strength, whatever it would give: an enemy that kills outright, an elite
    /// <see cref="EliteLevelMargin"/> or more levels above the bot (alone), or a risk at full health and mana above the highest share
    /// any pull is ever taken at (<see cref="Acceptable"/> at its cap, a quest objective's bar: <c>Tolerance x 0.9</c>). Remembered
    /// danger and the reward are not weighed. For a revival (<see cref="PlayerbotRisk.TooStrongNear"/>), where the bot does not choose
    /// a pull for its reward but may have to live beside the creature. It judges the bot at full health and mana although a reclaim
    /// revives it at half (vmangos HandleReclaimCorpseOpcode, <c>ResurrectPlayer(0.5f)</c>): a reclaim revives the ghost where it
    /// stands, which the camped-body rule keeps out of every creature's reach (<see cref="PlayerbotRecovery.ChooseReviveSpot"/>), so
    /// the bot can rest before such a fight; the half health matters only for a creature that would attack at once, and those are
    /// judged by <see cref="Assess"/> at the half health first.
    /// </summary>
    internal static bool BeyondStrength(PlayerbotEngagementFacts facts, PlayerbotRiskOptions options)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(options);
        if (facts.Enemies.Count == 0) return false;
        if (facts.Enemies.Any(enemy => enemy.Lethal)) return true;
        RiskEnemy target = facts.Enemies[0];
        if (target.Elite && target.Level >= facts.BotLevel + EliteLevelMargin) return true;
        float full = Risk(facts, facts.BotMaxHealth, facts.BotManaPct is null ? null : 100f, facts.Enemies);
        return full > Acceptable(float.PositiveInfinity, options.Tolerance);
    }

    /// <summary>
    /// <see cref="BeyondStrength"/> for a bot alone against <paramref name="enemy"/> alone, at full health and mana, no danger memory, no
    /// escape counted: the same verdict without a facts record, a list or LINQ (the hazard scan asks it for every visible creature of
    /// every bot every 2 seconds, <see cref="PlayerbotRisk.ScanHazards"/>). Full mana leaves the bot's damage whole
    /// (<see cref="ManaFactor"/> at 100 is 1).
    /// </summary>
    internal static bool BeyondStrengthAlone(byte botLevel, uint botMaxHealth, float botDps, in RiskEnemy enemy, PlayerbotRiskOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (enemy.Lethal) return true;
        if (enemy.Elite && enemy.Level >= botLevel + EliteLevelMargin) return true;
        if (botMaxHealth == 0) return true;
        int above = enemy.Level - botLevel;
        float risk = enemy.Dps * CrushFactor(above) * (enemy.Health / (MathF.Max(0.1f, botDps) * HitFactor(above))) / botMaxHealth;
        return risk > Acceptable(float.PositiveInfinity, options.Tolerance);
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
        int players = Math.Max(1, facts.GroupSize);
        float dps = MathF.Max(0.1f, facts.BotDps * ManaFactor(facts.ManaDependence, manaPct)) * players;
        float elapsed = 0, taken = 0;
        byte level = facts.BotLevel;
        foreach (RiskEnemy enemy in enemies.OrderBy(e => e.Health / HitFactor(e.Level - level)))
        {
            elapsed += enemy.Health / (dps * HitFactor(enemy.Level - level));
            taken += enemy.Dps * CrushFactor(enemy.Level - level) * elapsed;
        }

        float risk = taken / ((float)health * players);
        risk *= 1f + (0.5f * Math.Max(0, facts.DangerHits));
        if (facts.ReadyEscapes > 0) risk *= 0.9f;
        return risk;
    }

    /// <summary>
    /// The share of the bot's damage that lands on a creature <paramref name="levelsAbove"/> levels above it, relative to one of its own
    /// level: vmangos Unit::MagicSpellHitResult, a player against a creature, hits <c>96 - diff</c> percent below a difference of 3 and
    /// <c>94 - (diff - 2) x 11</c> from 3 on (at least 1): 0.86 at +3, 0.75 at +4, 0.64 at +5. The melee table loses about as much (the
    /// skill gap's misses, dodges and glancing blows). The bot's damage estimate comes from its fights with creatures about its level, so
    /// a creature well above takes this much longer to kill, and hits all that time. Never above 1: a lower creature is not made easier
    /// than the estimate already has it.
    /// </summary>
    internal static float HitFactor(int levelsAbove)
    {
        if (levelsAbove <= 0) return 1f;
        int hit = levelsAbove < 3 ? 96 - levelsAbove : 94 - ((levelsAbove - 2) * 11);
        return Math.Max(1, hit) / 96f;
    }

    /// <summary>
    /// The damage a creature <paramref name="levelsAbove"/> levels above the bot deals relative to its template: vmangos
    /// Unit::RollMeleeOutcomeAgainst lets a creature 4 or more levels above its victim, whose weapon skill (5 a level) is 15 or more
    /// above the victim's defense (capped at 5 a level), crush for 150% damage with a chance of 2% a skill point less 15%: 25% at +4
    /// (x1.125), 35% at +5.
    /// </summary>
    internal static float CrushFactor(int levelsAbove)
    {
        if (levelsAbove < 4) return 1f;
        float chance = Math.Clamp(((levelsAbove * 5 * 2) - 15) / 100f, 0f, 1f);
        return 1f + (0.5f * chance);
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
    /// kill) retreats at <see cref="PlayerbotRiskOptions.RetreatHealthPct"/>, against several enemies when one more round of their max
    /// hits (<see cref="PlayerbotFightFacts.RoundDamage"/>) would leave it at or below that line, or when it would die within 4 seconds
    /// (whether the retreat can succeed is judged at the current health); a single enemy at
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
        // The mean rate lags a burst: against several enemies also leave when one round of their aligned blows would end at or under
        // the line (a retreat the bot can survive now, which a round later it may not be). A lone enemy keeps the plain line.
        bool roundBelowLine = facts.Enemies > 1 && facts.RoundDamage > 0f && facts.BotMaxHealth != 0
            && (facts.BotHealth - Math.Min(facts.BotHealth, facts.RoundDamage)) * 100f / facts.BotMaxHealth <= options.RetreatHealthPct;
        bool plain = healthPct <= options.RetreatHealthPct || ttd < 4f;
        if (plain || roundBelowLine)
        {
            // A retreat that cannot break contact before the bot dies is only a death with no blow dealt on the way: fight on.
            if (facts.Pursuit is { } pursuit && !RetreatSucceeds(pursuit, facts.BotHealth, facts.DpsIn))
                return new(false, ttk, ttd, CorneredReason);
            if (!plain) return new(true, ttk, ttd, $"losing-burst-{facts.Enemies}");
            return new(true, ttk, ttd, facts.Enemies > 1 ? $"losing-to-{facts.Enemies}" : "losing");
        }

        return new(false, ttk, ttd, "behind");
    }

    /// <summary>The reason of a fight kept up because the retreat could not succeed (<see cref="RetreatSucceeds"/>).</summary>
    internal const string CorneredReason = "cornered-no-escape";

    /// <summary>The share of its health the bot may lose on the run and still count the retreat a success.</summary>
    internal const float RetreatHealthBudget = 0.9f;

    /// <summary>The gap a fleeing bot must open before a slower pursuer stops landing blows (melee reach plus both combat radii, about 5 yards).</summary>
    internal const float PursuerReachYards = 5f;

    /// <summary>
    /// Whether a retreat breaks contact before it kills the bot (pure estimate). The bot runs <see cref="PlayerbotPursuit.EscapeYards"/>
    /// at its run speed (times the best speed escape ready); a pursuer no faster than the bot falls behind at once (it never lands
    /// a blow on the run), a faster one keeps hitting for the whole run less the seconds the ready escapes keep it off (a root, a
    /// stun, a fear, a stealth; a snare slows the nearest one first). The damage taken is the observed damage rate
    /// (<paramref name="dpsIn"/>) shared out by the pursuers' own damage per second; the retreat succeeds when it stays under
    /// <see cref="RetreatHealthBudget"/> of <paramref name="botHealth"/>. An unknown speed counts as faster. The escapes are taken as
    /// one after another, the way <see cref="PlayerbotRetreat"/> casts them (each once), which flatters a pair of the same kind.
    /// </summary>
    internal static bool RetreatSucceeds(PlayerbotPursuit pursuit, uint botHealth, float dpsIn)
        => RetreatDamage(pursuit, dpsIn) < botHealth * RetreatHealthBudget;

    /// <summary>
    /// The damage <see cref="RetreatSucceeds"/> predicts the bot takes on the run (0 when nothing would hit it). A pursuer at least as
    /// fast as the bot stays in contact for the run, and at least <see cref="PlayerbotPursuit.MinContactSeconds"/> (its leash clock); the
    /// blows land <see cref="PlayerbotPursuit.FleeDamageFactor"/> times as often as in the fight the rate was observed in.
    /// </summary>
    internal static float RetreatDamage(PlayerbotPursuit pursuit, float dpsIn)
    {
        ArgumentNullException.ThrowIfNull(pursuit);
        if (!(pursuit.EscapeYards > 0f) || !(dpsIn > 0.01f)) return 0f;
        PlayerbotEscapeKit kit = pursuit.Kit;
        float botSpeed = MathF.Max(0.1f, pursuit.BotRunSpeed * kit.SelfSpeed);
        float run = pursuit.EscapeYards / botSpeed;
        float leash = float.IsFinite(pursuit.MinContactSeconds) ? MathF.Max(0f, pursuit.MinContactSeconds) : 0f;
        float flee = float.IsFinite(pursuit.FleeDamageFactor) ? Math.Clamp(pursuit.FleeDamageFactor, 1f, 2.5f) : 1f;
        float cast = float.IsFinite(kit.CastSeconds) ? MathF.Max(0f, kit.CastSeconds) : 0f;
        float totalDps = 0f;
        foreach (RiskPursuer pursuer in pursuit.Pursuers) totalDps += MathF.Max(0f, pursuer.Dps);
        if (!(totalDps > 0f)) return 0f;
        float damage = 0f;
        for (int index = 0; index < pursuit.Pursuers.Count; index++)
        {
            RiskPursuer pursuer = pursuit.Pursuers[index];
            bool nearest = index == 0;
            float speed = (float.IsFinite(pursuer.RunSpeed) && pursuer.RunSpeed > 0f ? pursuer.RunSpeed : float.PositiveInfinity)
                * (nearest ? kit.VictimSpeed : 1f);
            float free = kit.AllFreeSeconds + (nearest ? kit.VictimFreeSeconds : 0f);
            // A pursuer starts in melee contact. One as fast as the bot stays there and keeps hitting the whole run (live 2026-10-17: 74% of
            // 'losing' retreats died when equal speed counted as an escape), and past the run's end until its leash clock lets it give up
            // (live 2026-10-17: still 51% died with the run alone counted); a slower one hits until the gap opens past its reach. When a
            // holding escape has a cast time (Entangling Roots, Fear, Howl of Terror), the bot stands still and takes blows during the cast;
            // once the hold lands, the bot opens a free * botSpeed gap that only a strictly faster pursuer can close after the hold expires.
            float postEscape;
            if (free > 0f)
            {
                if (free >= run || speed <= botSpeed) postEscape = 0f;
                else
                {
                    float catchUp = float.IsPositiveInfinity(speed)
                        ? 0f
                        : MathF.Max(0f, (free * botSpeed) - PursuerReachYards) / (speed - botSpeed);
                    postEscape = MathF.Max(0f, MathF.Max(run, leash) - free - catchUp);
                }
            }
            else
            {
                postEscape = speed >= botSpeed ? MathF.Max(run, leash) : MathF.Min(run, PursuerReachYards / (botSpeed - speed));
            }

            float exposed = cast + postEscape;
            damage += dpsIn * flee * (MathF.Max(0f, pursuer.Dps) / totalDps) * exposed;
        }

        return damage;
    }

    /// <summary>
    /// <see cref="PlayerbotPursuit.FleeDamageFactor"/> from the bot's avoidance in percent: the dodge, parry and block that a blow from
    /// behind cannot roll come out of the table and become hits, so a share <c>a</c> of avoided blows makes the rest land <c>1 / (1 - a)</c>
    /// times as often (held to 60% avoidance).
    /// </summary>
    internal static float FleeDamageFactor(float dodgePct, float parryPct, float blockPct)
    {
        static float Pct(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 100f) : 0f;
        float avoided = MathF.Min(60f, Pct(dodgePct) + Pct(parryPct) + Pct(blockPct)) / 100f;
        return 1f / (1f - avoided);
    }
}

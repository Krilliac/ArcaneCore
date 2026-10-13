using System.Globalization;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>A likely cause, with the facts behind it, and the quest title if the signal names a known quest.</summary>
internal sealed record QaClassification(QaCause Cause, string Detail, string? QuestTitle);

/// <summary>
/// Guesses why a signal happened, from the loaded content (<see cref="IQaContentFacts"/>); pure and deterministic. Rules, in order:
/// missing data (a relay path or script that is not there; a stalled quest/target creature with no spawns on the map, no template, or a
/// ScriptName the server has no script for), behaviour bug (a fault, quarantine, disable, or a death loop with no attacker), content difficulty
/// (a death loop against elites, against several kinds of attacker, or against attackers well above the quest's level), outside the home (a
/// stall of a homed bot that stood outside its home area: expected, and nothing was set aside for other bots), otherwise unknown.
/// A missing-data claim is only made while the content is loaded (<see cref="IQaContentFacts.Available"/>).
/// </summary>
internal static class PlayerbotQaClassifier
{
    /// <summary>Attackers this many levels above a bot (or a quest in an old warning with no bot level) make a death loop a content-difficulty finding.</summary>
    internal const int LevelMargin = 3;

    /// <summary>Different attacker entries that make a death loop a content-difficulty finding.</summary>
    internal const int ManyAttackers = 3;

    internal static QaClassification Classify(QaSignal s, IQaContentFacts facts)
    {
        string? title = s.Quest != 0 ? facts.QuestTitle(s.Quest) : null;
        switch (s.Kind)
        {
            case QaSignalKind.RelayDataMissing when s.Entry != 0:
            {
                string detail = string.Create(CultureInfo.InvariantCulture,
                    $"no creature_movement / creature_movement_template path {s.PathId} for entry {s.Entry} (relay script {s.Relay} {s.Detail})");
                if (facts.Available && facts.HasWaypointPath(s.Entry, s.PathId))
                {
                    detail += "; a path is loaded now, so the content changed after the warning or the path is in another table";
                }

                return new QaClassification(QaCause.MissingData, detail, title);
            }

            case QaSignalKind.RelayDataMissing:
                return new QaClassification(QaCause.Unknown, string.Create(CultureInfo.InvariantCulture,
                    $"relay script {s.Relay} uses a step the server does not implement: {s.Detail}"), title);

            case QaSignalKind.RelayScriptMissing:
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture,
                    $"relay script {s.Relay} has no rows in dbscripts_on_relay"), title);

            case QaSignalKind.ActionFault or QaSignalKind.Quarantine or QaSignalKind.Disabled:
                return new QaClassification(QaCause.BehaviourBug, FaultDetail(s), title);

            case QaSignalKind.DeathLoop:
                return DeathLoop(s, facts, title);

            case QaSignalKind.Stall or QaSignalKind.SharedSetAside:
                return Stall(s, facts, title);

            case QaSignalKind.SpellUnsupported:
                return new QaClassification(QaCause.NotImplemented, SpellPrefix(s) + QaSpellGaps.Explain(s.Reason, s.Detail), title);

            case QaSignalKind.ServerFault:
                return new QaClassification(QaCause.ServerFault, "the server caught " + (s.ExceptionType.Length != 0 ? s.ExceptionType : "an exception") + " at its '"
                    + s.Goal + "' catch site and recovered; the frames below are where it was thrown", title);

            case QaSignalKind.UnreachableGoal:
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture,
                    $"bots skipped target {s.Target} without walking: the walking navigation mesh (steep slopes excluded) does not join the ground they stood on to the goal's ({s.Detail}). A spawn on an island platform or beyond a mesh gap: check the spawn and the mesh there"), title);

            case QaSignalKind.SpellRefused:
                return Refusal(s, title);

            case QaSignalKind.SpellNoEffect:
                return new QaClassification(QaCause.NotImplemented, SpellPrefix(s) + "completed casts hit their targets (no miss, resist or immunity) "
                    + "but dealt no damage, restored no health and left no aura, cast after cast"
                    + (s.Detail.Length != 0 ? " (" + s.Detail + ")" : string.Empty)
                    + ": an effect that silently does nothing, or a rule that drops it", title);

            default:
                // Every kind has a case above (PlayerbotQaKindExhaustivenessTests): a new kind must say how it is classified.
                throw new ArgumentOutOfRangeException(nameof(s), s.Kind, "no classification for this bot QA kind");
        }
    }

    private static string SpellPrefix(QaSignal s)
        => PlayerbotQaAggregator.SpellOf(s) + (s.EffectIndex >= 0 ? string.Create(CultureInfo.InvariantCulture, $" effect {s.EffectIndex} ({s.EffectName})") : string.Empty) + ": ";

    private static QaClassification Refusal(QaSignal s, string? title)
    {
        if (PlayerbotQaSpellRefusals.Of(s.Reason) is not { } rule)
        {
            return new QaClassification(QaCause.Unknown, SpellPrefix(s) + "refused with " + s.Reason, title);
        }

        string why = rule.Class == QaRefusalClass.Expected
            ? $"refused with {s.Reason} again and again by one bot: {rule.Why}"
            : $"refused with {s.Reason}: {rule.Why}";
        return new QaClassification(rule.Cause, SpellPrefix(s) + why + (s.Detail.Length != 0 ? " (" + s.Detail + ")" : string.Empty), title);
    }

    private static string FaultDetail(QaSignal s)
    {
        string type = s.ExceptionType.Length != 0 ? s.ExceptionType : "fault";
        string code = s.FaultCode.Length != 0 ? s.FaultCode : s.Detail;
        string prefix = s.Kind switch
        {
            QaSignalKind.Quarantine => "bot quarantined after a ",
            QaSignalKind.Disabled => "bot disabled after repeated ",
            _ => "bot update threw a ",
        };
        return prefix + type + ": " + code;
    }

    private static QaClassification Stall(QaSignal s, IQaContentFacts facts, string? title)
    {
        if (s.Kind == QaSignalKind.Stall && s.OutsideHome)
        {
            return new QaClassification(QaCause.OutsideHome,
                "the bot stood outside its home area (on its way back, or on a turn-in or capital errand that took it out): expected for a homed "
                + "bot; it set the goal aside for itself only, not for every bot", title);
        }

        uint target = s.Target;
        // A stall's quest= is the last quest the brain remembers; it is checked only when the goal is that quest. A non-quest goal is classified
        // by its own target (GitHub #154: an Explore stall was blamed on quest 109's content). A set-aside's goal is "Quest" for a quest one and
        // "Entry" for an entry one, so an entry set-aside is still checked as a creature (review of #168).
        bool questGoal = string.Equals(s.Goal, "Quest", StringComparison.Ordinal);
        if (facts.Available)
        {
            IReadOnlyList<uint> objectives = questGoal && s.Quest != 0 ? facts.QuestObjectiveCreatures(s.Quest) : [];
            QaTemplateFacts? template = target != 0 ? facts.Template(target) : null;
            // A quest goal's target may be a game object; it is a creature when it has a template or the quest names it as an objective creature.
            bool creature = target != 0 && (!questGoal || template is not null || objectives.Contains(target));
            if (creature && Spawns(s, facts, target) == 0)
            {
                return new QaClassification(QaCause.MissingData, NoSpawns(s, target, template is null), title);
            }

            if (target == 0 && !s.ReturnTrip && objectives.Count > 0 && objectives.All(entry => Spawns(s, facts, entry) == 0))
            {
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture,
                    $"none of the quest objective creatures ({string.Join('/', objectives)}) has a spawn {Where(s)}"), title);
            }

            if (creature && template is null)
            {
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture, $"creature entry {target} has no creature_template row"), title);
            }

            if (creature && template is { ScriptName.Length: > 0 } scripted && !facts.HasScript(target, s.Map))
            {
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture,
                    $"creature entry {target} has ScriptName {scripted.ScriptName}, which is not in the server's SD2 script list for map {s.Map} (no named AI, entry script, map boss AI or instance-script AI)"), title);
            }
        }

        // A walk home whose plan failed logs target=0 (the destinations set the target only for a plan that succeeded). The ender is then the
        // quest's only ender creature; with none (a game object ends it, or the content is unknown) or several (which one the bot was going
        // to is not known) nothing is claimed and the generic verdict below stands.
        uint tripTarget = target;
        if (questGoal && s.ReturnTrip && s.Quest != 0 && target == 0 && facts.QuestEnderEntries(s.Quest) is [var only]) tripTarget = only;
        if (questGoal && s.ReturnTrip && s.Quest != 0 && tripTarget != 0 && EnderTrip(s, facts, tripTarget, title) is { } trip) return trip;

        string subject = questGoal && s.Quest != 0 ? string.Create(CultureInfo.InvariantCulture, $"quest {s.Quest}{(title is null ? string.Empty : " (" + title + ")")}") : $"goal {s.Goal}";
        return new QaClassification(QaCause.Unknown, "no missing content found for " + subject
            + (target != 0 ? string.Create(CultureInfo.InvariantCulture, $", target {target}") : string.Empty), title);
    }

    /// <summary>
    /// A stall the brain marked as the walk home to a completed quest's ender (<see cref="QaSignal.ReturnTrip"/>) whose target is an ender of
    /// the quest. The mark is needed: in vanilla one NPC often gives and ends a quest, so the target alone does not say whether the bot was
    /// going to take the quest or to turn it in. Nothing is missing for it: the cause names the ender, how far it is from the bot and whether
    /// it is on another map, so the finding says where the bot was going instead of "no missing content found". Null when the target is not
    /// one of the quest's enders.
    /// </summary>
    private static QaClassification? EnderTrip(QaSignal s, IQaContentFacts facts, uint target, string? title)
    {
        IReadOnlyList<QaEnderFact> spawns = [.. facts.QuestEnders(s.Quest, target)];
        if (spawns.Count == 0) return null;

        string who = string.Create(CultureInfo.InvariantCulture, $"ender {target}{(spawns[0].Name.Length == 0 ? string.Empty : " (" + spawns[0].Name + ")")}");
        string subject = string.Create(CultureInfo.InvariantCulture, $"quest {s.Quest}{(title is null ? string.Empty : " (" + title + ")")}");
        string where;
        if (!s.HasPosition)
        {
            where = "its distance from the bot is unknown (the stall has no position)";
        }
        else
        {
            QaEnderFact? best = null;
            float bestSquared = float.MaxValue;
            foreach (QaEnderFact e in spawns)
            {
                if (e.Map != s.Map) continue;
                float dx = e.X - s.X, dy = e.Y - s.Y;
                float squared = (dx * dx) + (dy * dy);
                if (squared < bestSquared) { bestSquared = squared; best = e; }
            }

            where = best is { } near
                ? string.Create(CultureInfo.InvariantCulture, $"nearest spawn {MathF.Sqrt(bestSquared):F0} yd from the bot on map {s.Map} at {near.X:F0},{near.Y:F0},{near.Z:F0}")
                : string.Create(CultureInfo.InvariantCulture, $"on another map ({string.Join('/', spawns.Select(e => e.Map).Distinct())}; the bot is on map {s.Map}), where the bot cannot walk");
        }

        return new QaClassification(QaCause.Unknown, string.Create(CultureInfo.InvariantCulture,
            $"no missing content found for {subject}: the bot was walking to its {who}, {where}"), title);
    }

    private static int Spawns(QaSignal s, IQaContentFacts facts, uint entry)
        => OnItsMap(s) ? facts.SpawnCount(s.Map, entry) : facts.SpawnCountAnyMap(entry);

    private static string Where(QaSignal s) => OnItsMap(s) ? string.Create(CultureInfo.InvariantCulture, $"on map {s.Map}") : "on any map";

    /// <summary>
    /// Whether the spawn check looks at the signal's map only. A shared set-aside's position is only its first stall (the bots behind it may
    /// have stalled on other maps, and its finding's cause is recomputed with every repeat): its check stays on every map. So does a walk home
    /// (<see cref="QaSignal.ReturnTrip"/>): the ender is where the quest was left, often on another map, and a spawn there is not missing data.
    /// </summary>
    private static bool OnItsMap(QaSignal s) => s.HasPosition && s.Kind != QaSignalKind.SharedSetAside && !s.ReturnTrip;

    private static string NoSpawns(QaSignal s, uint entry, bool noTemplate)
        => string.Create(CultureInfo.InvariantCulture, $"creature entry {entry} has no spawn {Where(s)}") + (noTemplate ? " and no creature_template row" : string.Empty);

    private static QaClassification DeathLoop(QaSignal s, IQaContentFacts facts, string? title)
    {
        IReadOnlyList<string>? observedSources = s.Bot?.DeathSources is { Count: > 0 } all ? all : null;
        bool creatureMajority = observedSources is not null
            && observedSources.Count(source => source == "Creature") * 2 > observedSources.Count;
        if (s.Attackers.Count == 0 || observedSources is not null && !creatureMajority)
        {
            IReadOnlyList<string> sources = observedSources ?? [s.Bot?.Source ?? "Unobserved"];
            int behaviourDeaths = sources.Count(source => source is "Fall" or "FallToVoid" or "Drowning" or "Exhausted"
                or "Lava" or "Slime" or "Fire" or "Self");
            QaCause cause = s.Bot is null || behaviourDeaths * 2 > sources.Count ? QaCause.BehaviourBug : QaCause.Unknown;
            string deathDetail = sources.Count == 1 ? DescribeDeathSource(sources[0])
                : "death sources in this loop: " + string.Join("; ", sources.Select(DescribeDeathSource));
            return new QaClassification(cause, (s.Attackers.Count == 0
                ? "died repeatedly with no attacker entry: " : "died repeatedly with nearby attackers: ") + deathDetail, title);
        }

        var notes = new List<string>();
        var descriptions = new List<string>();
        bool difficult = false;
        int? questLevel = s.Quest != 0 ? facts.QuestLevel(s.Quest) : null;
        int? referenceLevel = s.Bot is { Level: > 0 } bot ? bot.Level : questLevel;
        foreach (uint entry in s.Attackers)
        {
            QaAttackerFact? observed = s.Bot?.AttackerFacts.FirstOrDefault(a => a.Entry == entry);
            QaTemplateFacts? template = facts.Template(entry);
            if (observed is null && template is null)
            {
                descriptions.Add(string.Create(CultureInfo.InvariantCulture, $"{entry}: template unavailable, hostility unknown"));
                continue;
            }

            uint rank = observed?.Rank ?? template!.Value.Rank;
            byte minLevel = observed?.MinLevel ?? template!.Value.MinLevel;
            byte maxLevel = observed?.MaxLevel ?? template!.Value.MaxLevel;
            byte attackerLevel = observed?.Level ?? maxLevel;
            bool guard = observed?.Guard ?? template!.Value.Guard;
            bool? hostile = observed?.Hostile;
            string rankName = rank switch { 0 => "normal", 1 => "elite", 2 => "rare elite", 3 => "world boss", 4 => "rare", _ => rank.ToString(CultureInfo.InvariantCulture) };
            descriptions.Add(string.Create(CultureInfo.InvariantCulture,
                $"{entry}: level {attackerLevel} (template {minLevel}-{maxLevel}), rank {rankName}, guard {(guard ? "yes" : "no")}, hostile {(hostile is null ? "unknown" : hostile.Value ? "yes" : "no")}"));
            if (guard && hostile == true)
            {
                difficult = true;
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{entry} is a hostile guard"));
            }
            if (rank is >= 1 and <= 3)
            {
                difficult = true;
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{entry} is {(rank == 1 ? "an elite" : rank == 2 ? "a rare elite" : "a world boss")} (level {minLevel}-{maxLevel})"));
            }
            if (referenceLevel is { } level && attackerLevel >= level + LevelMargin)
            {
                difficult = true;
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{entry} is level {attackerLevel} against {(s.Bot is null ? "quest" : "bot")} level {level}"));
            }
        }

        if (s.Attackers.Count >= ManyAttackers)
        {
            difficult = true;
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{s.Attackers.Count} different attackers ({string.Join('/', s.Attackers)})"));
        }

        string detail = difficult ? string.Join("; ", notes)
            : string.Create(CultureInfo.InvariantCulture, $"died repeatedly to attackers {string.Join('/', s.Attackers)}; no observed elite, hostile guard or confirmed level gap");
        if (descriptions.Count > 0) detail += "; attacker facts: " + string.Join("; ", descriptions);
        return new QaClassification(difficult ? QaCause.ContentDifficulty : QaCause.Unknown, detail, title);
    }

    private static string DescribeDeathSource(string source) => source switch
    {
        "Fall" or "FallToVoid" => "fall damage",
        "Drowning" => "drowning environmental damage",
        "Exhausted" => "fatigue environmental damage",
        "Lava" => "lava environmental damage",
        "Slime" => "slime environmental damage",
        "Fire" => "fire environmental damage",
        "Creature" => "a creature killing blow",
        "Player" => "a player killing blow (no creature entry)",
        "PlayerPet" => "a player-controlled pet killing blow (no content creature entry)",
        "Self" => "a self-inflicted killing blow",
        "PeriodicCasterMissing" => "a damage-over-time killing tick whose original caster despawned or became unavailable",
        "Unknown" => "a null killing-blow source; an expired damage-over-time caster or other unresolved damage is possible",
        _ => "no killing-blow source was recorded; environmental damage, a despawned caster or a missed observer are possible",
    };

    /// <summary>
    /// The ranking score: a weight per kind, plus a logarithm of the count, plus the distinct bots (at most 20 count), plus 10 for a finding seen in
    /// the last hour. Deterministic given <paramref name="now"/>.
    /// </summary>
    internal static double Severity(QaSignalKind kind, long count, int distinctBots, DateTime lastSeenUtc, DateTime now)
    {
        double weight = QaKinds.Of(kind).Weight;
        double recent = now - lastSeenUtc <= TimeSpan.FromHours(1) ? 10 : 0;
        return Math.Round(weight + 10 * Math.Log2(1 + Math.Max(0, count)) + 5 * Math.Min(Math.Max(0, distinctBots), 20) + recent, 2);
    }
}

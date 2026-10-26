using System.Globalization;
using ArcaneCore.World.Playerbots.Population;

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

            case QaSignalKind.RunFailed:
                return RunFailure(s, title);

            case QaSignalKind.TickHang:
                return new QaClassification(QaCause.ServerFault, "the world tick hung in its " + (s.Stage.Length != 0 ? s.Stage : "unknown") + " phase while running "
                    + (s.Reason.Length != 0 ? s.Reason : "unknown work") + "; the watchdog's log line (event 7102) carries the durations and the slowest features", title);
            case QaSignalKind.UnreachableGoal:
                if (AcrossWater(s, facts))
                {
                    return new QaClassification(QaCause.BehaviourBug, string.Create(CultureInfo.InvariantCulture,
                        $"goal across water: the travel (boat or portal) failed; see bot-travel/leg-failed. Target {s.Target} spawns only across water from the bot on map {s.Map} ({s.Detail})"), title);
                }

                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture,
                    $"bots skipped target {s.Target} without walking: the walking navigation mesh (steep slopes excluded) does not join the ground they stood on to the goal's ({s.Detail}). A spawn on an island platform or beyond a mesh gap: check the spawn and the mesh there"), title);

            case QaSignalKind.SpellRefused:
                return Refusal(s, title);

            case QaSignalKind.SpellNoEffect:
                return new QaClassification(QaCause.NotImplemented, SpellPrefix(s) + "completed casts hit their targets (no miss, resist or immunity) "
                    + "but dealt no damage, restored no health and left no aura, cast after cast"
                    + (s.Detail.Length != 0 ? " (" + s.Detail + ")" : string.Empty)
                    + ": an effect that silently does nothing, or a rule that drops it", title);

            case QaSignalKind.Anomaly:
                return new QaClassification(IsContentDefect(s) ? QaCause.MissingData : QaCause.BehaviourBug,
                    (s.Goal.Length != 0 && !s.Goal.StartsWith(PlayerbotQaAnomalyFeed.BotSubsystemPrefix, StringComparison.Ordinal) ? "recurring world anomaly " : "recurring bot outcome anomaly ")
                    + (s.Goal.Length != 0 ? s.Goal : "bot") + "/" + (s.Reason.Length != 0 ? s.Reason : "anomaly")
                    + (s.Detail.Length != 0 ? ": " + s.Detail : string.Empty), title);

            default:
                // Every kind has a case above (PlayerbotQaKindExhaustivenessTests): a new kind must say how it is classified.
                throw new ArgumentOutOfRangeException(nameof(s), s.Kind, "no classification for this bot QA kind");
        }
    }

    /// <summary>
    /// A world anomaly whose cause is the imported content, not server behaviour. The reporters give those defects their own ledger keys
    /// (<c>loot/gameobject-loot-no-data</c>: a chest whose loot template names no rows or only unknown items; <c>gameobject/use-failed-unknown-lock</c>:
    /// a use refused because the object's lock id has no lock_template row), so the cause follows the key and never the last occurrence's detail.
    /// </summary>
    private static bool IsContentDefect(QaSignal s)
        => PlayerbotQaAnomalyFeed.ContentDefectKeys.Contains(s.FaultCode);

    private static string SpellPrefix(QaSignal s)
        => PlayerbotQaAggregator.SpellOf(s) + (s.EffectIndex >= 0 ? string.Create(CultureInfo.InvariantCulture, $" effect {s.EffectIndex} ({s.EffectName})") : string.Empty) + ": ";

    private static QaClassification RunFailure(QaSignal s, string? title)
    {
        string where = (s.Detail.Length != 0 ? s.Detail : string.Create(CultureInfo.InvariantCulture, $"map {s.Map}"))
            + (s.Segment > 0 ? string.Create(CultureInfo.InvariantCulture, $" wing {s.Segment}") : string.Empty) + " at " + (s.Stage.Length != 0 ? s.Stage : "an unknown stage");
        return s.Reason switch
        {
            "wiped" => new QaClassification(QaCause.ContentDifficulty, "the group wiped out its allowed number of times in " + where, title),
            "no-path-to-entrance" or "no-path-to-boss" => new QaClassification(QaCause.MissingData,
                "the group found no walkable path in " + where + " (navigation data, or a door the walker cannot open)", title),
            _ => new QaClassification(QaCause.Unknown, "the run ended with '" + s.Reason + "' in " + where, title),
        };
    }

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
        QaEnderFact? best = null;
        float bestSquared = float.MaxValue;
        if (!s.HasPosition)
        {
            where = "its distance from the bot is unknown (the stall has no position)";
        }
        else
        {
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

        string action = s.Bot?.TurnInRefusal is { Length: > 0 } refusal
            ? $"the bot reached its {who} and turn-in was refused ({FormatTurnInRefusal(refusal)})"
            : best is not null && bestSquared <= 10f * 10f
                ? $"the bot reached its {who} but could not complete the turn-in"
                : $"the bot was walking to its {who}";

        return new QaClassification(QaCause.Unknown, string.Create(CultureInfo.InvariantCulture,
            $"no missing content found for {subject}: {action}, {where}"), title);
    }

    private static string FormatTurnInRefusal(string code) => code switch
    {
        "InventoryFull" => "inventory full",
        "MissingDeliveryItem" => "missing required delivery item",
        "NotEnoughMoney" => "not enough money",
        "DuplicateUniqueItem" => "duplicate unique reward item",
        "ReputationRefused" => "reputation Unfriendly or worse",
        "OutOfRange" => "out of interaction range",
        "NoRouteToVisibleEnder" => "no exact route to visible ender",
        "IncompleteAtEnder" => "quest requirements not met at ender",
        "EnderDead" => "ender is dead",
        "EnderHostile" => "ender is hostile",
        "EnderInCombat" => "ender is in combat",
        "EnderNotInteractable" => "ender is not interactable",
        "RewardRefused" => "reward choice refused",
        "StageTimeout" => "turn-in stage timed out",
        _ => code,
    };

    /// <summary>
    /// Whether every spawn of an unreachable goal's target on the bot's map lies across water from the bot (<see cref="PopulationTownProgression.SeparatedByWater"/>):
    /// the walk failed because the boat or portal leg failed, not because content is missing. The log line carries only the bot's position, so the goal's
    /// position comes from the target's spawns; a spawn on the bot's own side keeps the mesh-gap verdict.
    /// </summary>
    private static bool AcrossWater(QaSignal s, IQaContentFacts facts)
    {
        if (!facts.Available || !s.HasPosition || s.Target == 0) return false;
        IReadOnlyList<(float X, float Y)> spawns = facts.SpawnPositions(s.Map, s.Target);
        if (spawns.Count == 0) return false;
        foreach ((float x, float y) in spawns)
        {
            if (!PopulationTownProgression.SeparatedByWater(s.Map, s.X, s.Y, s.Map, x, y)) return false;
        }

        return true;
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
        => ClassifyDeathLoop(
            s.Attackers,
            s.Bot is null ? [] : [new QaSample(s.AtUtc, s.HasPosition, s.Map, s.X, s.Y, s.Z, [.. s.Attackers], s.Message) { Bot = s.Bot }],
            s.Bot,
            s.Quest,
            facts,
            title);

    internal static QaClassification ClassifyDeathLoop(QaFinding finding, QaBotObservation? latestBot, IQaContentFacts facts)
        => ClassifyDeathLoop(
            finding.Attackers,
            finding.Samples,
            latestBot,
            finding.Quest,
            facts,
            finding.QuestTitle ?? (finding.Quest != 0 ? facts.QuestTitle(finding.Quest) : null),
            finding.AttackerFacts,
            finding.AttackerBotLevels,
            finding.MinBotLevel);

    internal static QaClassification ClassifyDeathLoop(
        IReadOnlyCollection<uint> attackers,
        IReadOnlyList<QaSample> samples,
        QaBotObservation? latestBot,
        uint quest,
        IQaContentFacts facts,
        string? title = null,
        IReadOnlyDictionary<uint, QaAttackerFact>? rememberedFacts = null,
        IReadOnlyDictionary<uint, int>? rememberedLevels = null,
        int rememberedMinBotLevel = 0)
    {
        QaBotObservation? bot = latestBot ?? samples.LastOrDefault(s => s.Bot is not null)?.Bot;
        IReadOnlyList<string>? observedSources = bot?.DeathSources is { Count: > 0 } all ? all : null;
        bool creatureMajority = observedSources is not null
            && observedSources.Count(source => source == "Creature") * 2 > observedSources.Count;
        if (attackers.Count == 0 || observedSources is not null && !creatureMajority)
        {
            IReadOnlyList<string> sources = observedSources ?? [bot?.Source ?? "Unobserved"];
            int behaviourDeaths = sources.Count(source => source is "Fall" or "FallToVoid" or "Drowning" or "Exhausted"
                or "Lava" or "Slime" or "Fire" or "Self");
            QaCause cause = bot is null || behaviourDeaths * 2 > sources.Count ? QaCause.BehaviourBug : QaCause.Unknown;
            string deathDetail = sources.Count == 1 ? DescribeDeathSource(sources[0])
                : "death sources in this loop: " + string.Join("; ", sources.Select(DescribeDeathSource));
            return new QaClassification(cause, (attackers.Count == 0
                ? "died repeatedly with no attacker entry: " : "died repeatedly with nearby attackers: ") + deathDetail, title);
        }

        var notes = new List<string>();
        var descriptions = new List<string>();
        bool difficult = false;
        int? questLevel = quest != 0 ? facts.QuestLevel(quest) : null;
        int? minBotLevel = rememberedMinBotLevel > 0 ? rememberedMinBotLevel : null;
        if (latestBot is { Level: > 0 } lb && (minBotLevel is null || lb.Level < minBotLevel.Value)) minBotLevel = lb.Level;
        foreach (QaSample sample in samples)
        {
            if (sample.Bot is { Level: > 0 } sb && (minBotLevel is null || sb.Level < minBotLevel.Value)) minBotLevel = sb.Level;
        }

        foreach (uint entry in attackers)
        {
            QaAttackerFact? observed = LookupAttackerFact(entry, samples, latestBot, rememberedFacts);
            QaTemplateFacts? template = facts.Template(entry);
            if (observed is null && template is null)
            {
                descriptions.Add(string.Create(CultureInfo.InvariantCulture, $"{entry}: template unavailable, hostility unknown"));
                continue;
            }

            uint rank = observed?.Rank ?? template?.Rank ?? 0;
            byte minLevel = observed?.MinLevel ?? template?.MinLevel ?? 0;
            byte maxLevel = observed?.MaxLevel ?? template?.MaxLevel ?? 0;
            byte attackerLevel = observed?.Level ?? maxLevel;
            bool guard = observed?.Guard ?? template?.Guard ?? false;
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

            int? attackerBotLevel = LookupAttackerBotLevel(entry, samples, latestBot, rememberedLevels) ?? minBotLevel;
            int? referenceLevel = attackerBotLevel ?? questLevel;
            if (referenceLevel is { } level && attackerLevel >= level + LevelMargin)
            {
                difficult = true;
                notes.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{entry} is level {attackerLevel} against {(attackerBotLevel is null ? "quest" : "bot")} level {level}"));
            }
        }

        if (attackers.Count >= ManyAttackers)
        {
            difficult = true;
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{attackers.Count} different attackers ({string.Join('/', attackers)})"));
        }

        string detail = difficult ? string.Join("; ", notes)
            : string.Create(CultureInfo.InvariantCulture, $"died repeatedly to attackers {string.Join('/', attackers)}; no observed elite, hostile guard or confirmed level gap");
        if (descriptions.Count > 0) detail += "; attacker facts: " + string.Join("; ", descriptions);
        return new QaClassification(difficult ? QaCause.ContentDifficulty : QaCause.Unknown, detail, title);
    }

    private static QaAttackerFact? LookupAttackerFact(
        uint entry,
        IReadOnlyList<QaSample> samples,
        QaBotObservation? latestBot,
        IReadOnlyDictionary<uint, QaAttackerFact>? rememberedFacts)
    {
        QaAttackerFact? merged = rememberedFacts is not null && rememberedFacts.TryGetValue(entry, out QaAttackerFact? rem) ? rem : null;
        foreach (QaSample sample in samples)
        {
            if (sample.Bot?.AttackerFacts.FirstOrDefault(a => a.Entry == entry) is { } fact)
            {
                merged = merged is null ? fact : QaFinding.MergeAttackerFact(merged, fact);
            }
        }

        if (latestBot?.AttackerFacts.FirstOrDefault(a => a.Entry == entry) is { } latest)
        {
            merged = merged is null ? latest : QaFinding.MergeAttackerFact(merged, latest);
        }

        return merged;
    }

    private static int? LookupAttackerBotLevel(
        uint entry,
        IReadOnlyList<QaSample> samples,
        QaBotObservation? latestBot,
        IReadOnlyDictionary<uint, int>? rememberedLevels)
    {
        int? minLevel = rememberedLevels is not null && rememberedLevels.TryGetValue(entry, out int rem) && rem > 0 ? rem : null;
        foreach (QaSample sample in samples)
        {
            if (sample.Bot is { Level: > 0 } sb
                && (sample.Attackers.Contains(entry) || sb.AttackerFacts.Any(a => a.Entry == entry))
                && (minLevel is null || sb.Level < minLevel.Value))
            {
                minLevel = sb.Level;
            }
        }

        if (latestBot is { Level: > 0 } lb
            && lb.AttackerFacts.Any(a => a.Entry == entry)
            && (minLevel is null || lb.Level < minLevel.Value))
        {
            minLevel = lb.Level;
        }

        return minLevel;
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

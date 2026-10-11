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
    /// <summary>Attackers this many levels above a quest's level make a death loop a content-difficulty finding.</summary>
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

            default:
                return Stall(s, facts, title);
        }
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
        // A stall's quest= is the last quest the brain remembers; it is checked only when the goal is that quest (a set-aside is always about its
        // quest). A non-quest goal is classified by its own target (GitHub #154: an Explore stall was blamed on quest 109's content).
        bool questGoal = s.Kind == QaSignalKind.SharedSetAside || string.Equals(s.Goal, "Quest", StringComparison.Ordinal);
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

            if (target == 0 && objectives.Count > 0 && objectives.All(entry => Spawns(s, facts, entry) == 0))
            {
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture,
                    $"none of the quest objective creatures ({string.Join('/', objectives)}) has a spawn {Where(s)}"), title);
            }

            if (creature && template is null)
            {
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture, $"creature entry {target} has no creature_template row"), title);
            }

            if (creature && template is { ScriptName.Length: > 0 } scripted && !facts.HasScript(target, scripted.ScriptName))
            {
                return new QaClassification(QaCause.MissingData, string.Create(CultureInfo.InvariantCulture,
                    $"creature entry {target} has ScriptName {scripted.ScriptName}, which is not in the server's SD2 script list"), title);
            }
        }

        string subject = questGoal && s.Quest != 0 ? string.Create(CultureInfo.InvariantCulture, $"quest {s.Quest}{(title is null ? string.Empty : " (" + title + ")")}") : $"goal {s.Goal}";
        return new QaClassification(QaCause.Unknown, "no missing content found for " + subject
            + (target != 0 ? string.Create(CultureInfo.InvariantCulture, $", target {target}") : string.Empty), title);
    }

    private static int Spawns(QaSignal s, IQaContentFacts facts, uint entry)
        => OnItsMap(s) ? facts.SpawnCount(s.Map, entry) : facts.SpawnCountAnyMap(entry);

    private static string Where(QaSignal s) => OnItsMap(s) ? string.Create(CultureInfo.InvariantCulture, $"on map {s.Map}") : "on any map";

    /// <summary>
    /// Whether the spawn check looks at the signal's map only. A shared set-aside's position is only its first stall (the bots behind it may
    /// have stalled on other maps, and its finding's cause is recomputed with every repeat): its check stays on every map.
    /// </summary>
    private static bool OnItsMap(QaSignal s) => s.HasPosition && s.Kind != QaSignalKind.SharedSetAside;

    private static string NoSpawns(QaSignal s, uint entry, bool noTemplate)
        => string.Create(CultureInfo.InvariantCulture, $"creature entry {entry} has no spawn {Where(s)}") + (noTemplate ? " and no creature_template row" : string.Empty);

    private static QaClassification DeathLoop(QaSignal s, IQaContentFacts facts, string? title)
    {
        if (s.Attackers.Count == 0)
        {
            return new QaClassification(QaCause.BehaviourBug, "died repeatedly with no attacker: an environmental death or a bot that walks into it again", title);
        }

        var notes = new List<string>();
        bool difficult = false;
        int? questLevel = s.Quest != 0 ? facts.QuestLevel(s.Quest) : null;
        foreach (uint entry in s.Attackers)
        {
            if (facts.Template(entry) is not { } t) continue;
            if (t.Rank is >= 1 and <= 3)
            {
                difficult = true;
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{entry} is {(t.Rank == 1 ? "an elite" : t.Rank == 2 ? "a rare elite" : "a world boss")} (level {t.MinLevel}-{t.MaxLevel})"));
            }
            else if (questLevel is { } level && t.MaxLevel >= level + LevelMargin)
            {
                difficult = true;
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"{entry} is level {t.MinLevel}-{t.MaxLevel} against quest level {level}"));
            }
        }

        if (s.Attackers.Count >= ManyAttackers)
        {
            difficult = true;
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{s.Attackers.Count} different attackers ({string.Join('/', s.Attackers)})"));
        }

        return difficult
            ? new QaClassification(QaCause.ContentDifficulty, string.Join("; ", notes), title)
            : new QaClassification(QaCause.Unknown, string.Create(CultureInfo.InvariantCulture, $"died repeatedly to attackers {string.Join('/', s.Attackers)}; none is an elite or far above the quest level"), title);
    }

    /// <summary>
    /// The ranking score: a weight per kind, plus a logarithm of the count, plus the distinct bots (at most 20 count), plus 10 for a finding seen in
    /// the last hour. Deterministic given <paramref name="now"/>.
    /// </summary>
    internal static double Severity(QaSignalKind kind, long count, int distinctBots, DateTime lastSeenUtc, DateTime now)
    {
        double weight = kind switch
        {
            QaSignalKind.Quarantine or QaSignalKind.Disabled => 45,
            QaSignalKind.ActionFault => 40,
            QaSignalKind.SharedSetAside => 35,
            QaSignalKind.DeathLoop => 30,
            QaSignalKind.RelayDataMissing or QaSignalKind.RelayScriptMissing => 25,
            _ => 15,
        };
        double recent = now - lastSeenUtc <= TimeSpan.FromHours(1) ? 10 : 0;
        return Math.Round(weight + 10 * Math.Log2(1 + Math.Max(0, count)) + 5 * Math.Min(Math.Max(0, distinctBots), 20) + recent, 2);
    }
}

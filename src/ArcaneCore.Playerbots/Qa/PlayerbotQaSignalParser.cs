using System.Globalization;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Turns the warning lines of <see cref="ManagedPlayerbotFeature"/> and the creature scripts into <see cref="QaSignal"/>s. Pure and
/// thread-safe (invariant culture, source-generated regexes with no backtracking blow-up: every pattern is anchored and uses bounded classes).
/// The formats are the log templates of ManagedPlayerbotFeature.cs (stall, set-aside, fault, quarantine, disabled),
/// <see cref="PlayerbotStallWatch"/> (the stall and death-loop text) and CreatureMapSystem.RelayScripts.cs / RelayCommands.cs (relay lines);
/// the contract tests build the real strings and parse them, so rewording one fails a test instead of blinding QA.
/// </summary>
internal static partial class PlayerbotQaSignalParser
{
    internal const int MaxMessageChars = 600;
    internal const int MaxDiagChars = 2048;

    /// <summary>The end of a stall line of a bot that stood outside its home (ManagedPlayerbotFeature.LogStall).</summary>
    internal const string OutsideHomeSuffix = "; it gives up that goal outside its home";

    /// <summary>
    /// Written before the <c>qa[...]</c> context of a stall of the walk home to a completed quest's ender (PlayerbotBrain.QaReportContext):
    /// the target is then the ender the bot was going to turn in at. A stall on the way to take a quest from an NPC that also ends it lacks it.
    /// </summary>
    internal const string ReturnTripToken = " return-trip";

    /// <summary>The delimiter before the single-line diagnostic snapshot appended to stall, unreachable, and fault lines.</summary>
    internal const string DiagDelimiter = "; diag: ";

    private const string Xyz = @"(?<x>-?\d{1,9}(?:\.\d{1,3})?),(?<y>-?\d{1,9}(?:\.\d{1,3})?),(?<z>-?\d{1,9}(?:\.\d{1,3})?)";

    [GeneratedRegex(@"^Playerbot (?<bot>\S+) \((?<name>[^)]*)\) stalled (?<s>\d{1,9})s: goal=(?<goal>\w{1,32}) target=(?<t>\d{1,10}) quest=(?<q>\d{1,10}) at " + Xyz + @" map (?<m>\d{1,10})",
        RegexOptions.CultureInvariant)]
    private static partial Regex StallPattern();

    /// <summary>The skip of a goal no walk reaches (PlayerbotBrain.UnreachableReport): the goal, what it names, where the bot stood and the two components.</summary>
    [GeneratedRegex(@"^Playerbot (?<bot>\S+) \((?<name>[^)]*)\) goal unreachable on foot: goal=(?<goal>\w{1,32}) target=(?<t>\d{1,10}) quest=(?<q>\d{1,10}) at " + Xyz + @" map (?<m>\d{1,10}) components=(?<c>\d{1,9}/\d{1,9})",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnreachablePattern();

    [GeneratedRegex(@"^Playerbot (?<bot>\S+) \((?<name>[^)]*)\) death loop: died (?<n>\d{1,6}) times in (?<s>\d{1,9})s within (?<yd>\d{1,5}) yd of " + Xyz + @" map (?<m>\d{1,10}) attackers=(?<a>none|\d{1,10}(?:/\d{1,10})*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex DeathLoopPattern();

    // The stall positions are optional: builds before 2026-10-11 logged the set-aside without them. Only the first is captured.
    [GeneratedRegex(@"^Playerbots: (?<n>\d{1,6}) bots stalled on (?<kind>quest|entry) (?<id>\d{1,10}) for (?<min>\d{1,9}) min; every bot sets it aside(?: \(stalls at " + Xyz + @" map (?<m>\d{1,10}))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex SetAsidePattern();

    [GeneratedRegex(@"^Playerbot (?<bot>\S+) (?<what>[A-Za-z]{1,32}) failed \((?<type>[A-Za-z0-9_.`+]{1,200}): (?<msg>.*?)\)(?: \(\d+ more since\))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FaultPattern();

    [GeneratedRegex(@"^Playerbot (?<bot>\S+) quarantined for (?<sec>\d{1,9}) s \(fault (?<n>\d{1,6}) of (?<max>\d{1,6})\): (?<fault>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex QuarantinePattern();

    [GeneratedRegex(@"^Playerbot (?<bot>\S+) disabled after (?<n>\d{1,6}) faults within (?<w>\d{1,9}) s: (?<fault>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex DisabledPattern();

    [GeneratedRegex(@"^relay script (?<r>\d{1,10}) uses unsupported (?<what>[A-Z_]{1,40}) (?:waypoint )?path (?<p>\d{1,10}) missing for entry (?<e>\d{1,10})",
        RegexOptions.CultureInvariant)]
    private static partial Regex RelayPathPattern();

    [GeneratedRegex(@"^relay script (?<r>\d{1,10}) uses unsupported (?<what>.{1,200}?); that step is skipped",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RelayUnsupportedPattern();

    // CreatureMapSystem.ReportFormationStep: a command 51 formation subcommand the server cannot run ("... {What}; that step does nothing").
    [GeneratedRegex(@"^relay script (?<r>\d{1,10}) command 51 subcommand (?<sub>\d{1,10}): (?<what>.{1,200}?); that step does nothing",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RelayFormationPattern();

    [GeneratedRegex(@"^relay script (?<r>\d{1,10}) does not exist", RegexOptions.CultureInvariant)]
    private static partial Regex RelayMissingPattern();

    [GeneratedRegex(@"playerbot-[a-z0-9]+(?:-[a-z0-9]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex FaultCodePattern();

    /// <summary>
    /// Parse one formatted warning line. False for any line QA does not know (the normal case for most warnings). <paramref name="botKey"/> is the
    /// bot id taken from the log state when the caller has it; otherwise the id in the text is used.
    /// </summary>
    internal static bool TryParse(string? message, DateTime atUtc, Guid? botKey, out QaSignal? signal)
    {
        signal = null;
        if (string.IsNullOrEmpty(message) || message.Length > 4096) return false;
        char first = message[0];
        if (first == 'P')
        {
            return message.StartsWith("Playerbots: ", StringComparison.Ordinal) ? TrySetAside(message, atUtc, out signal)
                : message.StartsWith("Playerbot ", StringComparison.Ordinal) && TryBot(message, atUtc, botKey, out signal);
        }

        return first == 'r' && message.StartsWith("relay script ", StringComparison.Ordinal) && TryRelay(message, atUtc, out signal);
    }

    private static bool TryBot(string rawMessage, DateTime atUtc, Guid? botKey, out QaSignal? signal)
    {
        signal = null;
        string message = rawMessage;
        string? diag = null;
        int diagIdx = rawMessage.IndexOf(DiagDelimiter, StringComparison.Ordinal);
        if (diagIdx >= 0)
        {
            string beforeDiag = rawMessage[..diagIdx];
            if (!beforeDiag.Contains(" failed (", StringComparison.Ordinal) || beforeDiag.EndsWith(')'))
            {
                diag = CapDiag(rawMessage[(diagIdx + DiagDelimiter.Length)..].Trim());
                if (diag.Length == 0) diag = null;
                message = beforeDiag;
            }
        }

        Match m = StallPattern().Match(message);
        if (m.Success)
        {
            if (!Position(m, out float x, out float y, out float z) || !U(m, "t", out uint target) || !U(m, "q", out uint quest)
                || !U(m, "m", out uint map) || !I(m, "s", out int seconds)) return false;
            int qa = message.IndexOf(" qa[", StringComparison.Ordinal); // the anchor QaBotObservation.FromLog uses
            signal = new QaSignal
            {
                Kind = QaSignalKind.Stall, AtUtc = atUtc, BotKey = Key(m, botKey), Goal = m.Groups["goal"].Value, Target = target, Quest = quest,
                HasPosition = true, Map = map, X = x, Y = y, Z = z, Seconds = seconds, Message = Clean(message, m),
                Bot = QaBotObservation.FromLog(message),
                OutsideHome = message.EndsWith(OutsideHomeSuffix, StringComparison.Ordinal),
                ReturnTrip = qa >= 0 && message.AsSpan(0, qa).EndsWith(ReturnTripToken, StringComparison.Ordinal),
                Diag = diag,
            };
            return true;
        }

        m = UnreachablePattern().Match(message);
        if (m.Success)
        {
            if (!Position(m, out float x, out float y, out float z) || !U(m, "t", out uint target) || !U(m, "q", out uint quest) || !U(m, "m", out uint map)) return false;
            signal = new QaSignal
            {
                Kind = QaSignalKind.UnreachableGoal, AtUtc = atUtc, BotKey = Key(m, botKey), Goal = m.Groups["goal"].Value, Target = target, Quest = quest,
                HasPosition = true, Map = map, X = x, Y = y, Z = z, Detail = "components " + m.Groups["c"].Value, Message = Clean(message, m),
                Bot = QaBotObservation.FromLog(message),
                Diag = diag,
            };
            return true;
        }

        m = DeathLoopPattern().Match(message);
        if (m.Success)
        {
            if (!Position(m, out float x, out float y, out float z) || !U(m, "m", out uint map) || !I(m, "s", out int seconds) || !I(m, "n", out int deaths)) return false;
            var attackers = new List<uint>();
            string list = m.Groups["a"].Value;
            if (list != "none")
            {
                foreach (string part in list.Split('/'))
                {
                    if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint entry)) return false;
                    attackers.Add(entry);
                }
            }

            QaBotObservation? observation = QaBotObservation.FromLog(message);
            signal = new QaSignal
            {
                Kind = QaSignalKind.DeathLoop, AtUtc = atUtc, BotKey = Key(m, botKey), HasPosition = true, Map = map, X = x, Y = y, Z = z,
                Attackers = [.. attackers.Distinct().Order()], Seconds = seconds, Count = deaths, Message = Clean(message, m),
                Bot = observation, Goal = observation?.Goal ?? string.Empty, Quest = observation?.Quest ?? 0,
                Diag = diag,
            };
            return true;
        }

        m = QuarantinePattern().Match(message);
        if (m.Success)
        {
            string fault = m.Groups["fault"].Value;
            signal = new QaSignal
            {
                Kind = QaSignalKind.Quarantine, AtUtc = atUtc, BotKey = Key(m, botKey), Detail = Cap(fault), FaultCode = CodeOf(fault),
                Count = I(m, "n", out int n) ? n : 0, Message = Clean(message, m),
                Diag = diag,
            };
            return true;
        }

        m = DisabledPattern().Match(message);
        if (m.Success)
        {
            string fault = m.Groups["fault"].Value;
            signal = new QaSignal
            {
                Kind = QaSignalKind.Disabled, AtUtc = atUtc, BotKey = Key(m, botKey), Detail = Cap(fault), FaultCode = CodeOf(fault),
                Count = I(m, "n", out int n) ? n : 0, Message = Clean(message, m),
                Diag = diag,
            };
            return true;
        }

        m = FaultPattern().Match(message);
        if (m.Success)
        {
            string text = m.Groups["msg"].Value;
            string cleanLine = message;
            if (diag is null)
            {
                int innerDiagIdx = text.IndexOf(DiagDelimiter, StringComparison.Ordinal);
                if (innerDiagIdx >= 0)
                {
                    diag = CapDiag(text[(innerDiagIdx + DiagDelimiter.Length)..].Trim());
                    if (diag.Length == 0) diag = null;
                    string cleanMsg = text[..innerDiagIdx];
                    Group msgGroup = m.Groups["msg"];
                    cleanLine = message[..msgGroup.Index] + cleanMsg + message[(msgGroup.Index + msgGroup.Length)..];
                    text = cleanMsg;
                }
            }

            signal = new QaSignal
            {
                Kind = QaSignalKind.ActionFault, AtUtc = atUtc, BotKey = Key(m, botKey), Goal = m.Groups["what"].Value,
                ExceptionType = m.Groups["type"].Value, Detail = Cap(text), FaultCode = CodeOf(text), Message = Clean(cleanLine, m),
                Diag = diag,
            };
            return true;
        }

        return false;
    }

    private static bool TrySetAside(string message, DateTime atUtc, out QaSignal? signal)
    {
        signal = null;
        Match m = SetAsidePattern().Match(message);
        if (!m.Success || !U(m, "id", out uint id) || !I(m, "min", out int minutes) || !I(m, "n", out int bots)) return false;
        bool quest = m.Groups["kind"].Value == "quest";
        float x = 0, y = 0, z = 0;
        uint map = 0;
        bool where = m.Groups["m"].Success && Position(m, out x, out y, out z) && U(m, "m", out map);
        signal = new QaSignal
        {
            Kind = QaSignalKind.SharedSetAside, AtUtc = atUtc, BotCount = bots, Goal = quest ? "Quest" : "Entry",
            Quest = quest ? id : 0, Target = quest ? 0 : id, Minutes = minutes, Message = Cap(message),
            HasPosition = where, Map = where ? map : 0, X = where ? x : 0, Y = where ? y : 0, Z = where ? z : 0,
        };
        return true;
    }

    private static bool TryRelay(string message, DateTime atUtc, out QaSignal? signal)
    {
        signal = null;
        Match m = RelayPathPattern().Match(message);
        if (m.Success)
        {
            if (!U(m, "r", out uint relay) || !U(m, "p", out uint path) || !U(m, "e", out uint entry)) return false;
            signal = new QaSignal
            {
                Kind = QaSignalKind.RelayDataMissing, AtUtc = atUtc, Relay = relay, PathId = path, Entry = entry,
                Detail = m.Groups["what"].Value, Message = Cap(message),
            };
            return true;
        }

        m = RelayUnsupportedPattern().Match(message);
        if (m.Success)
        {
            if (!U(m, "r", out uint relay)) return false;
            signal = new QaSignal { Kind = QaSignalKind.RelayDataMissing, AtUtc = atUtc, Relay = relay, Detail = Cap(m.Groups["what"].Value), Message = Cap(message) };
            return true;
        }

        m = RelayFormationPattern().Match(message);
        if (m.Success)
        {
            if (!U(m, "r", out uint relay)) return false;
            signal = new QaSignal
            {
                Kind = QaSignalKind.RelayDataMissing, AtUtc = atUtc, Relay = relay,
                Detail = Cap("command 51 subcommand " + m.Groups["sub"].Value + ": " + m.Groups["what"].Value), Message = Cap(message),
            };
            return true;
        }

        m = RelayMissingPattern().Match(message);
        if (m.Success && U(m, "r", out uint missing))
        {
            signal = new QaSignal { Kind = QaSignalKind.RelayScriptMissing, AtUtc = atUtc, Relay = missing, Message = Cap(message) };
            return true;
        }

        return false;
    }

    private static Guid? Key(Match m, Guid? fallback)
        => Guid.TryParse(m.Groups["bot"].Value, out Guid id) ? id : fallback;

    /// <summary>The message with the bot id and the "(Name)" segment taken out: <c>Playerbot &lt;bot&gt; stalled ...</c>.</summary>
    private static string Clean(string message, Match m)
    {
        Group bot = m.Groups["bot"];
        int end = bot.Index + bot.Length;
        Group name = m.Groups["name"];
        if (name.Success)
        {
            // "Playerbot <id> (<name>) rest": drop both, keep the rest.
            int rest = name.Index + name.Length + 1;
            return Cap("Playerbot <bot>" + message[rest..]);
        }

        return Cap("Playerbot <bot>" + message[end..]);
    }

    private static string Cap(string text) => text.Length <= MaxMessageChars ? text : text[..MaxMessageChars];

    private static string CapDiag(string text) => text.Length <= MaxDiagChars ? text : text[..MaxDiagChars];

    private static string CodeOf(string text) => FaultCodePattern().Match(text) is { Success: true } code ? code.Value : string.Empty;

    private static bool U(Match m, string group, out uint value)
        => uint.TryParse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool I(Match m, string group, out int value)
        => int.TryParse(m.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool Position(Match m, out float x, out float y, out float z)
    {
        x = y = z = 0;
        return float.TryParse(m.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out x) && float.IsFinite(x)
            && float.TryParse(m.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out y) && float.IsFinite(y)
            && float.TryParse(m.Groups["z"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out z) && float.IsFinite(z);
    }
}

using System.Globalization;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>Facts observed from a creature while it threatened a bot, including the combat reaction at that moment.</summary>
internal sealed record QaAttackerFact(uint Entry, byte Level, byte MinLevel, byte MaxLevel, uint Rank, bool Guard, bool Hostile);

/// <summary>Per occurrence context. It is evidence, never part of the finding fingerprint or publication threshold.</summary>
internal sealed record QaBotObservation(int Level, string Race, string Team, string Class, string Goal, uint Quest,
    uint Graveyard, float? GraveyardDistance, string Source, IReadOnlyList<QaAttackerFact> AttackerFacts,
    IReadOnlyList<string>? DeathSources = null, string? TurnInRefusal = null)
{
    internal string ToLogSuffix()
    {
        string facts = AttackerFacts.Count == 0 ? "none" : string.Join('/', AttackerFacts.OrderBy(a => a.Entry).Select(a =>
            string.Create(CultureInfo.InvariantCulture, $"{a.Entry}:{a.Level}:{a.MinLevel}:{a.MaxLevel}:{a.Rank}:{(a.Guard ? 1 : 0)}:{(a.Hostile ? 1 : 0)}")));
        string distance = GraveyardDistance is { } yards ? yards.ToString("F1", CultureInfo.InvariantCulture) : "unknown";
        string sources = DeathSources is { Count: > 0 } ? ",deathSources=" + string.Join('/', DeathSources) : string.Empty;
        string refusal = TurnInRefusal is { Length: > 0 } && Word(TurnInRefusal) ? ",turnInRefusal=" + TurnInRefusal : string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $" qa[level={Level},race={Race},team={Team},class={Class},goal={Goal},quest={Quest},graveyard={Graveyard},distance={distance},source={Source},attackerFacts={facts}{sources}{refusal}]");
    }

    internal static QaBotObservation? FromLog(string message)
    {
        int start = message.IndexOf(" qa[", StringComparison.Ordinal);
        if (start < 0) return null; // warnings from older servers retain their original meaning
        int end = message.IndexOf(']', start + 4);
        if (end < 0 || end - start > 1024) return null;
        string[] fields = message[(start + 4)..end].Split(',');
        if (fields.Length is < 10 or > 12 || !Field(fields[0], "level", out string? levelText)
            || !Field(fields[1], "race", out string? race) || !Word(race)
            || !Field(fields[2], "team", out string? team) || team is not ("Alliance" or "Horde")
            || !Field(fields[3], "class", out string? @class) || !Word(@class)
            || !Field(fields[4], "goal", out string? goal) || !Word(goal)
            || !Field(fields[5], "quest", out string? questText)
            || !Field(fields[6], "graveyard", out string? graveyardText)
            || !Field(fields[7], "distance", out string? distanceText)
            || !Field(fields[8], "source", out string? source) || !Word(source)
            || !Field(fields[9], "attackerFacts", out string? factsText, 512)
            || !int.TryParse(levelText, NumberStyles.None, CultureInfo.InvariantCulture, out int level) || level is < 1 or > 255
            || !uint.TryParse(questText, NumberStyles.None, CultureInfo.InvariantCulture, out uint quest)
            || !uint.TryParse(graveyardText, NumberStyles.None, CultureInfo.InvariantCulture, out uint graveyard)) return null;
        float? distance = null;
        if (distanceText != "unknown")
        {
            if (!float.TryParse(distanceText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float yards)
                || !float.IsFinite(yards) || yards < 0) return null;
            distance = yards;
        }

        var facts = new List<QaAttackerFact>();
        if (factsText != "none")
        {
            foreach (string part in factsText.Split('/'))
            {
                string[] values = part.Split(':');
                if (facts.Count >= 16 || values.Length != 7
                    || !uint.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint entry)
                    || !byte.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte actual)
                    || !byte.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out byte min)
                    || !byte.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out byte max)
                    || !uint.TryParse(values[4], NumberStyles.None, CultureInfo.InvariantCulture, out uint rank)
                    || values[5] is not ("0" or "1") || values[6] is not ("0" or "1")) return null;
                facts.Add(new QaAttackerFact(entry, actual, min, max, rank, values[5] == "1", values[6] == "1"));
            }
        }

        List<string>? sources = null;
        string? turnInRefusal = null;
        for (int i = 10; i < fields.Length; i++)
        {
            if (Field(fields[i], "deathSources", out string? sourceList, 512))
            {
                if (sources is not null) return null;
                sources = [.. sourceList.Split('/')];
                if (sources.Count is < 1 or > 16 || sources.Any(s => !Word(s))) return null;
            }
            else if (Field(fields[i], "turnInRefusal", out string? refusalText, 32))
            {
                if (turnInRefusal is not null || !Word(refusalText)) return null;
                turnInRefusal = refusalText;
            }
            else
            {
                return null;
            }
        }

        return new QaBotObservation(level, race, team, @class, goal, quest, graveyard, distance, source, facts, sources, turnInRefusal);
    }

    private static bool Field(string text, string name, out string value, int maxLength = 32)
    {
        string prefix = name + "=";
        value = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : string.Empty;
        return value.Length > 0 && value.Length <= maxLength;
    }

    private static bool Word(string text) => text.Length is > 0 and <= 32 && text.All(char.IsAsciiLetter);
}

using System.Globalization;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots;

/// <summary>How a bulk playerbot command names its bots.</summary>
internal enum FleetSelectorKind
{
    /// <summary>One bot by (case-insensitive) character name.</summary>
    Name,

    /// <summary>One bot by id.</summary>
    Guid,

    /// <summary>Every bot whose name starts with the letters before a trailing '*'.</summary>
    Prefix,

    /// <summary>Every registered bot.</summary>
    All,
}

/// <summary>Which argument shape a bulk command takes after its selector.</summary>
internal enum FleetVerb
{
    /// <summary><c>[#count] [force] [to $player]</c></summary>
    Summon,

    /// <summary><c>$tele|here|at $map $x $y $z [force] [to $player]</c> (<c>at</c> takes no <c>to</c>)</summary>
    Send,

    /// <summary><c>[force]</c> (start, stop, revive, dismiss)</summary>
    Plain,

    /// <summary><c>[to $player]</c> (follow, stay)</summary>
    Leash,

    /// <summary><c>here #radius [force]</c>, <c>at $map $x $y $z #radius [force]</c> or <c>clear</c> (defend)</summary>
    Defend,

    /// <summary><c>#entry [raids 1|2] [force]</c> (siege start)</summary>
    Siege,
}

/// <summary>
/// A parsed bulk command line. <see cref="Head"/> is the tele name of <c>send</c> (the words between selector and tail), or <c>here</c>,
/// <c>at</c> or <c>clear</c>. <see cref="At"/> holds the four words (<c>$map $x $y $z</c>) of an <c>at</c> form, unvalidated (null otherwise).
/// </summary>
internal sealed record FleetRequest(FleetSelectorKind Kind, string Text, Guid BotId, int? Count, bool Force, string? ToPlayer, string Head, string RawTail, float Radius = 0f, uint Entry = 0, int Raids = 1, string[]? At = null)
{
    /// <summary>The selector as typed, for messages.</summary>
    public string Selector => Kind == FleetSelectorKind.Prefix ? Text + "*" : Text;
}

/// <summary>One matched bot as the ordering sees it: on the target's map (and how far) or elsewhere.</summary>
internal readonly record struct FleetCandidate(Guid BotId, string Name, bool SameMap, float Distance);

/// <summary>
/// The selector grammar of the bulk playerbot commands, apart from the world so it can be tested alone. Candidates come from
/// <see cref="IPlayerbotService.Snapshot"/>, which holds registered managed bots only: a real player can never match, whatever the
/// prefix. Pure; any thread.
/// </summary>
internal static class PlayerbotFleetSelection
{
    /// <summary>Whether the first word of <paramref name="text"/> selects many bots (<c>all</c> or <c>Prefix*</c>).</summary>
    internal static bool IsMulti(string text)
    {
        string first = FirstWord(text);
        return first.Equals("all", StringComparison.OrdinalIgnoreCase) || first.EndsWith('*');
    }

    private static string FirstWord(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimStart();
        int end = span.IndexOf(' ');
        return (end < 0 ? span : span[..end]).ToString();
    }

    /// <summary>
    /// Parse <c>selector tail</c>. The selector is <c>all</c>, <c>Letters*</c> (letters only, one trailing star: a bare star or a
    /// star inside a word is refused), a bot id or a character name. The tail depends on <paramref name="verb"/>; false for any
    /// shape that does not fit (the command then shows its help).
    /// </summary>
    internal static bool TryParse(string text, FleetVerb verb, out FleetRequest request)
    {
        request = null!;
        string[] words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        string selector = words[0];
        FleetSelectorKind kind;
        string name = selector;
        Guid id = Guid.Empty;
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase)) { kind = FleetSelectorKind.All; name = "all"; }
        else if (selector.Contains('*', StringComparison.Ordinal))
        {
            if (!selector.EndsWith('*')) return false;
            name = selector[..^1];
            if (name.Length == 0 || !name.All(char.IsLetter)) return false;
            kind = FleetSelectorKind.Prefix;
        }
        else if (Guid.TryParse(selector, out id)) kind = FleetSelectorKind.Guid;
        else kind = FleetSelectorKind.Name;

        List<string> tail = [.. words.Skip(1)];
        string raw = string.Join(' ', tail);
        string? to = null;
        if (verb != FleetVerb.Plain && tail.Count >= 2 && tail[^2].Equals("to", StringComparison.OrdinalIgnoreCase))
        {
            to = tail[^1];
            tail.RemoveRange(tail.Count - 2, 2);
        }

        bool force = false;
        if (verb != FleetVerb.Leash && tail.Count > 0 && tail[^1].Equals("force", StringComparison.OrdinalIgnoreCase))
        {
            force = true;
            tail.RemoveAt(tail.Count - 1);
        }

        int? count = null;
        string head = string.Empty;
        float radius = 0f;
        uint entry = 0;
        int raids = 1;
        string[]? at = null;
        switch (verb)
        {
            case FleetVerb.Siege:
                if (to is not null || tail.Count is not (1 or 3)) return false;
                if (!uint.TryParse(tail[0], NumberStyles.None, CultureInfo.InvariantCulture, out entry) || entry == 0) return false;
                if (tail.Count == 3
                    && (!tail[1].Equals("raids", StringComparison.OrdinalIgnoreCase)
                        || !int.TryParse(tail[2], NumberStyles.None, CultureInfo.InvariantCulture, out raids) || raids is < 1 or > 2))
                    return false;
                break;
            case FleetVerb.Summon:
                if (tail.Count > 1) return false;
                if (tail.Count == 1)
                {
                    if (!int.TryParse(tail[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1) return false;
                    count = n;
                }

                break;
            case FleetVerb.Send:
                if (tail.Count == 0) return false;
                if (IsAtWord(tail[0]))
                {
                    // "at $map $x $y $z": the numbers are checked by the command, which says what is wrong with them.
                    if (to is not null || tail.Count != 5) return false;
                    head = "at";
                    at = [.. tail.Skip(1)];
                    break;
                }

                head = string.Join(' ', tail);
                break;
            case FleetVerb.Defend:
                if (to is not null) return false;
                if (tail.Count == 1 && tail[0].Equals("clear", StringComparison.OrdinalIgnoreCase)) head = "clear";
                else if (tail.Count == 6 && IsAtWord(tail[0])
                    && float.TryParse(tail[5], NumberStyles.Float, CultureInfo.InvariantCulture, out radius) && float.IsFinite(radius) && radius > 0f)
                {
                    head = "at";
                    at = [.. tail.Skip(1).Take(4)];
                }
                else if (tail.Count == 2 && tail[0].Equals("here", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(tail[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius) && float.IsFinite(radius) && radius > 0f)
                    head = "here";
                else return false;
                break;
            default:
                if (tail.Count != 0) return false;
                break;
        }

        request = new FleetRequest(kind, name, id, count, force, to, head, raw, radius, entry, raids, at);
        return true;
    }

    /// <summary>Whether <paramref name="word"/> is the keyword that starts an explicit position, <c>at</c>.</summary>
    internal static bool IsAtWord(string word) => word.Equals("at", StringComparison.OrdinalIgnoreCase);

    /// <summary>The registered bots the selector names, by name. Empty when none does (the command then fails closed).</summary>
    internal static List<PlayerbotStatus> Match(FleetRequest request, IReadOnlyList<PlayerbotStatus> snapshot)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        var found = new List<PlayerbotStatus>();
        foreach (PlayerbotStatus status in snapshot)
        {
            bool hit = request.Kind switch
            {
                FleetSelectorKind.All => true,
                FleetSelectorKind.Guid => status.BotId == request.BotId,
                FleetSelectorKind.Prefix => status.Name.StartsWith(request.Text, StringComparison.OrdinalIgnoreCase),
                _ => status.Name.Equals(request.Text, StringComparison.OrdinalIgnoreCase),
            };
            if (hit) found.Add(status);
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>The bots on the target's map first, nearest first, then the others by name.</summary>
    internal static List<FleetCandidate> Order(IEnumerable<FleetCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return [.. candidates
            .OrderBy(c => c.SameMap ? 0 : 1)
            .ThenBy(c => c.SameMap ? c.Distance : 0f)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Whether a selection is too big to act on without <c>force</c>: the bots it will touch (the count when one is given, else every
    /// match) exceed <paramref name="cap"/> (<see cref="PlayerbotOptions.MaxBulkCommand"/>).
    /// </summary>
    internal static bool NeedsForce(int matched, int? count, bool force, int cap)
        => !force && (count ?? matched) > cap;

    /// <summary>The refusal text of <see cref="NeedsForce"/>.</summary>
    internal static string ForceMessage(string verb, int matched, int cap)
        => string.Create(CultureInfo.InvariantCulture, $"Playerbot {verb}: {matched} bots match; give a count <= {cap} or add 'force'.");

    /// <summary>The message when the selector names no registered bot.</summary>
    internal static string NothingMatches(FleetRequest request) => $"No playerbot matches '{request.Selector}'.";
}

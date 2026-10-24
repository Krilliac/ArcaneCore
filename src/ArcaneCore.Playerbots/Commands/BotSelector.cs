using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots.Control;

namespace ArcaneCore.World.Playerbots.Commands;

/// <summary>One bot a selector named.</summary>
public readonly record struct BotPick(Guid BotId, string Name);

/// <summary>The live attributes of a bot that <c>where:</c> filters read; a null member is unknown (the bot is not in the world).</summary>
public readonly record struct BotAttributes(uint MapId, uint? Zone, uint? Level, string? Class, string? Race);

/// <summary>
/// The bots a selector can name: a snapshot of the known bots plus the lookups the grammar needs. The production source is
/// <see cref="FeatureBotSelectorSource"/>; tests supply their own. World thread (the live attributes read players).
/// </summary>
public interface IBotSelectorSource
{
    /// <summary>Every known bot (running or not).</summary>
    IReadOnlyList<PlayerbotStatus> Snapshot();

    /// <summary>A bot by id (a guid) or exact name.</summary>
    bool TryResolve(string idOrName, out Guid botId, out string name);

    bool IsRunning(Guid botId);

    /// <summary>Whether the bot is under <c>.botctl</c> control.</summary>
    bool IsControlled(Guid botId);

    /// <summary>The bots under <c>.botctl</c> control, by the controllers (so a controlled bot missing from <see cref="Snapshot"/> is still named).</summary>
    IReadOnlyList<BotPick> Controlled();

    /// <summary>The members of the named bot alias (<c>@alias</c>), or null when there is none.</summary>
    IReadOnlyList<string>? Alias(string name);

    BotAttributes Attributes(PlayerbotStatus bot);
}

/// <summary>The outcome of <see cref="BotSelector.TryResolve"/>.</summary>
/// <param name="Bots">The named bots, in selector order, each once.</param>
/// <param name="Broad">The selector named bots by a rule, not one at a time (<c>all</c>, <c>where:</c>, <c>running</c>, <c>controlled</c>, <c>@alias</c>, a <c>Prefix*</c>): writes against it need a confirm token (I7).</param>
public sealed record BotSelection(IReadOnlyList<BotPick> Bots, bool Broad);

/// <summary>
/// The bot selector grammar (I11), shared by <c>.playerbot</c> and <c>.botctl</c>: <c>$id | $name | $prefix* | all | where:k=v[&amp;k=v]</c>,
/// plus the botctl forms <c>controlled</c> (alias <c>mine</c>), <c>running</c>, <c>@alias</c> and comma lists of any of them. A leading
/// <c>$</c> on a part is optional (botctl has always taken bare names). Failures are <c>code: message</c> strings (<c>bad-selector</c>,
/// <c>no-match</c>, <c>no-alias</c>), the format <c>.botctl</c> reports. <c>where:</c> keys: map, zone, level (n or a-b), class, race,
/// controlled (0/1), running (0/1), state, goal, name (contains).
/// </summary>
public static class BotSelector
{
    /// <summary>How deep aliases may nest.</summary>
    public const int MaxAliasDepth = 3;

    /// <summary>The filter keys of <c>where:</c>.</summary>
    public static IReadOnlyList<string> FilterKeys { get; } = ["map", "zone", "level", "class", "race", "controlled", "running", "state", "goal", "name"];

    /// <summary>
    /// Resolve <paramref name="selector"/> against <paramref name="source"/>. Returns null with the selection set, else the error. A selector
    /// that matches no bot is an error (<c>no-match</c>), so a write never silently does nothing.
    /// </summary>
    public static string? TryResolve(string? selector, IBotSelectorSource source, out BotSelection selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bots = new List<BotPick>();
        bool broad = false;
        selection = new BotSelection([], false);
        string text = (selector ?? string.Empty).Trim();
        string? error = Resolve(source, text, bots, ref broad, 0);
        if (error is not null)
        {
            return error;
        }

        if (bots.Count == 0)
        {
            return "no-match: no playerbot matches '" + text + "'";
        }

        selection = new BotSelection(bots, broad);
        return null;
    }

    private static string? Resolve(IBotSelectorSource source, string selector, List<BotPick> bots, ref bool broad, int depth)
    {
        IReadOnlyList<PlayerbotStatus> snapshot = source.Snapshot();
        var seen = new HashSet<Guid>(bots.Select(b => b.BotId));

        void Add(Guid id, string name)
        {
            if (seen.Add(id)) bots.Add(new BotPick(id, name));
        }

        IEnumerable<PlayerbotStatus> ByName() => snapshot.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

        foreach (string raw in selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // A '$' part is a name (plan 1.3): it never means a keyword, an alias or a filter, only an id, a name or a prefix*.
            bool literal = raw.StartsWith('$');
            string part = literal ? raw[1..] : raw;
            if (part.Length == 0)
            {
                return "bad-selector: '$' needs a bot name or id after it";
            }

            if (literal)
            {
                if (part.EndsWith('*'))
                {
                    broad = true;
                    string namePrefix = part[..^1];
                    if (namePrefix.Length == 0) return "bad-selector: a prefix needs at least one letter before '*'";
                    foreach (PlayerbotStatus s in ByName().Where(s => s.Name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))) Add(s.BotId, s.Name);
                }
                else if (source.TryResolve(part, out Guid literalId, out string literalName))
                {
                    Add(literalId, literalName);
                }
                else
                {
                    return "no-match: no playerbot '" + part + "'";
                }
            }
            else if (part.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                broad = true;
                foreach (PlayerbotStatus s in ByName()) Add(s.BotId, s.Name);
            }
            else if (part.Equals("controlled", StringComparison.OrdinalIgnoreCase) || part.Equals("mine", StringComparison.OrdinalIgnoreCase))
            {
                broad = true;
                foreach (BotPick c in source.Controlled().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)) Add(c.BotId, c.Name);
            }
            else if (part.Equals("running", StringComparison.OrdinalIgnoreCase))
            {
                broad = true;
                foreach (PlayerbotStatus s in ByName().Where(s => source.IsRunning(s.BotId))) Add(s.BotId, s.Name);
            }
            else if (part.StartsWith('@'))
            {
                broad = true;
                if (depth > MaxAliasDepth) return "bad-selector: aliases nest too deep";
                if (source.Alias(part[1..]) is not { } members) return "no-alias: no bot alias '" + part + "' (.botctl alias)";
                foreach (string member in members)
                {
                    bool innerBroad = false;
                    string? inner = Resolve(source, member, bots, ref innerBroad, depth + 1);
                    if (inner is not null) return inner;
                }

                seen = [.. bots.Select(b => b.BotId)];
            }
            else if (part.StartsWith("where:", StringComparison.OrdinalIgnoreCase))
            {
                broad = true;
                string? problem = Filter(source, snapshot, part[6..], Add);
                if (problem is not null) return problem;
            }
            else if (part.EndsWith('*'))
            {
                broad = true;
                string prefix = part[..^1];
                if (prefix.Length == 0) return "bad-selector: a prefix needs at least one letter before '*'";
                foreach (PlayerbotStatus s in ByName().Where(s => s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) Add(s.BotId, s.Name);
            }
            else if (source.TryResolve(part, out Guid id, out string name))
            {
                Add(id, name);
            }
            else
            {
                return "no-match: no playerbot '" + part + "'";
            }
        }

        return null;
    }

    private static string? Filter(IBotSelectorSource source, IReadOnlyList<PlayerbotStatus> snapshot, string filter, Action<Guid, string> add)
    {
        var terms = new List<(string Key, string Value)>();
        foreach (string term in filter.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = term.IndexOf('=');
            if (eq <= 0) return "bad-selector: filter term '" + term + "' is not key=value";
            string key = term[..eq].ToLowerInvariant();
            if (!FilterKeys.Contains(key))
                return "bad-selector: unknown filter key '" + key + "' (" + string.Join(", ", FilterKeys) + ")";
            terms.Add((key, term[(eq + 1)..]));
        }

        foreach (PlayerbotStatus status in snapshot.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            BotAttributes attributes = source.Attributes(status);
            bool all = true;
            foreach ((string key, string value) in terms)
            {
                bool match = key switch
                {
                    "map" => Num(value, attributes.MapId),
                    "zone" => attributes.Zone is { } zone && Num(value, zone),
                    "level" => attributes.Level is { } level && Range(value, level),
                    "class" => attributes.Class is { } cls && cls.Equals(value, StringComparison.OrdinalIgnoreCase),
                    "race" => attributes.Race is { } race && race.Equals(value, StringComparison.OrdinalIgnoreCase),
                    "controlled" => source.IsControlled(status.BotId) == Flag(value),
                    "running" => source.IsRunning(status.BotId) == Flag(value),
                    "state" => status.State.ToString().Equals(value, StringComparison.OrdinalIgnoreCase),
                    "goal" => status.Goal.ToString().Equals(value, StringComparison.OrdinalIgnoreCase),
                    "name" => status.Name.Contains(value, StringComparison.OrdinalIgnoreCase),
                    _ => throw new FormatException(key),
                };
                if (!match)
                {
                    all = false;
                    break;
                }
            }

            if (all) add(status.BotId, status.Name);
        }

        return null;

        static bool Flag(string value) => value is "1" or "true" or "yes" or "on";
        static bool Num(string value, uint actual) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint n) && n == actual;
        static bool Range(string value, uint actual)
        {
            int dash = value.IndexOf('-');
            if (dash < 0) return Num(value, actual);
            return uint.TryParse(value[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out uint low)
                && uint.TryParse(value[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out uint high)
                && actual >= low && actual <= high;
        }
    }
}

/// <summary>
/// The production <see cref="IBotSelectorSource"/>: the playerbot feature's bots, and (when given) the <c>.botctl</c> hub's controlled
/// bots and aliases. Without a hub nothing is controlled and no alias exists.
/// </summary>
internal sealed class FeatureBotSelectorSource(ManagedPlayerbotFeature feature, BotControlHub? hub = null) : IBotSelectorSource
{
    public IReadOnlyList<PlayerbotStatus> Snapshot() => feature.Snapshot();

    public bool TryResolve(string idOrName, out Guid botId, out string name) => feature.TryResolveBotId(idOrName, out botId, out name);

    public bool IsRunning(Guid botId) => feature.IsActive(botId);

    public bool IsControlled(Guid botId) => hub?.Find(botId) is not null;

    public IReadOnlyList<BotPick> Controlled() => hub is null ? [] : [.. hub.Controlled.Select(c => new BotPick(c.BotId, c.Name))];

    public IReadOnlyList<string>? Alias(string name) => hub?.Group(name);

    public BotAttributes Attributes(PlayerbotStatus bot)
    {
        Player? player = feature.FindSession(bot.BotId)?.Player;
        if (player is null)
        {
            return new BotAttributes(bot.MapId, null, null, null, null);
        }

        uint? zone = player.Map is not null ? feature.ZoneAt(player.MapId, new(player.X, player.Y, player.Z)) : null;
        return new BotAttributes(player.MapId, zone, player.Level, player.Class.ToString(), player.Race.ToString());
    }
}

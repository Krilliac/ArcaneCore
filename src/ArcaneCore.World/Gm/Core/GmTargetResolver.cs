using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Core;

/// <summary>The form of a target selector (docs: debug-commands plan, section 1.3).</summary>
public enum GmSelectorKind
{
    /// <summary>Omitted: the selected unit, then the issuer.</summary>
    Default,

    /// <summary><c>$name</c>: an online player or bot.</summary>
    Name,

    /// <summary><c>0xHEX</c>: any world object by full guid.</summary>
    Guid,

    /// <summary><c>guid #low [map #m]</c>: a creature by guid counter on the issuer's map (or map #m).</summary>
    Counter,

    /// <summary><c>spawn #n</c>: a creature or game object by database spawn guid, searched across loaded maps.</summary>
    Spawn,

    /// <summary><c>entry #n [map #m]</c>: the nearest instance of a template entry (to the issuer, or the first one on map #m from the console).</summary>
    Entry,

    /// <summary><c>nearest [#yards] [creature|gobject|player|transport]</c>.</summary>
    Nearest,

    /// <summary><c>offline $name</c>: a character record that is not online (commands that say they accept it; Administrator).</summary>
    Offline,
}

/// <summary>A parsed target selector. Pure data: parsing touches no world state (<see cref="TryParse"/>, <see cref="TryTakeLeading"/>).</summary>
/// <param name="Kind">The selector form.</param>
/// <param name="Text">The selector as typed (for replies), empty for <see cref="GmSelectorKind.Default"/>.</param>
/// <param name="Name">The normalised player name of <see cref="GmSelectorKind.Name"/> / <see cref="GmSelectorKind.Offline"/>.</param>
/// <param name="Value">The full guid (<see cref="GmSelectorKind.Guid"/>), the guid counter (<see cref="GmSelectorKind.Counter"/>) or the spawn guid (<see cref="GmSelectorKind.Spawn"/>).</param>
/// <param name="Entry">The template entry of <see cref="GmSelectorKind.Entry"/>.</param>
/// <param name="MapId">The optional <c>map #m</c> of <see cref="GmSelectorKind.Counter"/> and <see cref="GmSelectorKind.Entry"/>.</param>
/// <param name="Yards">The search radius of <see cref="GmSelectorKind.Nearest"/>.</param>
/// <param name="Kinds">The kinds <see cref="GmSelectorKind.Nearest"/> was limited to (<see cref="GmTargetKinds.Any"/> when not).</param>
public sealed record GmSelector(
    GmSelectorKind Kind,
    string Text = "",
    string Name = "",
    ulong Value = 0,
    uint Entry = 0,
    uint? MapId = null,
    float Yards = GmSelector.DefaultYards,
    GmTargetKinds Kinds = GmTargetKinds.Any)
{
    public const float DefaultYards = 30f;

    /// <summary>The largest radius <c>nearest</c> accepts.</summary>
    public const float MaxYards = 500f;

    public static GmSelector Default { get; } = new(GmSelectorKind.Default);

    /// <summary>Parse the whole of <paramref name="text"/> as one selector (empty: <see cref="Default"/>). False with the reason in <paramref name="error"/>.</summary>
    public static bool TryParse(string? text, out GmSelector selector, out string error)
    {
        if (!TryTakeLeading(text, out selector, out string rest, out error))
        {
            return false;
        }

        if (rest.Length != 0)
        {
            error = $"Unexpected '{rest}' after the target.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parse the selector that starts <paramref name="args"/>. When the first word is no selector form the result is <see cref="Default"/>
    /// and <paramref name="rest"/> is all of <paramref name="args"/> (trimmed), so a command with a leading optional target can hand the
    /// remainder to its own parser. Optional parts of a selector (<c>map #m</c>, <c>#yards</c>, a kind word) are taken only when they have
    /// the shape the grammar gives them, so a plain number after a selector stays an argument. A leading <c>#n</c> is never a selector
    /// (the plan uses <c>#n</c> for numeric arguments), so it stays in <paramref name="rest"/>.
    /// </summary>
    public static bool TryTakeLeading(string? args, out GmSelector selector, out string rest, out string error)
    {
        error = string.Empty;
        selector = Default;
        string text = (args ?? string.Empty).Trim();
        rest = text;
        if (text.Length == 0)
        {
            return true;
        }

        List<(int Start, int End)> words = Words(text);
        string first = text[words[0].Start..words[0].End];
        int used;
        switch (first.ToLowerInvariant())
        {
            case "spawn":
                if (words.Count < 2 || !TryNumber(Word(text, words, 1), out ulong spawn) || spawn == 0 || spawn > uint.MaxValue)
                {
                    error = "spawn needs a spawn guid: spawn #123";
                    return false;
                }

                selector = new GmSelector(GmSelectorKind.Spawn, Join(text, words, 2), Value: spawn);
                used = 2;
                break;

            case "guid":
                if (words.Count < 2 || !TryNumber(Word(text, words, 1), out ulong low) || low == 0 || low > MaxCounter)
                {
                    error = "guid needs a guid counter: guid #123 [map #1]";
                    return false;
                }

                used = 2;
                if (!TryTakeMap(text, words, ref used, out uint? counterMap, out error))
                {
                    return false;
                }

                selector = new GmSelector(GmSelectorKind.Counter, Join(text, words, used), Value: low, MapId: counterMap);
                break;

            case "entry":
                if (words.Count < 2 || !TryNumber(Word(text, words, 1), out ulong entry) || entry == 0 || entry > uint.MaxValue)
                {
                    error = "entry needs a template entry: entry #123 [map #1]";
                    return false;
                }

                used = 2;
                if (!TryTakeMap(text, words, ref used, out uint? mapId, out error))
                {
                    return false;
                }

                selector = new GmSelector(GmSelectorKind.Entry, Join(text, words, used), Entry: (uint)entry, MapId: mapId);
                break;

            case "nearest":
                used = 1;
                float yards = DefaultYards;
                GmTargetKinds kinds = GmTargetKinds.None;
                bool haveYards = false;
                while (used < words.Count)
                {
                    string word = Word(text, words, used);
                    if (!haveYards && word.Length > 1 && word[0] == '#' && char.IsAsciiDigit(word[1]))
                    {
                        if (!float.TryParse(word.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture, out yards) || !float.IsFinite(yards) || yards <= 0 || yards > MaxYards)
                        {
                            error = string.Create(CultureInfo.InvariantCulture, $"nearest takes a radius from 0 to {MaxYards:F0} yards: nearest #20 creature");
                            return false;
                        }

                        haveYards = true;
                    }
                    else if (kinds == GmTargetKinds.None && KindOfWord(word) is { } kind)
                    {
                        kinds = kind;
                    }
                    else
                    {
                        break;
                    }

                    used++;
                }

                selector = new GmSelector(GmSelectorKind.Nearest, Join(text, words, used), Yards: yards, Kinds: kinds == GmTargetKinds.None ? GmTargetKinds.Any : kinds);
                break;

            case "offline":
                if (words.Count < 2 || !TryName(Word(text, words, 1), out string offline))
                {
                    error = "offline needs a character name: offline $name";
                    return false;
                }

                selector = new GmSelector(GmSelectorKind.Offline, Join(text, words, 2), Name: offline);
                used = 2;
                break;

            default:
                if (first[0] == '$')
                {
                    if (!TryName(first, out string name))
                    {
                        error = $"'{first}' is not a valid player name.";
                        return false;
                    }

                    selector = new GmSelector(GmSelectorKind.Name, first, Name: name);
                    used = 1;
                }
                else if (first.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryNumber(first, out ulong guid) || guid == 0)
                    {
                        error = $"'{first}' is not a guid: 0xF130000123000001";
                        return false;
                    }

                    selector = new GmSelector(GmSelectorKind.Guid, first, Value: guid);
                    used = 1;
                }
                else
                {
                    return true;
                }

                break;
        }

        rest = used >= words.Count ? string.Empty : text[words[used].Start..].TrimEnd();
        return true;
    }

    /// <summary>The largest guid counter of an entry-carrying guid (24 bits).</summary>
    private const ulong MaxCounter = 0x00FFFFFF;

    /// <summary>The optional <c>map #m</c> at word <paramref name="used"/>; a <c>map</c> word without a map id is an error.</summary>
    private static bool TryTakeMap(string text, List<(int Start, int End)> words, ref int used, out uint? mapId, out string error)
    {
        mapId = null;
        error = string.Empty;
        if (used >= words.Count || !Word(text, words, used).Equals("map", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (used + 1 >= words.Count || !TryNumber(Word(text, words, used + 1), out ulong map) || map > uint.MaxValue)
        {
            error = "map needs a map id: map #1";
            return false;
        }

        mapId = (uint)map;
        used += 2;
        return true;
    }

    private static List<(int Start, int End)> Words(string text)
    {
        var words = new List<(int, int)>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i > start) words.Add((start, i));
        }

        return words;
    }

    private static string Word(string text, List<(int Start, int End)> words, int index) => text[words[index].Start..words[index].End];

    private static string Join(string text, List<(int Start, int End)> words, int count) => text[words[0].Start..words[count - 1].End];

    private static GmTargetKinds? KindOfWord(string word) => word.ToLowerInvariant() switch
    {
        "creature" => GmTargetKinds.Creature,
        "gobject" => GmTargetKinds.GameObject,
        "player" => GmTargetKinds.Player,
        "transport" => GmTargetKinds.Transport,
        _ => null,
    };

    /// <summary>A number with an optional leading <c>#</c>: decimal, or hex after <c>0x</c>.</summary>
    private static bool TryNumber(string word, out ulong value)
    {
        value = 0;
        string digits = word.StartsWith('#') ? word[1..] : word;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return digits.Length > 2 && ulong.TryParse(digits.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        return digits.Length > 0 && ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryName(string word, out string name)
    {
        name = string.Empty;
        return word.Length > 1 && word[0] == '$' && PlayerNames.TryNormalize(word[1..], out name);
    }
}

/// <summary>
/// The one target resolver of the debug commands (I1): the selector grammar of the plan (<see cref="GmSelector"/>), the target-rank refusal
/// (<see cref="CommandContext.CanActOn"/>, applied here so a command cannot skip it), the <c>World.Post</c> rule and one reply when nothing
/// matches. World thread only (it reads live maps and players; <see cref="InvalidOperationException"/> from any other).
/// <para>
/// Results are handed to a continuation (<see cref="ResolveThen(CommandContext, GmSelector, GmTargetRequest, Action{GmTarget})"/>) because
/// two things must not run inline: a search of a map the issuer's thread does not own (other maps may be updating in parallel on map
/// threads), and a write to a target on another map. Both are queued with <c>World.Post</c> and run at the start of the next tick on the
/// world thread, when no map updates; the command stays open (<see cref="CommandContext.Defer"/>) until then. The issuer's own map is searched
/// inline. Every failure replies and calls <see cref="CommandContext.Fail"/>, so a handler just returns true.
/// </para>
/// </summary>
public static class GmTargetResolver
{
    public const string NoTarget = "No target. Select one, or name it: $name, 0xHEX, guid #n, spawn #n, entry #n, nearest.";

    public const string NeedsMap = "That selector needs a position on a map; name a player with $name, or add 'map #m' to guid and entry.";

    public const string OfflineNeedsAdministrator = "Offline characters need Administrator.";

    public const string OfflineNotAccepted = "This command does not accept offline characters.";

    public const string SelectionGone = "Your selected target is not on this map.";

    public const string TargetGone = "The target is no longer in the world.";

    public static string NotFound(string selector) => $"No target matches '{selector}'.";

    public static string WrongKind(GmTargetKinds accepted) => $"That target is not valid here; this command takes: {Describe(accepted)}.";

    /// <summary>Resolve the whole of <paramref name="selector"/> (empty: the default chain) and call <paramref name="use"/> with the target. See the class remarks for when <paramref name="use"/> runs.</summary>
    public static void ResolveThen(CommandContext context, string? selector, GmTargetRequest request, Action<GmTarget> use)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!GmSelector.TryParse(selector, out GmSelector parsed, out string error))
        {
            Refuse(context, "bad-selector", error);
            return;
        }

        ResolveThen(context, parsed, request, use);
    }

    /// <summary>
    /// Resolve the selector that starts <paramref name="args"/> and call <paramref name="use"/> with the target and what follows the selector.
    /// False after replying when the selector is malformed (a command then returns true); true once resolution started (<paramref name="use"/> may run later).
    /// </summary>
    public static bool ResolveLeadingThen(CommandContext context, string? args, GmTargetRequest request, Action<GmTarget, string> use)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(use);
        if (!GmSelector.TryTakeLeading(args, out GmSelector parsed, out string rest, out string error))
        {
            Refuse(context, "bad-selector", error);
            return false;
        }

        ResolveThen(context, parsed, request, target => use(target, rest));
        return true;
    }

    /// <summary>
    /// Resolve a parsed selector and call <paramref name="use"/> with the target. Failures reply instead (and <paramref name="use"/> never runs).
    /// <paramref name="use"/> runs inline when the target was found on the issuer's own map, else (a search of other maps, or a target
    /// on another map, read or write) at the start of the next tick on the world thread. With <see cref="GmTargetRequest.Write"/> an
    /// inline call still goes through <see cref="GmTarget.RunOnOwningMap"/>, so every write from the console is posted too.
    /// </summary>
    public static void ResolveThen(CommandContext context, GmSelector selector, GmTargetRequest request, Action<GmTarget> use)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(use);
        if (request.Accepted == GmTargetKinds.None)
        {
            // default(GmTargetRequest) and "new GmTargetRequest()" skip the optional-parameter defaults: no kinds named means any.
            request = request with { Accepted = GmTargetKinds.Any };
        }

        if (!context.World.IsWorldThread)
        {
            throw new InvalidOperationException("targets are resolved on the world thread");
        }

        GmTarget? found = Search(context, selector, request, posted: false, out bool deferred);
        if (deferred)
        {
            IDisposable hold = context.Defer();
            context.World.Post(() =>
            {
                try
                {
                    // Start of a tick: no map is updating, so every map may be read and the write needs no further hop.
                    if (context.CanReplyLater && Search(context, selector, request, posted: true, out _) is { } later)
                    {
                        use(later);
                    }
                }
                finally
                {
                    hold.Dispose();
                }
            });
            return;
        }

        if (found is null)
        {
            return;
        }

        // Plan 3.2 rule 7: reads of another map's object are posted too, since that map's ungated stages may be mutating it right now.
        if (request.Write || (!context.IsConsole && found.Object is { } obj && !ReferenceEquals(obj.Map, context.Player.Map)))
        {
            found.RunOnOwningMap(context, use);
        }
        else
        {
            use(found);
        }
    }

    /// <summary>The kind of <paramref name="obj"/>; <see cref="GmTargetKinds.None"/> for objects no command targets (items, corpses...).</summary>
    public static GmTargetKinds KindOf(WorldObject obj) => obj switch
    {
        Player => GmTargetKinds.Player,
        Creature => GmTargetKinds.Creature,
        GameObject go => go.Guid.High == HighGuid.Transport ? GmTargetKinds.Transport : GmTargetKinds.GameObject,
        _ => GmTargetKinds.None,
    };

    /// <summary>
    /// One resolution attempt. Null with <paramref name="deferred"/> set means "search other maps in a posted action" (only when
    /// <paramref name="posted"/> is false); null otherwise means the failure was already replied.
    /// </summary>
    private static GmTarget? Search(CommandContext context, GmSelector selector, GmTargetRequest request, bool posted, out bool deferred)
    {
        deferred = false;
        if (selector.Kind == GmSelectorKind.Offline)
        {
            if (!request.AllowOffline)
            {
                return Refuse(context, "offline-not-accepted", OfflineNotAccepted);
            }

            return context.Security >= AccountSecurity.Administrator
                ? new GmTarget(null, selector.Name)
                : Refuse(context, "security", OfflineNeedsAdministrator);
        }

        if (selector.Kind == GmSelectorKind.Name)
        {
            Player? named = context.World.FindOnlinePlayer(selector.Name);
            return named is null ? Refuse(context, "no-target", NotFound(selector.Text)) : Accept(context, named, request);
        }

        Player? self = context.IsConsole ? null : context.Player;
        switch (selector.Kind)
        {
            case GmSelectorKind.Default:
                return self is null ? Refuse(context, "no-target", NoTarget) : ResolveDefault(context, self, request);

            case GmSelectorKind.Guid:
            {
                var guid = new ObjectGuid(selector.Value);
                WorldObject? found = self is null ? context.World.FindOnlinePlayer(guid) : Find(context, self, guid);
                if (found is null && !posted)
                {
                    deferred = true;
                    return null;
                }

                found ??= OnAnyMap(context, map => map.FindObject(guid));
                return Finish(context, found, selector, request);
            }

            case GmSelectorKind.Spawn:
            {
                uint spawn = (uint)selector.Value;
                WorldObject? found = self?.Map is { } home ? BySpawn(context, home, spawn, request.Accepted) : null;
                if (found is null && !posted)
                {
                    deferred = true;
                    return null;
                }

                found ??= OnAnyMap(context, map => BySpawn(context, map, spawn, request.Accepted));
                return Finish(context, found, selector, request);
            }

            case GmSelectorKind.Counter:
            case GmSelectorKind.Entry:
            {
                if (self is null && selector.MapId is null)
                {
                    return Refuse(context, "needs-map", NeedsMap);
                }

                Map? map;
                if (self is not null && (selector.MapId is null || selector.MapId == self.MapId))
                {
                    map = self.Map;
                }
                else if (!posted)
                {
                    deferred = true;
                    return null;
                }
                else
                {
                    map = selector.MapId is { } wanted ? context.World.FindMap(wanted) : null;
                }

                WorldObject? found = map is null ? null
                    : selector.Kind == GmSelectorKind.Counter ? ByCounter(context, map, (uint)selector.Value, request.Accepted)
                    : ByEntry(context, map, ReferenceEquals(map, self?.Map) ? self : null, selector.Entry, request.Accepted);
                return Finish(context, found, selector, request);
            }

            case GmSelectorKind.Nearest:
                return self is null
                    ? Refuse(context, "needs-map", NeedsMap)
                    : Finish(context, Nearest(context, self, selector.Yards, selector.Kinds & request.Accepted), selector, request);

            default:
                return Refuse(context, "bad-selector", NotFound(selector.Text));
        }
    }

    private static GmTarget? Finish(CommandContext context, WorldObject? found, GmSelector selector, GmTargetRequest request)
        => found is null ? Refuse(context, "no-target", NotFound(selector.Text)) : Accept(context, found, request);

    private static GmTarget? ResolveDefault(CommandContext context, Player self, GmTargetRequest request)
    {
        // The chain is the selection, then self; the issuer's combat target is not part of it.
        ObjectGuid guid = self.Selection;
        if (guid.IsEmpty)
        {
            // Nothing selected: the issuer itself, when that is a valid kind for the command.
            return (request.Accepted & GmTargetKinds.Player) != 0 ? Accept(context, self, request) : Refuse(context, "no-target", NoTarget);
        }

        WorldObject? found = Find(context, self, guid);
        return found is null ? Refuse(context, "no-target", SelectionGone) : Accept(context, found, request);
    }

    private static WorldObject? Find(CommandContext context, Player self, ObjectGuid guid)
        => context.World.FindOnlinePlayer(guid) is { } player ? player : self.Map?.FindObject(guid);

    private static GmTarget? Accept(CommandContext context, WorldObject found, GmTargetRequest request)
    {
        GmTargetKinds kind = KindOf(found);
        if (kind == GmTargetKinds.None || (kind & request.Accepted) == 0)
        {
            return Refuse(context, "wrong-kind", WrongKind(request.Accepted));
        }

        if (found is Player player && !context.CanActOn(player))
        {
            context.Fail("security");
            return null;
        }

        return new GmTarget(found);
    }

    /// <summary>The first hit of <paramref name="search"/> over every map in map-id then instance order. Only from a posted action (no map is updating).</summary>
    private static WorldObject? OnAnyMap(CommandContext context, Func<Map, WorldObject?> search)
    {
        foreach (Map map in context.World.Maps.OrderBy(m => m.MapId).ThenBy(m => m.InstanceId))
        {
            if (search(map) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static WorldObject? BySpawn(CommandContext context, Map map, uint spawn, GmTargetKinds accepted)
    {
        if ((accepted & GmTargetKinds.Creature) != 0 && CreaturesOf(context, map)?.FindBySpawnGuid(spawn) is { } creature)
        {
            return creature;
        }

        if ((accepted & (GmTargetKinds.GameObject | GmTargetKinds.Transport)) != 0)
        {
            foreach (GameObject go in map.FindUpdater<GameObjectMapSystem>()?.GameObjects ?? [])
            {
                if (go.Spawn?.Guid == spawn && (KindOf(go) & accepted) != 0)
                {
                    return go;
                }
            }
        }

        return null;
    }

    /// <summary>The creature of <paramref name="map"/> whose guid counter is <paramref name="counter"/> (the lowest full guid when several entries share it).</summary>
    private static WorldObject? ByCounter(CommandContext context, Map map, uint counter, GmTargetKinds accepted)
    {
        if ((accepted & GmTargetKinds.Creature) == 0 || CreaturesOf(context, map) is not { } creatures)
        {
            return null;
        }

        Creature? best = null;
        foreach (Creature creature in creatures.Creatures)
        {
            if (creature.Guid.High == HighGuid.Unit && creature.Guid.Counter == counter && (best is null || creature.Guid.Value < best.Guid.Value))
            {
                best = creature;
            }
        }

        return best;
    }

    /// <summary>
    /// The instance of <paramref name="entry"/> on <paramref name="map"/>: the nearest to <paramref name="from"/> when it stands on that map,
    /// else (the console, another map) the lowest full guid, compared as integers so the pick is stable.
    /// </summary>
    private static WorldObject? ByEntry(CommandContext context, Map map, Player? from, uint entry, GmTargetKinds accepted)
    {
        WorldObject? best = null;
        float bestDistance = float.MaxValue;
        void Consider(WorldObject candidate)
        {
            if (from is not null)
            {
                float distance = Distance(from, candidate);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            else if (best is null || candidate.Guid.Value < best.Guid.Value)
            {
                best = candidate;
            }
        }

        if ((accepted & GmTargetKinds.Creature) != 0 && CreaturesOf(context, map) is { } creatures)
        {
            foreach (Creature creature in creatures.Creatures)
            {
                if (creature.Entry == entry) Consider(creature);
            }
        }

        if ((accepted & (GmTargetKinds.GameObject | GmTargetKinds.Transport)) != 0 && map.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            foreach (GameObject go in objects.GameObjects)
            {
                if (go.Entry == entry && (KindOf(go) & accepted) != 0) Consider(go);
            }
        }

        return best;
    }

    private static WorldObject? Nearest(CommandContext context, Player self, float yards, GmTargetKinds kinds)
    {
        if (self.Map is not { } map)
        {
            return null;
        }

        WorldObject? best = null;
        float bestDistance = yards;
        void Consider(WorldObject candidate)
        {
            if (ReferenceEquals(candidate, self))
            {
                return;
            }

            float distance = Distance(self, candidate);
            if (distance <= bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        if ((kinds & GmTargetKinds.Player) != 0)
        {
            foreach (Player player in map.Players) Consider(player);
        }

        if ((kinds & GmTargetKinds.Creature) != 0 && CreaturesOf(context, map) is { } creatures)
        {
            foreach (Creature creature in creatures.Creatures) Consider(creature);
        }

        if ((kinds & (GmTargetKinds.GameObject | GmTargetKinds.Transport)) != 0 && map.FindUpdater<GameObjectMapSystem>() is { } objects)
        {
            foreach (GameObject go in objects.GameObjects)
            {
                if ((KindOf(go) & kinds) != 0) Consider(go);
            }
        }

        return best;
    }

    private static CreatureMapSystem? CreaturesOf(CommandContext context, Map map)
        => context.Session.Services.GetService<CreatureWorldFeature>()?.FindSystem(map);

    private static float Distance(WorldObject a, WorldObject b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static string Describe(GmTargetKinds kinds)
    {
        var names = new List<string>();
        if ((kinds & GmTargetKinds.Player) != 0) names.Add("player");
        if ((kinds & GmTargetKinds.Creature) != 0) names.Add("creature");
        if ((kinds & GmTargetKinds.GameObject) != 0) names.Add("gobject");
        if ((kinds & GmTargetKinds.Transport) != 0) names.Add("transport");
        return names.Count == 0 ? "nothing" : string.Join(", ", names);
    }

    private static GmTarget? Refuse(CommandContext context, string code, string message)
    {
        context.Fail(code);
        context.Reply(message);
        return null;
    }
}

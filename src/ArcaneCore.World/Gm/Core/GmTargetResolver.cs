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

/// <summary>The kinds of world object a command accepts as its target (a flag set).</summary>
[Flags]
public enum GmTargetKinds
{
    None = 0,
    Player = 1,
    Creature = 2,
    GameObject = 4,
    Transport = 8,

    /// <summary>Players and creatures (bots, pets and totems included).</summary>
    Unit = Player | Creature,

    Any = Player | Creature | GameObject | Transport,
}

/// <summary>The form of a target selector (docs: debug-commands plan, section 1.3).</summary>
public enum GmSelectorKind
{
    /// <summary>Omitted: the selected unit, then the issuer's target, then the issuer.</summary>
    Default,

    /// <summary><c>$name</c>: an online player or bot.</summary>
    Name,

    /// <summary><c>#guid</c> / <c>0xHEX</c>: any object of the issuer's map by full guid.</summary>
    Guid,

    /// <summary><c>spawn #n</c>: a creature or game object by database spawn guid.</summary>
    Spawn,

    /// <summary><c>entry #n [#map]</c>: the nearest instance of a template entry.</summary>
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
/// <param name="Value">The full guid (<see cref="GmSelectorKind.Guid"/>) or spawn guid (<see cref="GmSelectorKind.Spawn"/>).</param>
/// <param name="Entry">The template entry of <see cref="GmSelectorKind.Entry"/>.</param>
/// <param name="MapId">The optional map of <see cref="GmSelectorKind.Entry"/>.</param>
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
    /// remainder to its own parser. Optional parts of a selector (<c>#map</c>, <c>#yards</c>, a kind word) are taken only when they have
    /// the shape the grammar gives them, so a plain number after a selector stays an argument.
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

            case "entry":
                if (words.Count < 2 || !TryNumber(Word(text, words, 1), out ulong entry) || entry == 0 || entry > uint.MaxValue)
                {
                    error = "entry needs a template entry: entry #123 [#map]";
                    return false;
                }

                used = 2;
                uint? mapId = null;
                if (words.Count > 2 && Word(text, words, 2) is { Length: > 1 } mapWord && mapWord[0] == '#' && TryNumber(mapWord, out ulong map) && map <= uint.MaxValue)
                {
                    mapId = (uint)map;
                    used = 3;
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
                else if (first[0] == '#' || first.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryNumber(first, out ulong guid) || guid == 0)
                    {
                        error = $"'{first}' is not a guid: #123 or 0xF130000123000001";
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

/// <summary>What a command needs from its target.</summary>
/// <param name="Accepted">The object kinds it can act on (a target of another kind is refused).</param>
/// <param name="Write">The command changes the target: it must be on the issuer's own map (a console issuer has no map and is exempt).</param>
/// <param name="AllowOffline">The command handles <see cref="GmSelectorKind.Offline"/> (the resolver returns the name; the command reads the character record off the world thread).</param>
public readonly record struct GmTargetRequest(GmTargetKinds Accepted = GmTargetKinds.Any, bool Write = false, bool AllowOffline = false)
{
    public static GmTargetRequest Reading { get; } = new(GmTargetKinds.Any);

    public static GmTargetRequest Writing { get; } = new(GmTargetKinds.Any, Write: true);
}

/// <summary>A resolved target: a live object, or the name of an offline character.</summary>
public sealed record GmTarget(WorldObject? Object, string? OfflineName = null)
{
    public bool IsOffline => OfflineName is not null;

    public Unit? Unit => Object as Unit;

    public Player? Player => Object as Player;

    public Creature? Creature => Object as Creature;

    public GameObject? GameObject => Object as GameObject;
}

/// <summary>
/// The one target resolver of the debug commands (I1): the selector grammar of the plan (<see cref="GmSelector"/>), the target-rank refusal
/// (<see cref="CommandContext.CanActOn"/>, applied here so a command cannot skip it), the same-map rule for writes and one reply when
/// nothing matches. World thread only (it reads live maps and players; <see cref="InvalidOperationException"/> from any other). Every failure
/// replies, calls <see cref="CommandContext.Fail"/> and returns null/false, so a handler just returns true.
/// </summary>
public static class GmTargetResolver
{
    public const string NoTarget = "No target. Select one, or name it: $name, #guid, 0xHEX, spawn #n, entry #n, nearest.";

    public const string NeedsMap = "That selector needs a position on a map; name a player with $name.";

    public const string OtherMap = "The target is on another map; writes act only on your own map.";

    public const string OfflineNeedsAdministrator = "Offline characters need Administrator.";

    public const string OfflineNotAccepted = "This command does not accept offline characters.";

    public const string SelectionGone = "Your selected target is not on this map.";

    public static string NotFound(string selector) => $"No target matches '{selector}'.";

    public static string WrongKind(GmTargetKinds accepted) => $"That target is not valid here; this command takes: {Describe(accepted)}.";

    /// <summary>Resolve the whole of <paramref name="selector"/> (empty: the default chain). Null after replying.</summary>
    public static GmTarget? Resolve(CommandContext context, string? selector, GmTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!GmSelector.TryParse(selector, out GmSelector parsed, out string error))
        {
            return Refuse(context, "bad-selector", error);
        }

        return Resolve(context, parsed, request);
    }

    /// <summary>
    /// Resolve the selector that starts <paramref name="args"/>; <paramref name="rest"/> is what follows it. False after replying (a command
    /// then returns true).
    /// </summary>
    public static bool TryResolveLeading(CommandContext context, string? args, GmTargetRequest request, out GmTarget target, out string rest)
    {
        ArgumentNullException.ThrowIfNull(context);
        target = null!;
        if (!GmSelector.TryTakeLeading(args, out GmSelector parsed, out rest, out string error))
        {
            Refuse(context, "bad-selector", error);
            return false;
        }

        GmTarget? found = Resolve(context, parsed, request);
        if (found is null)
        {
            return false;
        }

        target = found;
        return true;
    }

    /// <summary>Resolve a parsed selector. Null after replying.</summary>
    public static GmTarget? Resolve(CommandContext context, GmSelector selector, GmTargetRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selector);
        if (request.Accepted == GmTargetKinds.None)
        {
            // default(GmTargetRequest) and "new GmTargetRequest()" skip the optional-parameter defaults: no kinds named means any.
            request = request with { Accepted = GmTargetKinds.Any };
        }

        if (!context.World.IsWorldThread)
        {
            throw new InvalidOperationException("targets are resolved on the world thread");
        }

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

        if (context.IsConsole)
        {
            return Refuse(context, selector.Kind == GmSelectorKind.Default ? "no-target" : "needs-map", selector.Kind == GmSelectorKind.Default ? NoTarget : NeedsMap);
        }

        Player self = context.Player;
        switch (selector.Kind)
        {
            case GmSelectorKind.Default:
                return ResolveDefault(context, self, request);

            case GmSelectorKind.Guid:
            {
                WorldObject? found = Find(context, self, new ObjectGuid(selector.Value));
                return found is null ? Refuse(context, "no-target", NotFound(selector.Text)) : Accept(context, found, request);
            }

            case GmSelectorKind.Spawn:
            {
                WorldObject? found = BySpawn(context, self.Map, (uint)selector.Value, request.Accepted);
                return found is null ? Refuse(context, "no-target", NotFound(selector.Text)) : Accept(context, found, request);
            }

            case GmSelectorKind.Entry:
            {
                WorldObject? found = ByEntry(context, self, selector.Entry, selector.MapId, request.Accepted);
                return found is null ? Refuse(context, "no-target", NotFound(selector.Text)) : Accept(context, found, request);
            }

            case GmSelectorKind.Nearest:
            {
                WorldObject? found = Nearest(context, self, selector.Yards, selector.Kinds & request.Accepted);
                return found is null ? Refuse(context, "no-target", NotFound(selector.Text)) : Accept(context, found, request);
            }

            default:
                return Refuse(context, "bad-selector", NotFound(selector.Text));
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

    private static GmTarget? ResolveDefault(CommandContext context, Player self, GmTargetRequest request)
    {
        ObjectGuid guid = self.Selection.IsEmpty ? self.Target : self.Selection;
        if (guid.IsEmpty)
        {
            // Nothing selected and no target: the issuer itself, when that is a valid kind for the command.
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

        if (request.Write && !context.IsConsole && !ReferenceEquals(found.Map, context.Player.Map))
        {
            return Refuse(context, "other-map", OtherMap);
        }

        return new GmTarget(found);
    }

    private static WorldObject? BySpawn(CommandContext context, Map? map, uint spawn, GmTargetKinds accepted)
    {
        if (map is null)
        {
            return null;
        }

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

    private static WorldObject? ByEntry(CommandContext context, Player self, uint entry, uint? mapId, GmTargetKinds accepted)
    {
        Map? map = self.Map;
        if (mapId is { } wanted && wanted != self.MapId)
        {
            map = context.World.FindMap(wanted);
        }

        if (map is null)
        {
            return null;
        }

        bool sameMap = ReferenceEquals(map, self.Map);
        WorldObject? best = null;
        float bestDistance = float.MaxValue;
        void Consider(WorldObject candidate)
        {
            // On another map there is no meaningful distance: the lowest guid is the stable pick.
            float distance = sameMap ? Distance(self, candidate) : candidate.Guid.Value;
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
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

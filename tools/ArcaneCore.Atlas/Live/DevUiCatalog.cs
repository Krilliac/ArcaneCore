using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Live;

/// <summary>One typed argument of a dev UI action. The UI builds its form from these and the server-side expansion validates against them again.</summary>
/// <param name="Name">The argument name (also its placeholder in <see cref="UiAction.Line"/>).</param>
/// <param name="Type"><c>uint</c> (digits only), <c>number</c> (finite) or <c>guid</c> (1 to 16 hex digits).</param>
/// <param name="Label">The form label.</param>
/// <param name="Min">Smallest accepted value for uint and number; null for none.</param>
/// <param name="Max">Largest accepted value for uint and number; null for none.</param>
/// <param name="Default">The value the form starts with, or null.</param>
public sealed record UiActionArg(string Name, string Type, string Label, double? Min = null, double? Max = null, double? Default = null)
{
    /// <summary>The argument as the catalog reports it.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["name"] = Name, ["type"] = Type, ["label"] = Label };
        if (Min is { } min)
        {
            json["min"] = min;
        }

        if (Max is { } max)
        {
            json["max"] = max;
        }

        if (Default is { } value)
        {
            json["default"] = value;
        }

        return json;
    }
}

/// <summary>
/// One thing the dev UI can do, as the context menu, the palette and the keyboard shortcuts offer it. The Atlas commands <c>action.at</c> and
/// <c>action.on</c> run it by <see cref="Id"/>, so the command line, MCP and the browser share one behaviour. An action is either a wire
/// <see cref="Template"/> of the daemon's action catalog or a command <see cref="Line"/> with typed placeholders; nothing a caller sends is
/// spliced into a line except numbers this class prints itself and a validated guid or character name.
/// </summary>
/// <param name="Id">The action id (<c>action.on template=...</c>).</param>
/// <param name="Label">The menu text.</param>
/// <param name="Scope"><c>point</c> (a clicked spot) or <c>entity</c> (a live entity).</param>
/// <param name="Group">Menu grouping: <c>move</c>, <c>spawn</c>, <c>life</c>, <c>edit</c>, <c>bot</c>, <c>ai</c>, <c>debug</c>.</param>
/// <param name="Template">The daemon template id, or null for a command line.</param>
/// <param name="Line">The command line with <c>{guid}</c>, <c>{name}</c>, <c>{x}</c> <c>{y}</c> <c>{z}</c> <c>{self}</c> and <c>{arg}</c> placeholders, or null for a template.</param>
/// <param name="Kinds">The entity kinds the action applies to (empty for a point action).</param>
/// <param name="Args">The typed arguments.</param>
/// <param name="NeedsPoint">Whether it needs a point (the clicked one, or one chosen with a second click when <paramref name="Pick"/> is set).</param>
/// <param name="Subject"><c>entity</c> (acts on the entity), <c>selection</c> (on the selected entities of <see cref="Kinds"/>) or <c>me</c> (the operator's character).</param>
/// <param name="Write">Whether it changes the world.</param>
/// <param name="Gated">Whether the command is refused on the live server (I8): shown only when the daemon's catalog lists it and the live marker is off.</param>
/// <param name="Undo">The undo kind (see <see cref="ActionInverse"/>), or null when there is no inverse.</param>
/// <param name="Pick"><c>point</c> when the UI asks for a second click before running, else null.</param>
/// <param name="Shortcut">The devui key (Alt combo) that runs it on the selection, or null.</param>
public sealed record UiAction(
    string Id,
    string Label,
    string Scope,
    string Group,
    string? Template,
    string? Line,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<UiActionArg> Args,
    bool NeedsPoint,
    string Subject,
    bool Write,
    bool Gated,
    string? Undo,
    string? Pick = null,
    string? Shortcut = null)
{
    /// <summary>The command path of a line action (the words before the first placeholder), or null for a template.</summary>
    public string? Path
    {
        get
        {
            if (Line is null)
            {
                return null;
            }

            var words = new List<string>();
            foreach (string word in Line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.StartsWith('{') || char.IsDigit(word[0]) || word.EndsWith('%'))
                {
                    break;
                }

                words.Add(word);
            }

            return string.Join(' ', words);
        }
    }

    /// <summary>The action as the catalog reports it.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["id"] = Id,
            ["label"] = Label,
            ["scope"] = Scope,
            ["group"] = Group,
            ["kinds"] = new JsonArray([.. Kinds.Select(k => (JsonNode)k)]),
            ["args"] = new JsonArray([.. Args.Select(a => (JsonNode)a.ToJson())]),
            ["needsPoint"] = NeedsPoint,
            ["subject"] = Subject,
            ["write"] = Write,
            ["gated"] = Gated,
            ["undo"] = Undo is not null,
        };
        if (Template is not null)
        {
            json["template"] = Template;
        }

        if (Path is { } path)
        {
            json["path"] = path;
        }

        if (Pick is not null)
        {
            json["pick"] = Pick;
        }

        if (Shortcut is not null)
        {
            json["shortcut"] = Shortcut;
        }

        return json;
    }
}

/// <summary>The dev UI's action catalog: the single list the menu, the palette, the shortcuts and the tests are built from.</summary>
public static class DevUiCatalog
{
    private static readonly string[] Creature = ["creature"];
    private static readonly string[] Bot = ["bot"];
    private static readonly string[] Movable = ["creature", "bot"];
    private static readonly string[] Characters = ["player", "bot"];
    private static readonly string[] Units = ["creature", "player", "bot"];
    private static readonly string[] AnyKind = ["creature", "player", "bot", "gameobject"];
    private static readonly string[] None = [];

    /// <summary>Every action, in menu order.</summary>
    public static IReadOnlyList<UiAction> All { get; } =
    [
        new("tele.me", "Teleport me here", "point", "move", "tele", null, None, [], NeedsPoint: true, Subject: "me", Write: true, Gated: false, Undo: "tele", Shortcut: "Alt+T"),
        new("tele.selected", "Teleport selected here", "point", "move", "tele", null, Characters, [], NeedsPoint: true, Subject: "selection", Write: true, Gated: false, Undo: "tele"),
        new("moveto.selected", "Move selected here", "point", "move", "moveto", null, Movable, [], NeedsPoint: true, Subject: "selection", Write: true, Gated: false, Undo: "moveto"),
        new("place.creature", "Spawn creature...", "point", "spawn", "place.creature", null, None, [new("entry", "uint", "Creature entry", 1, 4294967295)], NeedsPoint: true, Subject: "none", Write: true, Gated: false, Undo: "place"),
        new("place.gobject", "Spawn game object...", "point", "spawn", "place.gobject", null, None, [new("entry", "uint", "Game object entry", 1, 4294967295)], NeedsPoint: true, Subject: "none", Write: true, Gated: false, Undo: null),
        new("spawns", "Show spawn points here...", "point", "debug", "spawns", null, None, [new("radius", "uint", "Radius (yards)", 1, 5000, 100)], NeedsPoint: true, Subject: "none", Write: false, Gated: false, Undo: null),

        new("kill", "Kill", "entity", "life", "kill", null, Creature, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: "kill", Shortcut: "Alt+K"),
        new("heal", "Heal (full health)", "entity", "life", null, "alter health 100%", Creature, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null, Shortcut: "Alt+H"),
        new("respawn", "Respawn", "entity", "life", null, "creature respawn", Creature, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null),
        new("revive", "Revive", "entity", "life", null, "revive {name}", Characters, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null),
        new("despawn", "Despawn (temporary only)", "entity", "life", null, "creature delete", Creature, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null, Shortcut: "Delete"),
        new("setlevel", "Set level...", "entity", "edit", null, "alter level {level}", Creature, [new("level", "uint", "Level", 1, 255)], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null),
        new("setfaction", "Set faction...", "entity", "edit", null, "npc set faction {faction}", Creature, [new("faction", "uint", "Faction id", 0, 65535)], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null),
        new("setspeed", "Set speed...", "entity", "edit", null, "modify aspeed {rate}", Units, [new("rate", "number", "Speed rate", 0.1, 10, 1)], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null),
        new("moveto.pick", "Move to point...", "entity", "move", "moveto", null, Movable, [], NeedsPoint: true, Subject: "entity", Write: true, Gated: false, Undo: "moveto", Pick: "point"),

        new("bot.goto", "Bot: go to point...", "entity", "bot", null, "botctl move {guid} {x} {y} {z}", Bot, [], NeedsPoint: true, Subject: "entity", Write: true, Gated: false, Undo: null, Pick: "point"),
        new("bot.attack", "Selected bots: attack this", "entity", "bot", null, "botctl attack {guid} {unit}", AnyKind, [new("unit", "guid", "Unit to attack")], NeedsPoint: false, Subject: "selection-bots", Write: true, Gated: false, Undo: null),
        new("bot.follow", "Bot: follow me", "entity", "bot", null, "botctl follow {guid} {self}", Bot, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: null),
        new("bot.take", "Bot: take control", "entity", "bot", null, "botctl take {guid}", Bot, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: "bot.release"),
        new("bot.release", "Bot: release", "entity", "bot", null, "botctl release {guid}", Bot, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: false, Undo: "bot.take"),

        new("ai.pause", "AI: pause", "entity", "ai", null, "creature ai pause", Creature, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: true, Undo: "ai.resume"),
        new("ai.resume", "AI: resume", "entity", "ai", null, "creature ai resume", Creature, [], NeedsPoint: false, Subject: "entity", Write: true, Gated: true, Undo: "ai.pause"),
        new("debug", "Show AI state, threat and route", "entity", "debug", "debug", null, Creature, [], NeedsPoint: false, Subject: "entity", Write: false, Gated: false, Undo: null),
    ];

    /// <summary>The action with this id, or null.</summary>
    public static UiAction? Find(string id) => All.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));

    /// <summary>The longest text a character name argument may have; names are letters only in this game.</summary>
    public const int MaxNameLength = 12;

    /// <summary>A guid as the <c>.botctl</c> and <c>.atlas</c> leaves read it: <c>0x</c> and 16 upper-case hex digits.</summary>
    public static string GuidText(ulong guid) => "0x" + guid.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>A number in the shortest form that reads back as the same double.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A character name that is safe to put in a command line: 2 to 12 ASCII letters.</summary>
    public static bool IsPlainName(string? name)
        => name is { Length: >= 2 and <= MaxNameLength } && name.All(char.IsAsciiLetter);

    /// <summary>
    /// Validates <paramref name="supplied"/> against the action's declared arguments and returns each argument's printed text.
    /// Unknown arguments and missing required ones are refused with <see cref="CommandException.BadArguments"/>.
    /// </summary>
    public static Dictionary<string, string> PrintArgs(UiAction action, JsonObject? supplied)
    {
        ArgumentNullException.ThrowIfNull(action);
        var printed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (supplied is not null)
        {
            foreach ((string key, _) in supplied)
            {
                if (!action.Args.Any(a => string.Equals(a.Name, key, StringComparison.Ordinal)))
                {
                    throw Bad($"action '{action.Id}' takes no argument '{key}' (expected {string.Join(", ", action.Args.Select(a => a.Name))}).");
                }
            }
        }

        foreach (UiActionArg arg in action.Args)
        {
            JsonNode? node = null;
            supplied?.TryGetPropertyValue(arg.Name, out node);
            if (node is null)
            {
                if (arg.Default is { } fallback && arg.Type != "guid")
                {
                    printed[arg.Name] = Number(fallback);
                    continue;
                }

                throw Bad($"action '{action.Id}': argument '{arg.Name}' is required.");
            }

            printed[arg.Name] = PrintOne(action, arg, node);
        }

        return printed;
    }

    private static string PrintOne(UiAction action, UiActionArg arg, JsonNode node)
    {
        string text = node is JsonValue v && v.TryGetValue(out string? s) ? s.Trim() : node.ToJsonString();
        switch (arg.Type)
        {
            case "uint":
                if (text.Length is 0 or > 10 || !text.All(char.IsAsciiDigit)
                    || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong whole)
                    || whole < (arg.Min ?? 0) || whole > (arg.Max ?? uint.MaxValue))
                {
                    throw Bad($"action '{action.Id}': argument '{arg.Name}' must be a whole number from {Number(arg.Min ?? 0)} to {Number(arg.Max ?? uint.MaxValue)}.");
                }

                return whole.ToString(CultureInfo.InvariantCulture);
            case "number":
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value)
                    || value < (arg.Min ?? double.MinValue) || value > (arg.Max ?? double.MaxValue))
                {
                    throw Bad($"action '{action.Id}': argument '{arg.Name}' must be a number from {Number(arg.Min ?? double.MinValue)} to {Number(arg.Max ?? double.MaxValue)}.");
                }

                return Number(value);
            case "guid":
                return LiveGuid.TryParse(text, out ulong guid) && guid != 0
                    ? GuidText(guid)
                    : throw Bad($"action '{action.Id}': argument '{arg.Name}' must be a guid (1 to 16 hex digits).");
            default:
                throw new InvalidOperationException($"argument type '{arg.Type}' is not known");
        }
    }

    /// <summary>
    /// Expands a line action's <see cref="UiAction.Line"/> for one target. <paramref name="printed"/> comes from <see cref="PrintArgs"/>;
    /// <paramref name="name"/> is the target's character name and <paramref name="self"/> the operator character's guid text, each only
    /// when the line uses it. The result contains only text this method printed or validated.
    /// </summary>
    public static string ExpandLine(UiAction action, ulong? guid, string? name, ActionPoint? point, string? self, IReadOnlyDictionary<string, string> printed)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Line is null)
        {
            throw new InvalidOperationException($"action '{action.Id}' is a template, not a line");
        }

        ActionPoint Required(ActionPoint? p) => p ?? throw Bad($"action '{action.Id}' needs a point.");
        var line = new StringBuilder();
        foreach (string word in action.Line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            if (word.Length < 3 || word[0] != '{' || word[^1] != '}')
            {
                line.Append(word);
                continue;
            }

            string key = word[1..^1];
            line.Append(key switch
            {
                "guid" => guid is { } g ? GuidText(g) : throw Bad($"action '{action.Id}' needs a target."),
                "name" => IsPlainName(name) ? name! : throw Bad($"action '{action.Id}' needs the target's character name (2 to {MaxNameLength} letters); the target is not in the live data."),
                "x" => Number(Required(point).X),
                "y" => Number(Required(point).Y),
                "z" => Number(Required(point).Z),
                "self" => self ?? throw Bad($"action '{action.Id}' needs your character: run devui.character name=<your character> and have it in the live data."),
                _ => printed.TryGetValue(key, out string? text) ? text : throw new InvalidOperationException($"line placeholder '{key}' has no argument"),
            });
        }

        return line.ToString();
    }

    private static CommandException Bad(string message) => new(CommandException.BadArguments, message);
}

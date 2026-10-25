using System.Globalization;
using System.Text.Json;
using ArcaneCore.World.Gm.Atlas;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// One template of the action catalog: a fixed shape of command a tool may ask for by id, with typed arguments instead of free text. The
/// daemon expands it; nothing a tool sends is ever spliced into a command line except numbers formatted by the daemon itself, so a template
/// cannot be turned into a second command.
/// </summary>
/// <param name="Id">The template id on the wire.</param>
/// <param name="Summary">One line for the confirm summary and the docs.</param>
/// <param name="PerTarget">Whether it runs once per target (and needs at least one) rather than once.</param>
/// <param name="NeedsPoint">Whether the request must carry a point.</param>
/// <param name="UIntArgs">The unsigned integer arguments, by name.</param>
internal sealed record ActionTemplate(string Id, string Summary, bool PerTarget, bool NeedsPoint, string[] UIntArgs);

/// <summary>
/// The template catalog of the action channel (docs/areas/atlas-actions.md, "Templates") and its expansion into command lines. Pure; any thread.
/// </summary>
internal static class MapFeedActionCatalog
{
    private static readonly ActionTemplate[] Templates =
    [
        new("place.creature", "place a temporary creature", PerTarget: false, NeedsPoint: true, ["entry"]),
        new("place.gobject", "place a temporary game object", PerTarget: false, NeedsPoint: true, ["entry"]),
        new("moveto", "walk a creature to the point", PerTarget: true, NeedsPoint: true, []),
        new("tele", "teleport a player to the point", PerTarget: true, NeedsPoint: true, []),
        new("kill", "kill a creature", PerTarget: true, NeedsPoint: false, []),
        new("debug", "report a creature's debug facts", PerTarget: true, NeedsPoint: false, []),
        new("spawns", "list the spawn points around the point", PerTarget: false, NeedsPoint: true, ["radius"]),
    ];

    /// <summary>The templates, in documentation order.</summary>
    public static IReadOnlyList<ActionTemplate> All => Templates;

    /// <summary>The template with this id (exact, case-sensitive), or null.</summary>
    public static ActionTemplate? Find(string id) => Array.Find(Templates, t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// Check <paramref name="request"/> against <paramref name="template"/> (targets, point and arguments) and return the command lines to run:
    /// one per target for a per-target template, otherwise one. Null with <paramref name="error"/> set when the request does not fit.
    /// </summary>
    public static IReadOnlyList<ExpandedStep>? Expand(ActionTemplate template, ActionRequest request, out FeedError error)
    {
        error = default;
        if (template.PerTarget == (request.Targets.Length == 0))
        {
            error = new FeedError(MapFeedProtocol.BadArgs, template.PerTarget
                ? $"template {template.Id} needs at least one target"
                : $"template {template.Id} takes no targets");
            return null;
        }

        if (template.NeedsPoint && request.Point is null)
        {
            error = new FeedError(MapFeedProtocol.BadArgs, $"template {template.Id} needs a \"point\"");
            return null;
        }

        var numbers = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (string name in template.UIntArgs)
        {
            if (!request.Args.TryGetValue(name, out JsonElement value) || !TryUInt(value, out uint number))
            {
                error = new FeedError(MapFeedProtocol.BadArgs, $"template {template.Id} needs \"args.{name}\" as a non-negative whole number");
                return null;
            }

            numbers[name] = number;
        }

        GmClickPoint point = request.Point ?? default;
        string at = string.Create(CultureInfo.InvariantCulture, $"{Number(point.X)} {Number(point.Y)} {Number(point.Z)}");
        string withMap = string.Create(CultureInfo.InvariantCulture, $"{point.Map} {at}");
        switch (template.Id)
        {
            case "place.creature":
                return [new ExpandedStep(null, string.Create(CultureInfo.InvariantCulture, $"atlas place creature {numbers["entry"]} {withMap} {Number(point.O)}"))];
            case "place.gobject":
                return [new ExpandedStep(null, string.Create(CultureInfo.InvariantCulture, $"atlas place gobject {numbers["entry"]} {withMap} {Number(point.O)}"))];
            case "spawns":
                return [new ExpandedStep(null, string.Create(CultureInfo.InvariantCulture, $"atlas spawns {point.Map} {Number(point.X)} {Number(point.Y)} {numbers["radius"]}"))];
            case "moveto":
                return [.. request.Targets.Select(t => new ExpandedStep(t, $"atlas moveto {Guid(t)} {at}"))];
            case "tele":
                return [.. request.Targets.Select(t => new ExpandedStep(t, $"atlas tele {Guid(t)} {withMap}"))];
            case "kill":
                return [.. request.Targets.Select(t => new ExpandedStep(t, "creature kill"))];
            case "debug":
                return [.. request.Targets.Select(t => new ExpandedStep(t, $"atlas debug {Guid(t)}"))];
            default:
                error = new FeedError(ActionCodes.UnknownCommand, $"no template {template.Id}");
                return null;
        }
    }

    /// <summary>A guid as the <c>.atlas</c> leaves read it: <c>0x</c> and 16 upper-case hex digits.</summary>
    internal static string Guid(ulong guid) => "0x" + guid.ToString("X16", CultureInfo.InvariantCulture);

    /// <summary>A coordinate in the shortest form that reads back as the same float.</summary>
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A whole number from a JSON number, or from a string of ASCII digits only (no sign, no exponent, no spaces).</summary>
    private static bool TryUInt(JsonElement value, out uint number)
    {
        number = 0;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                return value.TryGetUInt32(out number);
            case JsonValueKind.String:
                string? text = value.GetString();
                return text is { Length: > 0 and <= 10 } && text.All(char.IsAsciiDigit)
                    && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
            default:
                return false;
        }
    }
}

/// <summary>One command line of an expanded action, and the target it is for (null for an action without targets).</summary>
/// <param name="Target">The guid this run acts on, or null.</param>
/// <param name="Line">The command line, without a prefix.</param>
internal sealed record ExpandedStep(ulong? Target, string Line);

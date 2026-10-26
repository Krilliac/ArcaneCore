using System.Globalization;
using System.Text.Json;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.World.Gm.Atlas;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>The stable codes of the action channel (docs/areas/atlas-actions.md, "Codes"). A refusal is an answer with <c>ok:false</c> and one of these, never a strike.</summary>
internal static class ActionCodes
{
    public const string ActionsOff = "actions-off";
    public const string LiveServer = "live-server";
    public const string RoleMismatch = MapFeedRoles.RoleMismatch;
    public const string WebLoginOff = "web-login-off";
    public const string NoTicket = "no-ticket";
    public const string Expired = "expired";
    public const string Banned = "banned";
    public const string TicketUnavailable = "ticket-unavailable";
    public const string AuditUnavailable = "audit-unavailable";
    public const string RateLimited = "rate-limited";
    public const string UnknownCommand = "unknown-command";
    public const string ConsoleOnly = "console-only";
    public const string Security = "security";
    public const string TooManyTargets = "too-many-targets";
    public const string TargetGone = "target-gone";
    public const string ConfirmRequired = "confirm-required";
    public const string ConfirmWrong = "confirm-wrong";
    public const string Timeout = MapFeedProtocol.Timeout;
    public const string WorldStopping = MapFeedProtocol.WorldStopping;
    public const string Failed = "failed";

    /// <summary>The audit result of an action whose connection ended before it was answered (the command may have run; the record says so by its other fields).</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The audit result of a command that was still open when the settle wait ended.</summary>
    public const string Unsettled = "unsettled";
}

/// <summary>One parsed <c>action</c> line. <see cref="HasId"/> is true whenever the id was readable, also when the rest was refused.</summary>
internal sealed class ActionRequest
{
    /// <summary>The longest command text an action may carry (characters).</summary>
    public const int MaxCommandChars = 1024;

    public long Id;

    public bool HasId;

    /// <summary><c>local</c> or <c>web</c>: the tier the request claims; it must equal the connection's role.</summary>
    public string Tier = string.Empty;

    /// <summary>A web actor's ticket (never logged), or null.</summary>
    public string? Ticket;

    /// <summary>The end user's address as the web listener saw it (audit only), or null.</summary>
    public string? Ip;

    /// <summary>A raw command line, without its leading dot, or null when the request names a template.</summary>
    public string? Command;

    /// <summary>A template id, or null.</summary>
    public string? Template;

    /// <summary>The template arguments (cloned, owned by the request).</summary>
    public Dictionary<string, JsonElement> Args = new(StringComparer.Ordinal);

    /// <summary>The selected guids, without duplicates, in request order.</summary>
    public ulong[] Targets = [];

    /// <summary>The clicked point, or null.</summary>
    public GmClickPoint? Point;

    /// <summary>The confirm code of a bulk request, or null.</summary>
    public string? Confirm;
}

/// <summary>Parser of the <c>action</c> line. Pure; any thread.</summary>
internal static class MapFeedActionProtocol
{
    /// <summary>Parse <paramref name="line"/>. False with <paramref name="error"/> when it is refused; <see cref="ActionRequest.HasId"/> then says whether it can be answered as a result.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> line, out ActionRequest request, out FeedError error)
    {
        request = new ActionRequest();
        error = default;
        using JsonDocument document = FeedExtensionJson.Parse(line);
        JsonElement root = document.RootElement;
        if (!FeedExtensionJson.TryId(root, out request.Id))
        {
            error = new FeedError(MapFeedProtocol.BadArgs, "an action needs an integer \"id\"");
            return false;
        }

        request.HasId = true;
        return TryActor(root, request, out error)
            && TryText(root, request, out error)
            && TryTargets(root, request, out error)
            && TryPoint(root, request, out error)
            && TryArgs(root, request, out error)
            && TryConfirm(root, request, out error);
    }

    private static FeedError Bad(string message) => new(MapFeedProtocol.BadArgs, message);

    private static bool TryActor(JsonElement root, ActionRequest request, out FeedError error)
    {
        error = default;
        if (!root.TryGetProperty("actor"u8, out JsonElement actor) || actor.ValueKind != JsonValueKind.Object
            || !actor.TryGetProperty("tier"u8, out JsonElement tier) || tier.ValueKind != JsonValueKind.String)
        {
            error = Bad("\"actor\" must be {\"tier\":\"local\"} or {\"tier\":\"web\",\"ticket\":\"...\"}");
            return false;
        }

        // The name a request may carry in "actor" is ignored: the actor's identity is the pipe's (local) or the ticket's (web).
        request.Tier = tier.ValueEquals("local"u8) ? "local" : tier.ValueEquals("web"u8) ? "web" : string.Empty;
        if (request.Tier.Length == 0)
        {
            error = Bad("\"actor.tier\" must be local or web");
            return false;
        }

        if (actor.TryGetProperty("ticket"u8, out JsonElement ticket))
        {
            if (ticket.ValueKind != JsonValueKind.String)
            {
                error = Bad("\"actor.ticket\" must be a string");
                return false;
            }

            request.Ticket = ticket.GetString();
        }

        if (actor.TryGetProperty("ip"u8, out JsonElement ip) && ip.ValueKind == JsonValueKind.String
            && ip.GetString() is { Length: > 0 and <= 64 } text && System.Net.IPAddress.TryParse(text, out System.Net.IPAddress? address))
        {
            request.Ip = address.ToString();
        }

        return true;
    }

    private static bool TryText(JsonElement root, ActionRequest request, out FeedError error)
    {
        error = default;
        bool hasCommand = root.TryGetProperty("command"u8, out JsonElement command);
        bool hasTemplate = root.TryGetProperty("template"u8, out JsonElement template);
        if (hasCommand == hasTemplate)
        {
            error = Bad("name exactly one of \"command\" (a command line) and \"template\" (a template id)");
            return false;
        }

        if (hasTemplate)
        {
            if (template.ValueKind != JsonValueKind.String || template.GetString() is not { Length: > 0 and <= 64 } id)
            {
                error = Bad("\"template\" must be a template id");
                return false;
            }

            request.Template = id;
            return true;
        }

        if (command.ValueKind != JsonValueKind.String || command.GetString() is not { } text || text.Length > ActionRequest.MaxCommandChars)
        {
            error = Bad($"\"command\" must be a string of at most {ActionRequest.MaxCommandChars} characters");
            return false;
        }

        // One leading '.' or '!' is the chat prefix and optional here. A line break (or any control character) would let a second command
        // ride along, so it is refused whole; a ';' is plain text to the command table.
        string trimmed = text.Trim();
        if (trimmed.Length > 0 && trimmed[0] is '.' or '!')
        {
            trimmed = trimmed[1..].TrimStart();
        }

        if (trimmed.Length == 0 || trimmed.Any(char.IsControl))
        {
            error = Bad("\"command\" must be one non-empty command line without control characters");
            return false;
        }

        request.Command = trimmed;
        return true;
    }

    private static bool TryTargets(JsonElement root, ActionRequest request, out FeedError error)
    {
        error = default;
        if (!root.TryGetProperty("targets"u8, out JsonElement targets))
        {
            return true;
        }

        if (targets.ValueKind != JsonValueKind.Array)
        {
            error = Bad("\"targets\" must be an array of guids (16 hex digits each)");
            return false;
        }

        if (targets.GetArrayLength() > MapFeedActionsOptions.MaxTargets)
        {
            error = new FeedError(ActionCodes.TooManyTargets, $"an action may name at most {MapFeedActionsOptions.MaxTargets} targets");
            return false;
        }

        var list = new List<ulong>(targets.GetArrayLength());
        foreach (JsonElement item in targets.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !TryGuid(item.GetString(), out ulong guid))
            {
                error = Bad("every target must be a non-zero guid as 1 to 16 hex digits");
                return false;
            }

            if (!list.Contains(guid))
            {
                list.Add(guid);
            }
        }

        request.Targets = [.. list];
        return true;
    }

    /// <summary>A guid as 1 to 16 hex digits with an optional <c>0x</c>; nothing else (no sign, no spaces, no separators), and not zero.</summary>
    internal static bool TryGuid(string? text, out ulong guid)
    {
        guid = 0;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> digits = text;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        if (digits.Length is 0 or > 16)
        {
            return false;
        }

        foreach (char c in digits)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out guid) && guid != 0;
    }

    private static bool TryPoint(JsonElement root, ActionRequest request, out FeedError error)
    {
        error = default;
        if (!root.TryGetProperty("point"u8, out JsonElement point))
        {
            return true;
        }

        if (point.ValueKind != JsonValueKind.Object
            || !point.TryGetProperty("map"u8, out JsonElement map) || map.ValueKind != JsonValueKind.Number || !map.TryGetUInt32(out uint mapId)
            || !TryCoordinate(point, "x"u8, required: true, out float x) || !TryCoordinate(point, "y"u8, required: true, out float y)
            || !TryCoordinate(point, "z"u8, required: true, out float z) || !TryCoordinate(point, "o"u8, required: false, out float o))
        {
            error = Bad("\"point\" must be {\"map\",\"x\",\"y\",\"z\"} with finite numbers (and optional \"instance\", \"o\")");
            return false;
        }

        uint instance = 0;
        if (point.TryGetProperty("instance"u8, out JsonElement instanceElement)
            && (instanceElement.ValueKind != JsonValueKind.Number || !instanceElement.TryGetUInt32(out instance)))
        {
            error = Bad("\"point.instance\" must be an instance id (a non-negative integer)");
            return false;
        }

        if (!GridDefines.IsValidMapCoord(x, y, z, o))
        {
            error = Bad("\"point\" lies outside the map");
            return false;
        }

        request.Point = new GmClickPoint(mapId, instance, x, y, z, o);
        return true;
    }

    private static bool TryCoordinate(JsonElement point, ReadOnlySpan<byte> name, bool required, out float value)
    {
        value = 0f;
        if (!point.TryGetProperty(name, out JsonElement element))
        {
            return !required;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out double number))
        {
            return false;
        }

        value = (float)number;
        return float.IsFinite(value);
    }

    private static bool TryArgs(JsonElement root, ActionRequest request, out FeedError error)
    {
        error = default;
        if (!root.TryGetProperty("args"u8, out JsonElement args))
        {
            return true;
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            error = Bad("\"args\" must be an object");
            return false;
        }

        foreach (JsonProperty property in args.EnumerateObject())
        {
            request.Args[property.Name] = property.Value.Clone();
        }

        return true;
    }

    private static bool TryConfirm(JsonElement root, ActionRequest request, out FeedError error)
    {
        error = default;
        if (!root.TryGetProperty("confirm"u8, out JsonElement confirm))
        {
            return true;
        }

        if (confirm.ValueKind != JsonValueKind.String || confirm.GetString() is not { Length: <= 16 } code)
        {
            error = Bad("\"confirm\" must be the code the daemon issued");
            return false;
        }

        request.Confirm = code;
        return true;
    }
}

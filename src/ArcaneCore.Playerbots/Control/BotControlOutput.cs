using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ArcaneCore.World.Playerbots.Control;

/// <summary>One bot's part of a <c>.botctl</c> result.</summary>
internal sealed class BotOutcome(string bot)
{
    public string Bot { get; } = bot;

    public bool Ok { get; private set; } = true;

    public string? Error { get; private set; }

    public string Detail { get; private set; } = string.Empty;

    public JsonObject? Data { get; set; }

    public BotOutcome Success(string detail = "")
    {
        Ok = true;
        Error = null;
        Detail = detail;
        return this;
    }

    public BotOutcome Failure(string code, string detail = "")
    {
        Ok = false;
        Error = code;
        Detail = detail;
        return this;
    }
}

/// <summary>
/// The result of one <c>.botctl</c> command: exactly one summary line (text <c>OK verb ...</c> / <c>ERR code ...</c>, or with
/// <c>--json</c> one JSON object on one line) after any per-bot lines, so a script reads one line per command (docs/ops/botctl.md,
/// "Output").
/// </summary>
internal sealed class BotControlResult(string verb)
{
    public string Verb { get; } = verb;

    public string? Error { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public List<BotOutcome> Bots { get; } = [];

    public JsonNode? Data { get; set; }

    public bool Ok => Error is null && Bots.All(b => b.Ok);

    /// <summary>The machine reason when not <see cref="Ok"/>: the command's error, or the first failing bot's code (<c>partial</c> when some succeeded).</summary>
    public string? Reason => Error ?? (Bots.All(b => b.Ok) ? null : Bots.Any(b => b.Ok) ? "partial" : Bots.First(b => !b.Ok).Error);

    public BotControlResult Fail(string code, string message)
    {
        Error = code;
        Message = message;
        return this;
    }

    public BotControlResult Say(string message)
    {
        Message = message;
        return this;
    }

    public BotOutcome Add(string bot)
    {
        var outcome = new BotOutcome(bot);
        Bots.Add(outcome);
        return outcome;
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["ok"] = Ok,
            ["verb"] = Verb,
        };
        if (Reason is { } reason) json["error"] = reason;
        if (Message.Length > 0) json["message"] = Message;
        if (Bots.Count > 0)
        {
            var bots = new JsonArray();
            foreach (BotOutcome outcome in Bots)
            {
                var bot = new JsonObject { ["bot"] = outcome.Bot, ["ok"] = outcome.Ok };
                if (outcome.Error is { } code) bot["error"] = code;
                if (outcome.Detail.Length > 0) bot["detail"] = outcome.Detail;
                if (outcome.Data is { } data) bot["data"] = data;
                bots.Add(bot);
            }

            json["bots"] = bots;
        }

        if (Data is { } extra) json["data"] = extra;
        return json;
    }

    /// <summary>The reply lines: per-bot lines (text), then the one summary line.</summary>
    public IEnumerable<string> ToLines(bool json)
    {
        if (json)
        {
            yield return ToJson().ToJsonString();
            yield break;
        }

        foreach (BotOutcome outcome in Bots)
        {
            string head = outcome.Ok ? "  " + outcome.Bot + ": ok" : "  " + outcome.Bot + ": error " + outcome.Error;
            if (outcome.Detail.Length > 0) head += " " + outcome.Detail;
            yield return head;
            if (outcome.Data is { } data)
                foreach (string line in BotControlOutput.TextLines(data, "    ")) yield return line;
        }

        if (Data is { } extra)
            foreach (string line in BotControlOutput.TextLines(extra, "  ")) yield return line;
        int ok = Bots.Count(b => b.Ok);
        string counts = Bots.Count == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" ({ok}/{Bots.Count} bots ok)");
        yield return Ok
            ? "OK " + Verb + counts + (Message.Length > 0 ? ": " + Message : string.Empty)
            : "ERR " + Reason + " " + Verb + counts + (Message.Length > 0 ? ": " + Message : string.Empty);
    }
}

internal static class BotControlOutput
{
    public static JsonObject EventJson(BotControlEvent evt) => new()
    {
        ["seq"] = evt.Seq,
        ["bot"] = evt.Bot,
        ["ms"] = evt.TimeMs,
        ["at"] = evt.At.ToString("O", CultureInfo.InvariantCulture),
        ["kind"] = evt.Kind,
        ["text"] = evt.Text,
        ["error"] = evt.Error,
    };

    public static string EventText(BotControlEvent evt)
        => string.Create(CultureInfo.InvariantCulture, $"{evt.Seq} {evt.Bot} {evt.Kind}{(evt.Error ? " !" : string.Empty)} {evt.Text}").TrimEnd();

    /// <summary>Flatten a JSON value into readable <c>key: value</c> lines (text mode).</summary>
    public static IEnumerable<string> TextLines(JsonNode node, string indent)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj)
            {
                switch (value)
                {
                    case JsonObject inner when inner.Count > 0:
                        yield return indent + key + ":";
                        foreach (string line in TextLines(inner, indent + "  ")) yield return line;
                        break;
                    case JsonArray array when array.Count > 0 && array.All(a => a is JsonObject):
                        yield return indent + key + ": " + array.Count.ToString(CultureInfo.InvariantCulture);
                        foreach (JsonNode? item in array) yield return indent + "  - " + Inline(item);
                        break;
                    default:
                        yield return indent + key + ": " + Inline(value);
                        break;
                }
            }
        }
        else if (node is JsonArray list)
        {
            foreach (JsonNode? item in list) yield return indent + "- " + Inline(item);
        }
        else
        {
            yield return indent + Inline(node);
        }
    }

    private static string Inline(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue value when value.TryGetValue(out string? text) => text,
        JsonObject obj => string.Join(' ', obj.Select(p => p.Key + "=" + Inline(p.Value))),
        JsonArray array => string.Join(',', array.Select(Inline)),
        _ => node.ToJsonString(),
    };

    public static string Join(IEnumerable<string> parts)
    {
        var text = new StringBuilder();
        foreach (string part in parts)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(part);
        }

        return text.ToString();
    }
}
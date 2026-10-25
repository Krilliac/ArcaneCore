using System.Globalization;
using System.Text;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// One row of a debug read: ordered named fields that print as a text line and as one JSON object, so a list command
/// builds its rows once and gets both the chat output and the stable <c>--json</c> output (I4).
/// Not thread safe; built and consumed on one thread.
/// </summary>
public sealed class GmRow
{
    private readonly List<KeyValuePair<string, object?>> _fields = [];

    /// <summary>The text line, or null to print <c>key=value</c> pairs.</summary>
    public string? Text { get; private set; }

    public IReadOnlyList<KeyValuePair<string, object?>> Fields => _fields;

    /// <summary>
    /// Append a field. <paramref name="value"/> is a string, bool, number, <see cref="Guid"/>, <see cref="ObjectGuid"/> (a hex string), enum (written by name) or null; anything else
    /// is written as its <see cref="object.ToString"/>. Keys are lower-case words and keep their insertion order.
    /// </summary>
    public GmRow Add(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _fields.Add(new KeyValuePair<string, object?>(key, value));
        return this;
    }

    /// <summary>The line chat prints for this row (the JSON output is unaffected).</summary>
    public GmRow WithText(string text)
    {
        Text = text;
        return this;
    }

    /// <summary>The chat line: <see cref="Text"/>, else the fields as <c>key=value</c> pairs.</summary>
    public string ToLine()
    {
        if (Text is not null)
        {
            return Text;
        }

        var builder = new StringBuilder();
        foreach (KeyValuePair<string, object?> field in _fields)
        {
            if (builder.Length != 0)
            {
                builder.Append(' ');
            }

            builder.Append(field.Key).Append('=').Append(GmPager.Format(field.Value));
        }

        return builder.ToString();
    }

    internal void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, object?> field in _fields)
        {
            writer.WritePropertyName(field.Key);
            GmPager.WriteValue(writer, field.Value);
        }

        writer.WriteEndObject();
    }
}

/// <summary>The paging and format request parsed from a read command's arguments (<see cref="GmPager.TryParse"/>).</summary>
/// <param name="Page">The 1-based page of a list.</param>
/// <param name="Json">Whether the reply is JSON Lines (a header line, then one object per row) instead of chat lines.</param>
public readonly record struct GmOutputOptions(int Page, bool Json)
{
    public static GmOutputOptions Default { get; } = new(1, false);
}

/// <summary>
/// Pager and stable JSON for debug reads (I4): <c>page #n</c> and <c>--json</c> on any list or single-entity read. A page holds
/// <see cref="RowsPerPage"/> rows (the <c>.inspect</c> cap). The JSON is JSON Lines (the console, RA and the control pipe split replies on
/// newlines, and each reply line is one JSON document): a header <c>{"command":..,"page":p,"pages":m,"rows":k}</c> (k = rows on this page),
/// then one object per row, in the order the rows were built. A single entity is the same with page 1 of 1. A failed request answers one
/// <c>{"error":..,"message":..}</c> line. The <c>--json</c> flag and <c>page #n</c> are read from the end of the arguments only. World thread
/// (it replies through <see cref="CommandContext.Reply"/>).
/// </summary>
public static class GmPager
{
    /// <summary>Rows a page prints (the same cap as <c>InspectCommands.MaxRows</c>).</summary>
    public const int RowsPerPage = 25;

    public const string JsonFlag = "--json";

    public const string PageNeedsNumber = "page needs a number: page #2";

    public static string PageOutOfRange(int page, int pages) => string.Create(CultureInfo.InvariantCulture, $"Page {page} does not exist; there {(pages == 1 ? "is 1 page" : $"are {pages} pages")}.");

    /// <summary>
    /// Strip the trailing option words from <paramref name="args"/>: <c>--json</c> and <c>page #n</c> / <c>page n</c>, in either order. They are
    /// read from the end only, so free text that merely contains <c>--json</c> keeps it. A trailing <c>page</c> without a number is the pager
    /// keyword and is refused. <paramref name="rest"/> is <paramref name="args"/> unchanged when no option is present, else the remaining
    /// words joined by single spaces. False with <paramref name="error"/> when the page is missing, not a positive number or past
    /// <see cref="int.MaxValue"/>; <paramref name="options"/> still carries <see cref="GmOutputOptions.Json"/> then.
    /// </summary>
    public static bool TryParse(string? args, out string rest, out GmOutputOptions options, out string error)
    {
        error = string.Empty;
        options = GmOutputOptions.Default;
        rest = args ?? string.Empty;
        string[] words = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int count = words.Length;
        bool json = false;
        int page = 1;
        bool havePage = false;

        while (count > 0)
        {
            string last = words[count - 1];
            if (!json && last.Equals(JsonFlag, StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                count--;
            }
            else if (!havePage && last.Equals("page", StringComparison.OrdinalIgnoreCase))
            {
                error = PageNeedsNumber;
                options = new GmOutputOptions(1, json);
                return false;
            }
            else if (!havePage && count >= 2 && words[count - 2].Equals("page", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(last.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1)
                {
                    error = PageNeedsNumber;
                    options = new GmOutputOptions(1, json);
                    return false;
                }

                havePage = true;
                count -= 2;
            }
            else
            {
                break;
            }
        }

        if (json || havePage)
        {
            rest = string.Join(' ', words.Take(count));
        }

        options = new GmOutputOptions(page, json);
        return true;
    }

    /// <summary>
    /// <see cref="TryParse"/> that answers a bad request itself (a JSON error object when <c>--json</c> was given, else the text) and marks
    /// the command failed. False means the reply was sent.
    /// </summary>
    public static bool TryParse(CommandContext context, string? args, out string rest, out GmOutputOptions options)
    {
        if (TryParse(args, out rest, out options, out string error))
        {
            return true;
        }

        ReplyError(context, options.Json, "bad-page", error);
        return false;
    }

    /// <summary>
    /// Reply one page of <paramref name="rows"/> under <paramref name="title"/>. Text: the title and total, the page's lines, and a footer
    /// naming the page and how to reach the next when there is more than one. A page past the end answers an error.
    /// </summary>
    public static void Reply(CommandContext context, string title, IReadOnlyList<GmRow> rows, GmOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        int total = rows.Count;
        int pages = Math.Max(1, (total + RowsPerPage - 1) / RowsPerPage);
        if (options.Page > pages)
        {
            ReplyError(context, options.Json, "page-out-of-range", PageOutOfRange(options.Page, pages), pages);
            return;
        }

        int first = (options.Page - 1) * RowsPerPage;
        int take = Math.Min(RowsPerPage, total - first);
        if (options.Json)
        {
            context.Reply(JsonLine(writer => WriteHeader(writer, title, options.Page, pages, take)));
            for (int i = 0; i < take; i++)
            {
                GmRow row = rows[first + i];
                context.Reply(JsonLine(row.WriteJson));
            }

            return;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{title}: {total}"));
        for (int i = 0; i < take; i++)
        {
            context.Reply(rows[first + i].ToLine());
        }

        if (pages > 1)
        {
            string footer = string.Create(CultureInfo.InvariantCulture, $"Page {options.Page}/{pages} (rows {first + 1}-{first + take} of {total}).");
            context.Reply(options.Page < pages
                ? footer + string.Create(CultureInfo.InvariantCulture, $" Add 'page #{options.Page + 1}' for more.")
                : footer);
        }
    }

    /// <summary>Reply a single entity: its <see cref="GmRow.ToLine"/>, or with <see cref="GmOutputOptions.Json"/> the header (page 1 of 1, one row) and the row object.</summary>
    public static void ReplyRow(CommandContext context, string title, GmRow row, GmOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(row);
        if (!options.Json)
        {
            context.Reply(row.ToLine());
            return;
        }

        context.Reply(JsonLine(writer => WriteHeader(writer, title, 1, 1, 1)));
        context.Reply(JsonLine(row.WriteJson));
    }

    private static void WriteHeader(Utf8JsonWriter writer, string command, int page, int pages, int rows)
    {
        writer.WriteStartObject();
        writer.WriteString("command", command);
        writer.WriteNumber("page", page);
        writer.WriteNumber("pages", pages);
        writer.WriteNumber("rows", rows);
        writer.WriteEndObject();
    }

    /// <summary>One JSON document on one line (the writer's default output has no newlines).</summary>
    private static string JsonLine(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    /// <summary>Reply a failed read: <c>{"error":code,"message":text}</c> as JSON, else the text; the command is marked failed with <paramref name="code"/>.</summary>
    public static void ReplyError(CommandContext context, bool json, string code, string message, int? pages = null)
    {
        context.Fail(code);
        if (!json)
        {
            context.Reply(message);
            return;
        }

        context.Reply(JsonLine(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("error", code);
            writer.WriteString("message", message);
            if (pages is { } count)
            {
                writer.WriteNumber("pages", count);
            }

            writer.WriteEndObject();
        }));
    }

    /// <summary>A guid as text: <c>0xF130...</c>. JSON carries it as a string (a number above 2^53 loses precision in double-based consumers).</summary>
    private static string HexGuid(ObjectGuid guid) => "0x" + guid.Value.ToString("X16", CultureInfo.InvariantCulture);

    internal static string Format(object? value) => value switch
    {
        null => "none",
        string text => text,
        bool flag => flag ? "true" : "false",
        ObjectGuid guid => HexGuid(guid),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    internal static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int or short or sbyte:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case byte or ushort or uint:
                writer.WriteNumberValue(Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong big:
                writer.WriteNumberValue(big);
                break;
            case float single:
                if (float.IsFinite(single)) writer.WriteNumberValue(single); else writer.WriteNullValue();
                break;
            case double real:
                if (double.IsFinite(real)) writer.WriteNumberValue(real); else writer.WriteNullValue();
                break;
            case decimal money:
                writer.WriteNumberValue(money);
                break;
            case Enum named:
                writer.WriteStringValue(named.ToString());
                break;
            case ObjectGuid guid:
                writer.WriteStringValue(HexGuid(guid));
                break;
            case Guid id:
                writer.WriteStringValue(id);
                break;
            default:
                writer.WriteStringValue(Format(value));
                break;
        }
    }
}

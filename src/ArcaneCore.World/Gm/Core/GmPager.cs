using System.Globalization;
using System.Text;
using System.Text.Json;
using ArcaneCore.World.Commands;

namespace ArcaneCore.World.Gm.Core;

/// <summary>
/// One row of a debug read: ordered named fields that print as a text line and as one JSON object, so a list command
/// builds its rows once and gets both the chat output and the stable <c>--json</c> output (docs/reference/gm-commands.md, I4).
/// Not thread safe; built and consumed on one thread.
/// </summary>
public sealed class GmRow
{
    private readonly List<KeyValuePair<string, object?>> _fields = [];

    /// <summary>The text line, or null to print <c>key=value</c> pairs.</summary>
    public string? Text { get; private set; }

    public IReadOnlyList<KeyValuePair<string, object?>> Fields => _fields;

    /// <summary>
    /// Append a field. <paramref name="value"/> is a string, bool, number, <see cref="Guid"/>, enum (written by name) or null; anything else
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
/// <param name="Json">Whether the reply is one JSON object instead of chat lines.</param>
public readonly record struct GmOutputOptions(int Page, bool Json)
{
    public static GmOutputOptions Default { get; } = new(1, false);
}

/// <summary>
/// Pager and stable JSON for debug reads (I4): <c>page #n</c> and <c>--json</c> on any list or single-entity read. A page holds
/// <see cref="RowsPerPage"/> rows (the <c>.inspect</c> cap). The JSON is one line (the console, RA and the control pipe split replies on
/// newlines) with a fixed shape: <c>{"title":..,"total":n,"page":p,"pages":m,"rows":[{..}]}</c> for a list, <c>{"title":..,"row":{..}}</c> for
/// one entity, <c>{"error":..}</c> when the request fails; field order is the order rows were built in. World thread (it replies through
/// <see cref="CommandContext.Reply"/>).
/// </summary>
public static class GmPager
{
    /// <summary>Rows a page prints (the same cap as <c>InspectCommands.MaxRows</c>).</summary>
    public const int RowsPerPage = 25;

    public const string JsonFlag = "--json";

    public const string PageNeedsNumber = "page needs a number: page #2";

    public static string PageOutOfRange(int page, int pages) => string.Create(CultureInfo.InvariantCulture, $"Page {page} does not exist; there {(pages == 1 ? "is 1 page" : $"are {pages} pages")}.");

    /// <summary>
    /// Strip <c>--json</c> (anywhere) and a trailing <c>page #n</c> / <c>page n</c> from <paramref name="args"/>. <paramref name="rest"/> is
    /// <paramref name="args"/> unchanged when neither is present, else the remaining words joined by single spaces. False with
    /// <paramref name="error"/> when the page is missing, not a positive number or past <see cref="int.MaxValue"/>.
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
        bool changed = false;

        if (words.Any(w => w.Equals(JsonFlag, StringComparison.OrdinalIgnoreCase)))
        {
            json = true;
            changed = true;
            words = [.. words.Where(w => !w.Equals(JsonFlag, StringComparison.OrdinalIgnoreCase))];
            count = words.Length;
        }

        if (count >= 1 && words[count - 1].Equals("page", StringComparison.OrdinalIgnoreCase))
        {
            error = PageNeedsNumber;
            return false;
        }

        if (count >= 2 && words[count - 2].Equals("page", StringComparison.OrdinalIgnoreCase))
        {
            string number = words[count - 1].TrimStart('#');
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1)
            {
                error = PageNeedsNumber;
                return false;
            }

            changed = true;
            count -= 2;
        }

        if (changed)
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

        bool json = (args ?? string.Empty).Contains(JsonFlag, StringComparison.OrdinalIgnoreCase);
        ReplyError(context, json, "bad-page", error);
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
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("title", title);
                writer.WriteNumber("total", total);
                writer.WriteNumber("page", options.Page);
                writer.WriteNumber("pages", pages);
                writer.WriteStartArray("rows");
                for (int i = 0; i < take; i++)
                {
                    rows[first + i].WriteJson(writer);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            context.Reply(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
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

    /// <summary>Reply a single entity: its <see cref="GmRow.ToLine"/>, or <c>{"title":..,"row":{..}}</c> with <see cref="GmOutputOptions.Json"/>.</summary>
    public static void ReplyRow(CommandContext context, string title, GmRow row, GmOutputOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(row);
        if (!options.Json)
        {
            context.Reply(row.ToLine());
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("title", title);
            writer.WritePropertyName("row");
            row.WriteJson(writer);
            writer.WriteEndObject();
        }

        context.Reply(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
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

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("error", code);
            writer.WriteString("message", message);
            if (pages is { } count)
            {
                writer.WriteNumber("pages", count);
            }

            writer.WriteEndObject();
        }

        context.Reply(Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
    }

    internal static string Format(object? value) => value switch
    {
        null => "none",
        string text => text,
        bool flag => flag ? "true" : "false",
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
            case Guid id:
                writer.WriteStringValue(id);
                break;
            default:
                writer.WriteStringValue(Format(value));
                break;
        }
    }
}

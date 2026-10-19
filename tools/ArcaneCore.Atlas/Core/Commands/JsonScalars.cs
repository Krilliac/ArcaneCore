using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core.Commands;

/// <summary>
/// Reads JSON numbers whatever backs the node. A <see cref="JsonValue"/> parsed from text converts to any numeric
/// type, but one created in code (<c>new JsonObject { ["map"] = 0u }</c>) only converts to the exact CLR type it
/// holds, so <c>TryGetValue&lt;double&gt;</c> on it fails. Every number is therefore read through its JSON text.
/// </summary>
public static class JsonScalars
{
    public static bool TryGetDouble(JsonNode? node, out double value)
    {
        value = 0;
        return TryNumberText(node, out string? text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>An integral number, also accepting integral values written as <c>3.0</c> or <c>1e3</c>.</summary>
    public static bool TryGetInt64(JsonNode? node, out long value)
    {
        value = 0;
        if (!TryNumberText(node, out string? text))
        {
            return false;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            && double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) < 9.0e15)
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    private static bool TryNumberText(JsonNode? node, out string? text)
    {
        text = null;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        try
        {
            text = value.ToJsonString();
            return true;
        }
        catch (ArgumentException)
        {
            // A NaN or infinity created in code has no JSON text.
            return false;
        }
    }
}

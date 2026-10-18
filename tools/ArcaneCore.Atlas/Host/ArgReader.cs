using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// Readers for the argument shapes the host commands accept beyond <see cref="CommandInvocation"/>'s getters. Values typed
/// on a command line arrive as JSON when they look like JSON (<c>name=2024</c> is a number), so text arguments accept
/// numbers and booleans by their JSON text. All failures are <see cref="CommandException.BadArguments"/>.
/// </summary>
internal static class ArgReader
{
    public static CommandException Bad(CommandInvocation invocation, string name, string expected)
        => new(CommandException.BadArguments, $"{invocation.Definition.Name}: argument '{name}' must be {expected}.");

    /// <summary>A text argument; numbers and booleans are accepted as their JSON text. Null when absent.</summary>
    public static string? Text(CommandInvocation invocation, string name)
    {
        if (!invocation.Args.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? text))
            {
                return text;
            }

            if (value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToJsonString();
            }
        }

        throw Bad(invocation, name, "text");
    }

    public static string RequiredText(CommandInvocation invocation, string name)
    {
        string? text = Text(invocation, name);
        return string.IsNullOrWhiteSpace(text)
            ? throw new CommandException(CommandException.BadArguments, $"{invocation.Definition.Name}: argument '{name}' is required.")
            : text;
    }

    /// <summary>A list of finite numbers written as a JSON array or as text such as <c>1,2,3</c> (comma, space or slash separated).</summary>
    public static double[]? Numbers(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                var values = new double[array.Count];
                for (int i = 0; i < values.Length; i++)
                {
                    if (!JsonScalars.TryGetDouble(array[i], out values[i]))
                    {
                        return null;
                    }
                }

                return values;
            case JsonValue value when value.TryGetValue(out string? text):
                string[] parts = text.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var parsed = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]) || !double.IsFinite(parsed[i]))
                    {
                        return null;
                    }
                }

                return parsed;
            default:
                return null;
        }
    }

    /// <summary>A point written as three numbers, or as two when <paramref name="allowTwo"/> (z then comes from <paramref name="fallbackZ"/>).</summary>
    public static WorldPoint Point(CommandInvocation invocation, string name, JsonNode? node, bool allowTwo = false, double fallbackZ = 0)
    {
        double[]? values = Numbers(node);
        if (values is { Length: 3 })
        {
            return new WorldPoint(values[0], values[1], values[2]);
        }

        if (allowTwo && values is { Length: 2 })
        {
            return new WorldPoint(values[0], values[1], fallbackZ);
        }

        throw Bad(invocation, name, allowTwo ? "[x, y] or [x, y, z] with finite numbers" : "[x, y, z] with three finite numbers");
    }

    /// <summary>A finite number inside [<paramref name="min"/>, <paramref name="max"/>].</summary>
    public static double Range(CommandInvocation invocation, string name, double value, double min, double max)
        => value >= min && value <= max
            ? value
            : throw Bad(invocation, name, string.Create(CultureInfo.InvariantCulture, $"within [{min}, {max}]"));

    /// <summary>The map id, defaulting to the view's current map.</summary>
    public static uint Map(CommandInvocation invocation, ViewState current)
        => invocation.Has("map") ? invocation.GetMapId() : current.MapId;
}

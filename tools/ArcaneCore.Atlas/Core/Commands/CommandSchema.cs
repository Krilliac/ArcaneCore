using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core.Commands;

/// <summary>JSON Schema (draft 2020-12 subset) for a command's arguments: the MCP tool input schema and <c>ctl help</c>.</summary>
public static class CommandSchema
{
    public static JsonObject InputSchema(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (CommandArg arg in definition.Args)
        {
            JsonObject property = TypeSchema(arg.Type);
            property["description"] = arg.Description;
            if (arg.Default is not null)
            {
                property["default"] = arg.Default.DeepClone();
            }

            if (arg.Choices is { Count: > 0 } choices)
            {
                property["enum"] = new JsonArray([.. choices.Select(c => (JsonNode)c)]);
            }

            properties[arg.Name] = property;
            if (arg.Required)
            {
                required.Add(arg.Name);
            }
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    /// <summary>A human-readable one-command help block.</summary>
    public static string Help(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var text = new System.Text.StringBuilder();
        text.Append(definition.Name).Append("  ").AppendLine(definition.Summary);
        foreach (CommandArg arg in definition.Args)
        {
            text.Append("  ").Append(arg.Name).Append(" (").Append(arg.Type).Append(arg.Required ? ", required" : string.Empty).Append(")  ")
                .AppendLine(arg.Description);
        }

        return text.ToString();
    }

    private static JsonObject TypeSchema(ArgType type) => type switch
    {
        ArgType.String => new JsonObject { ["type"] = "string" },
        ArgType.Integer => new JsonObject { ["type"] = "integer" },
        ArgType.Number => new JsonObject { ["type"] = "number" },
        ArgType.Boolean => new JsonObject { ["type"] = "boolean" },
        ArgType.Point3 => new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "number" },
            ["minItems"] = 3,
            ["maxItems"] = 3,
        },
        ArgType.StringList => new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        ArgType.Object => new JsonObject { ["type"] = "object" },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

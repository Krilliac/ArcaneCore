using System.Globalization;
using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core.Commands;

/// <summary>JSON shape of a command argument (also its JSON schema type).</summary>
public enum ArgType
{
    String,
    Integer,
    Number,
    Boolean,

    /// <summary>A world point as <c>[x, y, z]</c>.</summary>
    Point3,

    /// <summary>A list of strings.</summary>
    StringList,

    /// <summary>Any JSON object (described in the argument's description).</summary>
    Object,
}

/// <summary>One argument of a command.</summary>
public sealed record CommandArg(
    string Name,
    ArgType Type,
    string Description,
    bool Required = false,
    JsonNode? Default = null,
    IReadOnlyList<string>? Choices = null);

/// <summary>Which surfaces offer a command. Every command is reachable from HTTP and the CLI.</summary>
[Flags]
public enum CommandSurfaces
{
    None = 0,

    /// <summary>Works without a running UI (<c>run</c>, <c>script</c>, headless MCP).</summary>
    Headless = 1,

    /// <summary>Needs a running <c>serve</c> instance (it changes or reads the shared view).</summary>
    Instance = 2,

    /// <summary>Listed in the UI command palette.</summary>
    Palette = 4,

    All = Headless | Instance | Palette,
}

/// <summary>
/// A command: a dotted name, typed arguments and a handler returning JSON. The same definition serves the UI
/// palette, HTTP <c>POST /api/command</c>, <c>arcane-atlas ctl|run|script</c> and MCP tools.
/// </summary>
public sealed record CommandDefinition(
    string Name,
    string Summary,
    IReadOnlyList<CommandArg> Args,
    Func<CommandInvocation, CancellationToken, Task<JsonNode?>> Handler,
    CommandSurfaces Surfaces = CommandSurfaces.All);

/// <summary>A command failure with a stable machine-readable code.</summary>
public sealed class CommandException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Arguments missing, mistyped or out of range.</summary>
    public const string BadArguments = "bad-args";

    /// <summary>No command of that name.</summary>
    public const string UnknownCommand = "unknown-command";

    /// <summary>The command needs data, a client directory, a UI or a live connection that is not available.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The handler failed.</summary>
    public const string Failed = "failed";

    public string Code { get; } = code;
}

/// <summary>Outcome of one command, as every surface reports it.</summary>
public sealed record CommandResult(string Command, bool Ok, JsonNode? Data, string? ErrorCode, string? ErrorMessage, double ElapsedMs)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["command"] = Command, ["ok"] = Ok, ["elapsedMs"] = Math.Round(ElapsedMs, 3) };
        if (Ok)
        {
            json["data"] = Data?.DeepClone();
        }
        else
        {
            json["error"] = new JsonObject { ["code"] = ErrorCode, ["message"] = ErrorMessage };
        }

        return json;
    }
}

/// <summary>Who is calling, for the command log and surface checks.</summary>
public enum CommandOrigin
{
    Ui,
    Http,
    Cli,
    Script,
    Mcp,
}

/// <summary>Arguments plus the services a handler may use.</summary>
public sealed class CommandInvocation(CommandDefinition definition, JsonObject args, IServiceProvider services, CommandOrigin origin, bool headless)
{
    public CommandDefinition Definition { get; } = definition;

    public JsonObject Args { get; } = args;

    public IServiceProvider Services { get; } = services;

    public CommandOrigin Origin { get; } = origin;

    /// <summary>True when no <c>serve</c> instance or UI is behind this call.</summary>
    public bool Headless { get; } = headless;

    public T Service<T>()
        where T : class
        => Services.GetService(typeof(T)) as T
            ?? throw new CommandException(CommandException.Unavailable, $"{Definition.Name} needs {typeof(T).Name}, which this host does not provide.");

    public bool Has(string name) => Args.TryGetPropertyValue(name, out JsonNode? node) && node is not null;

    public string GetString(string name)
        => OptionalString(name) ?? throw Missing(name);

    public string? OptionalString(string name)
    {
        JsonNode? node = Value(name);
        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) => s,
            _ => throw Bad(name, "a string"),
        };
    }

    public long GetInt(string name) => OptionalInt(name) ?? throw Missing(name);

    public long? OptionalInt(string name)
    {
        JsonNode? node = Value(name);
        if (node is null)
        {
            return null;
        }

        if (JsonScalars.TryGetInt64(node, out long l))
        {
            return l;
        }

        if (node is JsonValue v && v.TryGetValue(out string? s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        throw Bad(name, "an integer");
    }

    public double GetNumber(string name) => OptionalNumber(name) ?? throw Missing(name);

    public double? OptionalNumber(string name)
    {
        JsonNode? node = Value(name);
        if (node is null)
        {
            return null;
        }

        if (JsonScalars.TryGetDouble(node, out double d))
        {
            return d;
        }

        if (node is JsonValue v && v.TryGetValue(out string? s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed))
        {
            return parsed;
        }

        throw Bad(name, "a finite number");
    }

    public bool GetBool(string name, bool fallback) => OptionalBool(name) ?? fallback;

    public bool? OptionalBool(string name)
    {
        JsonNode? node = Value(name);
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue(out bool b))
            {
                return b;
            }

            if (v.TryGetValue(out string? s) && bool.TryParse(s, out bool parsed))
            {
                return parsed;
            }
        }

        throw Bad(name, "true or false");
    }

    public WorldPoint GetPoint(string name) => OptionalPoint(name) ?? throw Missing(name);

    /// <summary>A point given as <c>[x, y, z]</c> or <c>"x,y,z"</c> (z required).</summary>
    public WorldPoint? OptionalPoint(string name)
    {
        JsonNode? node = Value(name);
        if (node is null)
        {
            return null;
        }

        double[]? values = node switch
        {
            JsonArray a when a.Count == 3 => a.Select(n => JsonScalars.TryGetDouble(n, out double d) ? d : double.NaN).ToArray(),
            JsonValue v when v.TryGetValue(out string? s) => ParseTriple(s),
            _ => null,
        };
        if (values is null || values.Length != 3 || values.Any(d => !double.IsFinite(d)))
        {
            throw Bad(name, "[x, y, z] with three finite numbers");
        }

        return new WorldPoint(values[0], values[1], values[2]);
    }

    public IReadOnlyList<string> GetStringList(string name)
    {
        JsonNode? node = Value(name);
        return node switch
        {
            null => [],
            JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : throw Bad(name, "a list of strings")).ToArray(),
            JsonValue v when v.TryGetValue(out string? s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => throw Bad(name, "a list of strings"),
        };
    }

    public JsonObject? OptionalObject(string name)
        => Value(name) switch
        {
            null => null,
            JsonObject o => o,
            _ => throw Bad(name, "a JSON object"),
        };

    /// <summary>Map id argument (<c>map</c>), checked to be a non-negative 32-bit value.</summary>
    public uint GetMapId(string name = "map")
    {
        long value = GetInt(name);
        return value is >= 0 and <= uint.MaxValue ? (uint)value : throw Bad(name, "a map id (0 or more)");
    }

    private JsonNode? Value(string name) => Args.TryGetPropertyValue(name, out JsonNode? node) ? node : null;

    private CommandException Missing(string name)
        => new(CommandException.BadArguments, $"{Definition.Name}: argument '{name}' is required.");

    private CommandException Bad(string name, string expected)
        => new(CommandException.BadArguments, $"{Definition.Name}: argument '{name}' must be {expected}.");

    private static double[]? ParseTriple(string text)
    {
        string[] parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return null;
        }

        var values = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values;
    }
}

/// <summary>Implemented by each feature area to add its commands.</summary>
public interface ICommandModule
{
    void Register(CommandRegistry registry);
}

/// <summary>All commands of one Atlas process. Thread-safe once registration has finished.</summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDefinition> _commands = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <summary>Raised after every invocation (the UI's automation log and the status bar listen).</summary>
    public event Action<CommandResult, CommandOrigin>? Invoked;

    public IReadOnlyCollection<CommandDefinition> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal)];
            }
        }
    }

    public void Register(CommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
        {
            throw new ArgumentException($"Command name '{definition.Name}' must be ASCII letters, digits, '.' or '-'.");
        }

        if (definition.Args.Select(a => a.Name).Distinct(StringComparer.Ordinal).Count() != definition.Args.Count)
        {
            throw new ArgumentException($"Command '{definition.Name}' declares an argument twice.");
        }

        lock (_gate)
        {
            if (_frozen)
            {
                throw new InvalidOperationException("Commands cannot be registered after the registry is frozen.");
            }

            if (!_commands.TryAdd(definition.Name, definition))
            {
                throw new ArgumentException($"Command '{definition.Name}' is already registered.");
            }
        }
    }

    public void Register(ICommandModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        module.Register(this);
    }

    /// <summary>Stops further registration; the host calls this once startup is done.</summary>
    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }

    public bool TryGet(string name, out CommandDefinition definition)
    {
        lock (_gate)
        {
            return _commands.TryGetValue(name, out definition!);
        }
    }

    /// <summary>
    /// Runs a command. Never throws for command failures: unknown names, bad arguments and handler exceptions become a
    /// failed <see cref="CommandResult"/> (cancellation still propagates).
    /// </summary>
    public async Task<CommandResult> InvokeAsync(
        string name,
        JsonObject? args,
        IServiceProvider services,
        CommandOrigin origin,
        bool headless,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        CommandResult result;
        if (!TryGet(name, out CommandDefinition? definition))
        {
            result = new CommandResult(name, false, null, CommandException.UnknownCommand, $"No command named '{name}'.", 0);
        }
        else if (headless && !definition.Surfaces.HasFlag(CommandSurfaces.Headless))
        {
            result = new CommandResult(name, false, null, CommandException.Unavailable,
                $"{name} needs a running 'arcane-atlas serve' instance.", 0);
        }
        else
        {
            args ??= [];
            try
            {
                CheckArguments(definition, args);
                JsonNode? data = await definition.Handler(new CommandInvocation(definition, args, services, origin, headless), cancellationToken)
                    .ConfigureAwait(false);
                result = new CommandResult(name, true, data, null, null, Elapsed(start));
            }
            catch (CommandException e)
            {
                result = new CommandResult(name, false, null, e.Code, e.Message, Elapsed(start));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or FormatException or NotSupportedException)
            {
                result = new CommandResult(name, false, null, CommandException.Failed, $"{e.GetType().Name}: {e.Message}", Elapsed(start));
            }
        }

        Invoked?.Invoke(result, origin);
        return result;
    }

    private static void CheckArguments(CommandDefinition definition, JsonObject args)
    {
        foreach ((string key, _) in args)
        {
            if (!definition.Args.Any(a => string.Equals(a.Name, key, StringComparison.Ordinal)))
            {
                throw new CommandException(CommandException.BadArguments,
                    $"{definition.Name}: unknown argument '{key}' (expected {string.Join(", ", definition.Args.Select(a => a.Name))}).");
            }
        }

        foreach (CommandArg arg in definition.Args)
        {
            bool present = args.TryGetPropertyValue(arg.Name, out JsonNode? value) && value is not null;
            if (arg.Required && !present)
            {
                throw new CommandException(CommandException.BadArguments, $"{definition.Name}: argument '{arg.Name}' is required.");
            }

            if (present && arg.Choices is { Count: > 0 } choices
                && !(value is JsonValue v && v.TryGetValue(out string? s) && choices.Contains(s, StringComparer.Ordinal)))
            {
                throw new CommandException(CommandException.BadArguments,
                    $"{definition.Name}: argument '{arg.Name}' must be one of {string.Join(", ", choices)}.");
            }
        }
    }

    private static double Elapsed(long start) => System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}

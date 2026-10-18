using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Core;

public sealed class CommandRegistryTests
{
    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static CommandRegistry Registry()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDefinition(
            "query.height",
            "Height at a point.",
            [
                new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                new CommandArg("at", ArgType.Point3, "Point.", Required: true),
                new CommandArg("mode", ArgType.String, "Which height.", Choices: ["terrain", "model"]),
                new CommandArg("layers", ArgType.StringList, "Layers."),
            ],
            (call, _) => Task.FromResult<JsonNode?>(new JsonObject
            {
                ["map"] = call.GetMapId(),
                ["z"] = call.GetPoint("at").Z,
                ["mode"] = call.OptionalString("mode") ?? "terrain",
                ["layers"] = call.GetStringList("layers").Count,
            })));
        registry.Register(new CommandDefinition(
            "view.get",
            "The current view.",
            [],
            (call, _) => Task.FromResult<JsonNode?>(call.Service<ViewStore>().Current.ToJson()),
            CommandSurfaces.Instance));
        registry.Register(new CommandDefinition(
            "broken",
            "Throws.",
            [],
            (_, _) => throw new InvalidDataException("bad file")));
        return registry;
    }

    private static Task<CommandResult> Run(CommandRegistry registry, string name, string args, bool headless = true)
        => registry.InvokeAsync(name, JsonNode.Parse(args)!.AsObject(), new NoServices(), CommandOrigin.Cli, headless, CancellationToken.None);

    [Fact]
    public async Task TypedArguments_AcceptJsonAndCliStrings()
    {
        CommandRegistry registry = Registry();

        CommandResult json = await Run(registry, "query.height", """{"map":0,"at":[1,2,3.5],"layers":["a","b"]}""");
        CommandResult cli = await Run(registry, "query.height", """{"map":"0","at":"1,2,3.5","mode":"model","layers":"a,b,c"}""");

        Assert.True(json.Ok, json.ErrorMessage);
        Assert.Equal(3.5, json.Data!["z"]!.GetValue<double>());
        Assert.Equal(2, json.Data["layers"]!.GetValue<int>());
        Assert.True(cli.Ok, cli.ErrorMessage);
        Assert.Equal("model", cli.Data!["mode"]!.GetValue<string>());
        Assert.Equal(3, cli.Data["layers"]!.GetValue<int>());
    }

    [Fact]
    public async Task NumbersBuiltInCode_AreReadLikeParsedOnes()
    {
        // JsonValue created from a CLR uint/float only converts to that exact type; the getters must not care.
        var args = new JsonObject { ["map"] = 1u, ["at"] = new JsonArray(1.0, 2f, (short)3) };

        CommandResult result = await Registry().InvokeAsync(
            "query.height", args, new NoServices(), CommandOrigin.Script, headless: true, CancellationToken.None);

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(1u, result.Data!["map"]!.GetValue<uint>());
        Assert.Equal(3.0, result.Data["z"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("""{"at":[1,2,3]}""")]
    [InlineData("""{"map":0}""")]
    [InlineData("""{"map":-1,"at":[1,2,3]}""")]
    [InlineData("""{"map":0,"at":[1,2]}""")]
    [InlineData("""{"map":0,"at":"1,2,nan"}""")]
    [InlineData("""{"map":0,"at":[1,2,3],"mode":"sideways"}""")]
    [InlineData("""{"map":0,"at":[1,2,3],"extra":1}""")]
    [InlineData("""{"map":1.5,"at":[1,2,3]}""")]
    public async Task BadArguments_FailWithBadArgs(string args)
    {
        CommandResult result = await Run(Registry(), "query.height", args);

        Assert.False(result.Ok);
        Assert.Equal(CommandException.BadArguments, result.ErrorCode);
    }

    [Fact]
    public async Task UnknownCommands_HandlerFailures_AndSurfaceChecks_AreResultsNotExceptions()
    {
        CommandRegistry registry = Registry();

        CommandResult unknown = await Run(registry, "nope", "{}");
        CommandResult broken = await Run(registry, "broken", "{}");
        CommandResult headless = await Run(registry, "view.get", "{}");
        CommandResult noStore = await Run(registry, "view.get", "{}", headless: false);

        Assert.Equal(CommandException.UnknownCommand, unknown.ErrorCode);
        Assert.Equal(CommandException.Failed, broken.ErrorCode);
        Assert.Contains("bad file", broken.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(CommandException.Unavailable, headless.ErrorCode);
        Assert.Equal(CommandException.Unavailable, noStore.ErrorCode);
        Assert.False(broken.ToJson()["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Invoked_ReportsEveryCall()
    {
        CommandRegistry registry = Registry();
        var names = new List<string>();
        registry.Invoked += (result, _) => names.Add(result.Command);

        await Run(registry, "nope", "{}");
        await Run(registry, "query.height", """{"map":0,"at":[0,0,0]}""");

        Assert.Equal(new[] { "nope", "query.height" }, names);
    }

    [Fact]
    public void Registration_RefusesDuplicates_BadNames_AndLateCommands()
    {
        CommandRegistry registry = Registry();
        static Task<JsonNode?> Nothing(CommandInvocation call, CancellationToken ct) => Task.FromResult<JsonNode?>(null);

        Assert.Throws<ArgumentException>(() => registry.Register(new CommandDefinition("broken", "again", [], Nothing)));
        Assert.Throws<ArgumentException>(() => registry.Register(new CommandDefinition("has space", "x", [], Nothing)));
        Assert.Throws<ArgumentException>(() => registry.Register(new CommandDefinition(
            "twice", "x", [new CommandArg("a", ArgType.String, "a"), new CommandArg("a", ArgType.String, "a")], Nothing)));
        registry.Freeze();
        Assert.Throws<InvalidOperationException>(() => registry.Register(new CommandDefinition("late", "x", [], Nothing)));
        Assert.Equal(new[] { "broken", "query.height", "view.get" }, registry.All.Select(c => c.Name));
    }

    [Fact]
    public void Schema_ListsTypesRequiredAndChoices()
    {
        Assert.True(Registry().TryGet("query.height", out CommandDefinition definition));
        JsonObject schema = CommandSchema.InputSchema(definition);

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(new[] { "map", "at" }, schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("integer", schema["properties"]!["map"]!["type"]!.GetValue<string>());
        Assert.Equal(3, schema["properties"]!["at"]!["minItems"]!.GetValue<int>());
        Assert.Equal(new[] { "terrain", "model" }, schema["properties"]!["mode"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("query.height", CommandSchema.Help(definition), StringComparison.Ordinal);
    }
}

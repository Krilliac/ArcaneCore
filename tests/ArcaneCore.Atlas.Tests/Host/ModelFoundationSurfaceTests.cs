using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host.Cli;
using ArcaneCore.Atlas.Reference.Client.Models;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Render;
using ArcaneCore.Atlas.Textures;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>
/// The surface rule: every command is reachable the same way from the CLI, scripts, MCP and HTTP, so each A1 command appears in
/// the registry's help, in MCP <c>tools/list</c> (headless included, because none of them needs a running instance), in
/// <c>/api/commands</c>, and answers through <see cref="LocalCommandClient"/> for each origin.
/// </summary>
public sealed class ModelFoundationSurfaceTests
{
    private static readonly string[] Commands = ["texture.get", "texture.stats", "model.package", "display.resolve"];

    private static void RegisterAll(AtlasContext context)
    {
        context.Services.Add<IClientFileSource>(new MemoryClientFiles());
        new TexturesModule().Register(context);
        new ModelPackagesModule().Register(context);
        new DisplayModule().Register(context);
    }

    [Fact]
    public async Task EveryCommand_IsInTheRegistry_WithADescription_AndAnArgumentSchema()
    {
        await using HostRig rig = HostRig.Create(headless: true, register: RegisterAll);

        foreach (string name in Commands)
        {
            CommandDefinition command = Assert.Single(rig.Context.Commands.All, c => c.Name == name);
            Assert.False(string.IsNullOrWhiteSpace(command.Summary), name);
            Assert.True(command.Surfaces.HasFlag(CommandSurfaces.Headless), name);
        }

        Assert.Contains(rig.Context.Commands.All, c => c.Name == "texture.get" && c.Args.Any(a => a.Name == "maxSize"));
    }

    [Fact]
    public async Task EveryCommand_IsAnMcpTool()
    {
        await using HostRig rig = HostRig.Create(headless: true, register: RegisterAll);
        var client = new LocalCommandClient(rig.Context, CommandOrigin.Mcp);
        await using var session = new McpSession(client);
        await session.HandshakeAsync();

        JsonNode list = await session.RequestAsync(McpSession.List(1), 1);

        string[] listed = [.. list.At("result.tools")!.AsArray().Select(t => t.Str("name"))];
        foreach (string name in Commands)
        {
            Assert.Contains(McpServer.ToolName(name), listed);
        }
    }

    [Fact]
    public async Task EveryCommand_IsInTheHttpCatalogue()
    {
        await using HostRig rig = await HostRig.StartAsync(register: RegisterAll);

        using HttpResponseMessage response = await rig.Http.GetAsync("api/commands");
        JsonNode body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        string[] names = [.. body.AsArray().Select(c => c.Str("name"))];
        foreach (string name in Commands)
        {
            Assert.Contains(name, names);
        }
    }

    [Theory]
    [InlineData(CommandOrigin.Cli)]
    [InlineData(CommandOrigin.Script)]
    [InlineData(CommandOrigin.Mcp)]
    public async Task EveryOrigin_ReachesTheSameAnswer(CommandOrigin origin)
    {
        await using HostRig rig = HostRig.Create(headless: true, register: RegisterAll);
        var client = new LocalCommandClient(rig.Context, origin);

        JsonObject stats = await client.InvokeAsync("texture.stats", null, CancellationToken.None);
        JsonObject resolve = await client.InvokeAsync("display.resolve", new JsonObject { ["kind"] = "creature", ["id"] = 1 }, CancellationToken.None);

        Assert.True(stats.Flag("ok"), stats.ToJsonString());
        Assert.Equal(0, stats.Num("data.count"));
        Assert.True(resolve.Flag("ok"), resolve.ToJsonString());
        Assert.Equal("no-row", resolve["data"]!["problems"]![0]!.GetValue<string>());
    }

    [Fact]
    public void TheModules_AreDiscoveredByCompose_InAWellFormedOrder()
    {
        IAtlasModule[] discovered = [.. AtlasModules.Discover(typeof(AtlasModules).Assembly)];

        foreach (Type type in new[] { typeof(TexturesModule), typeof(RenderMaterialsModule), typeof(ModelPackagesModule), typeof(DisplayModule) })
        {
            Assert.Contains(discovered, m => m.GetType() == type);
        }

        Assert.Equal(discovered.Length, discovered.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.True(new TexturesModule().Order < new ModelPackagesModule().Order);
    }
}

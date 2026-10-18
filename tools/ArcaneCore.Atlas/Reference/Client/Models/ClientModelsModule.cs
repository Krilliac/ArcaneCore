using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// Lane R3: the client WMO and M2 readers behind the <c>ref.wmo</c>, <c>ref.m2.collision</c> and <c>ref.m2.render</c>
/// layers and the <c>ref.model.info</c> and <c>ref.query.los</c> commands. Client files and placements come from the
/// services the foundation and terrain modules register; without them the layers report Missing with the reason.
/// </summary>
public sealed class ClientModelsModule : IAtlasModule
{
    public string Name => "Reference.Client.Models";

    public int Order => ModuleOrder.Layers;

    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ILogger log = context.Loggers.CreateLogger("ArcaneCore.Atlas.Reference.Client.Models");
        var cache = new ClientModelCache();
        var queries = new ReferenceModelQueries(context.Services, cache);
        context.Layers.Add(new ClientModelLayerSource(context.Services, cache, log));
        context.Services.Add(cache);
        context.Services.Add(queries);
        foreach (CommandDefinition command in CommandsFor(queries))
        {
            try
            {
                context.Commands.Register(command);
            }
            catch (ArgumentException e)
            {
                // Another lane already owns the name; the layers above stay registered.
                string problem = $"Module {Name} did not register command {command.Name}: {e.Message}";
                context.ReportProblem(problem);
                log.LogWarning("{Problem}", problem);
            }
        }
    }

    internal static IReadOnlyList<CommandDefinition> CommandsFor(ReferenceModelQueries queries) =>
    [
        new CommandDefinition(
            "ref.model.info",
            "Describe a client WMO (header, groups, doodad sets, extractor skip rules) or M2 (vertices, views, bounding hull).",
            [
                new CommandArg(
                    "path",
                    ArgType.String,
                    "Client path of the model, for example World\\wmo\\Azeroth\\Buildings\\GoldshireInn\\GoldshireInn.wmo or a .m2/.mdx doodad.",
                    Required: true),
            ],
            (invocation, _) => Task.FromResult<JsonNode?>(queries.ModelInfo(invocation.GetString("path")))),
        new CommandDefinition(
            "ref.query.los",
            "Cast a segment against the client's WMO and M2 collision triangles of the tiles it crosses: spell (ignores M2) and movement (with M2) hits.",
            [
                new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                new CommandArg("a", ArgType.Point3, "Segment start in world coordinates [x, y, z].", Required: true),
                new CommandArg("b", ArgType.Point3, "Segment end in world coordinates [x, y, z].", Required: true),
                new CommandArg(
                    "includeSkipped",
                    ArgType.Boolean,
                    "Also test triangles the vmap extractor drops (unreachable and antiportal WMO groups, destructible WMOs, doodads no kept group references).",
                    Default: JsonValue.Create(false)),
            ],
            (invocation, cancellationToken) => Task.FromResult<JsonNode?>(queries.LineOfSight(
                invocation.GetMapId(),
                invocation.GetPoint("a"),
                invocation.GetPoint("b"),
                invocation.GetBool("includeSkipped", false),
                cancellationToken))),
    ];
}

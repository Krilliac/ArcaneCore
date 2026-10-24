using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Reference.Display;

/// <summary>The <c>display.resolve</c> command: creature or game object display id to model path and skins.</summary>
public sealed class DisplayModule : IAtlasModule
{
    public string Name => "display";

    public int Order => ModuleOrder.Foundation + 60;

    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = new State(context.Services.Get<IClientFileSource>());
        context.Services.Add(state);
        context.Commands.Register(new CommandDefinition(
            "display.resolve",
            "Resolves a creature or game object display id through the client's display DBCs to the model path (.m2) and skin texture paths. A break in the chain is reported in problems (no-row, model-missing, texture-missing), not as an error.",
            [
                new CommandArg("kind", ArgType.String, "creature or gameobject.", Required: true, Choices: ["creature", "gameobject"]),
                new CommandArg("id", ArgType.Integer, "The display id (CreatureDisplayInfo or GameObjectDisplayInfo).", Required: true),
            ],
            state.Resolve));
    }

    /// <summary>The lazily loaded tables and resolver, shared by the commands and other modules.</summary>
    public sealed class State(IClientFileSource? files)
    {
        private readonly Lock _gate = new();
        private DisplayResolver? _resolver;

        public DisplayResolver Resolver
        {
            get
            {
                if (files is null)
                {
                    throw new CommandException(CommandException.Unavailable, "display.resolve needs the client files; set ARCANECORE_CLIENT_DIR (see client.status).");
                }

                lock (_gate)
                {
                    try
                    {
                        return _resolver ??= new DisplayResolver(DisplayTables.Load(files), files);
                    }
                    catch (InvalidDataException e)
                    {
                        throw new CommandException(CommandException.Failed, $"display tables: {e.Message}", e);
                    }
                }
            }
        }

        public Task<JsonNode?> Resolve(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            string kind = invocation.OptionalString("kind") ?? "";
            if (kind is not ("creature" or "gameobject"))
            {
                throw new CommandException(CommandException.BadArguments, "display.resolve: kind must be creature or gameobject.");
            }

            long id = invocation.GetInt("id");
            if (id is < 0 or > uint.MaxValue)
            {
                throw new CommandException(CommandException.BadArguments, "display.resolve: id must be a non-negative 32-bit whole number.");
            }

            DisplayResolver resolver = Resolver;
            return Task.Run<JsonNode?>(
                () =>
                {
                    DisplayResolution r = kind == "creature" ? resolver.ResolveCreature(id) : resolver.ResolveGameObject(id);
                    return new JsonObject
                    {
                        ["kind"] = kind,
                        ["id"] = id,
                        ["modelPath"] = r.ModelPath,
                        ["modelDataId"] = r.ModelDataId,
                        ["textures"] = new JsonArray([.. r.Textures.Select(t => (JsonNode?)(t is null ? null : JsonValue.Create(t)))]),
                        ["problems"] = new JsonArray([.. r.Problems.Select(p => (JsonNode?)JsonValue.Create(p))]),
                    };
                },
                cancellationToken);
        }
    }
}

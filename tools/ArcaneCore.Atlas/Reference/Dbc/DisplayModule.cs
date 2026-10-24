using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Reference.Dbc;

/// <summary>The <c>display.resolve</c> command: creature, game object, item or race display to model path, textures and the data the chain carries.</summary>
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
"Resolves a creature, game object, item or race display through the client's display DBCs. Creature: model path (.m2), skin texture paths, scale and the humanoid extra row (race, sex, look, equipment, baked texture). Game object: model path. Item: model and texture paths, icon, geoset groups, helmet visibility and the eight armour region textures. Race: the Character\\<race>\\<sex> model for a gender. A break in the chain is reported in problems (no-row, model-missing, texture-missing, extra-missing), not as an error.",
            [
                new CommandArg("kind", ArgType.String, "creature, gameobject, item or race.", Required: true, Choices: [.. DisplayResolver.Kinds]),
                new CommandArg("id", ArgType.Integer, "The display id (CreatureDisplayInfo, GameObjectDisplayInfo or ItemDisplayInfo) or, for kind race, the ChrRaces id.", Required: true),
                new CommandArg("gender", ArgType.Integer, "Race only: 0 male (default) or 1 female.", Default: JsonValue.Create(0)),
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
            if (!DisplayResolver.Kinds.Contains(kind))
            {
                throw new CommandException(CommandException.BadArguments, $"display.resolve: kind must be one of {string.Join(", ", DisplayResolver.Kinds)}.");
            }

            long id = invocation.GetInt("id");
            if (id is < 0 or > uint.MaxValue)
            {
                throw new CommandException(CommandException.BadArguments, "display.resolve: id must be a non-negative 32-bit whole number.");
            }

            long gender = invocation.OptionalInt("gender") ?? 0;
            if (gender is < 0 or > 1)
            {
                throw new CommandException(CommandException.BadArguments, "display.resolve: gender must be 0 (male) or 1 (female).");
            }

            DisplayResolver resolver = Resolver;
            return Task.Run<JsonNode?>(
                () =>
                {
                    DisplayResolution r = resolver.Resolve(kind, id, (int)gender);
                    var result = new JsonObject
                    {
                        ["kind"] = kind,
                        ["id"] = id,
                        ["modelPath"] = r.ModelPath,
                        ["modelDataId"] = r.ModelDataId,
                        ["textures"] = Strings(r.Textures),
                        ["problems"] = Strings(r.Problems),
                    };
                    if (r.Scale is { } scale)
                    {
                        result["scale"] = scale;
                        result["displayScale"] = r.DisplayScale;
                        result["modelScale"] = r.ModelScale;
                    }

                    if (r.Extra is { } extra)
                    {
                        result["extra"] = new JsonObject
                        {
                            ["race"] = extra.Race,
                            ["sex"] = extra.Sex,
                            ["skin"] = extra.Skin,
                            ["face"] = extra.Face,
                            ["hairStyle"] = extra.HairStyle,
                            ["hairColor"] = extra.HairColor,
                            ["facialHair"] = extra.FacialHair,
                            ["equipment"] = new JsonArray([.. extra.Equipment.Select(e => (JsonNode?)JsonValue.Create(e))]),
                            ["bakedTexture"] = extra.BakedTexture,
                        };
                    }

                    if (r.Item is { } item)
                    {
                        result["item"] = new JsonObject
                        {
                            ["models"] = Strings(item.Models),
                            ["modelTextures"] = Strings(item.ModelTextures),
                            ["icon"] = item.Icon,
                            ["geosetGroups"] = new JsonArray([.. item.GeosetGroups.Select(e => (JsonNode?)JsonValue.Create(e))]),
                            ["helmetGeosetVis"] = new JsonArray([.. item.HelmetGeosetVis.Select(e => (JsonNode?)JsonValue.Create(e))]),
                            ["regionTextures"] = Strings(item.RegionTextures),
                        };
                    }

                    if (r.Race is { } race)
                    {
                        result["race"] = new JsonObject
                        {
                            ["raceId"] = race.RaceId,
                            ["gender"] = race.Gender,
                            ["clientFile"] = race.ClientFile,
                            ["maleDisplayId"] = race.MaleDisplayId,
                            ["femaleDisplayId"] = race.FemaleDisplayId,
                        };
                    }

                    return result;
                },
                cancellationToken);
        }

        private static JsonArray Strings(IEnumerable<string?> values)
            => new([.. values.Select(v => (JsonNode?)(v is null ? null : JsonValue.Create(v)))]);
    }
}

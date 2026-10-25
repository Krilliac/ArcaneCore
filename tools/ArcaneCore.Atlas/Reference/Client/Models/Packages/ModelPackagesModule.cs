using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Reference.Dbc;
using ArcaneCore.Atlas.Render;

namespace ArcaneCore.Atlas.Reference.Client.Models;

/// <summary>
/// Model packages for the textured views (design section 2.5): the <see cref="ModelPackageCache"/> service and cache participant,
/// the <c>GET /api/bin/model</c> endpoint and the commands <c>model.info</c> and <c>model.package</c>. Registers without a client
/// too; the commands and the endpoint then report that the client files are not configured.
/// </summary>
public sealed class ModelPackagesModule : IAtlasModule
{
    public string Name => "model-packages";

    public int Order => ModuleOrder.Foundation + 70;

    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IClientFileSource? files = context.Services.Get<IClientFileSource>();
        ModelPackageCache? cache = files is null ? null : new ModelPackageCache(files);
        if (cache is not null)
        {
            context.Services.Add(cache);
            context.CacheParticipants.Add(cache);
            context.OnComposed(cache.Freeze);
        }

        context.BinaryEndpoints.Add(new ModelEndpoint(cache));
        var handlers = new Handlers(context.Services, files, cache);
        context.Commands.Register(new CommandDefinition(
            "model.info",
            "Describes a client M2 model for packaging: version, vertex count, bounds, every view (triangles, submeshes, texture units), every texture entry (type, flags, path, whether the file exists) and the submesh ids. Give path, or kind and id to go through the display DBCs. ref.model.info describes the same file for collision (WMO groups, doodad sets, bounding hull).",
            [
                new CommandArg("path", ArgType.String, "In-archive path of an .m2 (or .mdx) model, for example Creature\\Wolf\\Wolf.m2."),
                new CommandArg("kind", ArgType.String, "creature or gameobject; with id, resolves the model through the display DBCs.", Choices: ["creature", "gameobject"]),
                new CommandArg("id", ArgType.Integer, "The display id for kind."),
            ],
            handlers.Info));
        context.Commands.Register(new CommandDefinition(
            "model.package",
            "Builds the textured package of a client M2 (one ATG1 v2 chunk: positions, normals, uv0 and one batch per texture unit with a generic material) and reports its batches. With out, writes the ATG1 bytes.",
            [
                new CommandArg("key", ArgType.String, "Model key, m2:<path>. Give key or path."),
                new CommandArg("path", ArgType.String, "In-archive path of the model (the same as key=m2:<path>)."),
                new CommandArg("view", ArgType.Integer, "Embedded view (level of detail) 0 to 3; default 0."),
                new CommandArg("out", ArgType.String, "Path of an .atg file to write; must lie outside every git checkout. Absolute unless the command runs in your own process."),
            ],
            handlers.Package));
    }

    private sealed class Handlers(AtlasServices services, IClientFileSource? files, ModelPackageCache? cache)
    {
        public Task<JsonNode?> Info(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            string? path = invocation.OptionalString("path");
            string? kind = invocation.OptionalString("kind");
            long? id = invocation.OptionalInt("id");
            if ((path is null) == (kind is null) || (kind is null) != (id is null))
            {
                throw new CommandException(CommandException.BadArguments, "model.info: give either path, or kind together with id.");
            }

            IClientFileSource source = RequireFiles("model.info");
            return Task.Run<JsonNode?>(
                () =>
                {
                    try
                    {
                        JsonObject? display = null;
                        if (kind is not null)
                        {
                            DisplayModule.State state = services.Get<DisplayModule.State>()
                                ?? throw new CommandException(CommandException.Unavailable, "model.info with kind and id needs the display module.");
                            DisplayResolution resolution = kind == "creature" ? state.Resolver.ResolveCreature(id!.Value) : state.Resolver.ResolveGameObject(id!.Value);
                            display = new JsonObject
                            {
                                ["kind"] = kind,
                                ["id"] = id,
                                ["modelPath"] = resolution.ModelPath,
                                ["textures"] = new JsonArray([.. resolution.Textures.Select(t => (JsonNode?)(t is null ? null : JsonValue.Create(t)))]),
                                ["problems"] = new JsonArray([.. resolution.Problems.Select(p => (JsonNode?)JsonValue.Create(p))]),
                            };
                            path = resolution.ModelPath ?? throw new CommandException(
                                CommandException.Failed,
                                $"model.info: display {kind} {id} did not resolve to a model ({string.Join(", ", resolution.Problems)}).");
                        }

                        JsonObject info = Describe(source, path!);
                        if (display is not null)
                        {
                            info["display"] = display;
                        }

                        return info;
                    }
                    catch (ModelPackageException e)
                    {
                        throw Map("model.info", e);
                    }
                },
                cancellationToken);
        }

        public Task<JsonNode?> Package(CommandInvocation invocation, CancellationToken cancellationToken)
        {
            string? key = invocation.OptionalString("key");
            string? path = invocation.OptionalString("path");
            if ((key is null) == (path is null))
            {
                throw new CommandException(CommandException.BadArguments, "model.package: give exactly one of 'key' or 'path'.");
            }

            long view = invocation.OptionalInt("view") ?? 0;
            if (view is < 0 or > ModelPackageCache.MaxView)
            {
                throw new CommandException(CommandException.BadArguments, $"model.package: view must be 0 to {ModelPackageCache.MaxView}.");
            }

            string? outArg = invocation.OptionalString("out");
            string? outPath = outArg is null ? null : OutputPaths.Resolve("model.package", outArg, ".atg", invocation.Headless);
            ModelPackageCache models = cache ?? throw ClientMissing("model.package");
            string request = key ?? path!;
            return Task.Run<JsonNode?>(
                () =>
                {
                    try
                    {
                        ModelPackage package = models.Get(request, (int)view, cancellationToken);
                        var batches = new JsonArray();
                        foreach (ModelBatchInfo batch in package.Batches)
                        {
                            batches.Add(new JsonObject
                            {
                                ["firstTriangle"] = batch.FirstTriangle,
                                ["triangleCount"] = batch.TriangleCount,
                                ["geoset"] = batch.Geoset,
                                ["priority"] = batch.Priority,
                                ["material"] = batch.Material.ToJson(),
                            });
                        }

                        var data = new JsonObject
                        {
                            ["key"] = package.Key,
                            ["path"] = package.Path,
                            ["view"] = package.View,
                            ["vertices"] = package.Vertices,
                            ["triangles"] = package.Triangles,
                            ["batches"] = batches,
                            ["wireBytes"] = package.Wire.Length,
                            ["problems"] = new JsonArray([.. package.Problems.Select(p => (JsonNode?)JsonValue.Create(p))]),
                            ["model"] = package.Model.DeepClone(),
                        };
                        if (outPath is not null)
                        {
                            OutputPaths.WriteAtomically(outPath, stream => stream.Write(package.Wire));
                            data["written"] = new JsonObject { ["path"] = outPath, ["bytes"] = package.Wire.Length };
                        }

                        return data;
                    }
                    catch (ModelPackageException e)
                    {
                        throw Map("model.package", e);
                    }
                },
                cancellationToken);
        }

        private IClientFileSource RequireFiles(string command) => files ?? throw ClientMissing(command);

        private static CommandException ClientMissing(string command)
            => new(CommandException.Unavailable, $"{command} needs the client files; set ARCANECORE_CLIENT_DIR (see client.status).");

        private static CommandException Map(string command, ModelPackageException e)
            => e.Code == ModelPackageException.BadArguments
                ? new CommandException(CommandException.BadArguments, $"{command}: {e.Message}")
                : new CommandException(CommandException.Failed, $"{command}: {e.Code}: {e.Message}", e);

        private static JsonObject Describe(IClientFileSource source, string requested)
        {
            string path = requested.Replace('/', '\\').TrimStart('\\');
            if (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^4] + ".m2";
            }

            if (path.Contains(':', StringComparison.Ordinal) || path.Split('\\').Contains(".."))
            {
                throw new ModelPackageException(ModelPackageException.NotFound, $"{path} is not in the client files.");
            }

            if (!path.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModelPackageException(ModelPackageException.BadArguments, $"{path} is not an .m2 model; WMO descriptions are ref.model.info.");
            }

            byte[] bytes = source.Read(path) ?? throw new ModelPackageException(ModelPackageException.NotFound, $"{path} is not in the client files ({source.Description}).");
            M2PackageModel model;
            try
            {
                model = M2PackageModel.Parse(bytes);
            }
            catch (InvalidDataException e)
            {
                throw new ModelPackageException(ModelPackageException.Rejected, $"{path}: {e.Message}", e);
            }

            var min = new System.Numerics.Vector3(float.PositiveInfinity);
            var max = new System.Numerics.Vector3(float.NegativeInfinity);
            foreach (System.Numerics.Vector3 p in model.Positions)
            {
                min = System.Numerics.Vector3.Min(min, p);
                max = System.Numerics.Vector3.Max(max, p);
            }

            var views = new JsonArray();
            var submeshIds = new SortedSet<int>();
            for (int i = 0; i < model.Views.Length; i++)
            {
                M2ViewData view = model.Views[i];
                views.Add(new JsonObject
                {
                    ["index"] = i,
                    ["triangles"] = view.Triangles.Length / 3,
                    ["submeshes"] = view.Submeshes.Length,
                    ["textureUnits"] = view.Units.Length,
                });
                foreach (M2SubmeshEntry submesh in view.Submeshes)
                {
                    submeshIds.Add(submesh.Id);
                }
            }

            var textures = new JsonArray();
            foreach (M2TextureEntry texture in model.Textures)
            {
                var entry = new JsonObject { ["type"] = texture.Type, ["flags"] = texture.Flags };
                if (texture.Type == 0)
                {
                    entry["path"] = texture.Name;
                    entry["exists"] = texture.Name.Length > 0 && source.Exists(texture.Name);
                }

                textures.Add(entry);
            }

            bool any = model.Positions.Length > 0;
            return new JsonObject
            {
                ["path"] = path,
                ["name"] = model.Name,
                ["version"] = model.Version,
                ["vertices"] = model.Positions.Length,
                ["bounds"] = new JsonObject
                {
                    ["min"] = new JsonArray(any ? min.X : 0, any ? min.Y : 0, any ? min.Z : 0),
                    ["max"] = new JsonArray(any ? max.X : 0, any ? max.Y : 0, any ? max.Z : 0),
                },
                ["views"] = views,
                ["textures"] = textures,
                ["submeshIds"] = new JsonArray([.. submeshIds.Select(id => (JsonNode?)JsonValue.Create(id))]),
            };
        }
    }
}

/// <summary><c>GET /api/bin/model?path=&amp;view=</c> (or <c>key=m2:&lt;path&gt;</c>): the ATG1 v2 bytes of a model package.</summary>
internal sealed class ModelEndpoint(ModelPackageCache? cache) : IBinaryEndpoint
{
    public string Name => "model";

    public async Task<BinaryReply> HandleAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancellationToken)
    {
        string? request = query.GetValueOrDefault("path");
        if (string.IsNullOrEmpty(request))
        {
            request = query.GetValueOrDefault("key");
        }

        if (string.IsNullOrEmpty(request))
        {
            return BinaryReply.Error(400, ModelPackageException.BadArguments, "The 'path' (or 'key') query parameter is required.");
        }

        int view = 0;
        if (query.TryGetValue("view", out string? viewText)
            && !int.TryParse(viewText, NumberStyles.None, CultureInfo.InvariantCulture, out view))
        {
            return BinaryReply.Error(400, ModelPackageException.BadArguments, "'view' must be a whole number.");
        }

        if (cache is null)
        {
            return BinaryReply.Error(503, "unavailable", "The client files are not configured; set ARCANECORE_CLIENT_DIR.");
        }

        try
        {
            ModelPackage package = await Task.Run(() => cache.Get(request, view, cancellationToken), cancellationToken).ConfigureAwait(false);
            return BinaryReply.Ok(package.Wire, "ATG1");
        }
        catch (ModelPackageException e)
        {
            int status = e.Code switch
            {
                ModelPackageException.BadArguments => 400,
                ModelPackageException.NotFound => 404,
                ModelPackageException.Rejected => 422,
                _ => 500,
            };
            return BinaryReply.Error(status, e.Code, e.Message);
        }
    }
}

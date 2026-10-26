using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// The host's contribution (order <see cref="ModuleOrder.Host"/>, so it registers after the data modules): the commands
/// that read the registry and cache (<c>status</c>, <c>maps.list</c>, <c>tiles.status</c>, <c>query.pick</c>, <c>capture</c>,
/// ...), the view and bookmark commands, and the state the HTTP/WebSocket server and CLI verbs use. It also publishes every
/// view change and every command result on the <see cref="EventHub"/>.
/// </summary>
public sealed class HostModule : IAtlasModule
{
    private const int EventDataNodeLimit = 20_000;

    private readonly AtlasState? _state;

    /// <summary>Uses the state root from <c>ARCANECORE_ATLAS_STATE_DIR</c> or the per-user application data folder.</summary>
    public HostModule()
    {
    }

    /// <summary>Uses an explicit state root (tests).</summary>
    public HostModule(AtlasState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public string Name => "Host";

    public int Order => ModuleOrder.Host;

    public void Register(AtlasContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AtlasState state = _state ?? AtlasState.FromEnvironment();
        context.Services.Add(state);
        context.Services.Add(new BookmarkStore(state.BookmarksFile, context.Loggers));
        context.Services.Add(new UiHub());

        StatusCommands.Register(context.Commands);
        ViewCommands.Register(context.Commands);
        SessionCommands.Register(context.Commands);
        PickCommand.Register(context.Commands);
        CaptureCommand.Register(context.Commands);

        context.View.Applied += (previous, view, version, origin, followOrigin) => context.Events.Publish(
            EventHub.Topics.View,
            new JsonObject
            {
                ["version"] = version,
                ["origin"] = origin,
                ["view"] = view.ToJson(),
                ["cameraChanged"] = previous.Camera != view.Camera || previous.FollowGuid != view.FollowGuid,
                ["followOrigin"] = followOrigin,
            });
        context.Commands.Invoked += (result, origin) =>
        {
            if (QuietCommands.Contains(result.Command))
            {
                return;
            }

            JsonObject json = result.ToJson();
            json["origin"] = HostOrigin.Label(origin);
            if (json["data"] is { } data && !FitsInEvent(data))
            {
                json["data"] = new JsonObject { ["omitted"] = "The result is too large for the command log; read it from the command's own response." };
            }

            context.Events.Publish(EventHub.Topics.Command, json);
        };
    }

    /// <summary>Commands that run many times a second by design (an agent's position stream); they publish no <c>command</c> event.</summary>
    private static readonly HashSet<string> QuietCommands = new(StringComparer.Ordinal) { "session.move" };

    /// <summary>Whether a result is small enough to push to every UI (counted in JSON nodes, stopping early).</summary>
    private static bool FitsInEvent(JsonNode node)
    {
        int budget = EventDataNodeLimit;
        return Count(node, ref budget);
    }

    private static bool Count(JsonNode? node, ref int budget)
    {
        if (--budget < 0)
        {
            return false;
        }

        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (!Count(item, ref budget))
                    {
                        return false;
                    }
                }

                break;
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> pair in obj)
                {
                    if (!Count(pair.Value, ref budget))
                    {
                        return false;
                    }
                }

                break;
        }

        return true;
    }
}

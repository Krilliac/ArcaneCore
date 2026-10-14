using System.Globalization;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Commands;

/// <summary>
/// <c>.go xy</c>, <c>.go graveyard</c> and <c>.go taxinode</c> extensions under <c>.go</c>
/// (vmangos TeleportCommands.cpp: HandleGoXYCommand, HandleGoGraveyardCommand, HandleGoTaxinodeCommand).
/// </summary>
public sealed class GoCommands : ICommandExtension
{
    public string Path => "go";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("xy", AccountSecurity.Moderator,
            "Syntax: .go xy #x #y [#mapid]\nTeleport to ground or water level at (#x, #y) on the current or specified map.",
            GoXy, RetailLevel: 2),
        new ChatCommand("graveyard", AccountSecurity.Moderator,
            "Syntax: .go graveyard #graveyardId\nTeleport to the graveyard with the given WorldSafeLocs.dbc id.",
            GoGraveyard, RetailLevel: 2),
        new ChatCommand("taxinode", AccountSecurity.Moderator,
            "Syntax: .go taxinode #taxinode\nTeleport to the flight-path node with the given TaxiNodes.dbc id.",
            GoTaxiNode, RetailLevel: 2),
    ];

    private static bool GoXy(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out float x) || !args.ExtractFloat(out float y))
        {
            return false;
        }

        uint mapId = context.Player.MapId;
        if (!args.IsEmpty && (!args.ExtractUInt32(out mapId) || !args.IsEmpty))
        {
            return false;
        }

        TeleportFeature feature = TeleportCommands.Feature(context);
        if (!GridDefines.IsValidMapCoord(x) || !GridDefines.IsValidMapCoord(y) || !feature.Maps.Registry.Contains(mapId))
        {
            TeleportCommands.ReplyInvalid(context, x, y, mapId);
            return true;
        }

        float ground = feature.Maps.Terrain.For(mapId).GetWaterOrGroundLevel(x, y, GridDefines.MaxHeight);
        if (ground <= TerrainTile.InvalidHeight)
        {
            TeleportCommands.ReplyInvalid(context, x, y, mapId);
            return true;
        }

        return TeleportCommands.GoHelper(context, context.Player, mapId, x, y, ground, context.Player.Orientation);
    }

    private static bool GoGraveyard(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hgraveyard", out uint graveyardId) || !args.IsEmpty)
        {
            return false;
        }

        WorldSafeLoc? loc = WorldGraveyards.Of(context.World).Catalog.Find(graveyardId);
        if (loc is null)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Graveyard #{graveyardId} doesn't exist."));
            return true;
        }

        return TeleportCommands.GoHelper(context, context.Player, loc.MapId, loc.X, loc.Y, loc.Z, loc.Orientation);
    }

    private static bool GoTaxiNode(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Htaxinode", out uint nodeId) || !args.IsEmpty)
        {
            return false;
        }

        TaxiNode? node = context.Session.Services.GetService<QuestNpcFeature>()?.Services.Npcs.Nodes.FirstOrDefault(n => n.Id == nodeId);
        if (node is null)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Taxinode #{nodeId} doesn't exist."));
            return true;
        }

        return TeleportCommands.GoHelper(context, context.Player, node.MapId, node.X, node.Y, node.Z, context.Player.Orientation);
    }
}

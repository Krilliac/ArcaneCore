using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Teleport;

namespace ArcaneCore.World.Gm.Teleport;

/// <summary>
/// More vmangos <c>.go</c> sub-commands: target, trigger, xyzo, grid, forward, up and relative (TeleportCommands.cpp:280-322, 762-775,
/// 878-1036; all SEC_TICKETMASTER, Chat.cpp:400-412; texts mangos_string 262 and 357). The client is build 5875, where vmangos lands the
/// player with MSG_MOVE_FALL_LAND (NearLandTo); here the relative moves are a near teleport, which is what vmangos does up to 1.9.4.
/// </summary>
public sealed class GmGoMoreCommands : ICommandExtension
{
    internal static string TriggerNotFound(uint id) => string.Create(CultureInfo.InvariantCulture, $"Areatrigger ID {id} not found!");

    internal static string TriggerHasNoTarget(uint id) => string.Create(CultureInfo.InvariantCulture, $"Areatrigger {id} not has target coordinates");

    public string Path => "go";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("target", AccountSecurity.Moderator, "Syntax: .go target\nTeleport to the selected creature or player on your map.", GoTarget, RetailLevel: 2),
        new ChatCommand("trigger", AccountSecurity.Moderator,
            "Syntax: .go trigger #trigger_id [target]\nTeleport to the area trigger #trigger_id, or with 'target' to where the trigger sends a player.", GoTrigger, RetailLevel: 2),
        new ChatCommand("xyzo", AccountSecurity.Moderator, "Syntax: .go xyzo #x #y #z #orientation [#mapid]\nTeleport to the given position, facing #orientation.", GoXyzo, RetailLevel: 2),
        new ChatCommand("grid", AccountSecurity.Moderator, "Syntax: .go grid #gridX #gridY [#mapid]\nTeleport to the ground at the center of the grid.", GoGrid, RetailLevel: 2),
        new ChatCommand("forward", AccountSecurity.Moderator, "Syntax: .go forward [#yards]\nTeleport #yards (default 10) ahead of you.", GoForward, RetailLevel: 2),
        new ChatCommand("up", AccountSecurity.Moderator, "Syntax: .go up [#yards]\nTeleport #yards (default 10) up, or down for a negative value.", GoUp, RetailLevel: 2),
        new ChatCommand("relative", AccountSecurity.Moderator,
            "Syntax: .go relative #forward [#left [#up]]\nTeleport relative to your position and facing; negative values go back, right or down.", GoRelative, RetailLevel: 2),
    ];

    // HandleGoTargetCommand: an explicit selection on the same map instance; NearTeleportTo the unit's position and facing.
    private static bool GoTarget(CommandContext context, string text)
    {
        Player player = context.Player;
        Unit? target = player.Selection.IsEmpty ? null : GmSelectedUnit.Of(context);
        if (target is null || ReferenceEquals(target, player) || target.Map is null || !ReferenceEquals(target.Map, player.Map))
        {
            context.Reply(GmStrings.SelectCharOrCreature);
            return true;
        }

        TeleportCommands.Feature(context).Teleports.TeleportTo(player, player.MapId, target.X, target.Y, target.Z, target.Orientation);
        return true;
    }

    private static bool GoTrigger(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hareatrigger", out uint triggerId) || triggerId == 0)
        {
            return false;
        }

        WorldMaps maps = TeleportCommands.Feature(context).Maps;
        AreaTriggerTemplate? trigger = maps.FindAreaTrigger(triggerId);
        if (trigger is null)
        {
            context.Reply(TriggerNotFound(triggerId));
            return true;
        }

        bool toTarget = args.ExtractLiteral("target") is not null;
        if (!toTarget && !args.IsEmpty)
        {
            return false;
        }

        if (toTarget)
        {
            AreaTriggerTeleport? teleport = maps.FindAreaTriggerTeleport(triggerId);
            if (teleport is null)
            {
                context.Reply(TriggerHasNoTarget(triggerId));
                return true;
            }

            return TeleportCommands.GoHelper(context, context.Player, teleport.TargetMap, teleport.TargetX, teleport.TargetY, teleport.TargetZ, context.Player.Orientation);
        }

        return TeleportCommands.GoHelper(context, context.Player, trigger.MapId, trigger.X, trigger.Y, trigger.Z, context.Player.Orientation);
    }

    private static bool GoXyzo(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out float x) || !args.ExtractFloat(out float y) || !args.ExtractFloat(out float z) || !args.ExtractFloat(out float orientation)
            || !args.ExtractOptUInt32(out uint mapId, context.Player.MapId) || !args.IsEmpty)
        {
            return false;
        }

        return TeleportCommands.GoHelper(context, context.Player, mapId, x, y, z, orientation);
    }

    private static bool GoGrid(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out float gridX) || !args.ExtractFloat(out float gridY)
            || !args.ExtractOptUInt32(out uint mapId, context.Player.MapId) || !args.IsEmpty)
        {
            return false;
        }

        float x = (gridX - GridDefines.CenterGridId + 0.5f) * GridDefines.SizeOfGrids;
        float y = (gridY - GridDefines.CenterGridId + 0.5f) * GridDefines.SizeOfGrids;
        return GoGround(context, mapId, x, y);
    }

    private static bool GoForward(CommandContext context, string text)
    {
        float[] values = Leading(text, 10f);
        return GoRelativeTo(context, values[0], 0f, 0f, announce: false);
    }

    private static bool GoUp(CommandContext context, string text)
    {
        float[] values = Leading(text, 10f);
        return GoRelativeTo(context, 0f, 0f, values[0], announce: false);
    }

    private static bool GoRelative(CommandContext context, string text)
    {
        float[] values = Leading(text, 0f, 0f, 0f);
        return GoRelativeTo(context, values[0], values[1], values[2], announce: true);
    }

    // WorldObject::GetRelativePositions (Object.cpp): forward along the facing, left at facing + pi/2, up on z.
    private static bool GoRelativeTo(CommandContext context, float forward, float left, float up, bool announce)
    {
        Player player = context.Player;
        if (announce)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Teleportation : Forward-Backward {forward:F6} Left-Right {left:F6} Up-Down {up:F6}"));
        }

        float o = player.Orientation;
        float x = player.X + MathF.Cos(o + MathF.PI / 2f) * left + MathF.Cos(o) * forward;
        float y = player.Y + MathF.Sin(o + MathF.PI / 2f) * left + MathF.Sin(o) * forward;
        float z = player.Z + up;
        if (!GridDefines.IsValidMapCoord(x, y, z, o))
        {
            TeleportCommands.ReplyInvalid(context, x, y, player.MapId);
            return true;
        }

        TeleportCommands.Feature(context).Teleports.TeleportTo(player, player.MapId, x, y, z, o);
        return true;
    }

    // HandleGoHelper without z: the water or ground level at (x, y).
    private static bool GoGround(CommandContext context, uint mapId, float x, float y)
    {
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

    // sscanf("%f %f %f"): the leading numbers that parse; the rest keep their defaults.
    private static float[] Leading(string text, params float[] defaults)
    {
        float[] values = [.. defaults];
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < values.Length && i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            {
                break;
            }

            values[i] = value;
        }

        return values;
    }
}
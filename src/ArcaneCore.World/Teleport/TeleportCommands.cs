using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Teleport;

/// <summary>
/// <c>.tele</c> and <c>.go xyz</c> (vmangos TeleportCommands.cpp; security levels from the
/// cmangos-classic / vmangos command tables: both SEC_MODERATOR).
/// </summary>
public sealed class TeleportCommands : ICommandGroup
{
    /// <summary>mangos_string 164, LANG_COMMAND_TELE_NOTFOUND.</summary>
    public const string TeleNotFoundText = "Teleport location not found!";

    /// <summary>mangos_string 263, LANG_INVALID_TARGET_COORD (cmangos-classic text).</summary>
    public const string InvalidTargetText = "Target map or coordinates is invalid (X: {0:F6} Y: {1:F6} MapId: {2})";

    /// <summary>mangos_string 733, LANG_CANNOT_TELE_TO_BG (cmangos-classic text).</summary>
    public const string CannotTeleToBattlegroundText = "You cannot teleport to a battleground map.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("tele", AccountSecurity.Moderator, "Syntax: .tele #location — teleport to a location from the game_tele table (name, part of a name, or id).", Tele),
        new ChatCommand("go", AccountSecurity.Moderator, "Teleport to a position.", Children:
        [
            new ChatCommand("xyz", AccountSecurity.Moderator, "Syntax: .go xyz #x #y [#z [#mapid]] — teleport to a position; without #z, to the ground (or water surface) there.", GoXyz),
        ]),
    ];

    // vmangos ChatHandler::HandleTeleCommand: id or name (ExtractGameTeleFromLink), then HandleGoHelper.
    private static bool Tele(CommandContext context, string args)
    {
        string name = args.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        WorldMaps maps = Feature(context).Maps;
        GameTele? tele = uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)
            ? maps.GameTeles.FirstOrDefault(t => t.Id == id) ?? maps.FindGameTele(name)
            : maps.FindGameTele(name);
        if (tele is null)
        {
            context.Reply(TeleNotFoundText);
            return true;
        }

        return GoHelper(context, tele.MapId, tele.X, tele.Y, tele.Z, tele.Orientation);
    }

    // .go xyz x y [z [mapid]]. vmangos HandleGoXYZCommand requires z; cmangos-classic makes it
    // optional and asks the terrain (HandleGoHelper without zPtr) — ArcaneCore accepts both forms.
    private static bool GoXyz(CommandContext context, string args)
    {
        string[] parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4
            || !TryFloat(parts[0], out float x)
            || !TryFloat(parts[1], out float y))
        {
            return false;
        }

        float? z = null;
        if (parts.Length >= 3)
        {
            if (!TryFloat(parts[2], out float zValue))
            {
                return false;
            }

            z = zValue;
        }

        uint mapId = context.Player.MapId;
        if (parts.Length == 4 && !uint.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out mapId))
        {
            return false;
        }

        if (z is null)
        {
            // vmangos HandleGoHelper: check x/y before asking the terrain for z.
            if (!GridDefines.IsValidMapCoord(x) || !GridDefines.IsValidMapCoord(y) || !Feature(context).Maps.Registry.Contains(mapId))
            {
                ReplyInvalid(context, x, y, mapId);
                return true;
            }

            float ground = Feature(context).Maps.Terrain.For(mapId).GetWaterOrGroundLevel(x, y, GridDefines.MaxHeight);
            if (ground <= TerrainTile.InvalidHeight)
            {
                ReplyInvalid(context, x, y, mapId);
                return true;
            }

            z = ground;
        }

        return GoHelper(context, mapId, x, y, z.Value, context.Player.Orientation);
    }

    // vmangos ChatHandler::HandleGoHelper (full coordinates given): validate, then TeleportTo.
    private static bool GoHelper(CommandContext context, uint mapId, float x, float y, float z, float orientation)
        => GoHelper(context, context.Player, mapId, x, y, z, orientation);

    /// <summary>
    /// HandleGoHelper for <paramref name="subject"/> (the invoker, or a player another command
    /// moves): validate, stop a taxi flight or else remember the recall position, then TeleportTo
    /// (TeleportCommands.cpp:713-760).
    /// </summary>
    internal static bool GoHelper(CommandContext context, Player subject, uint mapId, float x, float y, float z, float orientation)
    {
        TeleportFeature feature = Feature(context);
        MapTemplate? target = feature.Maps.Registry.Find(mapId);
        if (target is null || !GridDefines.IsValidMapCoord(x, y, z, orientation))
        {
            ReplyInvalid(context, x, y, mapId);
            return true;
        }

        if (target.IsBattleground)
        {
            context.Reply(CannotTeleToBattlegroundText);
            return true;
        }

        GmTeleports.BeginCommandTeleport(context, subject);
        if (!feature.Teleports.TeleportTo(subject, mapId, x, y, z, orientation))
        {
            ReplyInvalid(context, x, y, mapId);
        }

        return true;
    }

    internal static void ReplyInvalid(CommandContext context, float x, float y, uint mapId)
        => context.Reply(string.Format(CultureInfo.InvariantCulture, InvalidTargetText, x, y, mapId));

    private static bool TryFloat(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    internal static TeleportFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<TeleportFeature>();
}

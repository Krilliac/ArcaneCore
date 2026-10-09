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
using Microsoft.Extensions.Logging;

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
        new ChatCommand("tele", AccountSecurity.Moderator, "Syntax: .tele #location — teleport to a location from the game_tele table (name, part of a name, or id).", Tele, Children:
        [
            new ChatCommand("add", AccountSecurity.Administrator, "Syntax: .tele add $name — save your current location.", Add, RetailLevel: 5),
            new ChatCommand("del", AccountSecurity.Administrator, "Syntax: .tele del $name — delete an exact named location.", Delete, RetailLevel: 5),
        ]),
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

    // vmangos HandleTeleAddCommand / ObjectMgr::AddGameTele (TeleportCommands.cpp:48-84,
    // ObjectMgr.cpp:10555-10575): persist the caller's current position.
    private static bool Add(CommandContext context, string args)
    {
        string name = args.Trim();
        if (name.Length is 0 or > 100 || name.Any(char.IsControl))
        {
            return false;
        }

        WorldMaps maps = Feature(context).Maps;
        if (maps.FindGameTele(name) is not null)
        {
            context.Reply("Teleport location already exists!");
            return true;
        }

        Player player = context.Player;
        GameTele proposed = new(0, player.X, player.Y, player.Z, player.Orientation, player.MapId, name);
        Persist(context, async store =>
        {
            GameTele? added = await store.AddAsync(proposed).ConfigureAwait(false);
            context.World.Post(() =>
            {
                if (added is not null && maps.GameTeles.All(t => t.Id != added.Id))
                {
                    maps.ReplaceGameTeles([.. maps.GameTeles, added]);
                }

                if (player.IsInWorld)
                {
                    context.Reply(added is null ? "Teleport location already exists!" : "Teleport location added.");
                }
            });
        });
        return true;
    }

    // vmangos HandleTeleDelCommand / ObjectMgr::DeleteGameTele (TeleportCommands.cpp:86-106,
    // ObjectMgr.cpp:10577-10604): unlike lookup, deletion requires the exact name.
    private static bool Delete(CommandContext context, string args)
    {
        string name = args.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        WorldMaps maps = Feature(context).Maps;
        Persist(context, async store =>
        {
            GameTele? deleted = await store.DeleteAsync(name).ConfigureAwait(false);
            context.World.Post(() =>
            {
                if (deleted is not null)
                {
                    maps.ReplaceGameTeles(maps.GameTeles.Where(t => t.Id != deleted.Id));
                }

                if (context.Player.IsInWorld)
                {
                    context.Reply(deleted is null ? TeleNotFoundText : "Teleport location deleted.");
                }
            });
        });
        return true;
    }

    private static void Persist(CommandContext context, Func<IGameTeleStore, Task> work)
    {
        IServiceScopeFactory scopes = context.Session.Services.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using IServiceScope scope = scopes.CreateScope();
                await work(scope.ServiceProvider.GetRequiredService<IGameTeleStore>()).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                context.Session.Logger.LogError(ex, "game_tele edit failed");
                context.World.Post(() =>
                {
                    if (context.Player.IsInWorld)
                    {
                        context.Reply("The teleport location could not be saved.");
                    }
                });
            }
        });
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

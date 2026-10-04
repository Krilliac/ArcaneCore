using System.Globalization;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Teleport;

namespace ArcaneCore.World.Gm.Death;

/// <summary>
/// The GM commands of the death area: <c>.revive</c> (vmangos CharacterCommands.cpp:615-635), <c>.gocorpse</c> (TeleportCommands.cpp:1347-1366)
/// and <c>.neargrave</c> (MiscCommands.cpp:1900-1965); levels from the vmangos command table (Chat.cpp:1240,1247,1271 through
/// <see cref="RetailCommandLevels"/>). The texts of <c>.neargrave</c> and <c>.gocorpse</c> are the mangos_string rows 164 and 454-461
/// (classic-db). <c>.revive</c> acts on online players only; vmangos also converts an offline player's corpse so that it logs in
/// resurrected, which needs the offline characters' corpse rows (a limit: it answers "Player not found!").
/// <c>.linkgrave</c> is not provided: vmangos inserts the link into <c>game_graveyard_zone</c>, and the graveyard catalog here is
/// read-only content (reload it with the importer).
/// </summary>
public sealed class DeathGmCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("revive", AccountSecurity.GameMaster, "Syntax: .revive [$playername]\nRevive the selected player (or the named one, or yourself): half health and mana, the corpse gone.", Revive),
        new ChatCommand("gocorpse", AccountSecurity.GameMaster, "Syntax: .gocorpse [$playername]\nTeleport to the corpse of the selected player (or the named one, or yourself).", GoCorpse),
        new ChatCommand("neargrave", AccountSecurity.GameMaster, "Syntax: .neargrave [alliance|horde]\nFind the graveyard nearest to you that serves your zone (for the given team, or any).", NearGrave),
    ];

    /// <summary>LANG_COMMAND_GRAVEYARDERROR (454).</summary>
    public static string GraveyardError(uint id) => string.Create(CultureInfo.InvariantCulture, $"No faction in Graveyard with id= #{id} , fix your DB");

    /// <summary>LANG_COMMAND_GRAVEYARDNEAREST (459).</summary>
    public static string GraveyardNearest(uint id, string team, uint zone) => string.Create(CultureInfo.InvariantCulture, $"Graveyard #{id} (faction: {team}) is nearest from linked to zone #{zone}.");

    /// <summary>LANG_COMMAND_ZONENOGRAFACTION (461).</summary>
    public static string ZoneNoGraveyardForFaction(uint zone, string team) => string.Create(CultureInfo.InvariantCulture, $"Zone #{zone} doesn't have linked graveyards for faction: {team}.");

    /// <summary>The name of a team in the replies: LANG_COMMAND_GRAVEYARD_ANY (456), _ALLIANCE (457), _HORDE (458), else _NOTEAM (455).</summary>
    public static string TeamName(uint team) => team switch
    {
        0 => "any",
        GraveyardCatalog.TeamAlliance => "alliance",
        GraveyardCatalog.TeamHorde => "horde",
        _ => "invalid team, please fix database",
    };

    /// <summary>The reply of <c>.revive</c> (vmangos LANG_CHARACTER_REVIVED_ONLINE, id 5031: the text is not in the reference tree, this is ArcaneCore's own wording).</summary>
    public static string Revived(string link) => $"Character {link} revived.";

    private static bool Revive(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player? target))
        {
            return true;
        }

        if (target.Map is { } map)
        {
            // vmangos revives whoever is selected, an alive player included (health then goes to half).
            map.Combat.ResurrectPlayer(target, CombatConstants.CorpseReclaimRestorePercent, applySickness: false);
            map.Combat.SpawnCorpseBones(target);
        }

        context.Reply(Revived(GmStrings.PlayerLink(target.Name)));
        return true;
    }

    private static bool GoCorpse(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player? target))
        {
            return true;
        }

        if (target.Combat.Corpse is not { } corpse)
        {
            context.Reply(GmStrings.TeleNotFound);
            return true;
        }

        return TeleportCommands.GoHelper(context, context.Player, corpse.MapId, corpse.X, corpse.Y, corpse.Z, context.Player.Orientation);
    }

    private static bool NearGrave(CommandContext context, string text)
    {
        string argument = text.Trim();
        uint team;
        if (argument.Length == 0)
        {
            team = 0;
        }
        else if ("horde".StartsWith(argument, StringComparison.Ordinal))
        {
            team = GraveyardCatalog.TeamHorde;
        }
        else if ("alliance".StartsWith(argument, StringComparison.Ordinal))
        {
            team = GraveyardCatalog.TeamAlliance;
        }
        else
        {
            return false; // the syntax text
        }

        Player player = context.Player;
        if (player.Map is not { } map)
        {
            return true;
        }

        uint zone = player.ZoneId;
        GraveyardCatalog catalog = WorldGraveyards.Of(context.World).Catalog;
        MapRegistry registry = WorldMaps.Of(context.World).Registry;
        (uint zoneId, uint areaId) = map.GetZoneAndAreaId(player.X, player.Y, player.Z);
        WorldSafeLoc? graveyard = GraveyardSelector.FindClosest(catalog, registry, map.MapId, player.X, player.Y, player.Z, zoneId, areaId, team);
        if (graveyard is null)
        {
            context.Reply(ZoneNoGraveyardForFaction(zone, TeamName(team)));
            return true;
        }

        // vmangos looks the link up in the player's zone: a graveyard that is only linked to the AREA has no data there.
        GraveyardEntry? link = catalog.LinksOf(zone).Where(e => e.Location.Id == graveyard.Id).Select(e => (GraveyardEntry?)e).FirstOrDefault();
        if (link is null)
        {
            context.Reply(GraveyardError(graveyard.Id));
            return true;
        }

        context.Reply(GraveyardNearest(graveyard.Id, TeamName(link.Value.Team), zone));
        return true;
    }
}

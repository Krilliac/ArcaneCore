using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Teleport;

/// <summary>vmangos TeleportCommands.cpp:370-477, 524-638; Chat.cpp:397,400.</summary>
public sealed class GmGoSpawnCommands : ICommandExtension
{
    public string Path => "go";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("creature", AccountSecurity.Moderator, "Syntax: .go creature #spawn_guid|id #entry|#name\nTeleport to a creature spawn.", GoCreature, RetailLevel: 2),
        new ChatCommand("object", AccountSecurity.Moderator, "Syntax: .go object #spawn_guid|id #entry|#name\nTeleport to a game object spawn.", GoObject, RetailLevel: 2),
    ];

    private static bool GoCreature(CommandContext context, string text)
    {
        if (!TryQuery(text, "Hcreature", "Hcreature_entry", out bool byEntry, out uint number, out string? name))
        {
            return false;
        }

        var content = context.Session.Services.GetRequiredService<CreatureWorldFeature>().Content;
        IEnumerable<CreatureSpawn> spawns = content.MapsWithSpawns.SelectMany(content.GetSpawns);
        if (name is not null)
        {
            spawns = spawns.Where(s => content.FindTemplate(s.Entry)?.Name.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
        }
        else if (byEntry)
        {
            spawns = spawns.Where(s => s.Entry == number);
        }
        else
        {
            spawns = spawns.Where(s => s.Guid == number);
        }

        CreatureSpawn? spawn = Closest(context.Player, spawns, s => s.MapId, s => s.X, s => s.Y, s => s.Z, s => s.Guid);
        if (spawn is null)
        {
            context.Reply(GmStrings.NoCreaturesFound);
            return true;
        }

        return TeleportCommands.GoHelper(context, context.Player, spawn.MapId, spawn.X, spawn.Y, spawn.Z, context.Player.Orientation);
    }

    private static bool GoObject(CommandContext context, string text)
    {
        if (!TryQuery(text, "Hgameobject", "Hgameobject_entry", out bool byEntry, out uint number, out string? name))
        {
            return false;
        }

        var content = context.Session.Services.GetRequiredService<GameObjectLootFeature>().Content;
        IEnumerable<GameObjectSpawn> spawns = content.Spawns;
        if (name is not null)
        {
            spawns = spawns.Where(s => content.FindTemplate(s.Entry)?.Name.Contains(name, StringComparison.OrdinalIgnoreCase) == true);
        }
        else if (byEntry)
        {
            spawns = spawns.Where(s => s.Entry == number);
        }
        else
        {
            spawns = spawns.Where(s => s.Guid == number);
        }

        GameObjectSpawn? spawn = Closest(context.Player, spawns, s => s.MapId, s => s.X, s => s.Y, s => s.Z, s => s.Guid);
        if (spawn is null)
        {
            context.Reply(GmStrings.NoGameObjectsFound);
            return true;
        }

        return TeleportCommands.GoHelper(context, context.Player, spawn.MapId, spawn.X, spawn.Y, spawn.Z, context.Player.Orientation);
    }

    private static bool TryQuery(string text, string guidLink, string entryLink, out bool byEntry, out uint number, out string? name)
    {
        byEntry = false;
        number = 0;
        name = null;
        var args = new CommandArgs(text);
        string? token = args.ExtractKeyFromLink([guidLink, entryLink], out int linkType, out _);
        if (token is null)
        {
            return false;
        }

        byEntry = linkType == 1;
        if (linkType < 0 && token.Equals("id", StringComparison.OrdinalIgnoreCase))
        {
            byEntry = true;
            token = args.ExtractKeyFromLink(entryLink, out _, out _);
            if (token is null)
            {
                return false;
            }
        }

        if (!uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            if (byEntry)
            {
                return false;
            }

            name = (token + (args.IsEmpty ? string.Empty : " " + args.Rest)).Trim();
            return name.Length != 0;
        }

        return number != 0 && args.IsEmpty;
    }

    private static T? Closest<T>(Player player, IEnumerable<T> spawns, Func<T, uint> map, Func<T, float> x,
        Func<T, float> y, Func<T, float> z, Func<T, uint> guid) where T : class
        => spawns.OrderBy(s => map(s) == player.MapId ? 0 : 1)
            .ThenBy(s => map(s) == player.MapId
                ? Math.Pow(x(s) - player.X, 2) + Math.Pow(y(s) - player.Y, 2) + Math.Pow(z(s) - player.Z, 2)
                : double.PositiveInfinity)
            .ThenBy(guid).FirstOrDefault();
}

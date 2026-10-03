using System.Globalization;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Instances;

/// <summary>
/// <c>.instance listbinds | unbind | stats</c> (vmangos InstanceCommands / level3.cpp
/// <c>HandleInstanceListBindsCommand</c>, <c>HandleInstanceUnbindCommand</c>,
/// <c>HandleInstanceStatsCommand</c>; SEC_ADMINISTRATOR there).
/// </summary>
public sealed class InstanceCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("instance", AccountSecurity.Administrator, "Instance commands.", Children:
        [
            new ChatCommand("listbinds", AccountSecurity.Administrator, "Syntax: .instance listbinds — the instance binds of the selected player (or yourself).", ListBinds),
            new ChatCommand("unbind", AccountSecurity.Administrator, "Syntax: .instance unbind #mapid|all — drop binds of the selected player (or yourself), except the map you are in.", Unbind),
            new ChatCommand("stats", AccountSecurity.Administrator, "Syntax: .instance stats — loaded instance maps and stored saves.", Stats),
        ]),
    ];

    private static bool ListBinds(CommandContext context, string args)
    {
        if (context.SelectedPlayerOrSelf() is not { } target)
        {
            return false;
        }

        InstanceManager instances = Feature(context).Instances;
        var text = new StringBuilder();
        IReadOnlyCollection<InstanceBind> binds = instances.GetPlayerBinds(target.Guid);
        foreach (InstanceBind bind in binds.OrderBy(b => b.Save.MapId))
        {
            text.Append(CultureInfo.InvariantCulture, $"map: {bind.Save.MapId} inst: {bind.Save.InstanceId} perm: {(bind.Permanent ? "yes" : "no")} canReset: {(bind.Save.CanReset ? "yes" : "no")}\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"player binds: {binds.Count}");
        if (instances.GroupOf(target.Guid) is { } group)
        {
            IReadOnlyCollection<InstanceBind> groupBinds = instances.GetGroupBinds(group);
            foreach (InstanceBind bind in groupBinds.OrderBy(b => b.Save.MapId))
            {
                text.Append(CultureInfo.InvariantCulture, $"\ngroup map: {bind.Save.MapId} inst: {bind.Save.InstanceId} perm: {(bind.Permanent ? "yes" : "no")}");
            }

            text.Append(CultureInfo.InvariantCulture, $"\ngroup binds: {groupBinds.Count}");
        }

        context.Reply(text.ToString());
        return true;
    }

    private static bool Unbind(CommandContext context, string args)
    {
        string arg = args.Trim();
        if (arg.Length == 0 || context.SelectedPlayerOrSelf() is not { } target || !context.CanActOn(target))
        {
            return false;
        }

        InstanceManager instances = Feature(context).Instances;
        uint[] maps;
        if (string.Equals(arg, "all", StringComparison.OrdinalIgnoreCase))
        {
            maps = [.. instances.GetPlayerBinds(target.Guid).Select(b => b.Save.MapId)];
        }
        else if (uint.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out uint mapId))
        {
            maps = [mapId];
        }
        else
        {
            return false;
        }

        int count = 0;
        foreach (uint mapId in maps)
        {
            if (target.IsInWorld && target.MapId == mapId)
            {
                continue; // vmangos: not the map the player is in
            }

            if (instances.UnbindPlayer(target.Guid, mapId))
            {
                count++;
            }
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"instances unbound: {count}"));
        return true;
    }

    private static bool Stats(CommandContext context, string args)
    {
        InstanceManager instances = Feature(context).Instances;
        context.Reply(string.Create(
            CultureInfo.InvariantCulture,
            $"instance maps loaded: {instances.LoadedMaps.Count}\nplayers in instances: {instances.LoadedMaps.Sum(m => m.PlayerCount)}\ninstance saves: {instances.Saves.Count}"));
        return true;
    }

    private static InstanceFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<InstanceFeature>();
}

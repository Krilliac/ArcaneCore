using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>vmangos HandleModifyFactionCommand (UnitCommands.cpp:2140-2207), with the audited player rank gate.</summary>
public sealed class ModifyFactionExtension : ICommandExtension
{
    public string Path => "modify";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("faction", AccountSecurity.GameMaster,
            "Syntax: .modify faction [#faction [#unitflags [#npcflags [#dynamicflags]]]]", Faction, RetailLevel: 3),
    ];

    private static bool Faction(CommandContext context, string text)
    {
        Unit? target = GmSelectedUnit.Require(context);
        if (target is null || target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        var args = new CommandArgs(text);
        if (args.IsEmpty)
        {
            context.Reply($"Faction {target.FactionTemplate}; flags {(uint)target.UnitFlags}; npc flags {target.GetUInt32(UpdateFields.UnitNpcFlags)}; dynamic flags {target.GetUInt32(UpdateFields.UnitDynamicFlags)}.");
            return true;
        }

        if (!args.ExtractUInt32(out uint faction))
        {
            return false;
        }

        if (context.Session.Services.GetService<FactionTemplateCatalog>()?.Find(faction) is null)
        {
            context.Reply($"Faction template {faction} does not exist.");
            return true;
        }

        if (!args.ExtractOptUInt32(out uint unitFlags, (uint)target.UnitFlags)
            || !args.ExtractOptUInt32(out uint npcFlags, target.GetUInt32(UpdateFields.UnitNpcFlags))
            || !args.ExtractOptUInt32(out uint dynamicFlags, target.GetUInt32(UpdateFields.UnitDynamicFlags))
            || !args.IsEmpty)
        {
            return false;
        }

        target.FactionTemplate = faction;
        target.SetUInt32(UpdateFields.UnitFieldFlags, unitFlags);
        target.SetUInt32(UpdateFields.UnitNpcFlags, npcFlags);
        target.SetUInt32(UpdateFields.UnitDynamicFlags, dynamicFlags);
        context.Reply($"Faction updated to {faction}.");
        return true;
    }
}

/// <summary>vmangos HandleNpcSetFactionIdCommand (CreatureCommands.cpp:530-556).</summary>
internal static class NpcFactionCommands
{
    public static bool SetFaction(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint faction) || !args.IsEmpty)
        {
            return false;
        }

        if (context.Session.Services.GetService<FactionTemplateCatalog>()?.Find(faction) is null)
        {
            context.Reply($"Faction template {faction} does not exist.");
            return true;
        }

        Creature? creature = GmNpcCommands.SystemOf(context)?.FindCreature(context.Player.Selection);
        if (creature is null)
        {
            context.Reply(GmNpcCommands.SelectCreature);
            return true;
        }

        creature.FactionTemplate = faction;
        context.Reply($"Faction updated to {faction}.");
        return true;
    }
}

using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using GridCoord = ArcaneCore.Game.Maps.Grid.GridCoord;

namespace ArcaneCore.World.Gm.Extra;

/// <summary>
/// More <c>.inspect</c> readouts (docs/reference/gm-commands.md): <c>auras</c> (every aura holder of the selected unit with its
/// remaining time, stacks, caster and effects), <c>group</c> (the selected player's group), <c>grid</c> (the grid and cell the selected
/// object sits in and the loaded grids of its map) and <c>visibility</c> (whether you and the selected object see each other). Read-only,
/// no vmangos equivalent.
/// </summary>
public sealed class ExtraInspectCommands : ICommandExtension
{
    /// <summary>Most aura holders <c>.inspect auras</c> lists.</summary>
    public const int MaxAuraRows = 40;

    public string Path => "inspect";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("auras", AccountSecurity.GameMaster,
            "Syntax: .inspect auras [$filter]\nList the aura holders of the selected unit (or you): spell id and name, remaining/maximum time, stacks, slot, caster, positive or negative, and each effect's aura type and amount. $filter keeps the spells whose name contains it.",
            Auras, RetailLevel: 3),
        new("group", AccountSecurity.GameMaster,
            "Syntax: .inspect group\nShow the selected player's group (or yours): id, raid or party, leader, loot method, and every member with sub-group, assistant flag, online state, map and distance to you.",
            GroupInfo, RetailLevel: 3),
        new("grid", AccountSecurity.GameMaster,
            "Syntax: .inspect grid\nShow the grid and cell the selected object (or you) is in, whether that grid is loaded and how many grids of the map are loaded, the active cells and the map's largest bounding radius.",
            Grid, RetailLevel: 3),
        new("visibility", AccountSecurity.GameMaster,
            "Syntax: .inspect visibility\nShow whether the selected object is in your client's visible set and in visibility range, whether you are in its visible set (a player), and the size of both visible sets.",
            Visibility, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    internal static string Time(int ms) => ms < 0 ? "permanent" : ms >= 60_000 ? (ms / 60_000.0).ToString("F1", C) + "m" : (ms / 1000.0).ToString("F1", C) + "s";

    private static bool Auras(CommandContext context, string args)
    {
        if (InspectCommands.Target(context) is not { } unit)
        {
            return true;
        }

        string filter = args.Trim();
        SpellSystem? spells = context.Session.Services.GetService<SpellFeature>()?.System;
        if (spells is null)
        {
            context.Reply("The spell system is not running.");
            return true;
        }

        IReadOnlyList<SpellAuraHolder> holders = spells.GetAuras(unit);
        var shown = holders.Where(h => filter.Length == 0 || h.Spell.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        context.Reply(string.Create(C, $"{InspectCommands.Describe(unit)}: {holders.Count} aura holders{(filter.Length > 0 ? $", {shown.Count} matching '{filter}'" : string.Empty)}"));
        foreach (SpellAuraHolder holder in shown.Take(MaxAuraRows))
        {
            string caster = holder.CasterGuid == unit.Guid ? "self" : holder.CasterGuid.IsEmpty ? "none" : holder.CasterGuid.ToString();
            string slot = holder.Slot == SpellAuraHolder.NoSlot ? "-" : holder.Slot.ToString(C);
            var effects = holder.Auras.OfType<SpellAura>().Select(a => string.Create(C, $"e{a.EffectIndex} {a.Type} {a.Amount}{(a.IsPeriodic ? $" every {a.Period}ms x{a.TickCount}" : string.Empty)}"));
            string flags = (holder.IsPositive ? "+" : "-") + (holder.Spell.IsPassive ? " passive" : string.Empty) + (holder.IsRemoved ? " removed" : string.Empty);
            context.Reply(string.Create(C,
                $"  {holder.Spell.Id} [{holder.Spell.Name}] {Time(holder.Duration)}/{Time(holder.MaxDuration)} stacks {holder.StackAmount} slot {slot} {flags} caster {caster}: {string.Join("; ", effects)}"));
        }

        if (shown.Count > MaxAuraRows)
        {
            context.Reply(string.Create(C, $"  ... {shown.Count - MaxAuraRows} more (use a filter)"));
        }

        return true;
    }

    private static bool GroupInfo(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (!GmTargets.TryPlayer(context, (string?)null, out Player player) || !context.CanActOn(player))
        {
            return true;
        }

        if (GmGroups.Of(context, player) is not { } group)
        {
            context.Reply($"{player.Name} is not in a group.");
            return true;
        }

        string leader = group.Members.FirstOrDefault(m => m.Guid == group.LeaderGuid)?.Name ?? group.LeaderGuid.ToString();
        context.Reply(string.Create(C,
            $"group {group.Id} ({(group.IsRaid ? "raid" : "party")}): {group.Members.Count} members, leader {leader}, loot {group.LootMethod}"));
        Player me = context.Player;
        foreach (GroupMemberSlot slot in group.Members.OrderBy(m => m.SubGroup).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            Player? online = context.World.FindOnlinePlayer(slot.Guid);
            string where = online is null
                ? "offline"
                : online.MapId == me.MapId
                    ? string.Create(C, $"map {online.MapId}, {MathF.Sqrt(((online.X - me.X) * (online.X - me.X)) + ((online.Y - me.Y) * (online.Y - me.Y)) + ((online.Z - me.Z) * (online.Z - me.Z))):F1} yd")
                    : string.Create(C, $"map {online.MapId}");
            string bot = online?.Session.IsPlayerbot == true ? " bot" : string.Empty;
            context.Reply(string.Create(C, $"  [{slot.SubGroup + 1}] {slot.Name}{(slot.Assistant ? " (assist)" : string.Empty)}{bot}: {where}"));
        }

        return true;
    }

    private static WorldObject? Subject(CommandContext context)
    {
        if (GmSelectedUnit.Of(context) is { } unit)
        {
            return unit;
        }

        return context.Player;
    }

    private static bool Grid(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        WorldObject? subject = Subject(context);
        if (subject is null || context.Player.Map is not { } map)
        {
            context.Reply("You are not in a map.");
            return true;
        }

        GridContainer grids = map.Grids;
        GridCoord grid = GridDefines.ComputeGridCoord(subject.X, subject.Y);
        CellCoord cell = GridDefines.ComputeCellCoord(subject.X, subject.Y);
        CellCoord? stored = grids.CellOf(subject);
        string name = subject is Unit u ? InspectCommands.Describe(u) : subject.Guid.ToString();
        context.Reply(string.Create(C,
            $"{name} at ({subject.X:F1}, {subject.Y:F1}, {subject.Z:F1}) map {map.MapId}: grid {grid} (id {grid.Id}, {(grids.IsGridLoaded(grid) ? "loaded" : "not loaded")}), cell {cell} (local {cell.LocalX},{cell.LocalY})"));
        context.Reply(string.Create(C,
            $"  in the grid container: {(stored is { } s ? $"cell {s}{(s == cell ? string.Empty : " (STALE: differs from the position)")}" : "no")}, {(grids.IsActive(subject) ? "active object" : "passive object")}, in an active cell: {grids.IsInActiveCell(subject)}"));
        context.Reply(string.Create(C,
            $"  map: {grids.LoadedGridCount} grids loaded, {grids.ObjectCount} objects, {grids.ActiveCellCount} active cells, largest bounding radius {grids.MaxBoundingRadius:F1} yd, active objects near this grid: {grids.ActiveObjectsNearGrid(grid)}"));
        return true;
    }

    private static bool Visibility(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        Player me = context.Player;
        if (GmSelectedUnit.Of(context) is not { } target || ReferenceEquals(target, me))
        {
            context.Reply(string.Create(C, $"You see {me.VisibleObjects.Count} objects. Select another unit to compare."));
            return true;
        }

        float dx = target.X - me.ViewPoint.X, dy = target.Y - me.ViewPoint.Y, dz = target.Z - me.ViewPoint.Z;
        bool inSet = me.VisibleObjects.Contains(target.Guid);
        bool inRange = target.MapId == me.MapId && Map.IsWithinVisibilityDistance(me.ViewPoint, target, inSet);
        context.Reply(string.Create(C,
            $"{InspectCommands.Describe(target)}: {MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz)):F1} yd from your view point, in visibility range {inRange}, in your visible set {inSet} (of {me.VisibleObjects.Count})"));
        if (inRange && !inSet)
        {
            context.Reply("  in range but not sent: a visibility rule (stealth, invisibility, GM visibility...) hides it, or the next visibility pass has not run yet.");
        }

        if (target is Player other)
        {
            context.Reply(string.Create(C, $"  {other.Name} has you in its visible set: {other.VisibleObjects.Contains(me.Guid)} (of {other.VisibleObjects.Count})"));
        }

        return true;
    }
}

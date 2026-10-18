using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Tools;

/// <summary>
/// <c>.creature spawnmany #entry #count [#radius]</c>: place up to 200 temporary creatures of one template spread over a disc around you at
/// ground height (stress and AI testing; clean up with <c>.creature despawntemp</c>). No vmangos equivalent.
/// </summary>
public sealed class CreatureSpawnManyCommand : ICommandExtension
{
    /// <summary>Most creatures one <c>.creature spawnmany</c> places.</summary>
    public const int MaxCount = 200;

    public string Path => "creature";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("spawnmany", AccountSecurity.GameMaster,
            "Syntax: .creature spawnmany #entry #count [#radius]\nPlace #count (1-200) temporary creatures of #entry spread evenly over a disc of #radius yards around you (default 15, 2-100) at ground height. Not saved; remove them with .creature despawntemp.",
            SpawnMany, RetailLevel: 3),
    ];

    private static bool SpawnMany(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint entry) || !args.ExtractUInt32(out uint count) || !args.ExtractOptFloat(out float radius, 15f) || !args.IsEmpty
            || count is 0 or > MaxCount || !float.IsFinite(radius) || radius < 2f || radius > 100f)
        {
            return false;
        }

        if (context.IsConsole)
        {
            context.Reply("Run this in game: the creatures are placed around you.");
            return true;
        }

        CreatureWorldFeature feature = context.Session.Services.GetRequiredService<CreatureWorldFeature>();
        if (feature.Content.FindTemplate(entry) is not { } template)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Creature template {entry} not found."));
            return true;
        }

        Player me = context.Player;
        if (me.Map is not { } map)
        {
            context.Reply("You are not in a map.");
            return true;
        }

        CreatureMapSystem system = feature.GetOrCreateSystem(map);
        // Sunflower (Vogel) spiral: even coverage of the disc for any count.
        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        for (int i = 0; i < count; i++)
        {
            float r = count == 1 ? 0f : radius * MathF.Sqrt((i + 0.5f) / count);
            float angle = i * golden;
            float x = me.X + (MathF.Cos(angle) * r), y = me.Y + (MathF.Sin(angle) * r);
            float z = map.Collision.GetHeight(x, y, me.Z + 5f);
            if (!float.IsFinite(z) || z <= TerrainTile.InvalidHeightValue + 1f || MathF.Abs(z - me.Z) > 30f) z = me.Z;
            system.SpawnTemporary(template, x, y, z, angle);
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Placed {count} temporary {template.Name} (entry {entry}) within {radius:0.#} yards."));
        return true;
    }
}

/// <summary>
/// <c>.gobject spawnmany #entry #count [#radius] [#despawnSeconds]</c>: place up to 50 temporary game objects of one template spread over a disc around you.
/// </summary>
public sealed class GameObjectSpawnManyCommand : ICommandExtension
{
    /// <summary>Most game objects one <c>.gobject spawnmany</c> places.</summary>
    public const int MaxCount = 50;

    public string Path => "gobject";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("spawnmany", AccountSecurity.Administrator,
            "Syntax: .gobject spawnmany #entry #count [#radius] [#despawnSeconds]\nPlace #count (1-50) temporary game objects of #entry spread evenly over a disc of #radius yards around you (default 10, 1-100) for #despawnSeconds (default 60, 0 = until restart). Not saved.",
            SpawnMany, RetailLevel: 4),
    ];

    private static bool SpawnMany(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint entry) || entry == 0 || !args.ExtractUInt32(out uint count)
            || !args.ExtractOptFloat(out float radius, 10f) || !args.ExtractOptUInt32(out uint despawnSeconds, 60) || !args.IsEmpty
            || count is 0 or > MaxCount || !float.IsFinite(radius) || radius < 1f || radius > 100f || despawnSeconds > 86400)
        {
            return false;
        }

        if (context.IsConsole)
        {
            context.Reply("Run this in game: the game objects are placed around you.");
            return true;
        }

        if (GmObjectCommands.SystemOf(context) is not { } system)
        {
            context.Reply("You are not in a map with game objects.");
            return true;
        }

        if (system.FindTemplate(entry) is not { } template)
        {
            context.Reply(GmObjectCommands.TemplateMissing(entry));
            return true;
        }

        Player me = context.Player;
        if (me.Map is not { } map)
        {
            context.Reply("You are not in a map.");
            return true;
        }

        float golden = MathF.PI * (3f - MathF.Sqrt(5f));
        int placed = 0;
        for (int i = 0; i < count; i++)
        {
            float r = count == 1 ? 0f : radius * MathF.Sqrt((i + 0.5f) / count);
            float angle = i * golden;
            float x = me.X + (MathF.Cos(angle) * r), y = me.Y + (MathF.Sin(angle) * r);
            float z = map.Collision.GetHeight(x, y, me.Z + 5f);
            if (!float.IsFinite(z) || z <= TerrainTile.InvalidHeightValue + 1f || MathF.Abs(z - me.Z) > 30f) z = me.Z;
            if (system.Summon(entry, x, y, z, angle, despawnSeconds) is not null)
            {
                placed++;
            }
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Placed {placed} temporary {template.Name} (entry {entry}) within {radius:0.#} yards (despawn {despawnSeconds} s)."));
        return true;
    }
}

/// <summary>
/// <c>.inspect items [$filter]</c> (every item the selected player carries: slot, entry, name, count, durability, guid) and
/// <c>.alter durability #percent [all]</c> (set worn, or with <c>all</c> also carried, items to a durability percentage; 0 breaks them).
/// No vmangos equivalent.
/// </summary>
public sealed class ItemToolsCommands : ICommandExtension
{
    /// <summary>Most rows <c>.inspect items</c> prints.</summary>
    public const int MaxRows = 60;

    public string Path => "inspect";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("items", AccountSecurity.GameMaster,
            "Syntax: .inspect items [$filter]\nList the items of the selected player (or yours), equipped, backpack, bags and bank: bag/slot, entry, name, count, durability and item guid; $filter keeps names containing it (at most 60 rows).",
            Items, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    internal static IEnumerable<Item> Carried(Player player)
    {
        var seen = new HashSet<ObjectGuid>();
        foreach (Item item in player.Inventory.AllItems)
        {
            if (seen.Add(item.Guid)) yield return item;
            if (item is Container bag)
            {
                for (int i = 0; i < bag.Size; i++)
                {
                    if (bag[i] is { } inner && seen.Add(inner.Guid)) yield return inner;
                }
            }
        }
    }

    internal static Player? TargetPlayer(CommandContext context)
    {
        Player? player = context.SelectedPlayerOrSelf();
        if (player is null)
        {
            context.Reply("No player selected.");
            return null;
        }

        return context.CanActOn(player) ? player : null;
    }

    private static bool Items(CommandContext context, string text)
    {
        string filter = text.Trim();
        if (TargetPlayer(context) is not { } player) return true;
        var items = Carried(player).Where(i => filter.Length == 0 || i.Template.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        context.Reply(string.Create(C, $"{player.Name}: {items.Count} item(s){(filter.Length > 0 ? $" matching '{filter}'" : string.Empty)}"));
        foreach (Item item in items.OrderBy(i => i.BagSlot).ThenBy(i => i.Slot).Take(MaxRows))
        {
            string durability = item.MaxDurability > 0 ? string.Create(C, $" dur {item.Durability}/{item.MaxDurability}{(item.Durability == 0 ? " BROKEN" : string.Empty)}") : string.Empty;
            context.Reply(string.Create(C, $"  bag {item.BagSlot} slot {item.Slot}: {item.Entry} [{item.Template.Name}] x{item.Count}{durability} guid {item.Guid.Counter}"));
        }

        if (items.Count > MaxRows) context.Reply(string.Create(C, $"  ... {items.Count - MaxRows} more (use a filter)"));
        return true;
    }
}

/// <summary><c>.alter durability #percent [all]</c>.</summary>
public sealed class DurabilityAlterCommand : ICommandExtension
{
    public string Path => "alter";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("durability", AccountSecurity.GameMaster,
            "Syntax: .alter durability #percent [all]\nSet the durability of the selected player's (or your) worn items, or with 'all' every carried item, to #percent (0-100) of the maximum; 0 breaks them and a broken worn item loses its stats. Saved.",
            Durability, RetailLevel: 3),
    ];

    private static bool Durability(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint percent) || percent > 100) return false;
        bool all = false;
        if (args.ExtractArg() is { } word)
        {
            if (!word.Equals("all", StringComparison.OrdinalIgnoreCase) || !args.IsEmpty) return false;
            all = true;
        }

        if (ItemToolsCommands.TargetPlayer(context) is not { } player) return true;
        if (!player.CanMutateQuestSettlementState)
        {
            context.Reply($"{player.Name} is settling; try again in a moment.");
            return true;
        }

        PlayerInventory inventory = player.Inventory;
        int changed = 0;
        foreach (Item item in ItemToolsCommands.Carried(player).ToList())
        {
            bool worn = item.Container is null && item.Slot < InventorySlots.EquipmentEnd;
            if (item.MaxDurability == 0 || (!all && !worn)) continue;
            uint target = (uint)((ulong)item.MaxDurability * percent / 100);
            if (item.Durability == target) continue;
            inventory.RepairDurability(item);
            if (target < item.MaxDurability) inventory.DurabilityPointsLoss(item, (int)(item.MaxDurability - target));
            changed++;
        }

        if (changed > 0) context.World.SavePlayer(player);
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"{player.Name}: {changed} {(all ? "carried" : "worn")} item(s) set to {percent}% durability{(changed > 0 && percent < 100 && !inventory.Options.DurabilityLossEnable ? " (durability loss is disabled on this server, so only repairs applied)" : string.Empty)}."));
        return true;
    }
}

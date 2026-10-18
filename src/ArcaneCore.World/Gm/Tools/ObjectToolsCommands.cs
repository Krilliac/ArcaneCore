using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Gm.Objects;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Tools;

/// <summary>Shared lookup of the game object a GM command acts on: by guid number, or the nearest one around the invoker.</summary>
internal static class GmGameObjectTarget
{
    /// <summary>How far <c>.inspect gobject</c> and the <c>.alter go*</c> commands look for the nearest object without a guid.</summary>
    public const float NearestRadius = 30f;

    /// <summary>The object with guid number <paramref name="counter"/> (0 = nearest within <see cref="NearestRadius"/>), or null after replying.</summary>
    public static GameObject? Find(CommandContext context, uint counter, out GameObjectMapSystem? system)
    {
        system = context.IsConsole ? null : GmObjectCommands.SystemOf(context);
        if (system is null)
        {
            context.Reply("You are not in a map with game objects.");
            return null;
        }

        Player me = context.Player;
        GameObject? go = counter != 0
            ? system.GameObjects.FirstOrDefault(g => g.Guid.Counter == counter)
            : system.GameObjects
                .Select(g => (Go: g, Distance: GmDistance.Between(me, g)))
                .Where(t => float.IsFinite(t.Distance) && t.Distance <= NearestRadius)
                .OrderBy(t => t.Distance).ThenBy(t => t.Go.Guid.Counter)
                .Select(t => t.Go).FirstOrDefault();
        if (go is null)
        {
            context.Reply(counter != 0
                ? GmObjectCommands.NotFound(counter)
                : string.Create(CultureInfo.InvariantCulture, $"No game object within {NearestRadius:F0} yards."));
        }

        return go;
    }

    /// <summary>Parse an optional trailing guid number (0 when absent); false on junk.</summary>
    public static bool TryOptionalGuid(CommandArgs args, out uint counter)
    {
        counter = 0;
        return args.IsEmpty || (args.ExtractUInt32(out counter) && args.IsEmpty);
    }
}

/// <summary>
/// <c>.inspect gobject [#guid]</c> (the nearest object's full use/loot/respawn state) and <c>.inspect cooldowns</c> (the running spell and
/// category cooldowns of any unit, creatures included; <c>.cooldown list</c> takes only players). Read-only, no vmangos equivalent.
/// </summary>
public sealed class ToolsInspectCommands : ICommandExtension
{
    /// <summary>Most loot rows <c>.inspect gobject</c> prints.</summary>
    public const int MaxLootRows = 16;

    public string Path => "inspect";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("gobject", AccountSecurity.GameMaster,
            "Syntax: .inspect gobject [#guid]\nShow a game object (the nearest within 30 yards without #guid): template, type, state, loot state, flags, dynamic flags, faction, user, owner, summoning spell, use count, cooldown, auto-close and respawn timers, skill-ups granted, non-zero data fields and its open loot.",
            GameObjectInfo, RetailLevel: 3),
        new("cooldowns", AccountSecurity.GameMaster,
            "Syntax: .inspect cooldowns\nList the running spell and category cooldowns of the selected unit (creature or player, yourself without a selection) with the time left.",
            Cooldowns, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    internal static string Seconds(long ms) => (ms / 1000.0).ToString("F1", C) + "s";

    private static bool GameObjectInfo(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!GmGameObjectTarget.TryOptionalGuid(args, out uint counter))
        {
            return false;
        }

        if (GmGameObjectTarget.Find(context, counter, out GameObjectMapSystem? system) is not { } go || system is null)
        {
            return true;
        }

        GameObjectInspection state = system.Inspect(go);
        GameObjectTemplate template = go.Template;
        string origin = go.Spawn is { } spawn ? string.Create(C, $"database spawn {spawn.Guid} (spawntimesecs {spawn.SpawnTimeSeconds})") : "runtime object";
        context.Reply(string.Create(C,
            $"{template.Name} entry {go.Entry} guid {go.Guid.Counter} type {go.Type} ({template.Type}) display {template.DisplayId} size {template.Size:0.##}, {GmDistance.Between(context.Player, go):F1} yd from you; {origin}{(state.Tracked ? string.Empty : ", NOT tracked by its map system")}"));
        context.Reply(string.Create(C,
            $"  state {go.State}, loot state {go.LootState}, {(go.IsSpawned ? "spawned" : "despawned")}; flags 0x{(uint)go.Flags:X} [{InspectCommands.FlagNames<GameObjectFlags>((uint)go.Flags)}], dynflags field 0x{go.GetUInt32(UpdateFields.GameobjectDynFlags):X} (per viewer), faction {go.GetUInt32(UpdateFields.GameobjectFaction)}, anim {go.GetUInt32(UpdateFields.GameobjectAnimprogress)}"));
        string user = go.User.IsEmpty ? "none" : context.World.FindOnlinePlayer(go.User)?.Name ?? go.User.ToString();
        string owner = go.OwnerGuid.IsEmpty ? "none" : context.World.FindOnlinePlayer(go.OwnerGuid)?.Name ?? go.OwnerGuid.ToString();
        context.Reply(string.Create(C,
            $"  user {user}, owner {owner}, spell {go.SpellId}, owner group {go.OwnerGroupId}, uses {state.UseCount}, skill-ups granted {go.SkillupSet.Count}, age {Seconds(state.AgeMs)}"));
        context.Reply(string.Create(C,
            $"  cooldown {(state.CooldownMs is { } cd ? Seconds(cd) : "none")}, auto-close {(state.AutoCloseInSeconds is { } close ? close + "s" : "none")}, respawn {(state.RespawnInMs is { } respawn ? "in " + Seconds(respawn) : "no timer")} (rolled delay {state.RolledRespawnSeconds}s, never despawns {go.NeverDespawns}, spawned by default {go.SpawnedByDefault})"));
        var data = template.Data.Select((v, i) => (v, i)).Where(t => t.v != 0).Select(t => string.Create(C, $"data{t.i}={t.v}")).ToList();
        context.Reply("  " + (data.Count == 0 ? "all data fields 0" : string.Join(" ", data)));
        if (go.Loot is { } loot)
        {
            context.Reply(string.Create(C,
                $"  loot: {loot.Items.Count} items, {loot.Gold} copper, owner {(loot.Owner.IsEmpty ? "none" : loot.Owner.ToString())}, {loot.Viewers.Count} viewers, closed {loot.IsClosed}"));
            foreach (LootItem item in loot.Items.Take(MaxLootRows))
            {
                context.Reply(string.Create(C,
                    $"    slot {item.Slot}: item {item.ItemId} x{item.Count}{(item.IsQuestItem ? " quest" : string.Empty)}{(item.IsPerPlayer ? " per-player" : string.Empty)}{(item.IsLooted ? " looted" : string.Empty)}"));
            }
        }

        return true;
    }

    private static bool Cooldowns(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (InspectCommands.Target(context) is not { } unit) return true;
        if (context.Session.Services.GetService<SpellFeature>()?.System is not { } spells)
        {
            context.Reply("The spell system is not running.");
            return true;
        }

        IReadOnlyList<InitialSpellCooldown> cooldowns = spells.GetActiveCooldowns(unit);
        context.Reply(string.Create(C, $"{InspectCommands.Describe(unit)}: {cooldowns.Count} running cooldown(s)"));
        foreach (InitialSpellCooldown cd in cooldowns.OrderByDescending(c => Math.Max(c.CooldownMs, c.CategoryCooldownMs)))
        {
            string name = spells.Store.Get(cd.SpellId)?.Name ?? "?";
            context.Reply(string.Create(C,
                $"  {cd.SpellId} [{name}]: spell {(cd.CooldownMs > 0 ? Seconds(cd.CooldownMs) : "-")}, category {cd.Category} {(cd.CategoryCooldownMs > 0 ? Seconds(cd.CategoryCooldownMs) : "-")}{(cd.ItemId != 0 ? $", item {cd.ItemId}" : string.Empty)}"));
        }

        return true;
    }
}

/// <summary>
/// More <c>.alter</c> setters: unit dynamic flags, combat reach and bounding radius, and a game object's state and flags. Not saved; the
/// server may overwrite them (a respawn, a loot change). No vmangos equivalent.
/// </summary>
public sealed class ToolsAlterCommands : ICommandExtension
{
    /// <summary>UNIT_DYNAMIC_FLAGS bit names (1.12 client).</summary>
    public static readonly IReadOnlyDictionary<string, uint> UnitDynFlags = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    {
        ["Lootable"] = 0x01,
        ["TrackUnit"] = 0x02,
        ["Tapped"] = LootService.UnitDynFlagTapped,
        ["TappedByPlayer"] = LootService.UnitDynFlagTappedByPlayer,
        ["SpecialInfo"] = 0x10,
        ["Dead"] = 0x20,
    };

    public string Path => "alter";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("dynflag", AccountSecurity.GameMaster,
            "Syntax: .alter dynflag $flag|#hex on|off\nSet or clear one dynamic flag of the selected unit: Lootable, TrackUnit, Tapped, TappedByPlayer, SpecialInfo, Dead or a hex bit.",
            DynFlag, RetailLevel: 3),
        new("combatreach", AccountSecurity.GameMaster,
            "Syntax: .alter combatreach #yards\nSet the combat reach of the selected unit (0-50 yards; melee range is built from it). A creature gets its model's value back when it respawns.",
            CombatReach, RetailLevel: 3),
        new("boundingradius", AccountSecurity.GameMaster,
            "Syntax: .alter boundingradius #yards\nSet the bounding radius of the selected unit (0-50 yards; distances and spell ranges subtract it).",
            BoundingRadius, RetailLevel: 3),
        new("gostate", AccountSecurity.GameMaster,
            "Syntax: .alter gostate active|ready|alternative|#state [#guid]\nSet the state of a game object (the nearest within 30 yards without #guid): active opens a door, ready closes it. Its loot state does not change.",
            GoState, RetailLevel: 3),
        new("goflag", AccountSecurity.GameMaster,
            "Syntax: .alter goflag $flag|#hex on|off [#guid]\nSet or clear one flag of a game object (the nearest within 30 yards without #guid): InUse, Locked, InteractCond, Transport, NoInteract, NoDespawn, Triggered or a hex bit.",
            GoFlag, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    internal static uint? ParseBit(string name, IReadOnlyDictionary<string, uint> names)
    {
        if (names.TryGetValue(name, out uint bit)) return bit;
        string hex = name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? name[2..] : name;
        return uint.TryParse(hex, NumberStyles.HexNumber, C, out uint raw) && raw != 0 && (raw & (raw - 1)) == 0 ? raw : null;
    }

    private static bool DynFlag(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (args.ExtractArg() is not { } name || !args.ExtractOnOff(out bool on) || !args.IsEmpty) return false;
        if (ParseBit(name, UnitDynFlags) is not { } bit)
        {
            context.Reply($"Unknown dynamic flag '{name}'. Names: {string.Join(", ", UnitDynFlags.Keys)}.");
            return true;
        }

        if (InspectCommands.Target(context) is not { } unit) return true;
        uint value = unit.GetUInt32(UpdateFields.UnitDynamicFlags);
        value = on ? value | bit : value & ~bit;
        unit.SetUInt32(UpdateFields.UnitDynamicFlags, value);
        string bitName = UnitDynFlags.FirstOrDefault(p => p.Value == bit).Key ?? string.Create(C, $"0x{bit:X}");
        context.Reply(string.Create(C, $"{InspectCommands.Name(unit)} dynamic flag {bitName} {(on ? "set" : "cleared")}; dynamic flags now 0x{value:X}."));
        return true;
    }

    private static bool TryYards(string text, out float yards)
        => float.TryParse(text.Trim(), NumberStyles.Float, C, out yards) && float.IsFinite(yards) && yards >= 0f && yards <= 50f;

    private static bool CombatReach(CommandContext context, string text)
    {
        if (!TryYards(text, out float yards)) return false;
        if (InspectCommands.Target(context) is not { } unit) return true;
        float before = unit.GetFloat(UpdateFields.UnitFieldCombatreach);
        unit.SetFloat(UpdateFields.UnitFieldCombatreach, yards);
        context.Reply(string.Create(C, $"{InspectCommands.Name(unit)} combat reach {before:0.###} -> {yards:0.###} yd."));
        return true;
    }

    private static bool BoundingRadius(CommandContext context, string text)
    {
        if (!TryYards(text, out float yards)) return false;
        if (InspectCommands.Target(context) is not { } unit) return true;
        float before = unit.GetFloat(UpdateFields.UnitFieldBoundingradius);
        unit.SetFloat(UpdateFields.UnitFieldBoundingradius, yards);
        context.Reply(string.Create(C, $"{InspectCommands.Name(unit)} bounding radius {before:0.###} -> {yards:0.###} yd."));
        return true;
    }

    private static bool GoState(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? word = args.ExtractArg();
        GameObjectState state;
        if (word is null) return false;
        if (uint.TryParse(word, NumberStyles.None, C, out uint raw) && raw <= 2) state = (GameObjectState)raw;
        else if (word.Equals("alternative", StringComparison.OrdinalIgnoreCase)) state = GameObjectState.ActiveAlternative;
        else if (!Enum.TryParse(word, ignoreCase: true, out state) || !Enum.IsDefined(state) || uint.TryParse(word, out _)) return false;
        if (!GmGameObjectTarget.TryOptionalGuid(args, out uint counter)) return false;
        if (GmGameObjectTarget.Find(context, counter, out _) is not { } go) return true;
        GameObjectState before = go.State;
        go.State = state;
        context.Reply(string.Create(C, $"{go.Template.Name} (guid {go.Guid.Counter}) state {before} -> {state}."));
        return true;
    }

    private static readonly IReadOnlyDictionary<string, uint> GoFlags = Enum.GetValues<GameObjectFlags>()
        .Where(f => f != GameObjectFlags.None).ToDictionary(f => f.ToString(), f => (uint)f, StringComparer.OrdinalIgnoreCase);

    private static bool GoFlag(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (args.ExtractArg() is not { } name || !args.ExtractOnOff(out bool on) || !GmGameObjectTarget.TryOptionalGuid(args, out uint counter)) return false;
        if (ParseBit(name, GoFlags) is not { } bit)
        {
            context.Reply($"Unknown game object flag '{name}'. Names: {string.Join(", ", GoFlags.Keys)}.");
            return true;
        }

        if (GmGameObjectTarget.Find(context, counter, out _) is not { } go) return true;
        go.Flags = on ? go.Flags | (GameObjectFlags)bit : go.Flags & ~(GameObjectFlags)bit;
        context.Reply(string.Create(C,
            $"{go.Template.Name} (guid {go.Guid.Counter}) flag {InspectCommands.FlagNames<GameObjectFlags>(bit)} {(on ? "set" : "cleared")}; flags now 0x{(uint)go.Flags:X}."));
        return true;
    }
}

using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Gm.Npc;
using ArcaneCore.World.Npc;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using GameQuest = ArcaneCore.Game.Quests.Quest;

namespace ArcaneCore.World.Gm.Tools;

/// <summary>
/// Read-only <c>.inspect</c> views of world and character state: <c>quests</c> (the quest log with objective progress), <c>instance</c>
/// (the instance script of your map: class, encounter flag, data slots, save string), <c>weather</c> (every zone weather of your map) and
/// <c>worldstates</c> (the SMSG_INIT_WORLD_STATES pairs you would get in your zone now). No vmangos equivalent.
/// </summary>
public sealed class WorldInspectCommands : ICommandExtension
{
    /// <summary>Highest data slot <c>.inspect instance</c> reads (GetData 0..this).</summary>
    public const uint MaxDataSlot = 63;

    public string Path => "inspect";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("quests", AccountSecurity.GameMaster,
            "Syntax: .inspect quests [$filter]\nList the quest log of the selected player (or yours): slot, id, title, status, each kill/object and item objective's progress and the timer; $filter keeps the titles containing it. Also counts the rewarded quests.",
            Quests, RetailLevel: 3),
        new("instance", AccountSecurity.GameMaster,
            "Syntax: .inspect instance\nShow the instance script of your map: its class, whether an encounter is in progress, the non-zero data slots (GetData 0-63), non-zero 64-bit slots (GetData64 0-63) and the save string.",
            Instance, RetailLevel: 3),
        new("weather", AccountSecurity.GameMaster,
            "Syntax: .inspect weather\nShow the weather of your zone and of every zone of your map that has weather: type, grade, permanent flag and whether the zone has a chance table.",
            Weather, RetailLevel: 3),
        new("worldstates", AccountSecurity.GameMaster,
            "Syntax: .inspect worldstates [#state]\nList the world state pairs SMSG_INIT_WORLD_STATES would send you in your zone now (defaults plus every provider: battlegrounds, events...), or only #state.",
            WorldStates, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool Quests(CommandContext context, string text)
    {
        string filter = text.Trim();
        Player? player = context.SelectedPlayerOrSelf();
        if (player is null || !context.CanActOn(player))
        {
            if (player is null) context.Reply("No player selected.");
            return true;
        }

        QuestNpcServices services = context.Session.Services.GetRequiredService<QuestNpcFeature>().Services;
        if (services.StateOf(player) is not { } state)
        {
            context.Reply($"{player.Name} has no loaded quest state.");
            return true;
        }

        PlayerQuestLog log = state.Quests;
        int rewarded = log.Statuses.Values.Count(s => s.Rewarded);
        var rows = new List<string>();
        for (int slot = 0; slot < QuestConstants.MaxQuestLogSize; slot++)
        {
            uint id = log.SlotQuestId(slot);
            if (id == 0) continue;
            GameQuest? quest = services.Quests.Get(id);
            string title = quest?.Title ?? "?";
            if (filter.Length > 0 && !title.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            QuestStatusData? data = log.Get(id);
            var objectives = new List<string>();
            if (quest is not null && data is not null)
            {
                for (int i = 0; i < QuestConstants.ObjectivesCount; i++)
                {
                    if (quest.ReqCreatureOrGOId[i] != 0)
                        objectives.Add(string.Create(C, $"o{i} {(quest.ReqCreatureOrGOId[i] > 0 ? "npc" : "go")} {Math.Abs(quest.ReqCreatureOrGOId[i])} {data.CreatureOrGOCount[i]}/{quest.ReqCreatureOrGOCount[i]}"));
                    if (quest.ReqItemId[i] != 0)
                        objectives.Add(string.Create(C, $"i{i} item {quest.ReqItemId[i]} {data.ItemCount[i]}/{quest.ReqItemCount[i]}"));
                }
            }

            string timer = data is { TimerEndUnix: > 0 } ? string.Create(C, $", fails in {Math.Max(0, data.TimerEndUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds())}s") : string.Empty;
            rows.Add(string.Create(C,
                $"  [{slot}] {id} [{title}] {data?.Status.ToString() ?? "no status"}{timer}{(objectives.Count > 0 ? ": " + string.Join(", ", objectives) : string.Empty)}"));
        }

        context.Reply(string.Create(C, $"{player.Name}: {rows.Count} logged quest(s){(filter.Length > 0 ? $" matching '{filter}'" : string.Empty)}, {rewarded} rewarded, {log.TimedQuests.Count} timed"));
        foreach (string row in rows) context.Reply(row);
        return true;
    }

    internal static InstanceData? ScriptOf(CommandContext context)
    {
        if (context.IsConsole || context.Player.Map is not { } map)
        {
            context.Reply("You are not in a map.");
            return null;
        }

        if (map.FindUpdater<InstanceData>() is not { } script)
        {
            context.Reply(string.Create(C, $"Map {map.MapId}/{map.InstanceId} has no instance script."));
            return null;
        }

        return script;
    }

    private static bool Instance(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (ScriptOf(context) is not { } script) return true;
        Map map = script.Instance;
        var data = new List<string>();
        var data64 = new List<string>();
        for (uint slot = 0; slot <= MaxDataSlot; slot++)
        {
            uint value = script.GetData(slot);
            if (value != 0) data.Add(string.Create(C, $"{slot}={value}"));
            ulong value64 = script.GetData64(slot);
            if (value64 != 0) data64.Add(string.Create(C, $"{slot}=0x{value64:X}"));
        }

        context.Reply(string.Create(C,
            $"map {map.MapId}/{map.InstanceId} [{map.Template?.Name ?? "?"}]: script {script.GetType().Name}, encounter in progress {script.IsEncounterInProgress}, {map.PlayerCount} player(s)"));
        context.Reply("  data: " + (data.Count == 0 ? "all slots 0-63 are 0" : string.Join(" ", data)));
        if (data64.Count > 0)
        {
            context.Reply("  data64: " + string.Join(" ", data64));
        }

        string save = script.GetSaveData() ?? "(none)";
        context.Reply("  save: " + (save.Length > 200 ? save[..200] + "..." : save));
        return true;
    }

    private static bool Weather(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (context.IsConsole || context.Player.Map is not { } map)
        {
            context.Reply("You are not in a map.");
            return true;
        }

        uint zone = context.Player.ZoneId;
        static string Describe(WeatherState w) => string.Create(C,
            $"zone {w.Zone}: {w.Type} grade {w.Grade:0.##}{(w.IsPermanent ? " permanent" : string.Empty)}{(w.Chances is null ? ", no chance table" : string.Empty)}");
        context.Reply(map.GetWeather(zone) is { } mine ? "Your " + Describe(mine) : string.Create(C, $"Your zone {zone} has no weather yet."));
        if (map.FindUpdater<MapWeather>() is { } weather)
        {
            foreach (uint other in weather.ActiveZones.Where(z => z != zone).Order().Take(20))
            {
                if (weather.Find(other) is { } w) context.Reply("  " + Describe(w));
            }
        }

        return true;
    }

    private static bool WorldStates(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        uint only = 0;
        if (!args.IsEmpty && (!args.ExtractUInt32(out only) || !args.IsEmpty)) return false;
        Player me = context.Player;
        List<WorldStatePair> states = WorldStateHooks.For(context.World).WorldStates.Build(me, me.ZoneId);
        var shown = states.Where(s => only == 0 || s.State == only).OrderBy(s => s.State).ToList();
        context.Reply(string.Create(C, $"{states.Count} world state(s) for map {me.MapId} zone {me.ZoneId}{(only != 0 ? $", {shown.Count} matching {only}" : string.Empty)}:"));
        foreach (string line in shown.Take(80).Chunk(8).Select(chunk => "  " + string.Join("  ", chunk.Select(s => string.Create(C, $"{s.State}={s.Value}")))))
        {
            context.Reply(line);
        }

        return true;
    }
}

/// <summary><c>.quest objective</c>: set quest objective progress.</summary>
public sealed class QuestObjectiveCommands : ICommandExtension
{
    public string Path => "quest";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("objective", AccountSecurity.Administrator,
            "Syntax: .quest objective #quest #index #count\nSet the kill or use-object progress of objective #index (0-3) of a quest in the selected player's (or your) log to #count (clamped to the required count); completion is re-checked both ways. Item objectives follow the bags: use .additem/.deleteitem.",
            Objective, RetailLevel: 4),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool Objective(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (args.ExtractKeyFromLink("Hquest", out _, out _) is not { } questText || !uint.TryParse(questText, NumberStyles.None, C, out uint questId)
            || !args.ExtractUInt32(out uint index) || !args.ExtractUInt32(out uint count) || !args.IsEmpty)
        {
            return false;
        }

        Player? player = context.SelectedPlayerOrSelf();
        if (player is null || !context.CanActOn(player))
        {
            if (player is null) context.Reply("No player selected.");
            return true;
        }

        QuestNpcServices services = context.Session.Services.GetRequiredService<QuestNpcFeature>().Services;
        if (services.Quests.Get(questId) is not { } quest)
        {
            context.Reply(string.Create(C, $"Quest {questId} not found."));
            return true;
        }

        GmObjectiveResult result = services.GmSetObjectiveCount(player, quest, (int)Math.Min(index, 99), count, out uint applied);
        context.Reply(result switch
        {
            GmObjectiveResult.Set => string.Create(C,
                $"{player.Name}: quest {quest.Id} [{quest.Title}] objective {index} now {applied}/{quest.ReqCreatureOrGOCount[(int)index]}; quest {services.StateOf(player)?.Quests.GetStatus(quest.Id)}."),
            GmObjectiveResult.NotInLog => string.Create(C, $"{player.Name} does not have quest {quest.Id} in progress."),
            GmObjectiveResult.ItemObjective => string.Create(C, $"Objective {index} of quest {quest.Id} is an item objective; give or take item {quest.ReqItemId[(int)index]} instead."),
            GmObjectiveResult.NoSuchObjective => string.Create(C, $"Quest {quest.Id} has no objective {index}."),
            _ => $"{player.Name}'s quest state is not loaded.",
        });
        return true;
    }
}

/// <summary><c>.alter instancedata #slot #value</c> and <c>.alter instancedata64 #slot [#value|selected]</c>: SetData/SetData64 on your map's instance script.</summary>
public sealed class InstanceAlterCommands : ICommandExtension
{
    public string Path => "alter";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("instancedata", AccountSecurity.Administrator,
            "Syntax: .alter instancedata #slot #value\nCall SetData(#slot, #value) on your map's instance script (encounter states: 0 not started, 1 in progress, 2 failed, 3 done, as the script defines) and show GetData before and after. The script may save it.",
            InstanceData, RetailLevel: 4),
        new("instancedata64", AccountSecurity.Administrator,
            "Syntax: .alter instancedata64 #slot [#value|selected]\nRead or write a 64-bit slot on your map's instance script (GetData64/SetData64). Pass a decimal or 0x hex 64-bit value, 'selected' to write the selected unit's 64-bit GUID, or omit the value to read the slot.",
            InstanceData64, RetailLevel: 4),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool InstanceData(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint slot) || !args.ExtractUInt32(out uint value) || !args.IsEmpty) return false;
        if (WorldInspectCommands.ScriptOf(context) is not { } script) return true;
        uint before = script.GetData(slot);
        script.SetData(slot, value);
        context.Reply(string.Create(C, $"{script.GetType().Name} data {slot}: {before} -> {script.GetData(slot)} (asked {value})."));
        return true;
    }

    private static bool InstanceData64(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint slot)) return false;
        string? token = args.ExtractArg();
        if (!args.IsEmpty) return false;
        if (WorldInspectCommands.ScriptOf(context) is not { } script) return true;
        ulong before = script.GetData64(slot);
        if (token is null)
        {
            context.Reply(string.Create(C, $"{script.GetType().Name} data64 {slot}: 0x{before:X16} ({before})."));
            return true;
        }

        ulong value;
        if (token.Equals("selected", StringComparison.OrdinalIgnoreCase))
        {
            if (GmSelectedUnit.Of(context) is not { } selected)
            {
                context.Reply("Select a unit first, or pass a 64-bit value.");
                return true;
            }

            value = selected.Guid.Value;
        }
        else if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!ulong.TryParse(token.AsSpan(2), NumberStyles.HexNumber, C, out value)) return false;
        }
        else if (!ulong.TryParse(token, NumberStyles.None, C, out value))
        {
            return false;
        }

        script.SetData64(slot, value);
        ulong after = script.GetData64(slot);
        context.Reply(string.Create(C,
            $"{script.GetType().Name} data64 {slot}: 0x{before:X16} ({before}) -> 0x{after:X16} ({after}) (asked 0x{value:X16})."));
        return true;
    }
}

/// <summary>
/// <c>.event info #event_id</c> (detailed schedule, spawns, quests and DB script count for an event) and
/// <c>.event trigger #id</c> (run <c>dbscripts_on_event</c> #id on the current map, or start game event #id).
/// </summary>
public sealed class GameEventToolsCommands : ICommandExtension
{
    public string Path => "event";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("info", AccountSecurity.GameMaster,
            "Syntax: .event info #event_id\nShow an event's schedule, state, holiday, prerequisites, attached creature/gameobject spawns and quests, and dbscripts_on_event step count.",
            EventInfo, RetailLevel: 3),
        new("trigger", AccountSecurity.Administrator,
            "Syntax: .event trigger #id\nTrigger dbscripts_on_event script #id on your map (with the selected unit or yourself as source), or start game event #id if no DB event script #id exists.",
            Trigger, RetailLevel: 4),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool EventInfo(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hgameevent", out uint id) || id == 0 || !args.IsEmpty)
        {
            return false;
        }

        GameEventService? service = context.Session.Services.GetService<GameEventFeature>()?.Service;
        GameEventDefinition? def = id <= ushort.MaxValue ? service?.Find((ushort)id) : null;
        IReadOnlyList<RelayScriptStep> dbSteps = context.Session.Services.GetService<CreatureWorldFeature>()?.Content.Ai.DbScripts.Get(DbScriptKind.Event, id) ?? [];
        if (def is null && dbSteps.Count == 0)
        {
            context.Reply(GameEventCommands.EventNotExistText);
            return true;
        }

        if (def is not null && service is not null)
        {
            ushort eid = def.Id;
            bool active = service.IsActiveEvent(eid);
            int cPos = service.Rows.Creatures.GetValueOrDefault(eid)?.Count ?? 0;
            int cNeg = service.Rows.Creatures.GetValueOrDefault(-eid)?.Count ?? 0;
            int goPos = service.Rows.GameObjects.GetValueOrDefault(eid)?.Count ?? 0;
            int goNeg = service.Rows.GameObjects.GetValueOrDefault(-eid)?.Count ?? 0;
            int quests = service.Rows.Quests.GetValueOrDefault(eid)?.Count ?? 0;
            context.Reply(string.Create(C,
                $"Event {eid}: {def.Description}{(active ? GameEventCommands.ActiveStateText : GameEventCommands.InactiveStateText)} (schedule {def.ScheduleType}, holiday {def.HolidayId}, disabled {def.Disabled})"));
            context.Reply(string.Create(C,
                $"  spawns: +{cPos}/-{cNeg} creature(s), +{goPos}/-{goNeg} gameobject(s), {quests} quest(s), {dbSteps.Count} dbscripts_on_event step(s)"));
        }
        else
        {
            context.Reply(string.Create(C, $"Event script {id}: no game_event row; {dbSteps.Count} dbscripts_on_event step(s)."));
        }

        return true;
    }

    private static bool Trigger(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hgameevent", out uint id) || id == 0 || !args.IsEmpty)
        {
            return false;
        }

        if (!context.IsConsole && GmNpcCommands.SystemOf(context) is { } system && system.HasDbScript(DbScriptKind.Event, id))
        {
            WorldObject source = (WorldObject?)GmSelectedUnit.Of(context) ?? context.Player;
            bool started = system.StartDbScript(DbScriptKind.Event, id, source, context.Player);
            int steps = context.Session.Services.GetService<CreatureWorldFeature>()?.Content.Ai.DbScripts.Get(DbScriptKind.Event, id).Count ?? 0;
            context.Reply(string.Create(C,
                $"Triggered dbscripts_on_event {id} ({steps} step(s), active {started}) on map {system.Map.MapId}."));
            return true;
        }

        GameEventService? service = context.Session.Services.GetService<GameEventFeature>()?.Service;
        if (id <= ushort.MaxValue && service is not null && service.Find((ushort)id) is { } def && service.IsValidEvent(def.Id))
        {
            if (!service.IsActiveEvent(def.Id))
            {
                service.StartEvent(def.Id, overwrite: true);
            }

            context.Reply(string.Format(C, GameEventCommands.StartedText, def.Id, def.Description));
            return true;
        }

        context.Reply(string.Create(C, $"No dbscripts_on_event or valid game_event found for id {id}."));
        return true;
    }
}

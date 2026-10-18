using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Game.WorldState.Weather;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Npc;
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
            "Syntax: .inspect instance\nShow the instance script of your map: its class, whether an encounter is in progress, the non-zero data slots (GetData 0-63) and the save string.",
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
        for (uint slot = 0; slot <= MaxDataSlot; slot++)
        {
            uint value = script.GetData(slot);
            if (value != 0) data.Add(string.Create(C, $"{slot}={value}"));
        }

        context.Reply(string.Create(C,
            $"map {map.MapId}/{map.InstanceId} [{map.Template?.Name ?? "?"}]: script {script.GetType().Name}, encounter in progress {script.IsEncounterInProgress}, {map.PlayerCount} player(s)"));
        context.Reply("  data: " + (data.Count == 0 ? "all slots 0-63 are 0" : string.Join(" ", data)));
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

/// <summary><c>.quest objective</c> and <c>.alter instancedata</c>: set quest objective progress and an instance script data slot.</summary>
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

/// <summary><c>.alter instancedata #slot #value</c>: SetData on your map's instance script.</summary>
public sealed class InstanceAlterCommands : ICommandExtension
{
    public string Path => "alter";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("instancedata", AccountSecurity.Administrator,
            "Syntax: .alter instancedata #slot #value\nCall SetData(#slot, #value) on your map's instance script (encounter states: 0 not started, 1 in progress, 2 failed, 3 done, as the script defines) and show GetData before and after. The script may save it.",
            InstanceData, RetailLevel: 4),
    ];

    private static bool InstanceData(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint slot) || !args.ExtractUInt32(out uint value) || !args.IsEmpty) return false;
        if (WorldInspectCommands.ScriptOf(context) is not { } script) return true;
        uint before = script.GetData(slot);
        script.SetData(slot, value);
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{script.GetType().Name} data {slot}: {before} -> {script.GetData(slot)} (asked {value})."));
        return true;
    }
}

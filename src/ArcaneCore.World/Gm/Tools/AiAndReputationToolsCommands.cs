using System.Globalization;
using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Gm.Npc;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Reputation.Gm;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Tools;

/// <summary>
/// <c>.ai waypoints [resume|#path]</c>: resume the selected creature's waypoint movement or switch it to a specific waypoint path.
/// </summary>
public sealed class AiWaypointToolsCommands : ICommandExtension
{
    public string Path => "ai";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("waypoints", AccountSecurity.GameMaster,
            "Syntax: .ai waypoints [resume|#path]\nResume the selected creature's waypoint movement (dropping temporary idle/follow/point movement), or switch it onto waypoint path #path (entry path first, else shared waypoint_path).",
            Waypoints, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool Waypoints(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        uint? requestedPath = null;
        if (!args.IsEmpty)
        {
            string? first = args.ExtractArg();
            if (first is null) return false;
            if (first.Equals("resume", StringComparison.OrdinalIgnoreCase))
            {
                if (!args.IsEmpty)
                {
                    if (!args.ExtractUInt32(out uint pathAfterResume) || !args.IsEmpty) return false;
                    requestedPath = pathAfterResume;
                }
            }
            else if (uint.TryParse(first, NumberStyles.None, C, out uint parsedPath) && args.IsEmpty)
            {
                requestedPath = parsedPath;
            }
            else
            {
                return false;
            }
        }

        if (InspectCommands.TargetCreature(context) is not { } creature) return true;
        if (GmNpcCommands.SystemOf(context) is not { } system) return true;
        CreatureContent content = context.Session.Services.GetService<CreatureWorldFeature>()?.Content ?? CreatureContent.Empty;

        if (requestedPath is null)
        {
            if (creature.Motion.DefaultType == MovementGeneratorType.Waypoint)
            {
                creature.Motion.Clear();
                int defaultPts = content.ResolveWaypointPath(creature.Spawn?.Guid ?? 0, creature.Template.Entry).Points.Count;
                context.Reply(string.Create(C,
                    $"{InspectCommands.Describe(creature)} resumed waypoint movement (last waypoint {creature.Motion.LastReachedWaypoint}, {defaultPts} point(s))."));
                return true;
            }

            requestedPath = 0;
        }

        uint pathId = requestedPath.Value;
        uint flags = 0;
        int pointCount;
        if (pathId == 0)
        {
            pointCount = content.ResolveWaypointPath(creature.Spawn?.Guid ?? 0, creature.Template.Entry).Points.Count;
        }
        else
        {
            pointCount = content.GetEntryWaypoints(creature.Template.Entry, pathId).Count;
            if (pointCount == 0 && content.GetWaypointPath(pathId).Count is > 0 and int sharedCount)
            {
                flags = CreatureMapSystem.ChangeMovementFlagWaypointPath;
                pointCount = sharedCount;
            }
        }

        creature.Motion.Clear();
        system.ChangeMovement(creature, 2, pathId, flags);
        if (pointCount == 0)
        {
            context.Reply(string.Create(C,
                $"{InspectCommands.Describe(creature)} has no waypoint path {(pathId == 0 ? "(default)" : pathId.ToString(C))}; standing idle."));
        }
        else
        {
            context.Reply(string.Create(C,
                $"{InspectCommands.Describe(creature)} started waypoint path {(pathId == 0 ? "default" : pathId.ToString(C))} ({pointCount} point(s), current {creature.Motion.CurrentType})."));
        }

        return true;
    }
}

/// <summary>
/// <c>.inspect aispells</c> (selected creature's EventAI/SmartAI spell-casting rows and timers) and
/// <c>.inspect reputation [$filter]</c> (selected player's reputation standings, base vs delta, rank progress and flags).
/// </summary>
public sealed class InspectAiSpellsAndReputationCommands : ICommandExtension
{
    public const int MaxRows = 30;

    public string Path => "inspect";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("aispells", AccountSecurity.GameMaster,
            "Syntax: .inspect aispells\nShow the selected creature's EventAI or SmartAI spell-casting rows, their armed/active state, current timer, phase mask, target and cast flags.",
            AiSpells, RetailLevel: 3),
        new("reputation", AccountSecurity.GameMaster,
            "Syntax: .inspect reputation [$filter]\nShow the selected player's (or your) reputation standings: modified or at-war factions by default, or factions matching $filter (or 'all'), with rank, total, base, delta, rank progress and flags.",
            Reputation, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool AiSpells(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (InspectCommands.TargetCreature(context) is not { } creature) return true;

        SpellStore store = context.Session.Services.GetService<SpellFeature>()?.System.Store ?? SpellStore.Empty;
        string SpellLabel(uint id) => store.Get(id) is { } s ? string.Create(C, $"{id} [{s.Name}]") : id.ToString(C);

        CreatureAI? ai = creature.AI;
        if (ai is CreatureEventAI eventAi)
        {
            var castRows = new List<string>();
            foreach (EventAiHolder holder in eventAi.Engine.Holders)
            {
                CreatureAiEvent ev = holder.Event;
                int actionIndex = 0;
                foreach (CreatureAiAction action in ev.Actions)
                {
                    actionIndex++;
                    if (action.Type != (byte)EventAiActionType.Cast) continue;
                    uint spellId = unchecked((uint)action.Param1);
                    var target = (EventAiTarget)action.Param2;
                    uint castFlags = unchecked((uint)action.Param3);
                    castRows.Add(string.Create(C,
                        $"  row {ev.Id} a{actionIndex}: spell {SpellLabel(spellId)} -> {target}, event {(EventAiEventType)ev.EventType}({ev.EventType}), armed {holder.Enabled}, timer {holder.TimerMs} ms (p1={ev.Param1} p2={ev.Param2} p3={ev.Param3} p4={ev.Param4}), chance {ev.Chance}%, invPhase 0x{ev.InversePhaseMask:X}, castFlags 0x{castFlags:X}"));
                }
            }

            context.Reply(string.Create(C,
                $"{InspectCommands.Describe(creature)} EventAI spells: phase {eventAi.Phase}, {castRows.Count} cast action(s) across {eventAi.Engine.Holders.Count} holder(s)"));
            foreach (string line in castRows.Take(MaxRows))
            {
                context.Reply(line);
            }

            if (castRows.Count == 0)
            {
                context.Reply("  No EventAI cast actions on this creature.");
            }

            return true;
        }

        if (ai is CreatureSmartAI smartAi)
        {
            SmartScript script = smartAi.Script;
            var castRows = new List<string>();
            foreach ((SmartHolder holder, bool inTimedList) in script.Events.Select(h => (h, false)).Concat(script.TimedActionList.Select(h => (h, true))))
            {
                SmartScriptRow row = holder.Row;
                if (row.ActionType != (byte)SmartAction.Cast) continue;
                uint spellId = row.ActionParam1;
                var castFlags = (SmartCastFlags)row.ActionParam2;
                var target = (SmartTarget)row.TargetType;
                castRows.Add(string.Create(C,
                    $"  row {row.Id}{(inTimedList ? " (timed-list)" : string.Empty)}: spell {SpellLabel(spellId)} -> {target}, event {(SmartEvent)row.EventType}({row.EventType}), active {holder.Active}, timer {holder.TimerMs} ms (p1={row.EventParam1} p2={row.EventParam2} p3={row.EventParam3} p4={row.EventParam4}), chance {row.EventChance}%, phaseMask 0x{row.EventPhaseMask:X}, castFlags {castFlags}"));
            }

            context.Reply(string.Create(C,
                $"{InspectCommands.Describe(creature)} SmartAI spells: phase {script.Phase}, {castRows.Count} cast action(s) across {script.Events.Count} event(s)"));
            foreach (string line in castRows.Take(MaxRows))
            {
                context.Reply(line);
            }

            if (castRows.Count == 0)
            {
                context.Reply("  No SmartAI cast actions on this creature.");
            }

            return true;
        }

        context.Reply(string.Create(C,
            $"{InspectCommands.Describe(creature)} AI {(ai is null ? "none" : ai.GetType().Name)} has no EventAI or SmartAI spell table (trainerSpell {creature.Template.TrainerSpell}, petSpellData {creature.Template.PetSpellDataId})."));
        return true;
    }

    private static bool Reputation(CommandContext context, string text)
    {
        string filter = text.Trim();
        if (ItemToolsCommands.TargetPlayer(context) is not { } player) return true;
        if (ReputationGmText.ServiceOf(context) is not { } service) return true;
        if (service.For(player) is not { } rep)
        {
            context.Reply($"{player.Name} has no loaded reputation state.");
            return true;
        }

        bool showAll = filter.Equals("all", StringComparison.OrdinalIgnoreCase);
        uint filterId = uint.TryParse(filter, NumberStyles.None, C, out uint parsedId) ? parsedId : 0;
        List<FactionState> allStates = [.. rep.States];
        int modifiedCount = allStates.Count(s => s.Standing != 0);
        int atWarCount = allStates.Count(s => s.IsAtWar);

        IEnumerable<FactionState> query = allStates;
        if (!showAll)
        {
            if (filter.Length > 0)
            {
                query = query.Where(s =>
                    (filterId != 0 && s.Faction.Id == filterId)
                    || s.Faction.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                query = query.Where(s => s.Standing != 0 || s.IsAtWar || s.ListId == rep.WatchedFaction);
            }
        }

        List<FactionState> matches = [.. query.OrderBy(s => s.Faction.Id)];
        context.Reply(string.Create(C,
            $"{player.Name} reputation: {allStates.Count} faction(s) ({modifiedCount} modified from base, {atWarCount} at war, watched slot {rep.WatchedFaction}), showing {Math.Min(matches.Count, MaxRows)} of {matches.Count}:"));
        foreach (FactionState state in matches.Take(MaxRows))
        {
            FactionRecord faction = state.Faction;
            int baseRep = rep.BaseReputation(faction);
            int total = baseRep + state.Standing;
            ReputationRank rank = ReputationMath.ToRank(total);
            int inRank = total - ReputationMath.FirstPointOf(rank);
            int rankWidth = ReputationMath.PointsInRank[(int)rank];
            string delta = state.Standing >= 0 ? $"+{state.Standing}" : state.Standing.ToString(C);
            context.Reply(string.Create(C,
                $"  [{faction.Id}] {faction.Name}: {ReputationRankNames.Name(rank)} {total} ({inRank}/{rankWidth}, base {baseRep}, delta {delta}) [{InspectCommands.FlagNames<FactionStateFlags>((uint)state.Flags)}]"));
        }

        if (matches.Count > MaxRows)
        {
            context.Reply(string.Create(C, $"  ... {matches.Count - MaxRows} more (pass a faction name or id filter)."));
        }

        return true;
    }
}

/// <summary>
/// <c>.alter reputation (#factionId|$name) (#value|+#delta|delta #d|$rank [#inRank]) [nospillover]</c>: set or adjust reputation by id, link, or faction name, with rank shortcuts and delta support.
/// </summary>
public sealed class AlterReputationToolsCommands : ICommandExtension
{
    public string Path => "alter";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("reputation", AccountSecurity.GameMaster,
            "Syntax: .alter reputation (#factionId|$factionName) (#value|+#delta|delta #amount|$rank [#inRank]) [nospillover]\nSet or adjust the selected player's (or your) reputation with a faction (by id, link, or quoted/single-word name) to an exact value, a signed delta (+500 or 'delta -500'), or a rank shortcut (hated..exalted), optionally suppressing spillover.",
            AlterReputation, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool AlterReputation(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (args.IsEmpty) return false;

        uint factionId = 0;
        string? factionName = null;
        if (!args.ExtractUInt32KeyFromLink("Hfaction", out factionId))
        {
            factionName = args.ExtractQuotedOrLiteral();
            if (string.IsNullOrWhiteSpace(factionName)) return false;
        }

        string? changeToken = args.ExtractArg();
        if (changeToken is null) return false;

        bool incremental = false;
        int amount;
        if (changeToken.Equals("delta", StringComparison.OrdinalIgnoreCase))
        {
            if (!args.ExtractInt32(out amount)) return false;
            incremental = true;
        }
        else if (changeToken.StartsWith('+') && int.TryParse(changeToken.AsSpan(1), NumberStyles.None, C, out int plusDelta))
        {
            amount = plusDelta;
            incremental = true;
        }
        else if (int.TryParse(changeToken, NumberStyles.AllowLeadingSign, C, out int directValue))
        {
            amount = directValue;
        }
        else if (ReputationRankNames.TryParsePrefix(changeToken, out ReputationRank rank))
        {
            amount = ReputationMath.FirstPointOf(rank);
            int maxInRank = ReputationMath.PointsInRank[(int)rank] - 1;
            if (!args.IsEmpty && args.ExtractInt32(out int inRankDelta))
            {
                if (inRankDelta < 0 || inRankDelta > maxInRank)
                {
                    context.Reply(ReputationGmText.DeltaRange(maxInRank));
                    return true;
                }

                amount += inRankDelta;
            }
        }
        else
        {
            context.Reply(ReputationGmText.InvalidParameter(changeToken));
            return true;
        }

        bool noSpillover = false;
        if (!args.IsEmpty)
        {
            string? tail = args.ExtractArg();
            if (tail is null || !tail.Equals("nospillover", StringComparison.OrdinalIgnoreCase) || !args.IsEmpty)
            {
                return false;
            }

            noSpillover = true;
        }

        if (ItemToolsCommands.TargetPlayer(context) is not { } target) return true;
        if (ReputationGmText.ServiceOf(context) is not { } service) return true;

        FactionRecord? faction = null;
        if (factionId != 0)
        {
            faction = service.Factions.Find(factionId);
            if (faction is null)
            {
                context.Reply(ReputationGmText.FactionUnknown(factionId));
                return true;
            }
        }
        else if (factionName is not null)
        {
            faction = service.Factions.All.FirstOrDefault(f => f.CanHaveReputation && f.Name.Equals(factionName, StringComparison.OrdinalIgnoreCase))
                ?? service.Factions.All.FirstOrDefault(f => f.CanHaveReputation && f.Name.Contains(factionName, StringComparison.OrdinalIgnoreCase))
                ?? service.Factions.All.FirstOrDefault(f => f.Name.Contains(factionName, StringComparison.OrdinalIgnoreCase));
            if (faction is null)
            {
                context.Reply(ReputationGmText.FactionNotFound);
                return true;
            }
        }

        if (faction is null) return false;
        if (!faction.CanHaveReputation)
        {
            context.Reply(ReputationGmText.NoReputation(faction.Name, faction.Id));
            return true;
        }

        int before = service.GetReputation(target, faction.Id);
        ReputationRank beforeRank = service.GetRank(target, faction.Id);
        bool changed = incremental
            ? service.ModifyReputation(target, faction.Id, amount, noSpillover)
            : noSpillover
                ? service.For(target)?.Apply(faction, amount, incremental: false) ?? false
                : service.SetReputation(target, faction.Id, amount);
        if (!changed)
        {
            context.Reply(ReputationGmText.NotChanged(GmStrings.PlayerLink(target.Name)));
            return true;
        }

        int after = service.GetReputation(target, faction.Id);
        ReputationRank afterRank = service.GetRank(target, faction.Id);
        int baseRep = service.For(target)?.BaseReputation(faction) ?? 0;
        int standing = service.For(target)?.State(faction)?.Standing ?? (after - baseRep);
        string delta = standing >= 0 ? $"+{standing}" : standing.ToString(C);
        context.Reply(string.Create(C,
            $"{target.Name} reputation with {faction.Name} ({faction.Id}): {before} ({ReputationRankNames.Name(beforeRank)}) -> {after} ({ReputationRankNames.Name(afterRank)}, base {baseRep}, delta {delta})."));
        return true;
    }
}

using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The area-trigger legs of a journey (<see cref="TravelMeans.AreaTrigger"/>): a trigger whose <c>areatrigger_teleport</c> row leads to
/// another place of its own map, and which a bot may simply walk into. Only the triggers of <see cref="TravelTriggerIds"/> are offered: the
/// ones known to lead somewhere a walk cannot (Teldrassil's tree top to Rut'theran and back). Thread affinity: world thread.
/// </summary>
internal sealed partial class PlayerbotTravel
{
    /// <summary>
    /// The same-map teleport triggers a journey may take: 527 and 542 (Teldrassil, the tree top and Rut'theran Village; both unconditional).
    /// UNVERIFIED: 702 and 704 were asked for too if same-map and unconditional; the content available here has no areatrigger tables to check
    /// that, so they are not listed.
    /// </summary>
    internal static readonly uint[] TravelTriggerIds = [527, 542];

    /// <summary>A trigger leg is done on its target's map within this of the target (the teleport lands near it).</summary>
    internal const float TriggerArriveYards = ArriveYards * 3f;

    /// <summary>Standing at a trigger's centre this long without it firing (the bot was already inside its volume) fails the leg.</summary>
    internal const uint TriggerCentreMs = 5_000;

    /// <summary>The triggers of <see cref="TravelTriggerIds"/> this bot may take now: on one map, no requirement beyond its level, not excluded.</summary>
    private static List<TravelTrigger> TriggersFor(WorldSession session, Player player, TravelExclusions? excluded)
    {
        List<TravelTrigger> triggers = [];
        WorldMaps maps = WorldMaps.Of(session.World);
        foreach (uint id in TravelTriggerIds)
        {
            if (excluded?.Triggers.Contains(id) == true) continue;
            if (maps.FindAreaTriggerTeleport(id) is not { } teleport) continue;
            if (teleport.RequiredItem != 0 || teleport.RequiredItem2 != 0 || teleport.RequiredQuestDone != 0 || teleport.RequiredCondition != 0) continue;
            if (teleport.RequiredLevel > player.Level) continue;
            AreaTriggerTemplate? template = null;
            foreach (AreaTriggerTemplate candidate in maps.AreaTriggers)
            {
                if (candidate.Id == id) { template = candidate; break; }
            }

            if (template is null || template.MapId != teleport.TargetMap) continue;
            triggers.Add(new TravelTrigger(id, new TravelPlace(template.MapId, new Vector3(template.X, template.Y, template.Z)),
                new TravelPlace(teleport.TargetMap, new Vector3(teleport.TargetX, teleport.TargetY, teleport.TargetZ))));
        }

        return triggers;
    }

    /// <summary>
    /// One step of a trigger leg: done on the target within <see cref="TriggerArriveYards"/>; else walk to the trigger's centre (the motion
    /// reports the volume it enters, <see cref="PlayerbotAreaTriggers"/>). A refusal of this trigger or a failed walk fails the leg and
    /// leaves the trigger out of the replan.
    /// </summary>
    private static StepResult TriggerLeg(WorldSession session, Player player, PlayerbotOptions options, TravelJourney journey, TravelLeg leg,
        uint elapsedMs, uint now, out string? why)
    {
        why = null;
        if (player.MapId == leg.To.MapId && TravelPlace.Of(player).DistanceTo(leg.To) <= TriggerArriveYards)
        {
            PlayerbotMovementControl.Stop(session, player);
            return StepResult.Next;
        }

        if (player.MapId != leg.From.MapId)
        {
            journey.Excluded.Triggers.Add(leg.TriggerId);
            why = string.Create(CultureInfo.InvariantCulture, $"trigger {leg.TriggerId}: on map {player.MapId}, the trigger on map {leg.From.MapId}");
            return StepResult.Failed;
        }

        if (PlayerbotAreaTriggers.TryConsumeTeleportRefusal(player, leg.TriggerId, journey.LegBeganMs, out string refusal))
        {
            journey.Excluded.Triggers.Add(leg.TriggerId);
            why = string.Create(CultureInfo.InvariantCulture, $"trigger {leg.TriggerId}: {refusal}");
            return StepResult.Failed;
        }

        if (Vector3.Distance(new Vector3(player.X, player.Y, player.Z), leg.From.Position) <= 3f)
        {
            if (journey.AtTriggerCentreMs == 0) journey.AtTriggerCentreMs = now | 1u;
            else if (unchecked(now - journey.AtTriggerCentreMs) > TriggerCentreMs)
            {
                journey.Excluded.Triggers.Add(leg.TriggerId);
                why = string.Create(CultureInfo.InvariantCulture, $"trigger {leg.TriggerId}: stood at its centre for {TriggerCentreMs / 1000} s and it did not fire");
                return StepResult.Failed;
            }
        }

        switch (journey.Walk.Step(session, player, options, leg.From.Position, elapsedMs, now, journey.SpendLeft(options.Travel), out string? walkWhy))
        {
            case JourneyWalk.Outcome.Yield:
                return StepResult.Yield;
            case JourneyWalk.Outcome.Failed:
                journey.Excluded.Triggers.Add(leg.TriggerId);
                why = string.Create(CultureInfo.InvariantCulture, $"trigger {leg.TriggerId}: walk: {walkWhy}");
                return StepResult.Failed;
            default:
                return StepResult.Traveling;
        }
    }
}

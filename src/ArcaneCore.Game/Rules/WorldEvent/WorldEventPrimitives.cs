namespace ArcaneCore.Game.Rules.WorldEvent;

/// <summary><c>world_event.started(mode)</c>: the game event started.</summary>
[RulePrimitive(RuleHostKind.WorldEvent, WorldEventRuleIds.Started, "world_event.started",
    Description = "The game event started (a restart that only resumes an event that was running counts as a start of mode 0 and 2).")]
[RuleParam(1, "mode", RuleParamKind.Int, Description = "0 every start, 1 only a fresh start, 2 only a resume after a restart.")]
public sealed class WorldEventStartedEvent : RuleEvent
{
    public override bool Matches(CompiledRule rule, in RuleEventArgs args) => rule.EventParam(1) switch
    {
        1 => args.Value1 == 0,
        2 => args.Value1 != 0,
        _ => true,
    };

    public override string? Validate(in RuleValidationContext context)
        => context.Parameters[0] is < 0 or > 2 ? "mode must be 0 (any), 1 (fresh) or 2 (resume)" : null;
}

/// <summary><c>world_event.stopped</c>: the game event stopped.</summary>
[RulePrimitive(RuleHostKind.WorldEvent, WorldEventRuleIds.Stopped, "world_event.stopped", Description = "The game event stopped.")]
public sealed class WorldEventStoppedEvent : RuleEvent;

/// <summary><c>world_event.update</c>: fired for every running event on every world tick; a repeatable rule with a cooldown makes it a timer.</summary>
[RulePrimitive(RuleHostKind.WorldEvent, WorldEventRuleIds.Update, "world_event.update",
    Description = "Fires on every world tick while the game event runs; give the rule the repeatable flag and a cooldown to run it on a period.")]
public sealed class WorldEventUpdateEvent : RuleEvent;

/// <summary>The shared check of the start and stop actions: an event id in the game-event range.</summary>
internal static class WorldEventParams
{
    internal static string? CheckEventId(in RuleValidationContext context)
        => context.Parameters[0] is < 1 or > ushort.MaxValue ? "event_id must be a game event number (1..65535)" : null;
}

/// <summary><c>world_event.start_event(event_id)</c>.</summary>
[RulePrimitive(RuleHostKind.WorldEvent, WorldEventRuleIds.StartEvent, "world_event.start_event", Description = "Start another game event (GameEventService.StartEvent). It does not change the event's schedule, so use it on serverside events (schedule_type 0); a scheduled event is stopped again by its own window.")]
[RuleParam(1, "event_id", RuleParamKind.Int, Description = "game_event.entry.")]
public sealed class WorldEventStartEventAction : RuleAction
{
    public override string? Validate(in RuleValidationContext context) => WorldEventParams.CheckEventId(context);

    public override RuleActionResult Execute(in RuleActionContext context)
        => context.Subject is WorldEventSubject subject
            ? subject.Control.StartEvent((ushort)context.Action.Param(1)) ? RuleActionResult.Done : RuleActionResult.NoTarget
            : RuleActionResult.Unsupported;
}

/// <summary><c>world_event.stop_event(event_id)</c>.</summary>
[RulePrimitive(RuleHostKind.WorldEvent, WorldEventRuleIds.StopEvent, "world_event.stop_event", Description = "Stop a game event (GameEventService.StopEvent). It does not change the event's schedule, so use it on serverside events (schedule_type 0); a scheduled event inside its window is started again by the schedule.")]
[RuleParam(1, "event_id", RuleParamKind.Int, Description = "game_event.entry.")]
public sealed class WorldEventStopEventAction : RuleAction
{
    public override string? Validate(in RuleValidationContext context) => WorldEventParams.CheckEventId(context);

    public override RuleActionResult Execute(in RuleActionContext context)
        => context.Subject is WorldEventSubject subject
            ? subject.Control.StopEvent((ushort)context.Action.Param(1)) ? RuleActionResult.Done : RuleActionResult.NoTarget
            : RuleActionResult.Unsupported;
}

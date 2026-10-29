namespace ArcaneCore.Game.Rules.WorldEvent;

/// <summary>
/// The <c>world_event</c> rule host (design section 3 and 9, P4): its subjects are the running game events. A game event is owned by
/// <see cref="RuleOwnerKind.GameEvent"/> with its <c>game_event.entry</c>; a global rule applies to every running event. Rules run on the
/// world thread, where <c>GameEventService</c> starts and stops events (<see cref="WorldEventRuleRunner"/>).
/// </summary>
public sealed class WorldEventHost : RuleHostDefinition
{
    public override RuleHostKind Kind => RuleHostKind.WorldEvent;

    public override string Name => "world_event";

    public override string Description => "Game events: rules run when an event starts, stops, and on every world tick while it runs.";

    public override IReadOnlyList<RuleOwnerKind> Owners { get; } = [RuleOwnerKind.Global, RuleOwnerKind.GameEvent];

    public override RuleCapabilities Capabilities => RuleCapabilities.Speak;
}

/// <summary>The ids of the world-event host's events and actions (the numbers rule rows use).</summary>
public static class WorldEventRuleIds
{
    /// <summary><c>world_event.started</c>.</summary>
    public const short Started = 1;

    /// <summary><c>world_event.stopped</c>.</summary>
    public const short Stopped = 2;

    /// <summary><c>world_event.update</c>.</summary>
    public const short Update = 3;

    /// <summary><c>world_event.start_event</c>.</summary>
    public const short StartEvent = 100;

    /// <summary><c>world_event.stop_event</c>.</summary>
    public const short StopEvent = 101;
}

/// <summary>What the world-event rules may do to the game-event system (implemented over <c>GameEventService</c>).</summary>
public interface IWorldEventControl
{
    /// <summary>Start event <paramref name="eventId"/> as <c>GameEventService.StartEvent</c> does; false for an unknown or unusable event.</summary>
    bool StartEvent(ushort eventId);

    /// <summary>Stop event <paramref name="eventId"/>; false for an unknown or unusable event.</summary>
    bool StopEvent(ushort eventId);

    /// <summary>Tell every player a line (an event announcement).</summary>
    void Announce(string text);
}

/// <summary>
/// A running game event as a rule subject. Owned by <see cref="WorldEventRuleRunner"/>; world thread only. It speaks by announcing to the
/// whole world (the <c>say</c> action of a world-event rule is a world announcement).
/// </summary>
public sealed class WorldEventSubject(ushort eventId, IWorldEventControl control) : IRuleSpeaker
{
    /// <summary>High bits of <see cref="Guid"/>: marks a game-event subject so it never equals an object guid.</summary>
    public const ulong GuidTag = 0x5745_0000_0000_0000UL;

    public ushort EventId { get; } = eventId;

    public ulong Guid => GuidTag | EventId;

    public bool IsInCombat => false;

    public RuleSubjectFacts Facts => default;

    internal IWorldEventControl Control { get; } = control ?? throw new ArgumentNullException(nameof(control));

    public void Say(RuleText text, IRuleSubject? target) => Control.Announce(text.Content);
}

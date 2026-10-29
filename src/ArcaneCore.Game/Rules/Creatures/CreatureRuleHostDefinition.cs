namespace ArcaneCore.Game.Rules.Creatures;

/// <summary>
/// The <c>creature</c> rule host (design 3): subjects are the creatures of a map, rules run on the owning map's thread at the points the
/// EventAI executor runs today. Rules attach to a creature entry, to one spawn guid, or to every creature (<c>global</c>). Creature
/// subjects can speak and cast. The vocabulary is in this namespace: events (<see cref="CreatureEvent"/>), targets
/// (<see cref="CreatureTarget"/>) and actions (<see cref="CreatureAction"/>); <c>docs/reference/rules-conversion.md</c> maps EventAI onto it.
/// </summary>
public sealed class CreatureRuleHostDefinition : RuleHostDefinition
{
    public override RuleHostKind Kind => RuleHostKind.Creature;

    public override string Name => "creature";

    public override string Description => "Creatures of a map: events from combat, health, timers, spells and sight, actions on the creature and its targets.";

    public override IReadOnlyList<RuleOwnerKind> Owners { get; } = [RuleOwnerKind.Global, RuleOwnerKind.Entry, RuleOwnerKind.Guid];

    public override RuleCapabilities Capabilities => RuleCapabilities.Speak | RuleCapabilities.Cast;
}

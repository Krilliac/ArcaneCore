namespace ArcaneCore.Game.Conditions;

/// <summary>
/// An <see cref="Npc.IConditionEvaluator"/> that is, or forwards to, a <see cref="ConditionEvaluator"/> over the loaded conditions table
/// (the world's ConditionFeature forwards to the evaluator it rebuilds). A caller whose objects are not a player at an NPC - the DB script
/// engine, which decides the map conditions itself - reads the table through it. World thread.
/// </summary>
public interface IConditionTableEvaluator
{
    /// <summary>The evaluator over the table loaded now.</summary>
    ConditionEvaluator Current { get; }
}

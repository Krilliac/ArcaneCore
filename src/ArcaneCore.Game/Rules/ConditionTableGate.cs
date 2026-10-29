using ArcaneCore.Game.Conditions;

namespace ArcaneCore.Game.Rules;

/// <summary>
/// An <see cref="IRuleConditionGate"/> over the loaded <c>conditions</c> table, read through <paramref name="current"/> at every check
/// (so a <c>.reload conditions</c> is seen at once). It asks the table the way a spawn group does, with no player and no NPC
/// (<see cref="ConditionEvaluator.EvaluateWithoutSubjects"/>): game events, holidays and the AND/OR/NOT built from them are decided; a type
/// about a player or creature is undecidable and fails closed. A host whose subjects are players or creatures supplies its own gate.
/// </summary>
public sealed class ConditionTableGate(Func<ConditionEvaluator> current) : IRuleConditionGate
{
    private readonly Func<ConditionEvaluator> _current = current ?? throw new ArgumentNullException(nameof(current));

    public bool? Evaluate(uint conditionId, IRuleSubject subject, IRuleSubject? invoker) => _current().EvaluateWithoutSubjects(conditionId);
}

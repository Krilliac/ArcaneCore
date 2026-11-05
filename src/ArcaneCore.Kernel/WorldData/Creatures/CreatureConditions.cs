namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>Comparison of a condition (cmangos <c>ConditionOperation</c>, Globals/Conditions.h).</summary>
public enum CreatureConditionOperation : uint
{
    None = 0,
    EqualTo = 1,
    NotEqualTo = 2,
    LessThan = 3,
    LessThanOrEqualTo = 4,
    GreaterThan = 5,
    GreaterThanOrEqualTo = 6,
}

/// <summary>How the two friend or enemy clauses of a combat condition combine (cmangos <c>ConditionLogic</c>, Globals/Conditions.h).</summary>
public enum CreatureConditionLogic : uint
{
    /// <summary>Only the first clause counts.</summary>
    None = 0,
    And = 1,
    Or = 2,
    Xor = 3,
}

/// <summary>One clause of a <c>unit_condition</c> row: a variable (cmangos <c>UnitCondition</c>), a comparison and a value.</summary>
public readonly record struct UnitConditionTerm(uint Variable, CreatureConditionOperation Operation, int Value);

/// <summary>
/// One <c>unit_condition</c> row (cmangos Globals/UnitCondition.h). All of its terms must hold, or with <see cref="AnyOf"/> (the OR flag) at
/// least one. Only the used terms are kept: a row's unused slots are variable 0.
/// </summary>
public sealed record UnitConditionDefinition(int Id, bool AnyOf, IReadOnlyList<UnitConditionTerm> Terms);

/// <summary>One friend or enemy clause of a combat condition: how many units meeting <see cref="ConditionId"/> are compared with <see cref="Count"/>.</summary>
public readonly record struct CombatConditionClause(int ConditionId, CreatureConditionOperation Operation, uint Count);

/// <summary>
/// One <c>combat_condition</c> row (cmangos Globals/CombatCondition.h): a world state expression, a unit condition on the creature and on its
/// target, and up to two counted clauses over its friends and over its enemies.
/// </summary>
public sealed record CombatConditionDefinition(
    int Id,
    uint WorldStateExpressionId,
    int SelfConditionId,
    int TargetConditionId,
    CreatureConditionLogic FriendLogic,
    CreatureConditionLogic EnemyLogic,
    CombatConditionClause Friend0,
    CombatConditionClause Friend1,
    CombatConditionClause Enemy0,
    CombatConditionClause Enemy1);

/// <summary>The <c>combat_condition</c> and <c>unit_condition</c> rows, immutable once built.</summary>
public sealed class CreatureConditionCatalog
{
    public static readonly CreatureConditionCatalog Empty = new([], []);

    private readonly Dictionary<int, CombatConditionDefinition> _combat = [];
    private readonly Dictionary<int, UnitConditionDefinition> _unit = [];

    public CreatureConditionCatalog(IEnumerable<CombatConditionDefinition> combat, IEnumerable<UnitConditionDefinition> unit)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(unit);
        foreach (CombatConditionDefinition row in combat) _combat[row.Id] = row;
        foreach (UnitConditionDefinition row in unit) _unit[row.Id] = row;
    }

    public CombatConditionDefinition? FindCombat(int id) => _combat.GetValueOrDefault(id);

    public UnitConditionDefinition? FindUnit(int id) => _unit.GetValueOrDefault(id);
}

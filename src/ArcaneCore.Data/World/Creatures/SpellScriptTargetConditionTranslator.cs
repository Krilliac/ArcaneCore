using System.Globalization;
using System.Text;
using ArcaneCore.Data.Npc;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>
/// Translates the vmangos <c>conditions</c> row a <c>spell_script_target.conditionId</c> names into the self-contained term list stored in
/// <see cref="SpellScriptTargetRow.TargetCondition"/> (read by <c>ScriptTargetCondition</c> in the game assembly, which documents the grammar).
/// Condition ids are per database, and the types the rows use are vmangos types that do not exist in the cmangos numbering the
/// <c>conditions</c> table holds (vmangos Conditions.h:101 SOURCE_ENTRY 16, :195 HEALTH_PERCENT 41, :217 OBJECT_IS_SPAWNED 48, :219
/// OBJECT_LOOT_STATE 49, :248 OBJECT_GO_STATE 55; mangos-classic Conditions.h marks 16 and 41 unused), so an id is never copied. A condition
/// translates when it is one of those five types, alone or under CONDITION_AND (-1, vmangos Conditions.cpp:162-175: every operand must hold, so
/// the tree flattens to a list); the reverse flag (0x1) of a leaf becomes <c>!</c>. Anything else is refused with a reason: OR, NOT, a
/// reversed AND, the swap-targets flag (0x2), a type outside the five, a health comparison other than 0, 1 or 2, an operand that is missing or
/// loops, or a result longer than <see cref="MaxLength"/>. Pure; thread-safe.
/// </summary>
public static class SpellScriptTargetConditionTranslator
{
    /// <summary>The longest term list the column holds.</summary>
    public const int MaxLength = 255;

    private const int AndType = -1, SourceEntryType = 16, HealthPercentType = 41, ObjectIsSpawnedType = 48, ObjectLootStateType = 49, ObjectGoStateType = 55;
    private const byte ReverseFlag = 0x1;
    private const int MaxDepth = 8;

    /// <summary>
    /// Translate vmangos condition <paramref name="vmangosConditionId"/> (0: none, which gives an empty list). <paramref name="lookup"/> finds a
    /// vmangos <c>conditions</c> row by its entry. False with <paramref name="failure"/> set when the condition cannot be expressed.
    /// </summary>
    public static bool TryTranslate(uint vmangosConditionId, Func<uint, ConditionRow?> lookup, out string expression, out string failure)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        expression = string.Empty;
        failure = string.Empty;
        if (vmangosConditionId == 0)
        {
            return true;
        }

        var terms = new List<string>();
        if (!Collect(vmangosConditionId, lookup, terms, 0, out failure))
        {
            return false;
        }

        expression = string.Join(';', terms);
        if (expression.Length > MaxLength)
        {
            expression = string.Empty;
            failure = $"the translated condition is {terms.Count} terms and longer than {MaxLength} characters";
            return false;
        }

        return true;
    }

    private static bool Collect(uint id, Func<uint, ConditionRow?> lookup, List<string> terms, int depth, out string failure)
    {
        failure = string.Empty;
        if (depth > MaxDepth)
        {
            failure = $"condition {id} nests more than {MaxDepth} levels (or loops)";
            return false;
        }

        if (lookup(id) is not { } row)
        {
            failure = $"vmangos condition {id} is not in the input";
            return false;
        }

        if (row.Type == AndType)
        {
            if (row.Flags != 0)
            {
                failure = $"condition {id} is an AND with flags 0x{row.Flags:X}";
                return false;
            }

            // Conditions.cpp:162-175: value1 and value2 always, value3 and value4 only when set.
            (uint Operand, bool Optional)[] operands = [(row.Value1, false), (row.Value2, false), (row.Value3, true), (row.Value4, true)];
            foreach ((uint operand, bool optional) in operands)
            {
                if ((!optional || operand != 0) && !Collect(operand, lookup, terms, depth + 1, out failure))
                {
                    return false;
                }
            }

            return true;
        }

        if ((row.Flags & ~ReverseFlag) != 0)
        {
            failure = $"condition {id} has flags 0x{row.Flags:X} (only the reverse flag is translated)";
            return false;
        }

        string? term = row.Type switch
        {
            HealthPercentType when row.Value2 <= 2 && row.Value3 == 0 && row.Value4 == 0 => Format("health", row.Value1, row.Value2),
            SourceEntryType when row.Value1 != 0 => Format("entry", [row.Value1, .. new[] { row.Value2, row.Value3, row.Value4 }.Where(v => v != 0)]),
            ObjectIsSpawnedType => "spawned",
            ObjectLootStateType => Format("loot", row.Value1),
            ObjectGoStateType => Format("gostate", row.Value1),
            _ => null,
        };
        if (term is null)
        {
            failure = $"condition {id} has type {row.Type} with values {row.Value1}, {row.Value2}, {row.Value3}, {row.Value4}, which has no script-target translation";
            return false;
        }

        terms.Add((row.Flags & ReverseFlag) != 0 ? "!" + term : term);
        return true;
    }

    private static string Format(string name, params uint[] values)
    {
        var text = new StringBuilder(name);
        foreach (uint value in values)
        {
            text.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }
}

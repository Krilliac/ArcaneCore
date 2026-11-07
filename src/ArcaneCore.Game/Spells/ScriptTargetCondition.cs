using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The condition of one <c>spell_script_target</c> row (vmangos <c>conditionId</c>, SpellMgr.cpp:2068), in the self-contained form the content
/// importer writes to <c>spell_script_target.TargetCondition</c>. Condition ids are per database, and the vmangos condition types that rows use
/// (SOURCE_ENTRY 16, HEALTH_PERCENT 41, OBJECT_IS_SPAWNED 48, OBJECT_LOOT_STATE 49, OBJECT_GO_STATE 55; vmangos Conditions.h:101, 195, 217,
/// 219, 248) are not the cmangos numbers of <c>ConditionType</c> (cmangos marks 16 and 41 unused), so the importer translates the vmangos
/// condition tree into this text and no condition id is stored. The text is a list of terms joined by <c>;</c>, all of which must hold (the
/// vmangos CONDITION_AND of the rows, flattened). A term is <c>[!]name[:value]...</c>, <c>!</c> being CONDITION_FLAG_REVERSE_RESULT:
/// <list type="bullet">
/// <item><c>health:percent:cmp</c> - the candidate unit's health percent, truncated, equal (cmp 0), at least (1) or at most (2) the percent (Conditions.cpp:472-486).</item>
/// <item><c>entry:e1[:e2[:e3[:e4]]]</c> - the caster's entry is one of the entries (Conditions.cpp:266-272; the caster is the condition's source).</item>
/// <item><c>spawned</c> - the candidate is a spawned game object (Conditions.cpp:554-557).</item>
/// <item><c>loot:state</c> - the candidate game object's <see cref="GameObjectLootState"/> (Conditions.cpp:558-561).</item>
/// <item><c>gostate:state</c> - the candidate game object's <see cref="GameObjectState"/> (Conditions.cpp:615-618).</item>
/// </list>
/// As in vmangos (ConditionEntry::Meets, Conditions.cpp:119-140) a term whose candidate has the wrong kind (a unit check on a game object, or the
/// reverse) is false, and the reverse flag does not turn that into true. A text this class cannot read holds for no candidate. Immutable; safe from any thread.
/// </summary>
public sealed class ScriptTargetCondition
{
    private enum Check
    {
        HealthPercent,
        SourceEntry,
        ObjectSpawned,
        ObjectLootState,
        ObjectGoState,
    }

    private readonly record struct Term(Check Check, bool Reverse, uint[] Values);

    private readonly Term[] _terms;
    private readonly bool _readable;

    private ScriptTargetCondition(Term[] terms, bool readable)
    {
        _terms = terms;
        _readable = readable;
    }

    /// <summary>A condition no candidate satisfies (the stand-in for text that cannot be read).</summary>
    public static ScriptTargetCondition Unsatisfiable { get; } = new([], readable: false);

    /// <summary>Read <paramref name="text"/>: null for an empty text (no condition), <see cref="Unsatisfiable"/> for one that cannot be read.</summary>
    public static ScriptTargetCondition? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Split(';', StringSplitOptions.TrimEntries);
        var terms = new Term[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryParseTerm(parts[i], out terms[i]))
            {
                return Unsatisfiable;
            }
        }

        return new ScriptTargetCondition(terms, readable: true);
    }

    private static bool TryParseTerm(string text, out Term term)
    {
        term = default;
        bool reverse = text.StartsWith('!');
        string[] fields = (reverse ? text[1..] : text).Split(':');
        var values = new uint[fields.Length - 1];
        for (int i = 1; i < fields.Length; i++)
        {
            if (!uint.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i - 1]))
            {
                return false;
            }
        }

        Check check;
        switch (fields[0])
        {
            case "health" when values.Length == 2:
                check = Check.HealthPercent;
                break;
            case "entry" when values.Length is >= 1 and <= 4:
                check = Check.SourceEntry;
                break;
            case "spawned" when values.Length == 0:
                check = Check.ObjectSpawned;
                break;
            case "loot" when values.Length == 1:
                check = Check.ObjectLootState;
                break;
            case "gostate" when values.Length == 1:
                check = Check.ObjectGoState;
                break;
            default:
                return false;
        }

        term = new Term(check, reverse, values);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> satisfies every term, with <paramref name="caster"/> as the condition's source (vmangos
    /// <c>IsConditionSatisfied(conditionId, candidate, map, caster)</c>). Reads state only; world thread.
    /// </summary>
    public bool Holds(WorldObject candidate, WorldObject caster)
    {
        if (!_readable)
        {
            return false;
        }

        foreach (Term term in _terms)
        {
            if (!Holds(term, candidate, caster))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Holds(Term term, WorldObject candidate, WorldObject caster)
    {
        uint[] v = term.Values;
        bool? result = term.Check switch
        {
            Check.HealthPercent => candidate is Unit unit ? Compare((long)unit.HealthPercent, v[0], v[1]) : null,
            Check.SourceEntry => EntryOf(caster) is var entry && (entry == v[0] || v.Length > 1 && entry == v[1] && v[1] != 0
                || v.Length > 2 && entry == v[2] && v[2] != 0 || v.Length > 3 && entry == v[3] && v[3] != 0),
            Check.ObjectSpawned => candidate is GameObject spawned ? spawned.IsSpawned : null,
            Check.ObjectLootState => candidate is GameObject looted ? (uint)looted.LootState == v[0] : null,
            Check.ObjectGoState => candidate is GameObject stated ? (uint)stated.State == v[0] : null,
            _ => null,
        };

        return result is { } value && value != term.Reverse;
    }

    /// <summary>The entry vmangos <c>WorldObject::GetEntry</c> gives: a creature's or game object's, 0 for a player.</summary>
    private static uint EntryOf(WorldObject source) => source switch
    {
        Creature creature => creature.Entry,
        GameObject gameObject => gameObject.Entry,
        _ => 0,
    };

    private static bool Compare(long percent, uint value, uint cmp) => cmp switch
    {
        0 => percent == value,
        1 => percent >= value,
        2 => percent <= value,
        _ => false,
    };
}

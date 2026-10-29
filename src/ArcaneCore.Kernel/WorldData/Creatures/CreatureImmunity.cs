namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>
/// The kind of a <c>creature_immunities</c> row (mangos-classic <c>enum SpellImmunity</c>, Globals/SharedDefines.h:462-470); the
/// <see cref="CreatureImmunity.Value"/> means what the comment says.
/// </summary>
public enum CreatureImmunityType : byte
{
    /// <summary>A Spell.dbc effect id (<c>SpellEffects</c>): spell effects of that kind do nothing.</summary>
    Effect = 0,

    /// <summary>An aura type (<c>AuraType</c>): effects that apply that aura do nothing ("aura state").</summary>
    State = 1,

    /// <summary>A school mask: spells of those schools are resisted entirely.</summary>
    School = 2,

    /// <summary>A school mask: damage of those schools is absorbed entirely.</summary>
    Damage = 3,

    /// <summary>A dispel type id (<c>DispelType</c>): spells with that dispel type do nothing.</summary>
    Dispel = 4,

    /// <summary>A Spell.dbc mechanic id (1..31): spells and effects with that mechanic do nothing.</summary>
    Mechanic = 5,
}

/// <summary>One <c>creature_immunities</c> row (cmangos <c>Entry, SetId, Type, Value</c>; ObjectMgr::LoadCreatureImmunities, ObjectMgr.cpp:946-976).</summary>
public sealed record CreatureImmunity(uint Entry, uint SetId, CreatureImmunityType Type, uint Value);

/// <summary>
/// The <c>creature_immunities</c> rows of one creature entry and set id, folded once at load into the masks and lists the spell rules ask
/// about. Immutable and safe to read from any thread. Mechanic, school and damage values are masks or small ids that fit the
/// 32-bit forms the spell rules already use; a value outside the range its type can hold is ignored.
/// </summary>
public sealed class CreatureImmunitySet
{
    private static readonly uint[] None = [];

    internal CreatureImmunitySet(uint entry, uint setId, IReadOnlyCollection<CreatureImmunity> rows)
    {
        Entry = entry;
        SetId = setId;
        var effects = new SortedSet<uint>();
        var states = new SortedSet<uint>();
        foreach (CreatureImmunity row in rows)
        {
            switch (row.Type)
            {
                case CreatureImmunityType.Effect:
                    effects.Add(row.Value);
                    break;
                case CreatureImmunityType.State:
                    states.Add(row.Value);
                    break;
                case CreatureImmunityType.School:
                    SchoolMask |= row.Value;
                    break;
                case CreatureImmunityType.Damage:
                    DamageMask |= row.Value;
                    break;
                case CreatureImmunityType.Dispel when row.Value < 32:
                    DispelMask |= 1u << (int)row.Value;
                    break;
                case CreatureImmunityType.Mechanic when row.Value is >= 1 and <= 31:
                    MechanicMask |= 1u << (int)(row.Value - 1);
                    break;
            }
        }

        Effects = effects.Count == 0 ? None : effects.ToArray();
        States = states.Count == 0 ? None : states.ToArray();
    }

    public uint Entry { get; }

    public uint SetId { get; }

    /// <summary>Bit <c>mechanic - 1</c> per mechanic row.</summary>
    public uint MechanicMask { get; }

    /// <summary>The OR of the school rows' masks.</summary>
    public uint SchoolMask { get; }

    /// <summary>The OR of the damage rows' masks.</summary>
    public uint DamageMask { get; }

    /// <summary>Bit <c>dispel type</c> per dispel row.</summary>
    public uint DispelMask { get; }

    /// <summary>The immune effect ids, ascending.</summary>
    public IReadOnlyList<uint> Effects { get; }

    /// <summary>The immune aura types, ascending.</summary>
    public IReadOnlyList<uint> States { get; }

    /// <summary>Whether the set is immune to effect id <paramref name="effect"/>.</summary>
    public bool ImmuneToEffect(uint effect) => Contains(Effects, effect);

    /// <summary>Whether the set is immune to aura type <paramref name="aura"/>.</summary>
    public bool ImmuneToState(uint aura) => Contains(States, aura);

    private static bool Contains(IReadOnlyList<uint> sorted, uint value)
    {
        for (int i = 0; i < sorted.Count; i++)
        {
            if (sorted[i] == value)
            {
                return true;
            }
        }

        return false;
    }
}

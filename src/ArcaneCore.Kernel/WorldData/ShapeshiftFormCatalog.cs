namespace ArcaneCore.Kernel.WorldData;

/// <summary>
/// One SpellShapeshiftForm.dbc row of build 5875 (cmangos-classic DBCStructure.h SpellShapeshiftFormEntry:
/// ID at field 0, flags1 at field 11, creatureType at field 12).
/// </summary>
/// <param name="Id">The form id (the ShapeshiftForm value, e.g. 17 = Battle Stance).</param>
/// <param name="Flags1">The form flags (bit 0 = SHAPESHIFT_FLAG_STANCE, vmangos SharedDefines.h:1471).</param>
/// <param name="CreatureType">The creature type the form counts as (&lt;= 0 humanoid).</param>
public sealed record ShapeshiftFormInfo(uint Id, uint Flags1, int CreatureType);

/// <summary>The shapeshift forms of the client (SpellShapeshiftForm.dbc), by form id.</summary>
public sealed class ShapeshiftFormCatalog
{
    /// <summary>The form id of Battle, Defensive and Berserker Stance (vmangos SharedDefines.h:1432-1434).</summary>
    private const uint BattleStance = 17;
    private const uint BerserkerStance = 19;

    private readonly Dictionary<uint, ShapeshiftFormInfo> _forms;

    public ShapeshiftFormCatalog(IEnumerable<ShapeshiftFormInfo> forms)
    {
        ArgumentNullException.ThrowIfNull(forms);
        _forms = [];
        foreach (ShapeshiftFormInfo form in forms)
        {
            if (!_forms.TryAdd(form.Id, form))
            {
                throw new ArgumentException($"duplicate shapeshift form {form.Id}", nameof(forms));
            }
        }
    }

    public static ShapeshiftFormCatalog Empty { get; } = new([]);

    /// <summary>
    /// The three warrior stances (forms 17-19) with <c>flags1 = 1</c>, for servers without a client
    /// SpellShapeshiftForm.dbc. vmangos documents SHAPESHIFT_FLAG_STANCE as "Form allows various player
    /// activities which normally cause 'You can't X while shapeshifted' errors (npc/go interaction, item use,
    /// etc)" (SharedDefines.h:1471), which a warrior stance does; the value is inferred from that, not read from a
    /// DBC. Only the warrior stance gate and aura handler rely on it, and neither result depends on the flag for
    /// warrior spells; the DBC (<c>Combat:ShapeshiftFormDbcPath</c>) is authoritative and covers every form.
    /// </summary>
    public static ShapeshiftFormCatalog WarriorStances { get; } = new(
        Enumerable.Range((int)BattleStance, (int)(BerserkerStance - BattleStance + 1))
            .Select(id => new ShapeshiftFormInfo((uint)id, 1, 0)));

    public int Count => _forms.Count;

    /// <summary>Every form, in no particular order.</summary>
    public IEnumerable<ShapeshiftFormInfo> Forms => _forms.Values;

    public bool TryGet(uint form, out ShapeshiftFormInfo info)
    {
        bool found = _forms.TryGetValue(form, out ShapeshiftFormInfo? value);
        info = value!;
        return found;
    }
}

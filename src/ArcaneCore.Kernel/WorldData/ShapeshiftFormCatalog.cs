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

    /// <summary>
    /// Every row of the build-5875 client SpellShapeshiftForm.dbc (32 rows, flags1 and creatureType), for servers
    /// without <c>Combat:ShapeshiftFormDbcPath</c>. Provenance: read from the developer's own 1.12.1 client
    /// (Data\dbc.MPQ, DBFilesClient\SpellShapeshiftForm.dbc), not from the GPL references and not downloaded; only
    /// the two meaningful columns are kept. flags1 is 7 (Stance | NotToggleable | PersistOnDeath) for the warrior
    /// stances 17-19, 8 (CanInteractNpc) for Shadowform 28, 1 (Stance) for Stealth 30 and the Moonkin form 31 (and
    /// the unused row 32), and 0 for every other row, the druid animal forms included. creatureType is 1 (beast)
    /// for 1, 3, 4, 5, 8, 14 and 16, -1 for 17 and 28 and 0 for the rest (vmangos Unit::GetCreatureType ignores
    /// values of 0 or below). A configured DBC always overrides this table.
    /// </summary>
    public static ShapeshiftFormCatalog Retail { get; } = new(
        [
            new(1, 0, 1), new(2, 0, 0), new(3, 0, 1), new(4, 0, 1), new(5, 0, 1), new(6, 0, 0), new(7, 0, 0), new(8, 0, 1),
            new(9, 0, 0), new(10, 0, 0), new(11, 0, 0), new(12, 0, 0), new(13, 0, 0), new(14, 0, 1), new(15, 0, 0), new(16, 0, 1),
            new(17, 7, -1), new(18, 7, 0), new(19, 7, 0),
            new(20, 0, 0), new(21, 0, 0), new(22, 0, 0), new(23, 0, 0), new(24, 0, 0), new(25, 0, 0), new(26, 0, 0), new(27, 0, 0),
            new(28, 8, -1), new(29, 0, 0), new(30, 1, 0), new(31, 1, 0), new(32, 1, 0),
        ]);
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

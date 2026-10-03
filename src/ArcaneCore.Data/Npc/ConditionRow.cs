namespace ArcaneCore.Data.Npc;

/// <summary>
/// One row of the <c>conditions</c> table in the cmangos classic-db layout
/// (<c>condition_entry, type, value1..value4, flags</c>; the <c>comments</c> column is not kept).
/// <see cref="Type"/> is signed: -3 NOT, -2 OR, -1 AND. The numbering of <see cref="Type"/> is cmangos's
/// (mangos-classic Conditions.h:30-82), NOT vmangos's (vmangos reuses ids 11, 13, 27, 28, 31, 35 and 40
/// for other conditions), so only dumps in the cmangos layout are importable.
/// </summary>
public sealed class ConditionRow
{
    public uint ConditionEntry { get; set; }

    public int Type { get; set; }

    public uint Value1 { get; set; }

    public uint Value2 { get; set; }

    public uint Value3 { get; set; }

    public uint Value4 { get; set; }

    public byte Flags { get; set; }
}

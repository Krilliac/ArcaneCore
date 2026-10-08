using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.ClientEffects;

/// <summary>One WorldStateUI.dbc row: a HUD element the client draws from a world-state field.</summary>
/// <param name="Id">The row id (field 0).</param>
/// <param name="MapId">MapID (field 1).</param>
/// <param name="AreaId">AreaID (field 2; 0 = the whole map).</param>
/// <param name="Icon">Icon (field 3).</param>
/// <param name="Text">String_lang[0], the enUS text (field 4).</param>
/// <param name="Tooltip">Tooltip_lang[0], the enUS tooltip (field 13).</param>
/// <param name="StateVariable">StateVariable (field 23): the world-state field that shows the dynamic icon (0 = none).</param>
/// <param name="Type">Type (field 24).</param>
/// <param name="ExtendedUI">ExtendedUI (field 35), e.g. "CAPTUREPOINT".</param>
/// <param name="ExtendedUIStateVariables">The non-zero ExtendedUIStateVariable[3] (fields 36-38).</param>
/// <remarks>
/// The world-state fields a row displays are named inside its text as <c>%NNNNw</c> (e.g. "%1581w/%1601w", the
/// Warsong Gulch flag captures); <see cref="StateVariable"/> and <see cref="ExtendedUIStateVariables"/> are the others it reads.
/// </remarks>
public sealed record WorldStateUIEntry(
    uint Id, int MapId, int AreaId, string Icon, string Text, string Tooltip, uint StateVariable, int Type, string ExtendedUI, IReadOnlyList<uint> ExtendedUIStateVariables);

/// <summary>
/// Reads the developer's build-5875 WorldStateUI.dbc: 39 four-byte fields (mangoszero DBCStructure_reference.h
/// WorldStateUIEntry: id 0, map 1, area 2, icon 3, text 4-11 + flags 12, tooltip 13-20 + flags 21, faction 22, state
/// variable 23, type 24, dynamic icon 25, dynamic tooltip 26-33 + flags 34, extended UI 35, its state variables 36-38).
/// Only the enUS strings are kept. Checked against the client-effective build-5875 file: field 22 is -1 on every row that
/// sets it, 23 holds 2338/2339 (the Warsong Gulch flag-carried icons) and 2426, 24 is 0-2, 35-38 are "CAPTUREPOINT"
/// 2427 2428. Another layout, a zero or repeated id is refused.
/// </summary>
public static class WorldStateUIDbcReader
{
    public const string FileName = "WorldStateUI.dbc";

    public const int FieldCount = 39;

    public static DbcTable<WorldStateUIEntry> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<WorldStateUIEntry> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ClientEffectDbc.RequireFields(file, FileName, FieldCount);
        var rows = new List<WorldStateUIEntry>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new WorldStateUIEntry(
                file.GetUInt32(row, 0), file.GetInt32(row, 1), file.GetInt32(row, 2), file.GetString(row, 3), file.GetString(row, 4),
                file.GetString(row, 13), file.GetUInt32(row, 23), file.GetInt32(row, 24), file.GetString(row, 35),
                [.. Enumerable.Range(36, 3).Select(field => file.GetUInt32(row, field)).Where(v => v != 0)]));
        }

        return new DbcTable<WorldStateUIEntry>(FileName, rows, r => r.Id);
    }
}

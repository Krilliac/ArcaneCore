namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// One <c>spell_cast_times</c> row: SpellCastTimes.dbc (cmangos-classic DBCStructure.h
/// SpellCastTimesEntry, format "niii"). Column names follow the DBC field names.
/// </summary>
public sealed class SpellCastTimeRow
{
    public uint Id { get; set; }

    public int CastTime { get; set; }

    public int CastTimePerLevel { get; set; }

    public int MinCastTime { get; set; }
}

/// <summary>
/// One <c>spell_duration</c> row: SpellDuration.dbc (cmangos-classic SpellDurationEntry, format
/// "niii": m_duration, m_durationPerLevel, m_maxDuration; -1 means permanent).
/// </summary>
public sealed class SpellDurationRow
{
    public uint Id { get; set; }

    public int Duration { get; set; }

    public int DurationPerLevel { get; set; }

    public int MaxDuration { get; set; }
}

/// <summary>
/// One <c>spell_range</c> row: SpellRange.dbc (cmangos-classic SpellRangeEntry, format
/// "nffi" + localized names, which are not kept).
/// </summary>
public sealed class SpellRangeRow
{
    public uint Id { get; set; }

    public float MinRange { get; set; }

    public float MaxRange { get; set; }

    public uint Flags { get; set; }
}

/// <summary>
/// One <c>spell_radius</c> row: SpellRadius.dbc (m_radius, m_radiusPerLevel, m_radiusMax; cmangos
/// reads only m_radius, format "nfxx", but the importer keeps all three).
/// </summary>
public sealed class SpellRadiusRow
{
    public uint Id { get; set; }

    public float Radius { get; set; }

    public float RadiusPerLevel { get; set; }

    public float RadiusMax { get; set; }
}

/// <summary>
/// One <c>playercreateinfo_spell</c> row: a spell every new character of this race and class
/// knows (cmangos-classic table and column names; ObjectMgr::LoadPlayerInfo).
/// </summary>
public sealed class PlayerCreateSpellRow
{
    public byte Race { get; set; }

    public byte Class { get; set; }

    public uint Spell { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// One <c>spell_target_position</c> row: the fixed destination of a TARGET_LOCATION_DATABASE
/// teleport (cmangos-classic table and column names; SpellMgr::LoadSpellTargetPositions).
/// </summary>
public sealed class SpellTargetPositionRow
{
    public uint Id { get; set; }

    public uint TargetMap { get; set; }

    public float TargetPositionX { get; set; }

    public float TargetPositionY { get; set; }

    public float TargetPositionZ { get; set; }

    public float TargetOrientation { get; set; }
}

/// <summary>Everything the spell system loads from the world database at startup.</summary>
public sealed record SpellContent(
    IReadOnlyList<SpellTemplateRow> Spells,
    IReadOnlyList<SpellCastTimeRow> CastTimes,
    IReadOnlyList<SpellDurationRow> Durations,
    IReadOnlyList<SpellRangeRow> Ranges,
    IReadOnlyList<SpellRadiusRow> Radii,
    IReadOnlyList<PlayerCreateSpellRow> CreateSpells,
    IReadOnlyList<SpellTargetPositionRow> TargetPositions)
{
    public IReadOnlyList<ArcaneCore.Data.World.Creatures.SpellScriptTargetRow> ScriptTargets { get; init; } = [];
    public static SpellContent Empty { get; } = new([], [], [], [], [], [], []);
}

/// <summary>
/// The DBC-derived part of <see cref="SpellContent"/>: what <c>tools/spell-import</c> writes.
/// </summary>
public sealed record SpellDbcContent(
    IReadOnlyList<SpellTemplateRow> Spells,
    IReadOnlyList<SpellCastTimeRow> CastTimes,
    IReadOnlyList<SpellDurationRow> Durations,
    IReadOnlyList<SpellRangeRow> Ranges,
    IReadOnlyList<SpellRadiusRow> Radii);

/// <summary>Reads (and, for the importer, replaces) the spell tables of the world database.</summary>
public interface ISpellContentStore
{
    /// <summary>Every spell row and the tables it references.</summary>
    Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the DBC-derived tables (<c>spell_template</c>, <c>spell_cast_times</c>,
    /// <c>spell_duration</c>, <c>spell_range</c>, <c>spell_radius</c>) in one transaction.
    /// <c>playercreateinfo_spell</c> and <c>spell_target_position</c> are DB content and are
    /// left alone.
    /// </summary>
    Task ReplaceDbcTablesAsync(SpellDbcContent content, CancellationToken cancellationToken = default);
}

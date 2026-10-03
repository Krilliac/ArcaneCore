namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Which database layout a source table is in, detected per table from its columns. Dialect is
/// a property of the table, not of the dump: a mixed dump is legal, a table that matches two
/// dialects (or none) is not.
/// </summary>
public enum ContentDialect
{
    /// <summary>No rows were seen, so there was nothing to detect.</summary>
    Unknown = 0,

    /// <summary>cmangos classic-db z2815 (Full_DB 1.12.1 "Melting Pot v2"): e.g. <c>creature_template.ModelId1</c>.</summary>
    CMangosClassic = 1,

    /// <summary>cmangos mangos-classic HEAD: e.g. <c>creature_template.DisplayId1/DisplayIdProbability1</c>.</summary>
    CMangosHead = 2,

    /// <summary>vmangos world database: snake_case columns and patch-versioned rows.</summary>
    VMangos = 3,

    /// <summary>cmangos, with the table's columns identical in z2815 and HEAD (the exact revision cannot be told).</summary>
    CMangos = 4,
}

/// <summary>Dialect families, for comparing against what the operator expects.</summary>
public static class ContentDialects
{
    /// <summary>True for every cmangos layout.</summary>
    public static bool IsCMangos(this ContentDialect dialect)
        => dialect is ContentDialect.CMangosClassic or ContentDialect.CMangosHead or ContentDialect.CMangos;

    /// <summary>The family name used on the command line: <c>cmangos</c>, <c>vmangos</c> or <c>unknown</c>.</summary>
    public static string Family(this ContentDialect dialect)
        => dialect.IsCMangos() ? "cmangos" : dialect == ContentDialect.VMangos ? "vmangos" : "unknown";
}

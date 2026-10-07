namespace ArcaneCore.World.Names;

/// <summary>The client name filter files (section <c>Names</c>): the profanity and reserved-name patterns of character and pet names.</summary>
public sealed class NameCatalogOptions
{
    public const string SectionName = "Names";

    /// <summary>
    /// Build-5875 NamesProfanity.dbc: the patterns a character or pet name may not contain (vmangos ObjectMgr::IsValidCharacterName). Set
    /// together with <see cref="NamesReservedDbcPath"/> or not at all; unset leaves only the SQL reserved names.
    /// </summary>
    public string? NamesProfanityDbcPath { get; set; }

    /// <summary>Build-5875 NamesReserved.dbc: the reserved-name patterns; required with <see cref="NamesProfanityDbcPath"/>.</summary>
    public string? NamesReservedDbcPath { get; set; }
}

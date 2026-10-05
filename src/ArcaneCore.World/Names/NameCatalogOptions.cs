namespace ArcaneCore.World.Names;

public sealed class NameCatalogOptions
{
    public const string SectionName = "Names";
    public string? NamesProfanityDbcPath { get; set; }
    public string? NamesReservedDbcPath { get; set; }
}

namespace ArcaneCore.World.Creatures;

public sealed class CreatureDisplayModelOptions
{
    public const string SectionName = "Creatures";

    public string CreatureDisplayInfoDbcPath { get; set; } = string.Empty;
    public string CreatureModelDataDbcPath { get; set; } = string.Empty;
}

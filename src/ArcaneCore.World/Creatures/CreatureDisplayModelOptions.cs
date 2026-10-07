namespace ArcaneCore.World.Creatures;

/// <summary>
/// The client display model files (section <c>Creatures</c>) that give creature and transform displays their native scale and model height
/// (vmangos <c>Unit::UpdateModelData</c>). Both paths are set together or not at all; a partial configuration or a malformed file refuses
/// startup.
/// </summary>
public sealed class CreatureDisplayModelOptions
{
    public const string SectionName = "Creatures";

    /// <summary>Build-5875 CreatureDisplayInfo.dbc (display scale and model id). Empty = no display model data: the default geometry.</summary>
    public string CreatureDisplayInfoDbcPath { get; set; } = string.Empty;

    /// <summary>Build-5875 CreatureModelData.dbc (model scale and collision height), required with <see cref="CreatureDisplayInfoDbcPath"/>.</summary>
    public string CreatureModelDataDbcPath { get; set; } = string.Empty;
}

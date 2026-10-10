namespace ArcaneCore.Game.Conditions;

/// <summary>Live condition facts derived from an instance script's saved encounter state.</summary>
public interface IInstanceConditionFacts
{
    /// <summary>A DungeonEncounter.dbc id completed on this instance, or null when this script does not own it.</summary>
    bool? HasCompletedEncounter(uint dbcEncounterId) => null;

    /// <summary>A signed map variable owned by this instance, or null when this script does not own it.</summary>
    int? MapVariable(uint id) => null;
}

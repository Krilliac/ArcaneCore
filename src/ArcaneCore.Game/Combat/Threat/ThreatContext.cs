namespace ArcaneCore.Game.Combat.Threat;

/// <summary>
/// What a threat contribution carries besides its amount (the flags of vmangos ThreatManager::addThreat,
/// Threat/ThreatManager.cpp:391-425). School, crit and spell scaling belong to the threat calculation that runs before the
/// list is touched; the list only needs to know how to treat the result.
/// </summary>
/// <param name="IsAssist">Threat from a positive spell or a heal (vmangos isAssistThreat): zero while the owner is confused or fleeing.</param>
/// <param name="NoNewEntry">The source may raise an existing entry but never create one (vmangos SPELL_ATTR_EX_NO_THREAT, addThreatDirectly noNew).</param>
public readonly record struct ThreatContext(bool IsAssist = false, bool NoNewEntry = false)
{
    /// <summary>Plain threat: not an assist, may create an entry.</summary>
    public static ThreatContext Default => default;
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos Unit::GetTeam (Objects/Unit.cpp:4960-4973): a unit's team is its faction template's Faction.dbc row's team field
/// (DBCStructure.h FactionEntry::team, m_parentFactionID): 469 Alliance, 67 Horde, anything else no team.
/// </summary>
public static class FactionTeams
{
    /// <summary>Faction.dbc 469, Alliance (vmangos Team ALLIANCE).</summary>
    public const uint AllianceFaction = 469;

    /// <summary>Faction.dbc 67, Horde (vmangos Team HORDE).</summary>
    public const uint HordeFaction = 67;

    /// <summary>The team of <paramref name="factionTemplate"/>; null when either row is missing or the team field is neither side.</summary>
    public static Team? Of(uint factionTemplate, FactionTemplateCatalog templates, FactionCatalog factions)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(factions);
        if (templates.Find(factionTemplate) is not { } template || factions.Find(template.Faction) is not { } faction)
        {
            return null;
        }

        return faction.ParentFactionId switch
        {
            AllianceFaction => Team.Alliance,
            HordeFaction => Team.Horde,
            _ => null,
        };
    }
}

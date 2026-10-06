using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// NPC service settings (configuration section "NpcServices"). Every table is optional: without
/// it the dependent service fails closed (no repair prices → nothing repaired; no bank slot
/// prices → no bank slot sold; no TaxiPathNode → straight flights between nodes; no
/// SkillLineAbility → no rank prerequisites and every spell fits every class/race).
/// </summary>
public sealed class NpcServiceOptions
{
    public const string SectionName = "NpcServices";

    /// <summary>Build-5875 TaxiPathNode.dbc (flight waypoints).</summary>
    public string? TaxiPathNodeDbcPath { get; set; }

    /// <summary>Build-5875 SkillLineAbility.dbc (trainer rank prerequisites, race/class fit).</summary>
    public string? SkillLineAbilityDbcPath { get; set; }

    /// <summary>Build-5875 DurabilityCosts.dbc (repair multipliers by item level).</summary>
    public string? DurabilityCostsDbcPath { get; set; }

    /// <summary>Build-5875 DurabilityQuality.dbc (repair quality factors).</summary>
    public string? DurabilityQualityDbcPath { get; set; }

    /// <summary>Build-5875 BankBagSlotPrices.dbc.</summary>
    public string? BankBagSlotPricesDbcPath { get; set; }

    /// <summary>
    /// creature_template gossip_menu_id and trainer_* per entry. Imported metadata is loaded first
    /// when an INpcTemplateServiceMetadataSource is registered; these explicit rows deliberately
    /// replace the complete source row for the same entry. A trainer without a row has trainer type
    /// Class and trainer class 0 and refuses everyone.
    /// </summary>
    public List<NpcTemplateMetadata> NpcTemplates { get; set; } = [];
}

/// <summary>The creature_template columns the NPC services need that the creature import lacks.</summary>
public sealed class NpcTemplateMetadata
{
    public uint Entry { get; set; }

    public uint GossipMenuId { get; set; }

    public TrainerType TrainerType { get; set; }

    public byte TrainerClass { get; set; }

    public byte TrainerRace { get; set; }

    public uint TrainerSpell { get; set; }
}

/// <summary>Adds the configured <see cref="NpcTemplateMetadata"/> to another lookup's answers.</summary>
public sealed class NpcTemplateMetadataLookup : ICreatureLookup
{
    private readonly ICreatureLookup _inner;
    private readonly Dictionary<uint, NpcTemplateMetadata> _byEntry;

    public NpcTemplateMetadataLookup(ICreatureLookup inner, IEnumerable<NpcTemplateMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(metadata);
        _inner = inner;
        _byEntry = [];
        foreach (NpcTemplateMetadata row in metadata)
        {
            _byEntry[row.Entry] = row;
        }
    }

    public ICreatureLookup Inner => _inner;

    public NpcInfo? Find(Player player, ObjectGuid guid)
    {
        NpcInfo? npc = _inner.Find(player, guid);
        if (npc is null || npc.IsGameObject || !_byEntry.TryGetValue(npc.Entry, out NpcTemplateMetadata? meta))
        {
            return npc;
        }

        return npc with
        {
            GossipMenuId = meta.GossipMenuId,
            TrainerType = meta.TrainerType,
            TrainerClass = meta.TrainerClass,
            TrainerRace = meta.TrainerRace,
            TrainerSpell = meta.TrainerSpell,
        };
    }
}

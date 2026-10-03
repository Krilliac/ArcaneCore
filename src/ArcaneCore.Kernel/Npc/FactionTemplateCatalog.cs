namespace ArcaneCore.Kernel.Npc;

/// <summary>
/// Build-5875 FactionTemplate.dbc fields, in file order. Verified against vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0, Database/DBCStructure.h FactionTemplateEntry.
/// Scalar records keep an injected catalog immutable even when its caller changes source collections.
/// </summary>
public sealed record FactionTemplateRecord(
    uint Id, uint Faction, uint Flags, uint OwnMask, uint FriendlyMask, uint HostileMask,
    uint Enemy1 = 0, uint Enemy2 = 0, uint Enemy3 = 0, uint Enemy4 = 0,
    uint Friend1 = 0, uint Friend2 = 0, uint Friend3 = 0, uint Friend4 = 0)
{
    /// <summary>DBCStructure.h IsHostileTo: explicit enemies precede friends, then the hostile mask.</summary>
    public bool IsHostileTo(FactionTemplateRecord target)
    {
        if (target.Faction != 0)
        {
            if (target.Faction == Enemy1 || target.Faction == Enemy2 || target.Faction == Enemy3 || target.Faction == Enemy4)
            {
                return true;
            }

            if (target.Faction == Friend1 || target.Faction == Friend2 || target.Faction == Friend3 || target.Faction == Friend4)
            {
                return false;
            }
        }

        return (HostileMask & target.OwnMask) != 0;
    }

    /// <summary>DBCStructure.h IsFriendlyTo: explicit enemies deny, friends allow, then either friendly mask.</summary>
    public bool IsFriendlyTo(FactionTemplateRecord target)
    {
        if (target.Faction != 0)
        {
            if (target.Faction == Enemy1 || target.Faction == Enemy2 || target.Faction == Enemy3 || target.Faction == Enemy4)
            {
                return false;
            }

            if (target.Faction == Friend1 || target.Faction == Friend2 || target.Faction == Friend3 || target.Faction == Friend4)
            {
                return true;
            }
        }

        return (FriendlyMask & target.OwnMask) != 0 || (OwnMask & target.FriendlyMask) != 0;
    }

    /// <summary>DBCEnums.h FACTION_TEMPLATE_FLAG_ATTACK_PVP_ACTIVE_PLAYERS (vmangos IsContestedGuardFaction).</summary>
    public bool IsContestedGuard => (Flags & FactionTemplateCatalog.ContestedGuardFlag) != 0;
}

/// <summary>Startup faction templates; unknown or state-dependent NPC reactions fail closed.</summary>
public sealed class FactionTemplateCatalog
{
    /// <summary>DBCEnums.h FACTION_TEMPLATE_FLAG_ATTACK_PVP_ACTIVE_PLAYERS; requires contested-PvP state.</summary>
    public const uint ContestedGuardFlag = 0x1000;
    private readonly Dictionary<uint, FactionTemplateRecord> _templates;

    public static FactionTemplateCatalog Empty { get; } = new([]);

    public FactionTemplateCatalog(IEnumerable<FactionTemplateRecord> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = templates.ToDictionary(row => row.Id);
        if (_templates.ContainsKey(0))
        {
            throw new ArgumentException("faction-template id zero is not a resolvable faction", nameof(templates));
        }
    }

    public int Count => _templates.Count;

    public FactionTemplateRecord? Find(uint id) => _templates.GetValueOrDefault(id);

    /// <summary>
    /// Object.cpp GetReactionTo evaluates reputation and contested PvP before template relations.
    /// Without those adapters, only NPCs with faction id zero and no contested-guard bit can resolve.
    /// A nonzero faction may be reputation-free, but proving that needs Faction.dbc and player state.
    /// </summary>
    public bool TryNpcHostility(uint npcTemplate, uint playerTemplate, out bool hostile)
    {
        hostile = true;
        if (Find(npcTemplate) is not { } npc || Find(playerTemplate) is not { } player
            || npc.Faction != 0 || npc.IsContestedGuard)
        {
            return false;
        }

        hostile = npc.IsHostileTo(player);
        return true;
    }
}

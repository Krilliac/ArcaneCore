namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>creature_linking: a spawn follows its master spawn when flag 0x200 is set.</summary>
public sealed record CreatureLink(uint SlaveGuid, uint MasterGuid, uint Flags);

/// <summary>creature_linking_template: an entry follows a master entry on one map within its search range.</summary>
public sealed record CreatureTemplateLink(uint SlaveEntry, uint MapId, uint MasterEntry, uint Flags, float SearchRange);

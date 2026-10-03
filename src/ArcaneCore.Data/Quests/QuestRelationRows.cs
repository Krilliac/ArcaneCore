namespace ArcaneCore.Data.Quests;

/// <summary><c>creature_questrelation</c> row (id = creature entry that starts <see cref="Quest"/>).</summary>
public sealed class CreatureQuestStarterRow
{
    public uint Id { get; set; }

    public uint Quest { get; set; }
}

/// <summary><c>creature_involvedrelation</c> row (id = creature entry that ends <see cref="Quest"/>).</summary>
public sealed class CreatureQuestEnderRow
{
    public uint Id { get; set; }

    public uint Quest { get; set; }
}

/// <summary><c>character_queststatus</c> row (vmangos character_queststatus; guid = character id).</summary>
public sealed class CharacterQuestStatusRow
{
    public int CharacterId { get; set; }

    public uint Quest { get; set; }

    public byte Status { get; set; }

    public bool Rewarded { get; set; }

    public bool Explored { get; set; }

    public long Timer { get; set; }

    public uint MobCount1 { get; set; }

    public uint MobCount2 { get; set; }

    public uint MobCount3 { get; set; }

    public uint MobCount4 { get; set; }

    public uint ItemCount1 { get; set; }

    public uint ItemCount2 { get; set; }

    public uint ItemCount3 { get; set; }

    public uint ItemCount4 { get; set; }

    public uint RewardChoice { get; set; }
}

/// <summary>
/// <c>character_taxi</c> row: the known flight-path mask (vmangos keeps it as the
/// space-separated <c>characters.taximask</c> string; a side table keeps this feature off the
/// shared characters row).
/// </summary>
public sealed class CharacterTaxiRow
{
    public int CharacterId { get; set; }

    public uint Mask0 { get; set; }

    public uint Mask1 { get; set; }

    public uint Mask2 { get; set; }

    public uint Mask3 { get; set; }

    public uint Mask4 { get; set; }

    public uint Mask5 { get; set; }

    public uint Mask6 { get; set; }

    public uint Mask7 { get; set; }
}

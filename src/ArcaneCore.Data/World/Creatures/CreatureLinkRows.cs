namespace ArcaneCore.Data.World.Creatures;

public sealed class CreatureLinkRow
{
    public uint SlaveGuid { get; set; }
    public uint MasterGuid { get; set; }
    public uint Flags { get; set; }
}

public sealed class CreatureTemplateLinkRow
{
    public uint SlaveEntry { get; set; }
    public uint MapId { get; set; }
    public uint MasterEntry { get; set; }
    public uint Flags { get; set; }
    public float SearchRange { get; set; }
}

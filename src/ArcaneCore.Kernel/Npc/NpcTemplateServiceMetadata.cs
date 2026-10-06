namespace ArcaneCore.Kernel.Npc;

/// <summary>Direct creature-template service metadata required by NPC interaction adapters.</summary>
public sealed class NpcTemplateServiceMetadata
{
    public uint Entry { get; init; }
    public uint GossipMenuId { get; init; }
    public uint TrainerType { get; init; }
    public byte TrainerClass { get; init; }
    public byte TrainerRace { get; init; }
    public uint TrainerSpell { get; init; }
}

/// <summary>Reads imported direct NPC service metadata from the world database.</summary>
public interface INpcTemplateServiceMetadataSource
{
    Task<IReadOnlyList<NpcTemplateServiceMetadata>> LoadAsync(CancellationToken cancellationToken = default);
}

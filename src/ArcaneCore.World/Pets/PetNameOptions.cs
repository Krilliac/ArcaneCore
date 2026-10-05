namespace ArcaneCore.World.Pets;

/// <summary>vmangos CheckPetName configuration (World.cpp:622-626; 1.12 defaults).</summary>
public sealed class PetNameOptions
{
    public const string SectionName = "Pets:Names";
    public int MinPetName { get; set; } = 2;
    public uint StrictPetNames { get; set; }
    public int RealmZone { get; set; } = 1;
    public int EffectiveMinPetName => Math.Clamp(MinPetName, 2, 12);
    public int EffectiveMaxPetName => 12;
}

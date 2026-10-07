namespace ArcaneCore.World.Pets;

/// <summary>vmangos CheckPetName configuration (World.cpp:622-626; 1.12 defaults).</summary>
public sealed class PetNameOptions
{
    public const string SectionName = "Pets:Names";

    /// <summary>Shortest pet name accepted (vmangos MinPetName, clamped to 2..12; the longest is always 12).</summary>
    public int MinPetName { get; set; } = 2;

    /// <summary>vmangos StrictPetNames: the alphabet rule of pet names as a mask (0 = any letters of the realm's language type).</summary>
    public uint StrictPetNames { get; set; }

    /// <summary>vmangos RealmZone (RealmZone.h): picks the language type the strict name rules check; 1 = development.</summary>
    public int RealmZone { get; set; } = 1;
    public int EffectiveMinPetName => Math.Clamp(MinPetName, 2, 12);
    public int EffectiveMaxPetName => 12;
}

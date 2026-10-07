using ArcaneCore.World.Characters;
using ArcaneCore.World.Characters.Creation;

namespace ArcaneCore.World.Pets;

/// <summary>Pet-name validation seam. External profanity/reserved catalogs are intentionally injectable and not bundled in this slice.</summary>
public sealed class PetNameRules
{
    public Func<string, bool>? ExternalVeto { get; set; }

    public int MinLength { get; set; } = 2;
    public int MaxLength { get; set; } = 12;
    public uint StrictMask { get; set; }
    public int RealmZone { get; set; } = 1;

    public void Apply(PetNameOptions options)
    {
        MinLength = options.EffectiveMinPetName;
        MaxLength = options.EffectiveMaxPetName;
        StrictMask = options.StrictPetNames;
        RealmZone = options.RealmZone;
    }

    public string? NormalizeAndValidate(string raw)
    {
        string normalized = raw;
        if (!CharacterNameRules.IsWellFormed(normalized)
            || CharacterNameRules.CodePointCount(normalized) < MinLength
            || CharacterNameRules.CodePointCount(normalized) > MaxLength
            || !CharacterNameRules.IsValidString(normalized, new NameRuleSettings(MinLength, StrictMask, RealmZone, false)))
        {
            return null;
        }

        return ExternalVeto?.Invoke(normalized) == false ? null : normalized;
    }
}

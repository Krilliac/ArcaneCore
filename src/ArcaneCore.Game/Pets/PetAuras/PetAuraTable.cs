namespace ArcaneCore.Game.Pets.PetAuras;

/// <summary>
/// One owner spell whose DUMMY effect or DUMMY aura gives the owner's pet an aura (vmangos <c>PetAura</c>, SpellMgr.h:140-187): the pet aura per pet
/// creature entry, entry 0 meaning any pet.
/// </summary>
/// <param name="Spell">The owner's spell (the talent rank or the castable spell).</param>
/// <param name="Auras">The aura cast on the pet, by pet creature entry (0 = every pet).</param>
public sealed record PetAuraEntry(uint Spell, IReadOnlyDictionary<uint, uint> Auras)
{
    /// <summary>vmangos <c>PetAura::GetAura(petEntry)</c>: the entry's aura, else the any-pet aura, else 0.</summary>
    public uint AuraFor(uint petEntry)
        => Auras.TryGetValue(petEntry, out uint aura) ? aura : Auras.GetValueOrDefault(0u);
}

/// <summary>
/// The vanilla <c>spell_pet_auras</c> rows (vmangos SpellMgr::LoadSpellPetAuras, SpellMgr.cpp:2222-2285; the build 5875 rows of classic-db z2815, which
/// are also azerothcore's vanilla ids): a code table, so no world table or importer is needed. The imp is 416, the felhunter 417, the voidwalker 1860, the
/// succubus 1863.
/// </summary>
public static class PetAuraTable
{
    public const uint Imp = 416;
    public const uint Felhunter = 417;
    public const uint Voidwalker = 1860;
    public const uint Succubus = 1863;

    /// <summary>Soul Link (warlock, castable, DUMMY effect at the caster's pet) → 25228 on the demon.</summary>
    public const uint SoulLink = 19028;

    /// <summary>Spirit Bond rank 1 and 2 (hunter talent, passive DUMMY aura) → 19579 / 24529 on the pet.</summary>
    public const uint SpiritBond1 = 19578;

    public const uint SpiritBond2 = 20895;

    /// <summary>Master Demonologist rank 1 (warlock talent, passive DUMMY aura); ranks 2 to 5 are 23822 to 23825.</summary>
    public const uint MasterDemonologist1 = 23785;

    /// <summary>Stalker's Ally (DUMMY aura at the caster's pet) → 28758.</summary>
    public const uint StalkersAlly = 28757;

    private static PetAuraEntry Demons(uint spell, uint imp, uint felhunter, uint voidwalker, uint succubus)
        => new(spell, new Dictionary<uint, uint> { [Imp] = imp, [Felhunter] = felhunter, [Voidwalker] = voidwalker, [Succubus] = succubus });

    private static PetAuraEntry AnyPet(uint spell, uint aura) => new(spell, new Dictionary<uint, uint> { [0] = aura });

    /// <summary>Every row, by owner spell.</summary>
    public static IReadOnlyDictionary<uint, PetAuraEntry> Entries { get; } = new[]
    {
        AnyPet(SoulLink, 25228),
        AnyPet(SpiritBond1, 19579),
        AnyPet(SpiritBond2, 24529),
        Demons(MasterDemonologist1, 23759, 23762, 23760, 23761),
        Demons(23822, 23826, 23837, 23841, 23833),
        Demons(23823, 23827, 23838, 23842, 23834),
        Demons(23824, 23828, 23839, 23843, 23835),
        Demons(23825, 23829, 23840, 23844, 23836),
        AnyPet(StalkersAlly, 28758),
    }.ToDictionary(e => e.Spell);

    /// <summary>vmangos <c>SpellMgr::GetPetAura(spellId)</c>.</summary>
    public static PetAuraEntry? Find(uint spellId) => Entries.GetValueOrDefault(spellId);
}

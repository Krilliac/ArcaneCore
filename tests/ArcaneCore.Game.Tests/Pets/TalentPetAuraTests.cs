using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.PetAuras;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Spells.Utility.Targets;
using ArcaneCore.Game.Talents;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The owner-to-pet talent auras (vmangos spell_pet_auras: SpellMgr::LoadSpellPetAuras, Unit::AddPetAura / RemovePetAura, Pet::CastPetAuras; the DUMMY aura
/// and DUMMY effect hooks, SpellAuras.cpp:2201-2208 and SpellEffects.cpp:1497-1502; Player::RemoveSpell, Player.cpp:3850-3852) and the talent pet hooks
/// (vmangos Player::ResetTalents → RemovePet, Player.cpp:4144-4146; mangos-classic HandleLearnTalentOpcode → CastOwnerTalentAuras). Spell shapes follow the
/// classic-db z2815 rows: Soul Link 19028 (DUMMY at TARGET_UNIT_CASTER_PET) → 25228 (two APPLY_AREA_AURA_PET effects, 3% damage done and 30% damage
/// split, 100 yards); Master Demonologist 23785 (passive DUMMY aura) → 23759 imp / 23762 felhunter / 23760 voidwalker / 23761 succubus (APPLY_AREA_AURA_PET);
/// Spirit Bond 19578 (passive DUMMY aura) → 19579 (APPLY_AREA_AURA_PET of OBS_MOD_HEALTH). Demons are CREATURE_TYPE_DEMON.
/// </summary>
public sealed class TalentPetAuraTests : IDisposable
{
    private const uint Imp = PetAuraTable.Imp;
    private const uint Voidwalker = PetAuraTable.Voidwalker;
    private const uint Wolf = 5002;
    private const uint SummonImp = 688;
    private const uint SummonVoidwalker = 697;
    private const uint SoulLink = PetAuraTable.SoulLink;
    private const uint SoulLinkAura = 25228;
    private const uint MasterDemonologist = PetAuraTable.MasterDemonologist1;
    private const uint ImpThreat = 23759;
    private const uint FelhunterResist = 23762;
    private const uint VoidwalkerMitigation = 23760;
    private const uint SuccubusDamage = 23761;
    private const uint SpiritBond = PetAuraTable.SpiritBond1;
    private const uint SpiritBondHeal = 19579;
    private const uint OtherHunterTalent = 990_901;

    private readonly SpellTestKit _spells;
    private readonly Map _map;
    private readonly CreatureMapSystem _creatures;
    private readonly SummonService _service;

    private static SpellInfo Instant(uint id, params SpellEffectInfo[] effects) => Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellInfo Passive(uint id) => Instant(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
    };

    private static SpellEffectInfo PetArea(AuraType aura, int value, int misc = 0) => Effect(SpellEffectName.ApplyAreaAuraPet, value, aura: aura, misc: misc) with { Radius = 100f };

    private static SpellInfo PetAura(uint id, params SpellEffectInfo[] effects) => Instant(id, effects) with { Duration = new SpellDuration(-1, 0, -1) };

    public TalentPetAuraTests()
    {
        _spells = new SpellTestKit(
            Instant(SummonImp, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Imp)),
            Instant(SummonVoidwalker, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)Voidwalker)),
            Instant(SoulLink, Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitCasterPet)) with { SpellVisual = 1 },
            PetAura(SoulLinkAura, PetArea(AuraType.ModDamagePercentDone, 3, misc: 127), PetArea(AuraType.SplitDamagePct, 30, misc: 127)),
            Passive(MasterDemonologist),
            PetAura(ImpThreat, PetArea(AuraType.ModThreat, -4, misc: 127)),
            PetAura(FelhunterResist, PetArea(AuraType.ModResistance, 1, misc: 126)),
            PetAura(VoidwalkerMitigation, PetArea(AuraType.ModDamagePercentTaken, -2, misc: 1)),
            PetAura(SuccubusDamage, PetArea(AuraType.ModDamagePercentDone, 2, misc: 127)),
            Passive(SpiritBond),
            Passive(OtherHunterTalent),
            PetAura(SpiritBondHeal, PetArea(AuraType.ObsModHealth, 1) with { Amplitude = 10_000 }));
        _map = _spells.World.GetMap(0);
        CreatureContent content = CreatureTestSupport.Content(
            [
                .. new[] { Imp, Voidwalker }.Select(entry => CreatureTestSupport.Template(entry, b =>
                {
                    b.Name = $"Demon {entry}";
                    b.Faction = 14;
                    b.MinLevel = 5;
                    b.MaxLevel = 5;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                }) with { CreatureType = 3 }),
                CreatureTestSupport.Template(Wolf, b =>
                {
                    b.Name = "Wolf";
                    b.Faction = 14;
                    b.MinLevel = 5;
                    b.MaxLevel = 5;
                    b.MinLevelHealth = 100;
                    b.MaxLevelHealth = 100;
                }),
            ],
            []);
        _creatures = new CreatureMapSystem(_map, content, random: new Random(1));
        _map.AddUpdater(_creatures);
        _service = new SummonService(systems: map => ReferenceEquals(map, _map) ? _creatures : null, random: new Random(3));
        _spells.System.Units = new MapObjectResolver();
        _service.Install(_spells.System);
        _service.InstallDemons(_spells.System);
        _spells.System.Summons = _service;
        PetTargets.Install(_spells.System);
        SpellScriptDispatcher.Install(_spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
    }

    public void Dispose() => _spells.Dispose();

    private Player Owner(Class playerClass, uint guid = 1)
    {
        (Player player, _) = _spells.AddPlayer(guid);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)playerClass);
        player.Level = 40;
        return player;
    }

    private SpellCastResult Cast(Unit caster, uint spell) => _spells.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

    private Creature Pet(Player owner) => Assert.IsType<Creature>(owner.GetPet());

    private bool Has(Unit unit, uint spell) => _spells.System.HasAura(unit, spell);

    private Creature HunterPet(Player hunter)
        => Assert.IsType<Creature>(_service.RestoreCurrentPet(hunter, new PersistentPetSnapshot((int)hunter.Guid.Low, 7701, Wolf, 5, 0, 100, 0, 0, 1, [], [])));

    // --- the auras ---------------------------------------------------------------------------------------------------

    [Fact]
    public void SoulLink_PutsItsAuraOnTheImp_AndOnTheWarlockFromIt_AndUnlearningItTakesItAway()
    {
        Player warlock = Owner(Class.Warlock);
        _spells.Spellbook.Teach(warlock, SoulLink);
        Assert.Equal(SpellCastResult.CastOk, Cast(warlock, SummonImp));
        Creature imp = Pet(warlock);

        Assert.Equal(SpellCastResult.CastOk, _spells.System.CastSpell(warlock, SoulLink, SpellCastTargets.ForUnit(imp.Guid), triggered: true));

        Assert.True(Has(imp, SoulLinkAura));
        Assert.True(Has(warlock, SoulLinkAura));                      // APPLY_AREA_AURA_PET: the owner gets it from the demon
        Assert.Contains(SoulLink, PetAuraService.For(_spells.System).GetPetAuras(warlock));

        Assert.True(_spells.System.RemoveSpell(warlock, SoulLink));    // the owner loses 19028

        Assert.False(Has(imp, SoulLinkAura));
        Assert.False(Has(warlock, SoulLinkAura));                     // the copy goes with its source
        Assert.Empty(PetAuraService.For(_spells.System).GetPetAuras(warlock));
    }

    [Fact]
    public void SoulLink_EndsWithAPetChange()
    {
        // vmangos PetAura::IsRemovedOnChangePet: Soul Link's DUMMY targets TARGET_UNIT_CASTER_PET, and a new summon runs CastPetAuras(false).
        Player warlock = Owner(Class.Warlock);
        Cast(warlock, SummonImp);
        _spells.System.CastSpell(warlock, SoulLink, SpellCastTargets.ForUnit(Pet(warlock).Guid), triggered: true);

        Assert.Equal(SpellCastResult.CastOk, Cast(warlock, SummonVoidwalker));

        Creature voidwalker = Pet(warlock);
        Assert.Equal(Voidwalker, voidwalker.Entry);
        Assert.False(Has(voidwalker, SoulLinkAura));
        _spells.Advance(100);                                          // the warlock's copy goes with the unsummoned imp's source
        Assert.False(Has(warlock, SoulLinkAura));
        Assert.DoesNotContain(SoulLink, PetAuraService.For(_spells.System).GetPetAuras(warlock));
    }

    [Fact]
    public void MasterDemonologist_IsOnTheDemonFromItsSummon_AndPicksTheVariantByEntry()
    {
        Player warlock = Owner(Class.Warlock);
        Assert.Equal(SpellCastResult.CastOk, Cast(warlock, MasterDemonologist)); // the talent's passive
        Assert.Contains(MasterDemonologist, PetAuraService.For(_spells.System).GetPetAuras(warlock));

        Assert.Equal(SpellCastResult.CastOk, Cast(warlock, SummonVoidwalker));
        Creature voidwalker = Pet(warlock);

        Assert.True(Has(voidwalker, VoidwalkerMitigation));
        Assert.False(Has(voidwalker, ImpThreat));
        Assert.False(Has(voidwalker, FelhunterResist));
        Assert.False(Has(voidwalker, SuccubusDamage));
        Assert.True(Has(warlock, VoidwalkerMitigation));

        Assert.Equal(SpellCastResult.CastOk, Cast(warlock, SummonImp));      // a different demon: its own variant
        Creature imp = Pet(warlock);
        Assert.True(Has(imp, ImpThreat));
        Assert.False(Has(imp, VoidwalkerMitigation));
        _spells.Advance(100);                                          // the voidwalker's copy on the warlock goes with the voidwalker
        Assert.True(Has(warlock, ImpThreat));
        Assert.False(Has(warlock, VoidwalkerMitigation));
    }

    [Fact]
    public void MasterDemonologist_GoesFromTheDemon_WhenTheTalentIsUnlearned()
    {
        Player warlock = Owner(Class.Warlock);
        _spells.Spellbook.Teach(warlock, MasterDemonologist);
        Cast(warlock, MasterDemonologist);
        Cast(warlock, SummonVoidwalker);
        Creature voidwalker = Pet(warlock);
        Assert.True(Has(voidwalker, VoidwalkerMitigation));

        Assert.True(_spells.System.RemoveSpell(warlock, MasterDemonologist)); // its DUMMY aura goes: RemovePetAura

        Assert.False(Has(voidwalker, VoidwalkerMitigation));
        Assert.False(Has(warlock, VoidwalkerMitigation));
    }

    [Fact]
    public void OnlyAPermanentPetTakesThem_AWarlocksNonDemonDoesNot()
    {
        Player warlock = Owner(Class.Warlock);
        Cast(warlock, SpiritBond);
        Creature wolf = HunterPetFor(warlock);

        PetAuraService.For(_spells.System).CastPetAuras(wolf, current: true);

        Assert.False(Has(wolf, SpiritBondHeal));
    }

    [Fact]
    public void SpiritBond_IsOnTheHuntersPetWhenItIsCalled()
    {
        Player hunter = Owner(Class.Hunter);
        Cast(hunter, SpiritBond);

        Creature wolf = HunterPet(hunter);

        Assert.True(Has(wolf, SpiritBondHeal));
        Assert.True(Has(hunter, SpiritBondHeal));
    }

    // --- the talent pet hooks ---------------------------------------------------------------------------------------

    private TalentService HunterTalents()
    {
        const uint hunterMask = 1u << ((int)Class.Hunter - 1);
        var catalog = new TalentCatalog(
            [new TalentTabRecord(1, hunterMask, 0)],
            [
                new TalentRecord(1, 1, 0, 0, [SpiritBond, 0, 0, 0, 0], 0, 0, 0),
                new TalentRecord(2, 1, 0, 1, [OtherHunterTalent, 0, 0, 0, 0], 0, 0, 0),
            ]);
        return new TalentService(catalog, _spells.System, new TalentOptions(), () => 1_800_000_000);
    }

    [Fact]
    public void ARespec_RemovesTheHuntersPet()
    {
        Player hunter = Owner(Class.Hunter);
        using TalentService talents = HunterTalents();
        talents.InitTalentForLevel(hunter);
        Assert.True(talents.LearnTalent(hunter, 1, 0));
        Creature wolf = HunterPet(hunter);
        using TalentPetHooks hooks = TalentPetHooks.Attach(talents, _service, _spells.System);

        Assert.True(talents.ResetTalents(hunter, noCost: true));

        Assert.True(hunter.PetGuid.IsEmpty);
        Assert.DoesNotContain(wolf, _creatures.Creatures);
    }

    [Fact]
    public void ALearnedTalent_RecastsTheOwnersTalentAurasOnThePet()
    {
        Player hunter = Owner(Class.Hunter);
        using TalentService talents = HunterTalents();
        talents.InitTalentForLevel(hunter);
        using TalentPetHooks hooks = TalentPetHooks.Attach(talents, _service, _spells.System);
        Creature wolf = HunterPet(hunter);
        Assert.True(talents.LearnTalent(hunter, 1, 0));        // Spirit Bond: its DUMMY aura puts the heal on the pet
        Assert.True(Has(wolf, SpiritBondHeal));
        _spells.System.RemoveAuras(wolf, SpiritBondHeal);       // the pet lost it (its death, a dispel)
        Assert.False(Has(wolf, SpiritBondHeal));

        Assert.True(talents.LearnTalent(hunter, 2, 0));        // any talent learned: TalentLearned

        Assert.True(Has(wolf, SpiritBondHeal));
    }

    /// <summary>A beast pet (Kind Pet) bound to a warlock, through the hunter pet path with the class switched for the call.</summary>
    private Creature HunterPetFor(Player warlock)
    {
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Creature wolf = HunterPet(warlock);
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        _spells.System.RemoveAuras(wolf, SpiritBondHeal);
        _spells.System.RemoveAuras(warlock, SpiritBondHeal);
        return wolf;
    }
}

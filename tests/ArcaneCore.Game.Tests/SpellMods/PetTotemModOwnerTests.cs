using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Mods;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;
using static ArcaneCore.Game.Tests.SpellMods.ModTestKit;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// A pet or a totem reads its owner player's modifiers (vmangos Unit::GetSpellModOwner, Unit.cpp:9008-9023; the pet's cooldown
/// goes through the owner at Creature.cpp:3294-3296). A creature that is neither, a pet whose owner is gone, and a switched-off
/// option get none.
/// </summary>
public sealed class PetTotemModOwnerTests
{
    private const uint PetCd = 947001;       // a pet spell with a 10 s cooldown, family mask 1
    private const uint OwnerCdMod = 947002;  // pct cooldown -10 on mask 1
    private const uint PetRangeBolt = 947003;

    private static PetTestKit Kit() => new(
    [
        InFamily(Spell(PetCd, Effect(SpellEffectName.Dummy, 0)) with { RecoveryTime = 10_000, StartRecoveryCategory = 0, StartRecoveryTime = 0 }),
        Pct(OwnerCdMod, SpellModOp.Cooldown, -10),
    ]);

    private static Creature SummonPet(PetTestKit kit, Player owner)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        return Assert.Single(kit.Creatures.Creatures);
    }

    [Fact]
    public void APetsSpellCooldown_UsesTheOwnersMod()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        kit.Spells.System.LearnSpell(owner, OwnerCdMod);

        kit.Cast(pet, PetCd);

        Assert.Equal(9000u, kit.Spells.System.GetState(pet.Guid)!.SpellCooldowns[PetCd] - kit.Spells.Now);
    }

    [Fact]
    public void WithoutTheMod_APetsCooldownIsTheSpellsOwn()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);

        kit.Cast(pet, PetCd);

        Assert.Equal(10_000u, kit.Spells.System.GetState(pet.Guid)!.SpellCooldowns[PetCd] - kit.Spells.Now);
    }

    [Fact]
    public void TheEngineAnswersAPetsAskThroughItsOwner()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        kit.Spells.System.LearnSpell(owner, OwnerCdMod);
        SpellInfo spell = kit.Spells.Store.Get(PetCd)!;

        Assert.Equal(90f, kit.Spells.System.SpellModifiers.Apply(pet, spell, SpellModOp.Cooldown, 100f));
        Assert.Equal(90f, kit.Spells.System.SpellModifiers.Apply(owner, spell, SpellModOp.Cooldown, 100f));
    }

    [Fact]
    public void ATotemUsesItsOwnersMods()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.FireTotemSpell));
        Creature totem = Assert.Single(kit.Creatures.Creatures);
        Assert.True(totem.IsTotem);
        kit.Spells.System.LearnSpell(owner, OwnerCdMod);

        Assert.Same(owner, kit.Spells.System.Mods.OwnerResolver.GetModOwner(totem));
        Assert.Equal(90f, kit.Spells.System.SpellModifiers.Apply(totem, kit.Spells.Store.Get(PetCd)!, SpellModOp.Cooldown, 100f));
    }

    [Fact]
    public void ACreatureThatIsNeitherPetNorTotem_GetsNone_EvenWithAnOwnerField()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.WildSpell));
        Creature wild = Assert.Single(kit.Creatures.Creatures);
        Assert.False(wild.IsPet);
        wild.SetOwnerGuid(owner.Guid);
        kit.Spells.System.LearnSpell(owner, OwnerCdMod);

        Assert.Null(kit.Spells.System.Mods.OwnerResolver.GetModOwner(wild));
        Assert.Equal(100f, kit.Spells.System.SpellModifiers.Apply(wild, kit.Spells.Store.Get(PetCd)!, SpellModOp.Cooldown, 100f));
    }

    [Fact]
    public void APetWhoseOwnerLeftTheMap_GetsNone_WithoutThrowing()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        kit.Spells.System.LearnSpell(owner, OwnerCdMod);
        kit.Spells.World.RemovePlayer(owner);
        kit.Spells.World.RunTick(0);

        Assert.Null(kit.Spells.System.Mods.OwnerResolver.GetModOwner(pet));
        Assert.Equal(100f, kit.Spells.System.SpellModifiers.Apply(pet, kit.Spells.Store.Get(PetCd)!, SpellModOp.Cooldown, 100f));
    }

    [Fact]
    public void AModOnTheOwner_RefreshesThePetsAffectedPassive()
    {
        const uint petPassive = 947010;
        const uint damageMod = 947011;
        using PetTestKit kit = new(
        [
            InFamily(Spell(petPassive, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
            {
                Attributes = SpellAttributes.Passive,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }),
            Pct(damageMod, SpellModOp.Damage, 10),
        ]);
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        kit.Spells.System.CastSpell(pet, petPassive, SpellCastTargets.ForSelf(), triggered: true);
        SpellAuraHolder before = Assert.Single(kit.Spells.System.GetAuras(pet), h => h.Spell.Id == petPassive);

        kit.Spells.System.LearnSpell(owner, damageMod);

        // Aura::ReapplyAffectedPassiveAuras (SpellAuras.cpp:1067-1073): the pet's passive the owner's mod affects is removed and cast again.
        SpellAuraHolder after = Assert.Single(kit.Spells.System.GetAuras(pet), h => h.Spell.Id == petPassive);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void AModOnTheOwner_DoesNotRefreshAGuardiansPassive()
    {
        const uint passive = 947010;
        const uint damageMod = 947011;
        using PetTestKit kit = new(
        [
            InFamily(Spell(passive, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
            {
                Attributes = SpellAttributes.Passive,
                Duration = new SpellDuration(-1, 0, -1),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            }),
            Pct(damageMod, SpellModOp.Damage, 10),
        ]);
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        Assert.Equal(SummonKind.Pet, pet.Summon!.Kind);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.GuardianSpell));
        Creature guardian = Assert.Single(kit.Creatures.Creatures, c => c.Guid != pet.Guid && c.Summon?.Kind == SummonKind.Guardian);
        kit.Spells.System.CastSpell(guardian, passive, SpellCastTargets.ForSelf(), triggered: true);
        kit.Spells.System.CastSpell(pet, passive, SpellCastTargets.ForSelf(), triggered: true);
        SpellAuraHolder guardianBefore = Assert.Single(kit.Spells.System.GetAuras(guardian), h => h.Spell.Id == passive);
        SpellAuraHolder petBefore = Assert.Single(kit.Spells.System.GetAuras(pet), h => h.Spell.Id == passive);

        kit.Spells.System.LearnSpell(owner, damageMod);

        // CONTROLLED_PET | CONTROLLED_TOTEMS only (SpellAuras.cpp:1075): the guardian keeps its holder, the pet gets a new one.
        Assert.Same(guardianBefore, Assert.Single(kit.Spells.System.GetAuras(guardian), h => h.Spell.Id == passive));
        Assert.NotSame(petBefore, Assert.Single(kit.Spells.System.GetAuras(pet), h => h.Spell.Id == passive));
    }

    private static PetTestKit PassiveKit(uint passive, uint damageMod) => new(
    [
        InFamily(Spell(passive, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.Dummy)) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        }),
        Pct(damageMod, SpellModOp.Damage, 10),
    ]);

    [Fact]
    public void AModOnTheOwner_RefreshesATotemsAffectedPassive()
    {
        const uint passive = 947020;
        const uint damageMod = 947021;
        using PetTestKit kit = PassiveKit(passive, damageMod);
        (Player owner, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.FireTotemSpell));
        Creature totem = Assert.Single(kit.Creatures.Creatures);
        Assert.True(totem.IsTotem);
        kit.Spells.System.CastSpell(totem, passive, SpellCastTargets.ForSelf(), triggered: true);
        SpellAuraHolder before = Assert.Single(kit.Spells.System.GetAuras(totem), h => h.Spell.Id == passive);

        kit.Spells.System.LearnSpell(owner, damageMod);

        // Aura::ReapplyAffectedPassiveAuras (SpellAuras.cpp:1074-1075) includes CONTROLLED_TOTEMS.
        SpellAuraHolder after = Assert.Single(kit.Spells.System.GetAuras(totem), h => h.Spell.Id == passive);
        Assert.NotSame(before, after);
    }

    [Fact]
    public void WithTheOwnerSwitchOff_NeitherATotemNorAPetIsRefreshed()
    {
        const uint passive = 947020;
        const uint damageMod = 947021;
        using PetTestKit kit = PassiveKit(passive, damageMod);
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.FireTotemSpell));
        Creature totem = Assert.Single(kit.Creatures.Creatures, c => c.IsTotem);
        kit.Spells.System.CastSpell(totem, passive, SpellCastTargets.ForSelf(), triggered: true);
        kit.Spells.System.CastSpell(pet, passive, SpellCastTargets.ForSelf(), triggered: true);
        SpellAuraHolder totemBefore = Assert.Single(kit.Spells.System.GetAuras(totem), h => h.Spell.Id == passive);
        SpellAuraHolder petBefore = Assert.Single(kit.Spells.System.GetAuras(pet), h => h.Spell.Id == passive);
        kit.Spells.System.Mods.Options.OwnerModsForPetsAndTotems = false;

        kit.Spells.System.LearnSpell(owner, damageMod);

        Assert.Same(totemBefore, Assert.Single(kit.Spells.System.GetAuras(totem), h => h.Spell.Id == passive));
        Assert.Same(petBefore, Assert.Single(kit.Spells.System.GetAuras(pet), h => h.Spell.Id == passive));
    }

    [Fact]
    public void APetsCast_ReadsButNeverSpendsTheOwnersCharges()
    {
        using PetTestKit kit = new(
        [
            InFamily(Spell(PetCd, Effect(SpellEffectName.Dummy, 0)) with { CastTime = new SpellCastTime(1000, 0, 0), StartRecoveryCategory = 0, StartRecoveryTime = 0 }),
            ModPassive(OwnerCdMod, AuraType.AddFlatModifier, SpellModOp.CastingTime, -500, procCharges: 1),
        ]);
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        kit.Spells.System.LearnSpell(owner, OwnerCdMod);

        kit.Spells.System.CastSpell(pet, PetCd, SpellCastTargets.ForSelf(), triggered: false);

        SpellMod mod = Assert.Single(kit.Spells.System.Mods.ModsOf(owner, SpellModOp.CastingTime));
        Assert.Equal(1, mod.Charges);
        Assert.Equal(500, kit.Spells.System.GetState(pet.Guid)!.CurrentCast!.CastTime);   // read: 1000 - 500
    }

    [Fact]
    public void TheSwitch_TurnsTheOwnerLookupOff()
    {
        using PetTestKit kit = Kit();
        (Player owner, _) = kit.AddPlayer(1);
        Creature pet = SummonPet(kit, owner);
        kit.Spells.System.LearnSpell(owner, OwnerCdMod);
        kit.Spells.System.Mods.Options.OwnerModsForPetsAndTotems = false;

        Assert.Null(kit.Spells.System.Mods.OwnerResolver.GetModOwner(pet));
        Assert.Same(owner, kit.Spells.System.Mods.OwnerResolver.GetModOwner(owner));
    }
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Regression tests for the pet and charm audit findings: charm bar casts, level-up flags, hunter teach spells across a reload, the
/// Summon Pet charm check, demon experience, abandon and name query of a charmed unit.
/// </summary>
public sealed class PetCharmFixesTests
{
    private const uint BeastEntry = 5261;
    private const uint LearnBite = 925001;
    private const uint Bite = 925002;
    private const uint CallSpell = 925003;
    private const uint SummonImpSpell = 925004;
    private const uint SummonImpDismissFirst = 925005;

    private static PetTestKit Kit()
    {
        PetTestKit kit = NewKit();
        kit.Service.InstallDemons(kit.Spells.System);
        ArcaneCore.Game.Spells.Utility.Targets.PetTargets.Install(kit.Spells.System);
        return kit;
    }

    private static PetTestKit NewKit() => new(
        [
            .. CharmPossessTests.ControlSpells(),
            Spell(LearnBite, Effect(SpellEffectName.LearnPetSpell, 0, trigger: Bite)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(Bite, Effect(SpellEffectName.Dummy, 1)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0)),
            Spell(SummonImpSpell, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)ImpEntry))
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(SummonImpDismissFirst, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)ImpEntry))
                with { StartRecoveryCategory = 0, StartRecoveryTime = 0, AttributesEx = (SpellAttributesEx)0x00000001 },
        ],
        petContent: new PetContent([], [new PetCreateSpells(BeastEntry, [LearnBite])]),
        extraTemplates:
        [
            CreatureTestKit.Template(BeastEntry, b =>
            {
                b.MinLevel = 20;
                b.MaxLevel = 20;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
                b.Faction = 14;
            }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
        ]);

    private static (Player Hunter, Creature Pet) Tame(PetTestKit kit, byte ownerLevel = 30)
    {
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = ownerLevel;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    private static Creature Mob(PetTestKit kit, float x = 8, float y = 5)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, x, y, 83.5f, 0);

    private static byte[] PetAction(ObjectGuid pet, uint action, ActionType type, ObjectGuid target = default)
    {
        var w = new PacketWriter(20);
        w.WriteUInt64(pet.Value);
        w.WriteUInt32(ActionButton.Make(action, type).Packed);
        w.WriteUInt64(target.Value);
        return w.ToArray();
    }

    private static SpellCastResult CastAt(PetTestKit kit, Unit caster, uint spell, Unit target)
        => kit.Spells.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    // --- idx 5: a charmed (non-summoned) creature casts from its bar -----------------------------------------------

    [Fact]
    public void CharmedCreature_CastsAKnownBarSpell_NotNotKnown()
    {
        using PetTestKit kit = Kit();
        (Player player, FakeSession session) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        Creature enemy = Mob(kit, 6, 6);
        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, player, CharmPossessTests.CharmSpell, mob));
        Assert.Null(mob.Summon); // a charm, not a summon: its CharmInfo lives in the unit control state
        Assert.True(mob.GetCharmInfo()!.LearnSpell(PetBiteSpell, ActionType.Disabled));
        session.Clear();

        kit.Controller.HandleAction(player, PetPackets.ReadAction(PetAction(mob.Guid, PetBiteSpell, ActionType.Disabled, enemy.Guid)));

        Assert.Empty(Packets(session, WorldOpcode.SmsgPetCastFailed));
    }

    // --- idx 39: a level-up keeps the rename and abandon flags ----------------------------------------------------

    [Fact]
    public void HunterPetLevelUp_KeepsRenameAndAbandonFlags()
    {
        using PetTestKit kit = Kit();
        (_, Creature pet) = Tame(kit);
        const UnitFlags kept = UnitFlags.PetRename | UnitFlags.PetAbandon;
        Assert.Equal(kept, pet.UnitFlags & kept);

        Assert.Equal(1, kit.Service.GivePetXp(pet, SummonService.PetXpForLevel(pet.Level)));

        Assert.Equal(kept, pet.UnitFlags & kept);
    }

    // --- idx 41: teach spells survive a dismiss and recall --------------------------------------------------------

    [Fact]
    public async Task HunterPet_KeepsItsTeachSpells_WhenCalledBackFromTheCache()
    {
        using PetTestKit kit = Kit();
        (Player hunter, Creature pet) = Tame(kit);
        Assert.Contains(Bite, pet.Summon!.Charm!.TeachSpells.Keys);
        PersistentPetSnapshot snapshot = kit.Service.CaptureCurrentPet(hunter)!;
        kit.Service.Unsummon(pet);
        Assert.True(hunter.PetGuid.IsEmpty);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(snapshot);
        await kit.Service.ReadCurrentPetAsync(hunter);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(hunter, CallSpell));

        Creature recalled = Assert.Single(kit.Creatures.Creatures, c => c.Guid == hunter.PetGuid);
        Assert.Contains(Bite, recalled.Summon!.Charm!.TeachSpells.Keys);
    }

    // --- idx 95: Summon Pet with a charm out -----------------------------------------------------------------------

    [Fact]
    public void SummonPet_WithACharmOut_FailsAlreadyHaveCharm()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1, 5, 5);
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        Creature enslaved = Mob(kit);
        Assert.Equal(SpellCastResult.CastOk, CastAt(kit, warlock, CharmPossessTests.CharmSpell, enslaved));

        Assert.Equal(SpellCastResult.AlreadyHaveCharm, kit.Cast(warlock, SummonImpSpell));

        Assert.True(warlock.PetGuid.IsEmpty);
        Assert.Equal(enslaved.Guid, warlock.CharmGuid);
    }

    [Fact]
    public void SummonPet_WithDismissPetFirst_ReleasesTheCharmAndSummons()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1, 5, 5);
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        Creature enslaved = Mob(kit);
        CastAt(kit, warlock, CharmPossessTests.CharmSpell, enslaved);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(warlock, SummonImpDismissFirst));

        Assert.True(warlock.CharmGuid.IsEmpty);
        Assert.True(enslaved.CharmerGuid.IsEmpty);
        Assert.False(warlock.PetGuid.IsEmpty);
    }

    // --- idx 40: a warlock demon gains no pet experience -----------------------------------------------------------

    [Fact]
    public void WarlockDemon_GainsNoPetExperience()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        warlock.Level = 30;
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(warlock, SummonImpSpell));
        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.Guid == warlock.PetGuid);
        warlock.Level = 40;
        byte level = demon.Level;

        Assert.Equal(0, kit.Service.GivePetXp(demon, 1_000_000));

        Assert.Equal(level, demon.Level);
        Assert.Equal(0u, demon.GetUInt32(UpdateFields.UnitFieldPetexperience));
    }

    // --- idx 46: CMSG_PET_ABANDON on a charmed unit releases it ----------------------------------------------------

    [Fact]
    public void AbandoningACharmedUnit_ReleasesIt()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = Mob(kit);
        CastAt(kit, player, CharmPossessTests.CharmSpell, mob);
        Assert.Equal(mob.Guid, player.CharmGuid);

        kit.Controller.HandleAbandon(player, mob.Guid);

        Assert.True(player.CharmGuid.IsEmpty);
        Assert.True(mob.CharmerGuid.IsEmpty);
        Assert.True(mob.IsAlive);
        Assert.Same(mob, kit.Creatures.FindCreature(mob.Guid));
    }

    // --- idx 100: name query of an enslaved demon -----------------------------------------------------------------

    [Fact]
    public void NameQuery_OfAnEnslavedDemon_IsAnswered()
    {
        using PetTestKit kit = Kit();
        (Player warlock, FakeSession session) = kit.AddPlayer(1, 5, 5);
        warlock.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        Creature demon = kit.Creatures.SpawnTemporary(
            kit.Content.FindTemplate(NpcCasterEntry)! with { CreatureType = 3, UnitClass = 0 }, 8, 5, 83.5f, 0);
        CastAt(kit, warlock, CharmPossessTests.CharmSpell, demon);
        uint number = demon.GetUInt32(UpdateFields.UnitFieldPetnumber);
        Assert.NotEqual(0u, number);
        session.Clear();

        kit.Controller.HandleNameQuery(warlock, number, demon.Guid);

        var r = new PacketReader(Assert.Single(Packets(session, WorldOpcode.SmsgPetNameQueryResponse)));
        Assert.Equal(number, r.ReadUInt32());
        Assert.Equal(demon.Template.Name, r.ReadCString());
    }
}

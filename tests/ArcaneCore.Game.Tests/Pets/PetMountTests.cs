using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The owner's pet while the owner rides (vmangos Player::Mount and Player::Unmount, Player.cpp:18199-18216 and 18243-18251;
/// Pet::SetEnabled, Pet.cpp:2362-2377; Pet::LoadPetFromDB, Pet.cpp:399-404). With vmangos' default <c>PetUnsummonAtMount = 0</c> a
/// mount aura leaves the pet out and greys its bar out (SMSG_PET_MODE, enabled byte 0x8), dismounting brings the bar back, a dismounted
/// owner without a pet gets back the one that was put away, and a pet that comes back while its owner rides starts greyed out.
/// </summary>
public sealed class PetMountTests
{
    private const uint Horse = 960101;          // aura 78 for creature 2402, as the classic riding mounts list it
    private const uint HorseEntry = 2402;
    private const uint HorseDisplay = 14337;
    private const uint Gryphon = 3837;           // a taxi mount creature and its display, as NpcTravelServiceTests flies
    private const uint GryphonDisplay = 1147;

    private sealed class Displays : IMountDisplaySource
    {
        public uint? FindMountDisplay(uint creatureEntry) => creatureEntry == HorseEntry ? HorseDisplay : null;
    }

    private static PetTestKit Kit()
    {
        var kit = new PetTestKit(
        [
            Spell(Horse, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Mounted, misc: (int)HorseEntry)) with
            {
                Duration = new SpellDuration(-1, 0, -1),
            },
        ]);
        LocomotionEnvironment.RegisterMountDisplays(kit.Spells.World, new Displays());
        return kit;
    }

    private static (PetTestKit Kit, Player Owner, FakeSession Session, Creature Pet) Summoned()
    {
        PetTestKit kit = Kit();
        (Player owner, FakeSession session) = kit.AddPlayer(1, 5, 5);
        kit.Cast(owner, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        return (kit, owner, session, pet);
    }

    // wow_messages smsg_pet_mode.wowm: guid, react, command, 0, enabled byte (0x8 = disabled, vmangos Pet.cpp:2374)
    private static byte[] PetMode(Creature pet, byte enabled)
        => [.. BitConverter.GetBytes(pet.Guid.Value), (byte)ReactState.Defensive, (byte)CommandState.Follow, 0, enabled];

    private static WorldOpcode[] MountTraffic(FakeSession session)
        => [.. session.Sent.Select(p => p.Opcode).Where(o => o is WorldOpcode.SmsgPetMode or WorldOpcode.SmsgMountresult or WorldOpcode.SmsgDismountresult)];

    private static TaxiFlightSystem Flights() => new(
        new NpcStore(NpcContent.Empty with
        {
            TaxiNodes =
            [
                new TaxiNode { Id = 1, MapId = 0, X = 0, Y = 0, Z = 83.5f, MountAlliance = Gryphon, MountHorde = Gryphon },
                new TaxiNode { Id = 2, MapId = 0, X = 64, Y = 0, Z = 83.5f, MountAlliance = Gryphon, MountHorde = Gryphon },
            ],
            TaxiPaths = [new TaxiPath { Id = 10, FromNode = 1, ToNode = 2, Price = 100 }],
        }),
        new TaxiPathNodeCatalog([new TaxiPathNodeRecord(1, 10, 0, 0, 0, 0, 83.5f, 0, 0), new TaxiPathNodeRecord(2, 10, 1, 0, 64, 0, 83.5f, 0, 0)]),
        e => e == Gryphon ? GryphonDisplay : 0u, () => 0u);

    [Fact]
    public void Mounting_GreysThePetBarOut_AndDismountingBringsItBack()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            session.Clear();
            Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, Horse));

            Assert.True(MountService.IsMounted(owner));
            Assert.Equal(pet.Guid, owner.PetGuid); // PetUnsummonAtMount = 0: the pet stays out
            Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid));
            Assert.False(pet.Summon!.Charm!.Enabled);
            Assert.Equal(PetMode(pet, 0x8), Assert.Single(Packets(session, WorldOpcode.SmsgPetMode)));
            // Player::Mount: the pet (:18201-18216) before SendMountResult(MOUNTRESULT_OK) (:18229)
            Assert.Equal(new[] { WorldOpcode.SmsgPetMode, WorldOpcode.SmsgMountresult }, MountTraffic(session));

            session.Clear();
            kit.Spells.System.RemoveAuras(owner, Horse);

            Assert.False(MountService.IsMounted(owner));
            Assert.True(pet.Summon.Charm.Enabled);
            Assert.Equal(PetMode(pet, 0x0), Assert.Single(Packets(session, WorldOpcode.SmsgPetMode)));
            // Player::Unmount: pet->SetEnabled(true) (:18248-18249) before SendDismountResult(DISMOUNTRESULT_OK) (:18253)
            Assert.Equal(new[] { WorldOpcode.SmsgPetMode, WorldOpcode.SmsgDismountresult }, MountTraffic(session));
        }
    }

    [Fact]
    public void DismountingWithoutAPet_BringsBackThePetThatWasPutAway()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            uint petNumber = pet.Summon!.Charm!.PetNumber;
            kit.Cast(owner, Horse);
            Assert.True(kit.Service.UnsummonPetTemporarily(owner)); // e.g. a near teleport beyond the pet's reach that is not acknowledged yet
            Assert.True(owner.PetGuid.IsEmpty);

            kit.Spells.System.RemoveAuras(owner, Horse);

            // Player::Unmount: no pet, so ResummonPetTemporaryUnSummonedIfAny (:18250-18251)
            Assert.False(owner.PetGuid.IsEmpty);
            Creature back = Assert.IsType<Creature>(kit.Creatures.FindCreature(owner.PetGuid));
            Assert.NotSame(pet, back);
            Assert.Equal(petNumber, back.Summon!.Charm!.PetNumber);
            Assert.True(back.Summon.Charm.Enabled);
            Assert.False(kit.Service.HasTemporarilyUnsummonedPet(owner));
        }
    }

    [Fact]
    public void APetThatComesBackWhileTheOwnerRides_StartsWithItsBarGreyedOut()
    {
        (PetTestKit kit, Player owner, FakeSession session, _) = Summoned();
        using (kit)
        {
            kit.Cast(owner, Horse);
            Assert.True(kit.Service.UnsummonPetTemporarily(owner));
            session.Clear();

            // the teleport's arrival brings it back while the mount aura is still on (Pet::LoadPetFromDB, Pet.cpp:401-404)
            Creature back = Assert.IsType<Creature>(kit.Service.ResummonTemporarilyUnsummonedPet(owner));

            Assert.True(MountService.IsMounted(owner));
            Assert.False(back.Summon!.Charm!.Enabled);
            Assert.Equal(0x8, Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells))[15]); // set before PetSpellInitialize
            Assert.Empty(Packets(session, WorldOpcode.SmsgPetMode));
        }
    }

    [Fact]
    public void AHunterPetLoadedWhileTheOwnerRides_StartsWithItsBarGreyedOut()
    {
        using PetTestKit kit = Kit();
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(owner, Horse);
        session.Clear();

        Creature pet = Assert.IsType<Creature>(kit.Service.RestoreCurrentPet(owner, new PersistentPetSnapshot(1, 701, PetEntry, 5, 0, 50, 0, 0, 1, [], [])));

        Assert.False(pet.Summon!.Charm!.Enabled);
        Assert.Equal(0x8, Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells))[15]);

        session.Clear();
        kit.Spells.System.RemoveAuras(owner, Horse);
        Assert.True(pet.Summon.Charm.Enabled);
        Assert.Equal(0x0, Assert.Single(Packets(session, WorldOpcode.SmsgPetMode))[^1]);
    }

    [Fact]
    public void AHunterPetLoadedDuringAResumedFlight_IsNotGreyedOut_SoItKeepsItsBarAfterLanding()
    {
        using PetTestKit kit = Kit();
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        TaxiFlightSystem flights = Flights();
        // At login the saved route resumes (NpcServicesFeature) before the hunter pet is loaded (PersistentPetFeature): the
        // flight's mount display is already set, but it is not a mount for the pet (vmangos never loads a pet during a flight,
        // Pet.cpp:216-221, IsPetNeedBeTemporaryUnsummoned -> IsTaxiFlying, Player.cpp:20944-20945).
        Assert.True(flights.ResumeFlight(owner, new TaxiFlightRoute([1, 2], [10], [0]), Gryphon, (_, _) => true));
        Assert.True(MountService.IsMounted(owner));
        session.Clear();

        Creature pet = Assert.IsType<Creature>(kit.Service.RestoreCurrentPet(owner, new PersistentPetSnapshot(1, 701, PetEntry, 5, 0, 50, 0, 0, 1, [], [])));

        Assert.True(pet.Summon!.Charm!.Enabled);
        Assert.Equal(0x0, Assert.Single(Packets(session, WorldOpcode.SmsgPetSpells))[15]);

        // Landing clears the display without a dismount event (TaxiFlightSystem.Finish), so nothing would give a greyed-out bar back.
        flights.LandNow(owner);
        Assert.False(MountService.IsMounted(owner));
        Assert.True(pet.Summon.Charm.Enabled);
    }
}

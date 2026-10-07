using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class PetRenameTests
{
    [Fact]
    public void HunterPet_RenamesOnce_AndPersistsMutableNameState()
    {
        using PetTestKit kit = new();
        (Player owner, var session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.NotEqual(UnitFlags.None, pet.UnitFlags & UnitFlags.PetRename);
        kit.Controller.PetNameNormalizer = name => name is "Rex" or "NewName" ? name : null;
        uint number = pet.Summon!.Charm!.PetNumber;
        kit.Controller.HandleRename(owner, new PetRenameRequest(pet.Guid, "Rex"));
        Assert.Equal("Rex", pet.Summon.Charm.Name);
        Assert.False(pet.Summon.Charm.RenameAllowed);
        Assert.Equal(UnitFlags.None, pet.UnitFlags & UnitFlags.PetRename);
        PersistentPetSnapshot snapshot = kit.Service.CaptureCurrentPet(owner)!;
        Assert.Equal((number, "Rex"), (snapshot.PetNumber, snapshot.Name));
        session.Clear();
        kit.Controller.HandleRename(owner, new PetRenameRequest(pet.Guid, "NewName"));
        Assert.Equal("Rex", pet.Summon.Charm.Name);
        Assert.Empty(session.Sent);
    }

    [Fact]
    public void Rename_RejectsInvalidNames_OtherOwners_HeldAndTransitOwners()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        (Player stranger, _) = kit.AddPlayer(2, 4, 0);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        kit.Controller.PetNameNormalizer = name => name == "Rex" ? name : null;
        kit.Controller.HandleRename(owner, new PetRenameRequest(pet.Guid, "!bad"));
        Assert.Equal(pet.Template.Name, pet.Summon!.Charm!.Name);
        kit.Controller.HandleRename(stranger, new PetRenameRequest(pet.Guid, "Rex"));
        Assert.Equal(pet.Template.Name, pet.Summon.Charm.Name);
        kit.Spells.System.IsInTransit = _ => true;
        kit.Controller.HandleRename(owner, new PetRenameRequest(pet.Guid, "Rex"));
        kit.Spells.System.IsInTransit = _ => false;
        Assert.Equal(pet.Template.Name, pet.Summon.Charm.Name);
        Guid hold = Guid.NewGuid();
        Assert.True(owner.BeginQuestSettlement(hold));
        kit.Controller.HandleRename(owner, new PetRenameRequest(pet.Guid, "Rex"));
        Assert.True(owner.EndQuestSettlement(hold));
        Assert.Equal(pet.Template.Name, pet.Summon.Charm.Name);
    }

    [Fact]
    public void RenamePacket_ParsesGuidAndCString()
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(42);
        writer.WriteCString("Rex");
        PetRenameRequest request = PetPackets.ReadRename(writer.ToArray());
        Assert.Equal(42ul, request.Pet.Value);
        Assert.Equal("Rex", request.Name);
        Assert.Empty(PetPackets.BuildNameInvalid());
    }
}

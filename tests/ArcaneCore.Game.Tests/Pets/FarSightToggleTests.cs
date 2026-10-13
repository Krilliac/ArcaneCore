using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.CharmPossessTests;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// CMSG_FAR_SIGHT (vmangos HandleFarSightOpcode, MiscHandler.cpp:1138-1155): 0 puts the camera back on the player and 1 back on the
/// PLAYER_FARSIGHT object, which stays named; nothing happens without one.
/// </summary>
public sealed class FarSightToggleTests
{
    [Fact]
    public void FarSightOff_ReturnsTheCamera_On_GoesBack_AndANewViewStartsUnreset()
    {
        using var kit = new PetTestKit(ControlSpells());
        (Player priest, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 8, 5, 83.5f, 0);

        Assert.False(UnitControl.SetFarSightActive(priest, false)); // no far sight object

        kit.Spells.System.CastSpell(priest, PossessSpell, SpellCastTargets.ForUnit(mob.Guid), triggered: true);
        Assert.Same(mob, priest.ViewPoint);

        Assert.True(UnitControl.SetFarSightActive(priest, false));
        Assert.Same(priest, priest.ViewPoint);
        Assert.Equal(mob.Guid.Value, priest.GetUInt64(UpdateFields.PlayerFarsight));

        Assert.True(UnitControl.SetFarSightActive(priest, true));
        Assert.Same(mob, priest.ViewPoint);

        Assert.True(UnitControl.SetFarSightActive(priest, false));
        kit.Spells.System.RemoveAuras(mob, PossessSpell);
        Assert.Same(priest, priest.ViewPoint);
        kit.Spells.System.CastSpell(priest, PossessSpell, SpellCastTargets.ForUnit(mob.Guid), triggered: true);
        Assert.Same(mob, priest.ViewPoint);
    }
}

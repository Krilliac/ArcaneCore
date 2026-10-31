using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Tentacle Call 26391 (Vanquished Tentacle of C'Thun): the guardian summon runs the spell script's OnSummon (vmangos SpellEffects.cpp:2911-2912) and the
/// script puts the tentacle at stay where it spawned (scripts/spells/spell_item.cpp:390-401).
/// </summary>
public sealed class VanquishedTentacleTests
{
    private const uint OtherTentacleSpell = 971_301;

    private static PetTestKit Rig()
    {
        var kit = new PetTestKit(
        [
            Spell(VanquishedTentacleScript.TentacleCall, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)PetTestKit.GuardianEntry)),
            Spell(OtherTentacleSpell, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)PetTestKit.GuardianEntry)),
        ]);
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void TheTentacle_StaysWhereItSpawned_AndDoesNotFollowItsOwner()
    {
        using PetTestKit kit = Rig();
        (Player owner, _) = kit.AddPlayer(1, 5, 6);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, VanquishedTentacleScript.TentacleCall));

        Creature tentacle = Assert.Single(kit.Creatures.Creatures);
        CharmInfo charm = tentacle.Summon!.Charm!;
        Assert.Equal(CommandState.Stay, charm.CommandState);
        Assert.True(charm.IsAtStay);
        Assert.False(charm.IsCommandFollow);
        Assert.Equal((tentacle.X, tentacle.Y, tentacle.Z), charm.StayPosition);

        (float x, float y) = (tentacle.X, tentacle.Y);
        owner.Relocate(40f, 40f, owner.Z);
        kit.Run(3_000);

        Assert.Equal((x, y), (tentacle.X, tentacle.Y));
    }

    [Fact]
    public void AnotherGuardianSpell_StillFollows()
    {
        using PetTestKit kit = Rig();
        (Player owner, _) = kit.AddPlayer(1, 5, 6);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, OtherTentacleSpell));

        CharmInfo charm = Assert.Single(kit.Creatures.Creatures).Summon!.Charm!;
        Assert.Equal(CommandState.Follow, charm.CommandState);
        Assert.False(charm.IsAtStay);
    }
}

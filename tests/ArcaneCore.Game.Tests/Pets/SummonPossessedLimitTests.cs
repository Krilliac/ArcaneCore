using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// SPELL_EFFECT_SUMMON_POSSESSED limits (vmangos Spell::CheckCast Spell.cpp:6120-6123, Player::SummonPossessedMinion Player.cpp:17515-17580,
/// Player::UnsummonPossessedMinion): ALREADY_HAVE_CHARM, the creature summon limit, and the pet put away for the minion's life.
/// </summary>
public sealed class SummonPossessedLimitTests
{
    private const uint EyeSpell = 920_101;
    private const uint CharmSpell = CharmPossessTests.CharmSpell;

    private static PetTestKit Kit() => new(
    [
        .. CharmPossessTests.ControlSpells(),
        Spell(EyeSpell, Effect(SpellEffectName.SummonPossessed, 0, misc: (int)GuardianEntry)) with { Duration = new SpellDuration(5_000, 0, 5_000) },
    ]);

    private static Creature? Minion(PetTestKit kit) => kit.Creatures.Creatures.FirstOrDefault(c => c.Entry == GuardianEntry);

    [Fact]
    public void SummonPossessed_PossessesTheMinion_AndPutsThePetAwayUntilItGoes()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(player, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(pet.Guid, player.PetGuid);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, EyeSpell));
        Creature minion = Assert.IsType<Creature>(Minion(kit));
        Assert.Equal(minion.Guid, player.CharmGuid);
        Assert.True(player.PetGuid.IsEmpty);
        Assert.True(kit.Service.HasTemporarilyUnsummonedPet(player));
        Assert.Equal(1u, kit.Creatures.GetCreatureSummonCount(player.Guid));

        kit.Run(6_000);

        Assert.Null(Minion(kit));
        Assert.True(player.CharmGuid.IsEmpty);
        Assert.False(player.PetGuid.IsEmpty);
        Assert.False(kit.Service.HasTemporarilyUnsummonedPet(player));
        Assert.Equal(0u, kit.Creatures.GetCreatureSummonCount(player.Guid));
    }

    [Fact]
    public void SummonPossessed_AtTheSummonLimit_SummonsNothing_AndKeepsThePet()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(player, PetSpell);
        kit.Creatures.SetCreatureSummonLimit(player.Guid, 0);

        kit.Cast(player, EyeSpell);

        Assert.Null(Minion(kit));
        Assert.True(player.CharmGuid.IsEmpty);
        Assert.False(player.PetGuid.IsEmpty);
        Assert.False(kit.Service.HasTemporarilyUnsummonedPet(player));
    }

    [Fact]
    public void SummonPossessed_WhileCharming_FailsWithAlreadyHaveCharm()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1, 5, 5);
        Creature mob = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, 8, 5, 83.5f, 0);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(player, CharmSpell, SpellCastTargets.ForUnit(mob.Guid), triggered: true));

        Assert.Equal(SpellCastResult.AlreadyHaveCharm, kit.Cast(player, EyeSpell));
        Assert.Null(Minion(kit));
    }

    [Fact]
    public void SummonLimit_DefaultsToVmangosMaxCreatureSummonLimit()
    {
        using PetTestKit kit = Kit();
        Assert.Equal(100u, kit.Creatures.GetCreatureSummonLimit(ObjectGuid.Player(7)));
    }
}

using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// A warrior or rogue with a bow, gun, crossbow or thrown weapon shoots a creature it cannot reach instead of standing with nothing
/// to do (the shot comes after everything the class does today, so no existing step is displaced).
/// </summary>
public sealed class PlayerbotRangedWeaponRotationTests
{
    private static readonly PlayerbotWarriorRotation Warrior = new();
    private static readonly PlayerbotRogueRotation Rogue = new();

    private static SpellInfo[] Shots =>
    [
        Hostile("Shoot Bow", 0), Hostile("Shoot Gun", 0), Hostile("Shoot Crossbow", 0), Hostile("Throw", 0),
    ];

    private static void WithRangedWeapon(RotationStateBuilder s) => s.HasRangedWeapon = true;

    [Fact]
    public void AWarriorAtTwentyYardsWithABow_ShootsIt()
    {
        RotationState state = State(Warrior, Shots, Self(1), Victim(20f), PlayerbotRole.MeleeDps, more: WithRangedWeapon);
        Assert.Equal("Shoot Bow", Next(Warrior.InCombat(state)));
    }

    [Fact]
    public void TheShotsAreTriedInOrder_BowGunCrossbowThrow()
    {
        string[] order = ["Shoot Bow", "Shoot Gun", "Shoot Crossbow", "Throw"];
        for (int refused = 0; refused < order.Length; refused++)
        {
            RotationState state = State(Warrior, Shots, Self(1), Victim(20f), PlayerbotRole.MeleeDps, blocked: order.Take(refused), more: WithRangedWeapon);
            Assert.Equal(order[refused], Next(Warrior.InCombat(state)));
        }
    }

    [Fact]
    public void AWarriorInMelee_DoesNotShoot()
    {
        RotationState state = State(Warrior, [Hostile("Heroic Strike"), .. Shots], Self(1), Victim(3f), PlayerbotRole.MeleeDps, power: 0, more: WithRangedWeapon);
        Assert.Null(Next(Warrior.InCombat(state)));
    }

    [Fact]
    public void AMovingWarrior_DoesNotShoot()
    {
        RotationState state = State(Warrior, Shots, Self(1), Victim(20f), PlayerbotRole.MeleeDps, more: s => { WithRangedWeapon(s); s.IsMoving = true; });
        Assert.Null(Next(Warrior.InCombat(state)));
    }

    [Fact]
    public void WithoutARangedWeapon_NothingIsShot()
    {
        RotationState state = State(Warrior, Shots, Self(1), Victim(20f), PlayerbotRole.MeleeDps);
        Assert.Null(Next(Warrior.InCombat(state)));
    }

    [Fact]
    public void ACastThatIsRefused_LeavesTheNextShotToTry()
    {
        // A bow with no arrows: the cast check refuses Shoot Bow, and the thrown weapon of the next entry is not in the book.
        RotationState state = State(Warrior, [Hostile("Shoot Bow", 0)], Self(1), Victim(20f), PlayerbotRole.MeleeDps, blocked: ["Shoot Bow"], more: WithRangedWeapon);
        Assert.Null(Next(Warrior.InCombat(state)));
    }

    [Fact]
    public void AWarriorsOtherStepsStillComeFirst()
    {
        // Rage 0 withholds nothing here: Hamstring on a runner is a step of the class at range and wins over the shot.
        RotationState state = State(Warrior, [Hostile("Hamstring", 0), .. Shots], Self(1), Victim(6f, moving: true), PlayerbotRole.MeleeDps, more: WithRangedWeapon);
        Assert.Equal("Hamstring", Next(Warrior.InCombat(state)));
    }

    [Fact]
    public void ARogueAtTwentyYardsWithAThrownWeapon_Throws()
    {
        RotationState state = State(Rogue, [Hostile("Throw", 0)], Self(4), Victim(20f), PlayerbotRole.MeleeDps, more: WithRangedWeapon);
        Assert.Equal("Throw", Next(Rogue.InCombat(state)));
    }

    [Fact]
    public void ARogueInMeleeOrMoving_DoesNotShoot_AndOutOfCombatTheShotIsNotAnOpener()
    {
        Assert.Equal("Sinister Strike", Next(Rogue.InCombat(State(Rogue, [Hostile("Sinister Strike"), Hostile("Throw", 0)], Self(4), Victim(3f),
            PlayerbotRole.MeleeDps, more: WithRangedWeapon))));
        Assert.Null(Next(Rogue.InCombat(State(Rogue, [Hostile("Throw", 0)], Self(4), Victim(20f), PlayerbotRole.MeleeDps,
            more: s => { WithRangedWeapon(s); s.IsMoving = true; }))));
        Assert.Null(Next(Rogue.InCombat(State(Rogue, [Hostile("Throw", 0)], Self(4), Victim(20f), PlayerbotRole.MeleeDps, inCombat: false,
            more: WithRangedWeapon))));
    }

    [Fact]
    public void TheShotsAreAbilitiesOfBothClasses()
    {
        foreach (PlayerbotClassRotation rotation in new PlayerbotClassRotation[] { Warrior, Rogue })
            Assert.All(new[] { "Shoot Bow", "Shoot Gun", "Shoot Crossbow", "Throw" }, name => Assert.Contains(name, rotation.Abilities));
    }
}

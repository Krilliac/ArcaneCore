using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// Issue #433: every sampled stall on Young Goretusk (454) was a rogue standing on goal Grind. Stealth cast on a creature
/// already inside detection range is broken before the next think, and the upkeep then spends every think recasting it.
/// </summary>
public sealed class PlayerbotRoguePullTests
{
    private static readonly PlayerbotRogueRotation Rogue = new();

    private static SpellInfo[] Book =>
    [
        Aura("Stealth", 1, SpellImplicitTarget.UnitCaster),
        Hostile("Sinister Strike"),
        Hostile("Cheap Shot"),
        Hostile("Garrote"),
        Hostile("Eviscerate"),
    ];

    [Fact]
    public void Rogue_DoesNotRestealthOnTopOfItsPullTarget()
    {
        Assert.Equal("Stealth", Pull(false, 25f));
        Assert.Equal("Stealth", Pull(false, PlayerbotRogueRotation.StealthOpenYards + 1f));
        Assert.Null(Pull(false, PlayerbotRogueRotation.StealthOpenYards));
        Assert.Null(Pull(false, 3f));
        // Nearly dead: still vanish into stealth, even in melee (vmangos ShouldEnterStealth).
        Assert.Equal("Stealth", Pull(false, 3f, healthPercent: 5f));
    }

    [Fact]
    public void Rogue_StrikesWhenAStealthedOpenerCannotBeCast()
    {
        // Knows the openers, none of them can be cast (no dagger, no energy, not behind): Sinister Strike breaks stealth.
        Assert.Equal("Sinister Strike", Pull(true, 3f, blocked: ["Cheap Shot", "Garrote", "Ambush", "Premeditation"]));
        // An opener that can be cast still wins.
        Assert.Equal("Cheap Shot", Pull(true, 3f, powerType: PowerType.Rage));
    }

    private static string? Pull(bool stealthed, float distance, float healthPercent = 100f, string[]? blocked = null,
        PowerType powerType = PowerType.Mana)
    {
        RotationUnit victim = Victim(distance) with { PowerType = powerType };
        RotationAction? action = stealthed
            ? Rogue.InCombat(State(Rogue, Book, Self(4, healthPercent), victim, PlayerbotRole.MeleeDps,
                inCombat: false, blocked: blocked, more: s => s.IsStealthed = true))
            : Rogue.OutOfCombat(State(Rogue, Book, Self(4, healthPercent), victim, PlayerbotRole.MeleeDps,
                inCombat: false, blocked: blocked));
        return Next(action);
    }
}

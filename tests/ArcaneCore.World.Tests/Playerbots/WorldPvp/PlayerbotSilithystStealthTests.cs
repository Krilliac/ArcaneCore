using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.WorldPvp;

/// <summary>
/// A carrier of the Silithyst flag must not stealth: the server drops the dust when it does (<c>OutdoorPvPFeature.OnHolderAdded</c>). The out-of-combat
/// upkeep of the classes that stealth (rogue Stealth and Vanish, druid Prowl) holds back while the rotation state says the bot carries it.
/// </summary>
public sealed class PlayerbotSilithystStealthTests
{
    private static readonly PlayerbotRogueRotation Rogue = new();
    private static readonly PlayerbotDruidRotation Druid = new();

    private static SpellInfo[] RogueBook =>
    [
        Aura("Stealth", 1, SpellImplicitTarget.UnitCaster),
        Aura("Vanish", 1, SpellImplicitTarget.UnitCaster),
        Hostile("Sinister Strike"),
        Hostile("Cheap Shot"),
    ];

    private static SpellInfo[] DruidBook =>
    [
        Aura("Prowl", 1, SpellImplicitTarget.UnitCaster),
        Aura("Cat Form", 1, SpellImplicitTarget.UnitCaster),
        Hostile("Claw"),
    ];

    private static string? RogueUpkeep(bool carries, float distance = 25f, float healthPercent = 100f)
        => Next(Rogue.OutOfCombat(State(Rogue, RogueBook, Self(4, healthPercent), Victim(distance), PlayerbotRole.MeleeDps, inCombat: false,
            more: b => b.NoStealth = carries)));

    [Fact]
    public void ARogue_StealthsForAPull_ButNotWhileCarryingTheFlag()
    {
        Assert.Equal("Stealth", RogueUpkeep(carries: false)); // the baseline: without the flag the same state stealths
        Assert.Null(RogueUpkeep(carries: true));
    }

    [Fact]
    public void ANearlyDeadRogue_DoesNotStealthWhileCarryingTheFlag()
    {
        Assert.Equal("Stealth", RogueUpkeep(carries: false, distance: 3f, healthPercent: 5f));
        Assert.Null(RogueUpkeep(carries: true, distance: 3f, healthPercent: 5f));
    }

    [Fact]
    public void ACarryingRogueInACombat_DoesNotVanish()
    {
        RotationAction? Fight(bool carries) => Rogue.InCombat(State(Rogue, RogueBook, Self(4, 5f), Victim(3f), PlayerbotRole.MeleeDps, inCombat: true,
            more: b => b.NoStealth = carries));
        Assert.Equal("Vanish", Next(Fight(carries: false)));
        Assert.NotEqual("Vanish", Next(Fight(carries: true)));
    }

    [Fact]
    public void ACatDruid_ProwlsForAPull_ButNotWhileCarryingTheFlag()
    {
        string? Upkeep(bool carries) => Next(Druid.OutOfCombat(State(Druid, DruidBook, Self(11), Victim(25f), PlayerbotRole.MeleeDps, inCombat: false,
            more: b => { b.Form = ShapeshiftForm.Cat; b.NoStealth = carries; })));
        Assert.Equal("Prowl", Upkeep(carries: false));
        Assert.Null(Upkeep(carries: true));
    }
}

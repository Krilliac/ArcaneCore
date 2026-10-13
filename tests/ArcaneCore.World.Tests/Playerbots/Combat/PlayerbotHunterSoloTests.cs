using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// A level 6-8 hunter is not in combat until something lands, and Hunter's Mark is the only opener it used to return. The brain
/// treats that as a finished combat think, so Auto Shot (and the white swing) never run. Bot QA #398: those hunters stood on
/// Rockjaw Bonesnapper for 120s with attackerFacts=none.
/// </summary>
public sealed class PlayerbotHunterSoloTests
{
    private static readonly PlayerbotHunterRotation Hunter = new();

    // Mark, the level-6 shot, and the level-1 strike. No Aspect of the Hawk and no pet (both level 10).
    private static SpellInfo[] HunterBook =>
    [
        Hostile("Auto Shot", 0), Hostile("Arcane Shot"), Debuff("Serpent Sting"), Hostile("Raptor Strike"), Debuff("Hunter's Mark"),
    ];

    [Fact]
    public void HunterNotInCombat_ShootsOrStrikes_InsteadOfOnlyMarking()
    {
        // Standing at range, the target unmarked: used to be Hunter's Mark every think, so the mob never died.
        Assert.Equal("Auto Shot", Next(Hunter.InCombat(State(Hunter, HunterBook, Self(3), Victim(20f), PlayerbotRole.RangeDps,
            inCombat: false, more: s => s.HasRangedWeapon = true))));

        // Still walking: an instant shot, not a mark. A mark while moving keeps the think and Auto Shot never starts.
        Assert.Equal("Arcane Shot", Next(Hunter.InCombat(State(Hunter, HunterBook, Self(3), Victim(20f), PlayerbotRole.RangeDps,
            inCombat: false, more: s => { s.HasRangedWeapon = true; s.IsMoving = true; }))));

        // In the dead zone and not yet in combat: Raptor Strike, not another mark, so the swing is not swallowed.
        Assert.Equal("Raptor Strike", Next(Hunter.InCombat(State(Hunter, HunterBook, Self(3), Victim(3f), PlayerbotRole.RangeDps,
            inCombat: false, more: s => s.HasRangedWeapon = true))));
    }
}

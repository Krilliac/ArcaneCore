using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Cooldown, global cooldown and school lockout ends are stored as absolute values of the 32-bit game clock
/// (<c>WorldClock.NowMs</c>, which wraps about every 49.7 days), so every comparison has to survive the wrap.
/// </summary>
public sealed class CooldownClockWrapTests
{
    private const uint BeforeTheWrap = uint.MaxValue - 5_000;

    [Fact]
    public void ASpellCooldownStartedBeforeTheWrap_StillHoldsAfterIt_AndThenExpires()
    {
        using var kit = new SpellTestKit();
        kit.Now = BeforeTheWrap;
        (Player player, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(player, SpellTestKit.CooldownSpell);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellTestKit.CooldownSpell, SpellCastTargets.ForSelf(), triggered: false));

        kit.Advance(2_000); // still before the wrap; the 10 s cooldown (end value wrapped to about 5000) holds
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(player, SpellTestKit.CooldownSpell, SpellCastTargets.ForSelf(), triggered: false));

        kit.Advance(5_000); // now after the wrap, 7 s into the 10 s cooldown
        Assert.True(kit.Now < 10_000u);
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(player, SpellTestKit.CooldownSpell, SpellCastTargets.ForSelf(), triggered: false));

        kit.Advance(4_000);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellTestKit.CooldownSpell, SpellCastTargets.ForSelf(), triggered: false));
    }

    [Fact]
    public void AnEndStoredJustBeforeTheWrap_IsExpiredOnceTheClockHasWrapped()
    {
        using var kit = new SpellTestKit();
        kit.Now = BeforeTheWrap;
        (Player player, _) = kit.AddPlayer(1);
        UnitSpellState state = kit.System.StateOf(player);
        state.SpellCooldowns[SpellTestKit.CooldownSpell] = BeforeTheWrap + 1_000; // still before the wrap, ends a second from now

        kit.Advance(6_000);

        Assert.True(kit.Now < 10_000u);
        Assert.Empty(state.SpellCooldowns); // a plain "until <= now" keeps it for another 49 days
    }

    [Fact]
    public void ASchoolLockoutAcrossTheWrap_HoldsUntilItsEnd()
    {
        using var kit = new SpellTestKit();
        kit.Now = BeforeTheWrap;
        (Player player, _) = kit.AddPlayer(1);
        kit.System.StateOf(player).SchoolLockouts[SpellSchool.Fire] = unchecked(BeforeTheWrap + 8_000); // wraps

        Assert.True(kit.System.IsSchoolLocked(player, SpellSchool.Fire));
        kit.Advance(6_000);
        Assert.True(kit.System.IsSchoolLocked(player, SpellSchool.Fire));
        kit.Advance(3_000);
        Assert.False(kit.System.IsSchoolLocked(player, SpellSchool.Fire));
    }

    [Fact]
    public void CooldownsRestoredAndCapturedAroundTheWrap_KeepTheirRemainingTime()
    {
        using var kit = new SpellTestKit();
        kit.Now = BeforeTheWrap;
        (Player player, _) = kit.AddPlayer(1);
        const long nowUnixMs = 1_000_000_000_000;

        int restored = kit.System.RestoreCooldowns(
            player, [new PersistedCooldown(SpellCooldownKind.Spell, SpellTestKit.CooldownSpell, nowUnixMs + 20_000)], nowUnixMs);
        Assert.Equal(1, restored);

        kit.Advance(7_000); // the 20 s cooldown has run 7 s and the clock has wrapped
        Assert.True(kit.Now < 10_000u);
        PersistedCooldown saved = Assert.Single(kit.System.CaptureState(player, nowUnixMs).Cooldowns);
        Assert.Equal(nowUnixMs + 13_000, saved.EndsAtUnixMs);
    }
}

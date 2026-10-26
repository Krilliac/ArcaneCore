using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Spell cooldowns are stored as absolute ends on the uint millisecond clock, which wraps every 49.7 days (vmangos keeps 64-bit
/// time points, SpellCaster.h:56-60). A cooldown that spans the wrap must keep running and then expire on time.
/// </summary>
public sealed class SpellCooldownClockWrapTests : IDisposable
{
    private const uint LongSpell = 947001;
    private const uint GcdSpell = 947002;

    private readonly SpellTestKit _kit;
    private readonly Player _player;

    public SpellCooldownClockWrapTests()
    {
        _kit = new SpellTestKit(
            Spell(LongSpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster)) with { RecoveryTime = 60_000, StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(GcdSpell, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitCaster)) with { RecoveryTime = 0, StartRecoveryCategory = 133, StartRecoveryTime = 1500 });
        (_player, _) = _kit.AddPlayer(1);
        _kit.Spellbook.Teach(_player, LongSpell, GcdSpell);
    }

    public void Dispose() => _kit.Dispose();

    [Fact]
    public void ACooldownStartedJustBeforeTheClockWraps_KeepsRunningAcrossTheWrap_AndExpiresOnTime()
    {
        _kit.Now = uint.MaxValue - 1000;
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, LongSpell, SpellCastTargets.ForSelf(), triggered: false));
        SpellInfo spell = _kit.Store.Get(LongSpell)!;

        // RED on main: now + 60 s overflowed to a small end, so until > now is false and the spell is ready the moment it was cast.
        Assert.False(_kit.System.IsSpellReady(_player, spell));

        _kit.Advance(5_000); // the clock is now past the wrap
        Assert.True(_kit.Now < 10_000u, "the clock wrapped");
        Assert.False(_kit.System.IsSpellReady(_player, spell));

        _kit.Advance(50_000);
        Assert.False(_kit.System.IsSpellReady(_player, spell));
        _kit.Advance(5_000);
        Assert.True(_kit.System.IsSpellReady(_player, spell));
    }

    [Fact]
    public void AGlobalCooldownStartedBeforeTheWrap_ExpiresAfterTheWrap_InsteadOfLockingForWeeks()
    {
        _kit.Now = uint.MaxValue - 500;
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_player, GcdSpell, SpellCastTargets.ForSelf(), triggered: false));
        SpellInfo spell = _kit.Store.Get(GcdSpell)!;

        _kit.Advance(3_000);
        Assert.True(_kit.Now < 5_000u, "the clock wrapped");
        Assert.True(_kit.System.IsSpellReady(_player, spell)); // RED on main: the stored end is near uint.MaxValue so it stays > now
    }
}

using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Druid;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Swiftmend (vmangos scripts/spells/spell_druid.cpp:101-160). Build 5875 shapes (classic-db z2815): Swiftmend 18562 (HEAL 1 at TARGET_UNIT_FRIEND,
/// family 7), Rejuvenation 774 (PERIODIC_HEAL 8 every 3 s for 12 s, family flag 0x10), Regrowth 8936 (HEAL plus PERIODIC_HEAL 14 every 3 s for 21 s,
/// family flag 0x40). Nothing crits, so the heals are exact.
/// </summary>
public sealed class SwiftmendScriptTests : IDisposable
{
    private const uint Rejuvenation = 774;
    private const uint Regrowth = 8936;
    private const int RejuvenationTick = 8;
    private const int RegrowthTick = 14;

    private readonly SpellTestKit _kit;
    private readonly Player _druid;
    private readonly Player _friend;

    private static SpellInfo AtFriend(SpellInfo spell) => spell with
    {
        RangeIndex = 5, Range = new SpellRange(0, 40), SpellFamilyName = SwiftmendScript.DruidFamily, School = SpellSchool.Nature,
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    public SwiftmendScriptTests()
    {
        _kit = new SpellTestKit(
            AtFriend(Spell(SwiftmendScript.Swiftmend, Effect(SpellEffectName.Heal, 1, SpellImplicitTarget.UnitFriend))) with
            {
                SpellFamilyFlags = 0x200000000,
            },
            AtFriend(Spell(Rejuvenation, Effect(SpellEffectName.ApplyAura, RejuvenationTick, SpellImplicitTarget.UnitFriend, AuraType.PeriodicHeal, amplitude: 3000))) with
            {
                SpellFamilyFlags = SwiftmendScript.RejuvenationFlag,
                Duration = new SpellDuration(12_000, 0, 12_000),
            },
            AtFriend(Spell(Regrowth,
                Effect(SpellEffectName.Heal, 84, SpellImplicitTarget.UnitFriend),
                Effect(SpellEffectName.ApplyAura, RegrowthTick, SpellImplicitTarget.UnitFriend, AuraType.PeriodicHeal, amplitude: 3000))) with
            {
                SpellFamilyFlags = SwiftmendScript.RegrowthFlag,
                Duration = new SpellDuration(21_000, 0, 21_000),
            });
        (_druid, _) = _kit.AddPlayer(1);
        (_friend, _) = _kit.AddPlayer(2, 3, 0);
        _kit.System.CombatRules = new NoCritNoResistRules();
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        _friend.MaxHealth = 10_000;
        _friend.Health = 1_000;
    }

    public void Dispose() => _kit.Dispose();

    private SpellCastResult Cast(uint spell) => _kit.System.CastSpell(_druid, spell, SpellCastTargets.ForUnit(_friend.Guid), triggered: true);

    [Fact]
    public void Swiftmend_ConsumesRejuvenation_AndHealsForFourOfItsTicks()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(Rejuvenation));
        uint before = _friend.Health;

        Assert.Equal(SpellCastResult.CastOk, Cast(SwiftmendScript.Swiftmend));

        Assert.Equal(before + 1 + (4 * RejuvenationTick), _friend.Health); // "12 sec of Rejuvenation"
        Assert.False(_kit.System.HasAura(_friend, Rejuvenation));
    }

    [Fact]
    public void Swiftmend_ConsumesRegrowth_AndHealsForSixOfItsTicks()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(Regrowth));
        uint before = _friend.Health;

        Assert.Equal(SpellCastResult.CastOk, Cast(SwiftmendScript.Swiftmend));

        Assert.Equal(before + 1 + (6 * RegrowthTick), _friend.Health); // "18 sec of Regrowth"
        Assert.False(_kit.System.HasAura(_friend, Regrowth));
    }

    [Fact]
    public void Swiftmend_WithBoth_TakesTheOneWithTheShortestTimeLeft()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(Regrowth));      // 21 s
        Assert.Equal(SpellCastResult.CastOk, Cast(Rejuvenation));  // 12 s: shorter
        uint before = _friend.Health;

        Assert.Equal(SpellCastResult.CastOk, Cast(SwiftmendScript.Swiftmend));

        Assert.Equal(before + 1 + (4 * RejuvenationTick), _friend.Health);
        Assert.False(_kit.System.HasAura(_friend, Rejuvenation));
        Assert.True(_kit.System.HasAura(_friend, Regrowth));
    }

    [Fact]
    public void Swiftmend_WithBoth_TakesRegrowthWhenItRunsOutFirst()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(Regrowth));
        _kit.Advance(10_000);                                       // Regrowth has 11 s left
        Assert.Equal(SpellCastResult.CastOk, Cast(Rejuvenation));  // 12 s
        uint before = _friend.Health;

        Assert.Equal(SpellCastResult.CastOk, Cast(SwiftmendScript.Swiftmend));

        Assert.Equal(before + 1 + (6 * RegrowthTick), _friend.Health);
        Assert.False(_kit.System.HasAura(_friend, Regrowth));
        Assert.True(_kit.System.HasAura(_friend, Rejuvenation));
    }

    [Fact]
    public void Swiftmend_IsRefused_WithoutRejuvenationOrRegrowth()
    {
        uint before = _friend.Health;

        Assert.Equal(SpellCastResult.TargetAurastate, Cast(SwiftmendScript.Swiftmend));

        Assert.Equal(before, _friend.Health);
    }
}

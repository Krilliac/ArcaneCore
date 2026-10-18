using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Bonus;
using ArcaneCore.Game.Spells.Shaman;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Healing Way (vmangos Unit::SpellHealingBonusTaken, Unit.cpp:5347-5355): 29203's DUMMY amount on the target raises the healing Healing Wave
/// does to it, 6% a stack; other heals are unchanged. The ids, family, flags and amounts follow the build 5875 rows.
/// </summary>
public sealed class HealingWayTests : IDisposable
{
    private const uint HealingWave = 331;
    private const uint LesserHeal = 963_201;

    private readonly SpellTestKit _kit;
    private readonly Player _shaman;
    private readonly Player _friend;

    public HealingWayTests()
    {
        _kit = new SpellTestKit(
            Spell(HealingWave, Effect(SpellEffectName.Heal, 1000, SpellImplicitTarget.UnitFriend)) with
            {
                SpellFamilyName = HealingWayRules.ShamanFamily, SpellFamilyFlags = 1UL << HealingWayRules.HealingWaveBit,
                DamageClass = SpellDamageClass.Magic, RangeIndex = 4, Range = new SpellRange(0, 40), SpellVisual = 1,
                StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(LesserHeal, Effect(SpellEffectName.Heal, 1000, SpellImplicitTarget.UnitFriend)) with
            {
                SpellFamilyName = HealingWayRules.ShamanFamily,
                DamageClass = SpellDamageClass.Magic, RangeIndex = 4, Range = new SpellRange(0, 40), SpellVisual = 1,
                StartRecoveryCategory = 0, StartRecoveryTime = 0,
            },
            Spell(HealingWayRules.HealingWayAura, Effect(SpellEffectName.ApplyAura, 6, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with
            {
                SpellFamilyName = 10, SpellFamilyFlags = 0x20000000, StackAmount = 3, SpellVisual = 1,
                Duration = new SpellDuration(15_000, 0, 15_000),
            });
        (_shaman, _) = _kit.AddPlayer(1, 0, 0);
        (_friend, _) = _kit.AddPlayer(2, 3, 0);
        _kit.System.AmountModifier = new SpellBonusModule(_kit.System);
        _friend.MaxHealth = 100_000;
        _friend.Health = 1_000;
    }

    public void Dispose() => _kit.Dispose();

    private uint Heal(uint spell)
    {
        _friend.Health = 1_000;
        Assert.Equal(SpellCastResult.CastOk, _kit.System.CastSpell(_shaman, spell, SpellCastTargets.ForUnit(_friend.Guid), triggered: true));
        return _friend.Health - 1_000;
    }

    [Fact]
    public void ThreeStacks_RaiseHealingWave_By18Percent_AndLeaveOtherHeals()
    {
        uint wave = Heal(HealingWave);
        uint other = Heal(LesserHeal);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(_kit.System.AddAura(_friend, HealingWayRules.HealingWayAura, caster: _shaman));
        }

        Assert.Equal(1.18f, HealingWayRules.TakenMultiplier(_kit.System, _friend, _kit.Store.Get(HealingWave)!), 3);
        Assert.Equal((uint)(wave * 1.18f), Heal(HealingWave), 1u);
        Assert.Equal(other, Heal(LesserHeal));
    }
}
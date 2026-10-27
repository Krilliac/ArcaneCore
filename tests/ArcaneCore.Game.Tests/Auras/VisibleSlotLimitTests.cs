using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// The 16 debuff limit (vmangos Unit::AddSpellAuraHolder, Unit.cpp:3284-3305, RemoveAuraDueToVisibleSlotLimit :3321-3346, categories of
/// SpellAuraHolder::CalculateForDebuffLimit, SpellAuras.cpp:7595-8051): a seventeenth debuff evicts the least important one, the oldest within
/// a category, and is itself refused when everything on the unit outranks it.
/// </summary>
public sealed class VisibleSlotLimitTests
{
    private const uint FirstDot = 949_201; // 949_201 .. 949_217: plain DoTs (category 1)
    private const uint Curse = 949_250; // a resistance debuff (category 3)
    private const uint Flurry = 949_260; // a melee haste debuff (category 3)

    private static SpellInfo Debuff(uint id, AuraType type, int value) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, value, SpellImplicitTarget.UnitEnemy, type, amplitude: type == AuraType.PeriodicDamage ? 3000u : 0u)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static (SpellTestKit Kit, Player Caster, Player Victim) Setup(params SpellInfo[] extra)
    {
        var spells = new List<SpellInfo>(extra);
        for (uint i = 0; i < 17; i++)
        {
            spells.Add(Debuff(FirstDot + i, AuraType.PeriodicDamage, 1));
        }

        spells.Add(Debuff(Curse, AuraType.ModResistance, -10));
        var kit = new SpellTestKit([.. spells]);
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 5, 0);
        victim.MaxHealth = 1_000_000;
        victim.Health = 1_000_000;
        kit.System.Relations = new FakeRelations { Hostile = { victim.Guid } };
        return (kit, caster, victim);
    }

    private static void Cast(SpellTestKit kit, Player caster, Player victim, uint spell)
    {
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: false));
        kit.Advance(kit.System.SpellBatchWindowMs); // with spell batching on, the held go resolves at the next boundary (0: no-op)
    }

    [Fact]
    public void TheSeventeenthDebuffOfOneCategory_EvictsTheOldest()
    {
        (SpellTestKit kit, Player caster, Player victim) = Setup();
        using (kit)
        {
            for (uint i = 0; i < 17; i++)
            {
                Cast(kit, caster, victim, FirstDot + i);
            }

            IReadOnlyList<SpellAuraHolder> auras = kit.System.GetAuras(victim);
            Assert.Equal(16, auras.Count(h => !h.IsPositive));
            Assert.False(kit.System.HasAura(victim, FirstDot));
            Assert.True(kit.System.HasAura(victim, FirstDot + 16));
            Assert.All(auras, h => Assert.NotEqual(SpellAuraHolder.NoSlot, h.Slot));
        }
    }

    [Fact]
    public void ALowCategoryDebuff_IsRefused_WhenEveryShownDebuffOutranksIt()
    {
        var curses = Enumerable.Range(0, 16).Select(i => Debuff(949_300 + (uint)i, AuraType.ModResistance, -1 - i)).ToArray();
        (SpellTestKit kit, Player caster, Player victim) = Setup(curses);
        using (kit)
        {
            foreach (SpellInfo curse in curses)
            {
                Cast(kit, caster, victim, curse.Id);
            }

            Cast(kit, caster, victim, FirstDot); // a category 1 DoT against sixteen category 3 debuffs

            Assert.False(kit.System.HasAura(victim, FirstDot));
            Assert.Equal(16, kit.System.GetAuras(victim).Count(h => !h.IsPositive));
        }
    }

    [Fact]
    public void AHigherCategoryDebuff_EvictsALowerOne()
    {
        (SpellTestKit kit, Player caster, Player victim) = Setup();
        using (kit)
        {
            for (uint i = 0; i < 16; i++)
            {
                Cast(kit, caster, victim, FirstDot + i);
            }

            Cast(kit, caster, victim, Curse);

            Assert.True(kit.System.HasAura(victim, Curse));
            Assert.False(kit.System.HasAura(victim, FirstDot));
            Assert.Equal(16, kit.System.GetAuras(victim).Count(h => !h.IsPositive));
        }
    }

    [Fact]
    public void Categories_FollowVmangos()
    {
        SpellInfo sunder = Debuff(1, AuraType.ModResistance, -10) with { SpellFamilyName = 4, SpellFamilyFlags = 1UL << 14 };
        SpellInfo stun = Debuff(2, AuraType.ModStun, 0);
        SpellInfo dot = Debuff(3, AuraType.PeriodicDamage, 5);
        SpellInfo channel = Debuff(4, AuraType.Dummy, 0) with { AttributesEx = SpellAttributesEx.IsChanneled };
        SpellInfo sap = Debuff(6770, AuraType.ModStun, 0);

        Assert.Equal(3, VisibleSlotLimit.DebuffScore(sunder, triggered: false));
        Assert.Equal(4, VisibleSlotLimit.DebuffScore(stun, triggered: false));
        Assert.Equal(2, VisibleSlotLimit.DebuffScore(stun, triggered: true));
        Assert.Equal(1, VisibleSlotLimit.DebuffScore(dot, triggered: false));
        Assert.Equal(0, VisibleSlotLimit.DebuffScore(dot, triggered: true));
        Assert.Equal(1, VisibleSlotLimit.DebuffScore(channel, triggered: false));
        Assert.Equal(0, VisibleSlotLimit.DebuffScore(sap, triggered: false)); // listed: category 0
    }

    [Fact]
    public void ARankOfAListedSpell_TakesItsFirstRanksCategory()
    {
        SpellInfo rank1 = Debuff(6770, AuraType.ModStun, 0) with { Name = "Sap", Rank = "Rank 1", SpellFamilyName = 8 };
        SpellInfo rank3 = Debuff(11297, AuraType.ModStun, 0) with { Name = "Sap", Rank = "Rank 3", SpellFamilyName = 8 };
        var store = new SpellStore([rank1, rank3], [], []);

        Assert.Equal(0, VisibleSlotLimit.DebuffScore(rank3, triggered: false, store));
        Assert.Equal(4, VisibleSlotLimit.DebuffScore(rank3, triggered: false)); // without the chain: a stun
    }
}
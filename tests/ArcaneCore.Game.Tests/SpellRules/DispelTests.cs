using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Retail dispel (vmangos Spell::EffectDispel, SpellEffects.cpp:2456-2570; GetDispellMask SpellEntry.h:460-467).</summary>
public sealed class DispelTests
{
    private const uint DispelAll = 940_001;
    private const uint DispelMagic = 940_002;
    private const uint DispelCurse = 940_003;
    private const uint DispelAllOne = 940_004;
    private const uint DispelMagicZero = 940_005;
    private const uint ShieldSlamShape = 940_006;
    private const uint SpellstoneShape = 940_007;
    private const uint MagicBuff = 940_010;
    private const uint MagicDebuff = 940_011;
    private const uint CurseBuff = 940_012;
    private const uint CurseDebuff = 940_013;
    private const uint DiseaseBuff = 940_014;
    private const uint StackingMagicBuff = 940_015;
    private const uint SecondMagicBuff = 940_016;

    private static SpellInfo Dispel(uint id, int value, int misc) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.Dispel, value, SpellImplicitTarget.Unit, misc: misc))
        with { RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static SpellInfo Aura(uint id, uint dispelType, bool debuff, uint stacks = 0) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy))
        with
        {
            Dispel = dispelType,
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
            Attributes = debuff ? SpellAttributes.AuraIsDebuff : SpellAttributes.None,
            StackAmount = stacks,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        Dispel(DispelAll, 4, 7),
        Dispel(DispelMagic, 1, 1),
        Dispel(DispelCurse, 1, 2),
        Dispel(DispelAllOne, 1, -1),
        Dispel(DispelMagicZero, 0, 1),
        Dispel(ShieldSlamShape, 1, 1) with { SpellFamilyName = 4, SpellFamilyFlags = 1UL << 32 },
        Dispel(SpellstoneShape, 2, 1) with { SpellFamilyName = 5, SpellFamilyFlags = 1UL << 17 },
        Aura(MagicBuff, 1, debuff: false),
        Aura(MagicDebuff, 1, debuff: true),
        Aura(CurseBuff, 2, debuff: false),
        Aura(CurseDebuff, 2, debuff: true),
        Aura(DiseaseBuff, 3, debuff: false),
        Aura(StackingMagicBuff, 1, debuff: false, stacks: 3),
        Aura(SecondMagicBuff, 1, debuff: false));

    private static (Player Caster, Player Friend, Player Enemy, FakeSession CasterSession) Setup(SpellTestKit kit)
    {
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, FakeSession session) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2, 2);
        (Player enemy, _) = kit.AddPlayer(3, 3);
        relations.Hostile.Add(enemy.Guid);
        return (caster, friend, enemy, session);
    }

    private static void Give(SpellTestKit kit, Unit target, uint spell) =>
        kit.System.CastSpell(target, spell, SpellCastTargets.ForSelf(), triggered: true);

    private static void Dispel(SpellTestKit kit, Unit caster, Unit target, uint spell) =>
        kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    private static List<byte[]> Sent(FakeSession session, WorldOpcode opcode)
    {
        var found = new List<byte[]>();
        foreach ((WorldOpcode op, byte[] payload) in session.Sent)
        {
            if (op == opcode)
            {
                found.Add(payload);
            }
        }

        return found;
    }

    [Fact]
    public void DispelAll_UsesTheMagicCurseDiseasePoisonMask_AndPolarityOnlyFiltersMagicAndPoison()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, _) = Setup(kit);
        foreach (uint spell in new[] { MagicBuff, MagicDebuff, CurseDebuff, DiseaseBuff })
        {
            Give(kit, enemy, spell);
        }

        Dispel(kit, caster, enemy, DispelAll); // value 4: every candidate

        Assert.False(kit.System.HasAura(enemy, MagicBuff));   // magic: positive from an enemy
        Assert.True(kit.System.HasAura(enemy, MagicDebuff));  // magic: negative stays on an enemy
        Assert.False(kit.System.HasAura(enemy, CurseDebuff)); // curse: no polarity rule
        Assert.False(kit.System.HasAura(enemy, DiseaseBuff)); // disease: no polarity rule
    }

    [Fact]
    public void DispelMagic_OnAFriend_RemovesTheDebuffOnly_AndCursesIgnorePolarity()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player friend, _, _) = Setup(kit);
        Give(kit, friend, MagicBuff);
        Give(kit, friend, MagicDebuff);
        Give(kit, friend, CurseBuff);

        Dispel(kit, caster, friend, DispelMagic);
        Assert.True(kit.System.HasAura(friend, MagicBuff));
        Assert.False(kit.System.HasAura(friend, MagicDebuff));

        Dispel(kit, caster, friend, DispelCurse); // a positive curse aura is removed from a friend: no polarity for curses
        Assert.False(kit.System.HasAura(friend, CurseBuff));
    }

    [Fact]
    public void NegativeMiscValue_MeansAllTypes()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, _) = Setup(kit);
        Give(kit, enemy, DiseaseBuff);

        Dispel(kit, caster, enemy, DispelAllOne);

        Assert.False(kit.System.HasAura(enemy, DiseaseBuff));
    }

    [Fact]
    public void ValueZero_ActsAsOne_AndNeverRemovesMoreThanTheValue()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, _) = Setup(kit);
        Give(kit, enemy, MagicBuff);
        Give(kit, enemy, SecondMagicBuff);

        Dispel(kit, caster, enemy, DispelMagicZero);

        Assert.Equal(1, new[] { MagicBuff, SecondMagicBuff }.Count(s => kit.System.HasAura(enemy, s)));
    }

    [Fact]
    public void AStackedHolder_LosesOneStackPerDispel_AndTheAuraAmountsFollow()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, _) = Setup(kit);
        for (int i = 0; i < 3; i++)
        {
            Give(kit, enemy, StackingMagicBuff);
        }

        SpellAuraHolder holder = kit.System.GetAuras(enemy).Single(h => h.Spell.Id == StackingMagicBuff);
        Assert.Equal(3, holder.StackAmount);

        Dispel(kit, caster, enemy, DispelMagicZero);
        Assert.Equal(2, kit.System.GetAuras(enemy).Single(h => h.Spell.Id == StackingMagicBuff).StackAmount);
        Dispel(kit, caster, enemy, DispelMagicZero);
        Dispel(kit, caster, enemy, DispelMagicZero);
        Assert.False(kit.System.HasAura(enemy, StackingMagicBuff));
    }

    [Fact]
    public void AMultiCountDispel_ConsumesSeveralStacksOfOneHolder()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, _) = Setup(kit);
        for (int i = 0; i < 3; i++)
        {
            Give(kit, enemy, StackingMagicBuff);
        }

        Dispel(kit, caster, enemy, DispelAll); // value 4 picks, magic buff only: three stacks go, then nothing is left

        Assert.False(kit.System.HasAura(enemy, StackingMagicBuff));
    }

    [Fact]
    public void ASuccessfulDispel_SendsOneLogWithTheDistinctSpellIds()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, FakeSession session) = Setup(kit);
        Give(kit, enemy, MagicBuff);
        Give(kit, enemy, SecondMagicBuff);
        session.Clear();

        Dispel(kit, caster, enemy, DispelAll);

        byte[] log = Assert.Single(Sent(session, WorldOpcode.SmsgSpelldispellog));
        byte[] expectedOrder1 = SpellRulePackets.BuildSpellDispelLog(enemy.Guid, caster.Guid, [MagicBuff, SecondMagicBuff]);
        byte[] expectedOrder2 = SpellRulePackets.BuildSpellDispelLog(enemy.Guid, caster.Guid, [SecondMagicBuff, MagicBuff]);
        Assert.True(log.AsSpan().SequenceEqual(expectedOrder1) || log.AsSpan().SequenceEqual(expectedOrder2));
        Assert.Empty(Sent(session, WorldOpcode.SmsgDispelFailed));
    }

    [Fact]
    public void ADispelResistedByTheSpellMod_SendsDispelFailed_AndRemovesNothing()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, FakeSession session) = Setup(kit);
        Give(kit, enemy, MagicBuff);
        kit.System.SpellModifiers = new MagicHitChanceTests.AddModifier(SpellModOp.ResistDispelChance, 100f);
        session.Clear();

        Dispel(kit, caster, enemy, DispelMagicZero);

        Assert.True(kit.System.HasAura(enemy, MagicBuff));
        Assert.Equal(SpellRulePackets.BuildDispelFailed(caster.Guid, enemy.Guid, [MagicBuff]), Assert.Single(Sent(session, WorldOpcode.SmsgDispelFailed)));
        Assert.Empty(Sent(session, WorldOpcode.SmsgSpelldispellog));
    }

    [Theory]
    [InlineData(49, true)]
    [InlineData(50, false)]
    public void ShieldSlam_DispelsWithFiftyPercent(int roll, bool dispelled)
    {
        using SpellTestKit kit = Kit();
        (Player caster, _, Player enemy, _) = Setup(kit);
        Give(kit, enemy, MagicBuff);
        kit.System.Random = new ScriptedRandom(roll);

        Dispel(kit, caster, enemy, ShieldSlamShape);

        Assert.Equal(dispelled, !kit.System.HasAura(enemy, MagicBuff));
    }

    [Fact]
    public void TheWarlockSpellstone_IgnoresFaction()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player friend, _, _) = Setup(kit);
        Give(kit, friend, MagicBuff);
        Give(kit, friend, MagicDebuff);

        Dispel(kit, caster, friend, SpellstoneShape); // value 2: both, positive and negative

        Assert.False(kit.System.HasAura(friend, MagicBuff));
        Assert.False(kit.System.HasAura(friend, MagicDebuff));
    }

    [Fact]
    public void NothingToDispel_UsesTheSameCandidateRules()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player friend, Player enemy, _) = Setup(kit);
        Give(kit, friend, CurseBuff);
        Give(kit, enemy, MagicDebuff);

        Assert.Single(kit.System.DispellableAuras(caster, friend, 2));   // curse buff on a friend: no polarity
        Assert.Empty(kit.System.DispellableAuras(caster, enemy, 1));     // negative magic on an enemy: filtered
        Assert.Single(kit.System.DispellableAuras(caster, friend, 7));   // dispel all
        Assert.Single(kit.System.DispellableAuras(caster, friend, uint.MaxValue)); // negative misc value = all
    }
}

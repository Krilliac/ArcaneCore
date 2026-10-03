using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Dispel;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells.Casters;

/// <summary>
/// Dispel as vmangos implements it (Spell::EffectDispel, SpellEffects.cpp:2456-2600): type mask from the misc value,
/// polarity only for magic and poison, one removal per stack, SMSG_SPELLDISPELLOG / SMSG_DISPEL_FAILED.
/// Spell shapes follow classic-db: Dispel Magic (effect Dispel, misc 1, value 1 or 2), Cure Disease (misc 3).
/// </summary>
public sealed class DispelTests
{
    private const uint DispelMagic = 6001;
    private const uint DispelMagicTwo = 6002;
    private const uint CureDisease = 6003;
    private const uint DispelEverything = 6004;
    private const uint DispelThree = 6005;
    private const uint MagicDebuffA = 6101;
    private const uint MagicDebuffB = 6102;
    private const uint MagicDebuffC = 6103;
    private const uint MagicBuff = 6104;
    private const uint DiseaseDebuff = 6105;
    private const uint CurseDebuff = 6106;
    private const uint PoisonDebuff = 6107;
    private const uint StealthBuff = 6108;
    private const uint StackingBuff = 6109;

    private static SpellInfo Dispeller(uint id, int value, int type) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.Dispel, value, SpellImplicitTarget.Unit, misc: type)) with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Debuff(uint id, uint dispelType) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
    {
        Dispel = dispelType,
        Duration = new SpellDuration(30000, 0, 30000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Buff(uint id, uint dispelType, uint stack = 0) => SpellTestKit.Spell(
        id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        Dispel = dispelType,
        StackAmount = stack,
        Duration = new SpellDuration(30000, 0, 30000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit NewKit() => new(
        Dispeller(DispelMagic, 1, 1),
        Dispeller(DispelMagicTwo, 2, 1),
        Dispeller(CureDisease, 1, 3),
        Dispeller(DispelEverything, 9, -1),
        Dispeller(DispelThree, 3, 1),
        Debuff(MagicDebuffA, 1),
        Debuff(MagicDebuffB, 1),
        Debuff(MagicDebuffC, 1),
        Buff(MagicBuff, 1),
        Debuff(DiseaseDebuff, 3),
        Debuff(CurseDebuff, 2),
        Debuff(PoisonDebuff, 4),
        Buff(StealthBuff, 5),
        Buff(StackingBuff, 1, stack: 3));

    private static (Player Caster, Player Friend, Player Enemy) Setup(SpellTestKit kit)
    {
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, _) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2, 2);
        (Player enemy, _) = kit.AddPlayer(3, 3);
        relations.Hostile.Add(enemy.Guid);
        kit.Spellbook.Teach(caster, DispelMagic, DispelMagicTwo, CureDisease, DispelEverything, DispelThree);
        return (caster, friend, enemy);
    }

    private static void Apply(SpellTestKit kit, Unit caster, uint spell, Unit target)
        => kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    [Fact]
    public void DispelMagic_OnAFriend_RemovesExactlyTheRequestedNumberOfDebuffs_AndNoBuffs()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player friend, Player enemy) = Setup(kit);
        Apply(kit, enemy, MagicDebuffA, friend);
        Apply(kit, enemy, MagicDebuffB, friend);
        Apply(kit, enemy, MagicDebuffC, friend);
        Apply(kit, friend, MagicBuff, friend);

        kit.System.HandleCastRequest(caster, DispelMagicTwo, SpellCastTargets.ForUnit(friend.Guid));

        Assert.True(kit.System.HasAura(friend, MagicBuff));
        Assert.Equal(1, new[] { MagicDebuffA, MagicDebuffB, MagicDebuffC }.Count(id => kit.System.HasAura(friend, id)));
    }

    [Fact]
    public void DispelMagic_OnAnEnemy_RemovesBuffsAndNotDebuffs()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _, Player enemy) = Setup(kit);
        Apply(kit, enemy, MagicBuff, enemy);
        Apply(kit, caster, MagicDebuffA, enemy);

        kit.System.HandleCastRequest(caster, DispelMagic, SpellCastTargets.ForUnit(enemy.Guid));

        Assert.False(kit.System.HasAura(enemy, MagicBuff));
        Assert.True(kit.System.HasAura(enemy, MagicDebuffA));
    }

    [Fact]
    public void CureDisease_RemovesTheDiseaseWhateverTheFactionOfTheTarget()
    {
        // HEAD applied the friendly/hostile polarity to every dispel type: a disease debuff on an enemy was never removed.
        using SpellTestKit kit = NewKit();
        (Player caster, Player friend, Player enemy) = Setup(kit);
        Apply(kit, caster, DiseaseDebuff, enemy);
        Apply(kit, enemy, DiseaseDebuff, friend);

        kit.System.HandleCastRequest(caster, CureDisease, SpellCastTargets.ForUnit(enemy.Guid));
        kit.System.HandleCastRequest(caster, CureDisease, SpellCastTargets.ForUnit(friend.Guid));

        Assert.False(kit.System.HasAura(enemy, DiseaseDebuff));
        Assert.False(kit.System.HasAura(friend, DiseaseDebuff));
    }

    [Fact]
    public void NegativeMiscValue_DispelsEveryDispelType_ExceptStealthAndInvisibility()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player friend, Player enemy) = Setup(kit);
        foreach (uint debuff in new[] { MagicDebuffA, DiseaseDebuff, CurseDebuff, PoisonDebuff })
        {
            Apply(kit, enemy, debuff, friend);
        }

        Apply(kit, friend, StealthBuff, friend);
        kit.System.HandleCastRequest(caster, DispelEverything, SpellCastTargets.ForUnit(friend.Guid));

        foreach (uint debuff in new[] { MagicDebuffA, DiseaseDebuff, CurseDebuff, PoisonDebuff })
        {
            Assert.False(kit.System.HasAura(friend, debuff));
        }

        Assert.True(kit.System.HasAura(friend, StealthBuff));
    }

    [Fact]
    public void EachStack_CountsAsOneRemoval_AndTheLogListsTheSpellOnce()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _, Player enemy) = Setup(kit);
        for (int i = 0; i < 3; i++)
        {
            Apply(kit, enemy, StackingBuff, enemy);
        }

        Assert.Equal(3, kit.System.GetAuras(enemy).Single(h => h.Spell.Id == StackingBuff).StackAmount);
        FakeSession session = (FakeSession)caster.Session;
        session.Clear();

        kit.System.HandleCastRequest(caster, DispelMagicTwo, SpellCastTargets.ForUnit(enemy.Guid));

        Assert.Equal(1, kit.System.GetAuras(enemy).Single(h => h.Spell.Id == StackingBuff).StackAmount);
        byte[] log = SpellTestKit.Packets(session, WorldOpcode.SmsgSpelldispellog).Single();
        Assert.Equal(StackingBuff, BitConverter.ToUInt32(log, log.Length - 4));
        Assert.Equal(1u, BitConverter.ToUInt32(log, log.Length - 8));

        kit.System.HandleCastRequest(caster, DispelMagic, SpellCastTargets.ForUnit(enemy.Guid));
        Assert.False(kit.System.HasAura(enemy, StackingBuff));
    }

    [Fact]
    public void ARemovalCountBeyondTheCandidates_StopsWhenNothingIsLeft()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player friend, Player enemy) = Setup(kit);
        Apply(kit, enemy, MagicDebuffA, friend);

        kit.System.HandleCastRequest(caster, DispelThree, SpellCastTargets.ForUnit(friend.Guid));

        Assert.False(kit.System.HasAura(friend, MagicDebuffA));
    }

    [Fact]
    public void ResistedDispel_KeepsTheAura_AndReportsTheFailure()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player friend, Player enemy) = Setup(kit);
        kit.System.DispelResistChance = (_, spell) => spell.Id == MagicDebuffA ? 100 : 0;
        Apply(kit, enemy, MagicDebuffA, friend);
        FakeSession session = (FakeSession)caster.Session;
        session.Clear();

        kit.System.HandleCastRequest(caster, DispelMagic, SpellCastTargets.ForUnit(friend.Guid));

        Assert.True(kit.System.HasAura(friend, MagicDebuffA));
        Assert.Empty(SpellTestKit.Packets(session, WorldOpcode.SmsgSpelldispellog));
        Assert.Equal(DispelPackets.BuildDispelFailed(caster.Guid, friend.Guid, [MagicDebuffA]), SpellTestKit.Packets(session, WorldOpcode.SmsgDispelFailed).Single());
    }

    [Fact]
    public void NothingToDispel_FailsTheCastFromTheSameCandidateSet()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, Player friend, Player enemy) = Setup(kit);
        Apply(kit, enemy, DiseaseDebuff, enemy);

        Assert.Equal(SpellCastResult.NothingToDispel, kit.System.HandleCastRequest(caster, DispelMagic, SpellCastTargets.ForUnit(friend.Guid)));
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, CureDisease, SpellCastTargets.ForUnit(enemy.Guid)));
    }

    [Fact]
    public void PacketLayouts_FollowWowMessages()
    {
        byte[] log = DispelPackets.BuildDispelLog(new ObjectGuid(2), new ObjectGuid(1), [527, 528]);
        var expected = new List<byte> { 0x01, 0x02, 0x01, 0x01 };
        expected.AddRange(BitConverter.GetBytes(2u));
        expected.AddRange(BitConverter.GetBytes(527u));
        expected.AddRange(BitConverter.GetBytes(528u));
        Assert.Equal([.. expected], log);

        byte[] failed = DispelPackets.BuildDispelFailed(new ObjectGuid(1), new ObjectGuid(2), [527]);
        var expectedFailed = new List<byte>();
        expectedFailed.AddRange(BitConverter.GetBytes(1ul));
        expectedFailed.AddRange(BitConverter.GetBytes(2ul));
        expectedFailed.AddRange(BitConverter.GetBytes(527u));
        Assert.Equal([.. expectedFailed], failed);
    }
}

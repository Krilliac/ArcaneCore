using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Skills;

/// <summary>
/// The lock and gathering rules against vmangos Spell.cpp:7869-7923 (CanOpenLock), :5948-5964 and :6040-6059 (the
/// cast checks) and SpellEffects.cpp:5371-5390 (skinning), plus the effect-check and game-object target seams.
/// </summary>
public sealed class GatheringRulesTests
{
    private const uint Herbalism = SkillIds.Herbalism;
    private const uint Mining = SkillIds.Mining;
    private const uint Lockpicking = SkillIds.Lockpicking;
    private const uint LockTypeMining = 3;
    private const uint LockTypeHerbalism = 2;
    private const uint LockTypePick = 1;
    private const uint LockTypeOpen = 5;

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
    }

    private static LockEntry Lock(uint id, params (uint Type, uint Index, uint Skill)[] cases)
    {
        uint[] types = new uint[LockEntry.Cases];
        uint[] indexes = new uint[LockEntry.Cases];
        uint[] skills = new uint[LockEntry.Cases];
        for (int i = 0; i < cases.Length; i++)
        {
            (types[i], indexes[i], skills[i]) = cases[i];
        }

        return new LockEntry(id, types, indexes, skills);
    }

    private static OpenLockCheck Check(LockEntry? entry, uint lockType, uint skill, uint castItem = 0, bool fromItem = false, int bonus = 0, bool player = true, uint lockId = 1)
        => GatheringRules.CanOpenLock(lockId, entry, lockType, castItem, fromItem, bonus, player, id => skill);

    [Fact]
    public void NoLock_OpensForAnyone_AnUnknownLockIsABadTarget()
    {
        Assert.Equal(SpellCastResult.CastOk, Check(null, LockTypeMining, 0, lockId: 0).Result);
        Assert.Equal(SpellCastResult.BadTargets, Check(null, LockTypeMining, 0).Result);
    }

    [Fact]
    public void MiningLock_NeedsTheRequiredSkill_AndReportsWhatItUsed()
    {
        LockEntry vein = Lock(1, (2, LockTypeMining, 65));
        OpenLockCheck low = Check(vein, LockTypeMining, 64);
        Assert.Equal((SpellCastResult.LowCastlevel, Mining, 65, 64), (low.Result, low.SkillId, low.RequiredSkill, low.SkillValue));
        OpenLockCheck exact = Check(vein, LockTypeMining, 65);
        Assert.Equal((SpellCastResult.CastOk, Mining, 65, 65), (exact.Result, exact.SkillId, exact.RequiredSkill, exact.SkillValue));
    }

    [Fact]
    public void TheSpellsSimpleValue_IsASkillBonus_ButNotWhenCastFromAnItem()
    {
        LockEntry lockbox = Lock(1, (2, LockTypePick, 100));
        Assert.Equal(SpellCastResult.LowCastlevel, Check(lockbox, LockTypePick, 70).Result);
        Assert.Equal(SpellCastResult.CastOk, Check(lockbox, LockTypePick, 70, bonus: 30).Result);
        // A cast from an item ignores the player's skill; only the bonus counts (skeleton keys and thieves' tools).
        OpenLockCheck fromItem = Check(lockbox, LockTypePick, 500, fromItem: true, bonus: 30);
        Assert.Equal((SpellCastResult.LowCastlevel, 30), (fromItem.Result, fromItem.SkillValue));
        Assert.Equal(SpellCastResult.CastOk, Check(lockbox, LockTypePick, 0, fromItem: true, bonus: 100).Result);
        // A caster that is not a player has no skill either.
        Assert.Equal(SpellCastResult.LowCastlevel, Check(lockbox, LockTypePick, 500, player: false).Result);
    }

    [Fact]
    public void AKeyCaseOpensWithTheCastingItem()
    {
        LockEntry chest = Lock(1, (1, 5060, 0), (2, LockTypePick, 150));
        Assert.Equal(SpellCastResult.CastOk, Check(chest, LockTypePick, 0, castItem: 5060, fromItem: true).Result);
        Assert.Equal(SpellCastResult.LowCastlevel, Check(chest, LockTypePick, 0, castItem: 9999, fromItem: true).Result);
        Assert.Equal(SpellCastResult.CastOk, Check(Lock(1, (1, 5060, 0)), LockTypeMining, 0, castItem: 5060, fromItem: true).Result);
        Assert.Equal(SpellCastResult.BadTargets, Check(Lock(1, (1, 0, 0)), LockTypeMining, 0, castItem: 5060, fromItem: true).Result);   // a zero key index never matches
    }

    [Fact]
    public void TheFirstFittingSkillCaseDecides_OtherLockTypesAreSkipped()
    {
        // Herbalism case first (not the spell's type), then Mining 50, then Mining 10: the first Mining case decides.
        LockEntry lockEntry = Lock(1, (2, LockTypeHerbalism, 1), (2, LockTypeMining, 50), (2, LockTypeMining, 10));
        OpenLockCheck check = Check(lockEntry, LockTypeMining, 20);
        Assert.Equal((SpellCastResult.LowCastlevel, 50), (check.Result, check.RequiredSkill));
    }

    [Fact]
    public void ASkillLessLockType_OpensWithoutSkill_AndNoMatchingCaseMeansLocked()
    {
        LockEntry treasure = Lock(1, (2, LockTypeOpen, 0));
        OpenLockCheck open = Check(treasure, LockTypeOpen, 0);
        Assert.Equal((SpellCastResult.CastOk, 0u), (open.Result, open.SkillId));
        // Lock 85 style: no case for the spell's type at all.
        Assert.Equal(SpellCastResult.BadTargets, Check(treasure, LockTypeMining, 500).Result);
        Assert.Equal(SpellCastResult.BadTargets, Check(Lock(1), LockTypeMining, 500).Result);
        // Blasting counts as a skill case even though no profession backs it (Seaforium charges).
        Assert.Equal(SpellCastResult.CastOk, Check(Lock(1, (2, GatheringRules.LockTypeBlasting, 0)), GatheringRules.LockTypeBlasting, 0).Result);
        Assert.Equal(SpellCastResult.LowCastlevel, Check(Lock(1, (2, GatheringRules.LockTypeBlasting, 5)), GatheringRules.LockTypeBlasting, 0).Result);
    }

    [Theory]
    [InlineData(Herbalism, 300, 400, false, 300, 400)]    // herbalism and mining never fail at the world maximum (300)
    [InlineData(Mining, 300, 400, false, 300, 400)]
    [InlineData(Lockpicking, 300, 400, true, 300, 400)]   // lockpicking and skinning still can
    [InlineData(SkillIds.Skinning, 300, 400, true, 300, 400)]
    [InlineData(Herbalism, 299, 400, true, 300, 400)]     // below the maximum: required 400 > irand(274, 336) always
    public void OrangeGatherFails_AtTheMaximum_OnlyForLockpickingAndSkinning(uint skill, int skillValue, int required, bool fails, int configMax, int unused)
    {
        _ = unused;
        Assert.Equal(fails, GatheringRules.OrangeGatherFails(skill, skillValue, required, (ushort)configMax, new FixedRandom(int.MaxValue)));
    }

    [Fact]
    public void OrangeGatherRoll_IsInclusiveOnBothEnds_AgainstTheRequiredSkill()
    {
        // irand(80 - 25, 80 + 37) = irand(55, 117): required 100 fails for rolls 55..99, not for 100..117.
        Assert.True(GatheringRules.OrangeGatherFails(Herbalism, 80, 100, 300, new FixedRandom(99)));
        Assert.False(GatheringRules.OrangeGatherFails(Herbalism, 80, 100, 300, new FixedRandom(100)));
        Assert.False(GatheringRules.OrangeGatherFails(Herbalism, 80, 100, 300, new FixedRandom(117)));
        Assert.True(GatheringRules.OrangeGatherFails(Herbalism, 80, 100, 300, new FixedRandom(0)));        // clamped to 55
        Assert.False(GatheringRules.OrangeGatherFails(Herbalism, 80, 55, 300, new FixedRandom(0)));        // required equal to the lowest roll: never fails
    }

    [Theory]
    [InlineData(50, 5, -50)]      // skill < 100: (level - 10) * 10
    [InlineData(50, 15, 50)]
    [InlineData(99, 30, 200)]
    [InlineData(100, 30, 150)]    // skill >= 100: level * 5
    [InlineData(300, 60, 300)]
    public void SkinningRequiredSkill_FollowsTheSkillBand(int skill, int level, int expected)
        => Assert.Equal(expected, GatheringRules.SkinningRequiredSkill(skill, level));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 0)]
    [InlineData(15, 50)]
    [InlineData(19, 90)]
    [InlineData(20, 100)]
    [InlineData(40, 200)]
    public void SkinningSkillUpLevel_IsZeroBelowLevelTen_ThenTenPerLevel_ThenFivePerLevel(int level, int expected)
        => Assert.Equal(expected, GatheringRules.SkinningSkillUpLevel(level));

    [Fact]
    public void GameObjectImplicitTarget26_Exists_AndTheGameObjectEffectRunsOnTheCasterAsCarrier()
    {
        Assert.Equal(26u, (uint)SpellImplicitTarget.GameObjectItem);

        (SpellSystem system, Entities.Player player) = Rig();
        Entities.Unit? seenTarget = null;
        system.RegisterEffect(SpellEffectName.OpenLock, c => seenTarget = c.Target);
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.GameObject, GameObject = ObjectGuid.WithEntry(HighGuid.GameObject, 1, 1) };
        Assert.Equal(SpellCastResult.CastOk, system.CastSpell(player, 9300, targets, triggered: true));
        Assert.Same(player, seenTarget);
    }

    [Fact]
    public void EffectChecks_RunAfterTheTargetRules_StrictAtPrepare_NonStrictWhenTheCastLands()
    {
        (SpellSystem system, Entities.Player player) = Rig();
        var seen = new List<(int Index, bool Strict, bool Triggered)>();
        system.RegisterEffectCheck(SpellEffectName.OpenLock, c =>
        {
            seen.Add((c.EffectIndex, c.Strict, c.Triggered));
            return SpellCastResult.CastOk;
        });
        Assert.Equal(SpellCastResult.CastOk, system.CastSpell(player, 9300, SpellCastTargets.ForSelf(), triggered: false));
        Assert.Equal([(0, true, false), (0, false, false)], seen);   // an instant cast: the prepare check, then the landing check
    }

    [Fact]
    public void AnEffectCheckThatRefuses_StopsTheCast_AndItsResultIsTheCastResult()
    {
        (SpellSystem system, Entities.Player player) = Rig();
        bool effectRan = false;
        system.RegisterEffect(SpellEffectName.OpenLock, _ => effectRan = true);
        system.RegisterEffectCheck(SpellEffectName.OpenLock, _ => SpellCastResult.TryAgain);
        Assert.Equal(SpellCastResult.TryAgain, system.CastSpell(player, 9300, SpellCastTargets.ForSelf(), triggered: false));
        Assert.False(effectRan);
        Assert.Contains(((FakeSession)player.Session).Sent, p => p.Opcode == WorldOpcode.SmsgCastResult && p.Payload[5] == (byte)SpellCastResult.TryAgain);

        // A spell without that effect never meets the check.
        Assert.Equal(SpellCastResult.CastOk, system.CastSpell(player, 9301, SpellCastTargets.ForSelf(), triggered: false));
    }

    private static (SpellSystem System, Entities.Player Player) Rig()
    {
        SpellInfo[] spells =
        [
            SpellTestKit.Spell(9300, SpellTestKit.Effect(SpellEffectName.OpenLock, 1, SpellImplicitTarget.GameObject, misc: 3)),
            SpellTestKit.Spell(9301, SpellTestKit.Effect(SpellEffectName.Dummy, 1)),
        ];
        var system = new SpellSystem(new SpellStore(spells, [], []), () => 0u);
        Entities.Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        return (system, player);
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Combat.RotationTestKit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

/// <summary>
/// The shared pieces under the class rotations: the role table (vmangos AutoAssignRole), spell data resolution at the highest
/// rank (PopulateSpellData), heal, buff and dispel target selection (SelectHealTarget, SelectMostEfficientHealingSpell,
/// SelectBuffTarget) and the brain's distancing decision.
/// </summary>
public sealed class PlayerbotCombatSupportTests
{
    [Theory]
    [InlineData(Class.Warrior, 0u, (int)PlayerbotRole.MeleeDps)]
    [InlineData(Class.Warrior, PlayerbotRoles.ShieldSlam, (int)PlayerbotRole.Tank)]
    [InlineData(Class.Rogue, 0u, (int)PlayerbotRole.MeleeDps)]
    [InlineData(Class.Hunter, 0u, (int)PlayerbotRole.RangeDps)]
    [InlineData(Class.Mage, 0u, (int)PlayerbotRole.RangeDps)]
    [InlineData(Class.Warlock, 0u, (int)PlayerbotRole.RangeDps)]
    [InlineData(Class.Paladin, 0u, (int)PlayerbotRole.Healer)]
    [InlineData(Class.Paladin, PlayerbotRoles.HolyShield, (int)PlayerbotRole.Tank)]
    [InlineData(Class.Paladin, PlayerbotRoles.SanctityAura, (int)PlayerbotRole.MeleeDps)]
    [InlineData(Class.Priest, 0u, (int)PlayerbotRole.Healer)]
    [InlineData(Class.Priest, PlayerbotRoles.Shadowform, (int)PlayerbotRole.RangeDps)]
    [InlineData(Class.Shaman, 0u, (int)PlayerbotRole.Healer)]
    [InlineData(Class.Shaman, PlayerbotRoles.ElementalMastery, (int)PlayerbotRole.RangeDps)]
    [InlineData(Class.Shaman, PlayerbotRoles.Stormstrike, (int)PlayerbotRole.MeleeDps)]
    [InlineData(Class.Druid, 0u, (int)PlayerbotRole.Healer)]
    [InlineData(Class.Druid, PlayerbotRoles.MoonkinForm, (int)PlayerbotRole.RangeDps)]
    [InlineData(Class.Druid, PlayerbotRoles.LeaderOfThePack, (int)PlayerbotRole.MeleeDps)]
    public void TheRoleFollowsTheTalentSignatureSpell(Class playerClass, uint known, int expected)
        => Assert.Equal((PlayerbotRole)expected, PlayerbotRoles.Assign(playerClass, id => id == known));

    [Fact]
    public void SpellData_ResolvesEachAbilityAtItsHighestRank_ByWholeName_AndSkipsPassives()
    {
        SpellInfo rend1 = Debuff("Rend", 1), rend3 = Debuff("Rend", 3), rend2 = Debuff("Rend", 2);
        SpellInfo passive = Debuff("Execute", 1) with { Attributes = SpellAttributes.Passive };
        SpellInfo judgementOf = Hostile("Judgement of Righteousness");
        SpellInfo heal1 = Heal("Lesser Heal", 50), heal2 = Heal("Heal", 300), renew = Renew("Renew", 10);
        PlayerbotAbilities book = PlayerbotAbilities.Resolve([rend1, rend3, rend2, passive, judgementOf, heal1, heal2, renew],
            ["Rend", "Execute", "Judgement", "Lesser Heal"]);
        Assert.Same(rend3, book["Rend"]);
        Assert.Null(book["Execute"]);
        Assert.Null(book["Judgement"]); // "Judgement of Righteousness" is not "Judgement"
        Assert.Same(heal1, book["Lesser Heal"]);
        Assert.Equal([heal2, heal1], book.DirectHeals);
        Assert.Equal([renew], book.PeriodicHeals);
        Assert.Equal(3, PlayerbotAbilities.RankOf(rend3));
        Assert.True(book.Known.Contains(passive.Id));
    }

    [Fact]
    public void HealTarget_IsTheBotBelowItsThreshold_ElseTheMostInjuredMember_ElseThePet()
    {
        var priest = new PlayerbotPriestRotation();
        RotationState Hurt(float self, float a, float b, float pet = 100f) => State(priest, [], Self(5, self), null, PlayerbotRole.Healer, more: s =>
        {
            s.Party.Add(Member(7, 1, a));
            s.Party.Add(Member(8, 4, b, distance: 35f)); // out of heal range
            s.Pet = RotationPetStatus.Alive;
            s.PetUnit = new RotationUnit { Guid = new ObjectGuid(0xF1400000000000AAUL), Health = (uint)pet, MaxHealth = 100, Distance = 3f };
        });
        Assert.Equal(BotGuid, PlayerbotClassRotation.SelectHealTarget(Hurt(50f, 20f, 10f), 60f, 80f)!.Guid);
        Assert.Equal(new ObjectGuid(7), PlayerbotClassRotation.SelectHealTarget(Hurt(70f, 20f, 10f), 60f, 80f)!.Guid);
        Assert.Equal(new ObjectGuid(0xF1400000000000AAUL), PlayerbotClassRotation.SelectHealTarget(Hurt(100f, 100f, 10f, pet: 50f), 60f, 80f)!.Guid);
        Assert.Null(PlayerbotClassRotation.SelectHealTarget(Hurt(100f, 100f, 10f), 60f, 80f));
    }

    [Fact]
    public void TheMostEfficientHeal_IsTheStrongestFirstNearestTheWound()
    {
        var priest = new PlayerbotPriestRotation();
        SpellInfo big = Heal("Greater Heal", 300), medium = Heal("Heal", 150), small = Heal("Lesser Heal", 80);
        RotationState state = State(priest, [small, big, medium], Self(5), null, PlayerbotRole.Healer);
        RotationUnit Wounded(uint missing) => new() { Guid = new ObjectGuid(7), Health = 1000 - missing, MaxHealth = 1000 };
        Assert.Same(small, PlayerbotClassRotation.MostEfficient(state, Wounded(100), state.Spells.DirectHeals));
        Assert.Same(medium, PlayerbotClassRotation.MostEfficient(state, Wounded(160), state.Spells.DirectHeals));
        Assert.Same(big, PlayerbotClassRotation.MostEfficient(state, Wounded(600), state.Spells.DirectHeals));
        // A heal that cannot be cast now (cooldown, mana) is skipped.
        RotationState noBig = State(priest, [small, big, medium], Self(5), null, PlayerbotRole.Healer, blocked: ["Greater Heal"]);
        Assert.Same(medium, PlayerbotClassRotation.MostEfficient(noBig, Wounded(600), noBig.Spells.DirectHeals));
    }

    [Fact]
    public void BuffTarget_IsTheFirstFriendInRangeWithoutTheBuff_AndTwoMissingTakeTheGroupVersion()
    {
        var mage = new PlayerbotMageRotation();
        SpellInfo single = Aura("Arcane Intellect"), group = Aura("Arcane Brilliance");
        RotationAction one = mage.OutOfCombat(State(mage, [single, group], Self(8, 100, "Arcane Intellect"), null, PlayerbotRole.RangeDps,
            inCombat: false, more: s =>
            {
                s.Party.Add(Member(7, 1, distance: 40f));          // too far
                s.Party.Add(Member(8, 5, auras: "Arcane Brilliance")); // has the group version
                s.Party.Add(Member(9, 4));
            }))!.Value;
        Assert.Equal((single, new ObjectGuid(9)), (one.Spell, one.Target));
        RotationAction two = mage.OutOfCombat(State(mage, [single, group], Self(8, 100, "Arcane Intellect"), null, PlayerbotRole.RangeDps,
            inCombat: false, more: s =>
            {
                s.Party.Add(Member(7, 1));
                s.Party.Add(Member(9, 4));
            }))!.Value;
        Assert.Equal((group, new ObjectGuid(7)), (two.Spell, two.Target));
    }

    [Theory]
    // melee bots close to melee and swing
    [InlineData(4f, 10f, false, false, false, (int)PlayerbotFightPosition.ChaseToMelee)]
    [InlineData(4f, 3f, false, false, false, (int)PlayerbotFightPosition.Melee)]
    // casters and healers stand at 25 yards
    [InlineData(25f, 32f, false, false, false, (int)PlayerbotFightPosition.ChaseToRange)]
    [InlineData(25f, 24f, false, false, false, (int)PlayerbotFightPosition.Hold)]
    [InlineData(25f, 6f, false, true, false, (int)PlayerbotFightPosition.Hold)]       // the enemy is coming: no running into melee
    [InlineData(25f, 3f, false, true, false, (int)PlayerbotFightPosition.Melee)]      // it is here: swing back
    [InlineData(25f, 3f, false, false, false, (int)PlayerbotFightPosition.Hold)]      // a target not fighting the bot is not engaged in melee
    [InlineData(25f, 20f, false, false, true, (int)PlayerbotFightPosition.ChaseToMelee)] // nothing to cast for a while
    // hunters shoot from 8 yards or more and melee inside the dead zone
    [InlineData(30f, 20f, true, true, false, (int)PlayerbotFightPosition.Hold)]
    [InlineData(30f, 8f, true, true, false, (int)PlayerbotFightPosition.Hold)]
    [InlineData(30f, 7.9f, true, true, false, (int)PlayerbotFightPosition.ChaseToMelee)]
    [InlineData(30f, 3f, true, true, false, (int)PlayerbotFightPosition.Melee)]
    public void TheDistancingDecision(float preferred, float distance, bool hunter, bool targetOnBot, bool idleTooLong, int expected)
        => Assert.Equal((PlayerbotFightPosition)expected, PlayerbotBrain.DecidePosition(preferred, distance, hunter, targetOnBot, idleTooLong));

    /// <summary>
    /// The server's swing reach decides melee, not the 3D distance: on a slope (the replay's Kobold Laborer 2 yards away and 3.9 yards
    /// up, 4.4 yards in 3D) the bot is in melee and swings; out of that reach it still closes in.
    /// </summary>
    [Theory]
    [InlineData(4f, 4.4f, false, false, true, (int)PlayerbotFightPosition.Melee)]
    [InlineData(4f, 4.4f, false, false, false, (int)PlayerbotFightPosition.ChaseToMelee)]
    [InlineData(25f, 4.4f, false, true, true, (int)PlayerbotFightPosition.Melee)]   // a caster reached on a slope swings back
    [InlineData(25f, 4.4f, false, false, true, (int)PlayerbotFightPosition.Hold)]   // ... but does not engage one not fighting it
    [InlineData(4f, 3f, false, false, false, (int)PlayerbotFightPosition.Melee)]
    public void TheDistancingDecision_UsesTheServersMeleeReach(float preferred, float distance, bool hunter, bool targetOnBot, bool inMeleeReach, int expected)
        => Assert.Equal((PlayerbotFightPosition)expected,
            PlayerbotBrain.DecidePosition(preferred, distance, hunter, targetOnBot, idleTooLong: false, inMeleeReach));
}

using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Application;
using ArcaneCore.Game.Spells.Rules.Diminishing;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Diminishing-return groups, the per-unit tracker and the rule through the spell system (vmangos Spell.cpp:1733-1800, Unit.cpp:7618-7690, SpellEntry.cpp:281-390).</summary>
public sealed class DiminishingTests
{
    private const uint Stun = 930_001;
    private const uint StunAndDamage = 930_002;
    private const uint Fear = 930_003;
    private const uint Silence = 930_004;
    private const uint KidneyShotShape = 930_005;

    private static SpellInfo WithMechanic(uint id, SpellMechanic mechanic, params AuraType[] auras) =>
        SpellTestKit.Spell(id, [.. auras.Select(a => SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, a))])
        with
        {
            Mechanic = (uint)mechanic,
            Duration = new SpellDuration(4000, 0, 4000),
            SpellVisual = 1,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit()
    {
        SpellInfo stunAndDamage = WithMechanic(StunAndDamage, SpellMechanic.Stun, AuraType.ModStun);
        stunAndDamage = stunAndDamage with
        {
            Effects = [SpellTestKit.Effect(SpellEffectName.SchoolDamage, 6, SpellImplicitTarget.UnitEnemy), stunAndDamage.Effects[0]],
        };
        return new SpellTestKit(
            WithMechanic(Stun, SpellMechanic.Stun, AuraType.ModStun),
            stunAndDamage,
            WithMechanic(Fear, SpellMechanic.Fear, AuraType.ModFear),
            WithMechanic(Silence, SpellMechanic.Silence, AuraType.ModSilence),
            WithMechanic(KidneyShotShape, SpellMechanic.Stun, AuraType.ModStun) with { SpellFamilyName = 8, SpellFamilyFlags = 1UL << 21 });
    }

    private static (Player Caster, Player Victim, DiminishingRule Rule) Setup(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        var relations = new FakeRelations();
        relations.Hostile.Add(victim.Guid);
        kit.System.Relations = relations;
        var rule = new DiminishingRule();
        rule.Attach(kit.System);
        return (caster, victim, rule);
    }

    private static void Hit(SpellTestKit kit, Unit caster, Unit victim, uint spell) =>
        kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: true);

    private static int DurationOf(SpellTestKit kit, Unit unit, uint spell) =>
        kit.System.GetAuras(unit).Single(h => h.Spell.Id == spell).MaxDuration;

    // --- classifier -------------------------------------------------------------------------------

    [Theory]
    [InlineData(SpellMechanic.Stun, false, DiminishingGroup.ControlStun)]
    [InlineData(SpellMechanic.Stun, true, DiminishingGroup.TriggerStun)]
    [InlineData(SpellMechanic.Root, false, DiminishingGroup.ControlRoot)]
    [InlineData(SpellMechanic.Root, true, DiminishingGroup.TriggerRoot)]
    [InlineData(SpellMechanic.Sleep, false, DiminishingGroup.Sleep)]
    [InlineData(SpellMechanic.Polymorph, false, DiminishingGroup.Polymorph)]
    [InlineData(SpellMechanic.Fear, false, DiminishingGroup.Fear)]
    [InlineData(SpellMechanic.Charm, false, DiminishingGroup.Charm)]
    [InlineData(SpellMechanic.Silence, false, DiminishingGroup.Silence)]
    [InlineData(SpellMechanic.Disarm, false, DiminishingGroup.Disarm)]
    [InlineData(SpellMechanic.Freeze, false, DiminishingGroup.Freeze)]
    [InlineData(SpellMechanic.Knockout, false, DiminishingGroup.Knockout)]
    [InlineData(SpellMechanic.Sapped, false, DiminishingGroup.Knockout)]
    [InlineData(SpellMechanic.Banish, false, DiminishingGroup.Banish)]
    [InlineData(SpellMechanic.Horror, false, DiminishingGroup.DeathCoil)]
    [InlineData(SpellMechanic.Snare, false, DiminishingGroup.None)]
    [InlineData(SpellMechanic.None, false, DiminishingGroup.None)]
    public void ByMechanic_FollowsTheVmangosPriorityList(SpellMechanic mechanic, bool triggered, DiminishingGroup expected)
    {
        Assert.Equal(expected, DiminishingClassifier.GetGroup(SpellTestKit.Spell(1) with { Mechanic = (uint)mechanic }, triggered));
    }

    [Fact]
    public void StunBeatsRoot_AndAnEffectMechanicCounts()
    {
        SpellInfo frostNovaShape = SpellTestKit.Spell(1, new SpellEffectInfo(), new SpellEffectInfo { Mechanic = (uint)SpellMechanic.Root });
        SpellInfo both = frostNovaShape with { Mechanic = (uint)SpellMechanic.Stun };

        Assert.Equal(DiminishingGroup.ControlRoot, DiminishingClassifier.GetGroup(frostNovaShape, false));
        Assert.Equal(DiminishingGroup.ControlStun, DiminishingClassifier.GetGroup(both, false));
    }

    [Fact]
    public void ExplicitFamilyAndIdCases_OverrideTheMechanic()
    {
        SpellInfo stun = SpellTestKit.Spell(1) with { Mechanic = (uint)SpellMechanic.Stun };

        Assert.Equal(DiminishingGroup.KidneyShot, DiminishingClassifier.GetGroup(stun with { SpellFamilyName = 8, SpellFamilyFlags = 1UL << 21 }, false));
        Assert.Equal(DiminishingGroup.None, DiminishingClassifier.GetGroup(stun with { SpellFamilyName = 8, SpellFamilyFlags = 1UL << 24 }, false)); // blind
        Assert.Equal(DiminishingGroup.Freeze, DiminishingClassifier.GetGroup(SpellTestKit.Spell(1) with { SpellFamilyName = 9, SpellFamilyFlags = 1UL << 3 }, false));
        Assert.Equal(DiminishingGroup.WarlockFear, DiminishingClassifier.GetGroup(
            SpellTestKit.Spell(1) with { SpellFamilyName = 5, SpellFamilyFlags = 1UL << 31, Mechanic = (uint)SpellMechanic.Fear }, false));
        Assert.Equal(DiminishingGroup.WarlockFear, DiminishingClassifier.GetGroup(SpellTestKit.Spell(6358) with { SpellFamilyName = 5 }, false)); // Seduction (inside the warlock family case)
        Assert.Equal(DiminishingGroup.LimitOnly, DiminishingClassifier.GetGroup(SpellTestKit.Spell(1) with { SpellFamilyName = 5, SpellFamilyFlags = 1UL << 31 }, false)); // curses
        Assert.Equal(DiminishingGroup.LimitOnly, DiminishingClassifier.GetGroup(SpellTestKit.Spell(1) with { SpellFamilyName = 4, SpellFamilyFlags = 1UL << 1 }, false)); // hamstring
        Assert.Equal(DiminishingGroup.ControlRoot, DiminishingClassifier.GetGroup(SpellTestKit.Spell(1) with { SpellFamilyName = 11, SpellFamilyFlags = 1UL << 31 }, false)); // frost shock
        Assert.Equal(DiminishingGroup.None, DiminishingClassifier.GetGroup(stun with { SpellFamilyName = 3, SpellVisual = 4325 }, false)); // ice block
        Assert.Equal(DiminishingGroup.TriggerStun, DiminishingClassifier.GetGroup(SpellTestKit.Spell(12355), false)); // impact
        Assert.Equal(DiminishingGroup.TriggerStun, DiminishingClassifier.GetGroup(SpellTestKit.Spell(18093), false)); // pyroclasm
        foreach (uint id in new uint[] { 7922, 20253, 20614, 20615 })
        {
            Assert.Equal(DiminishingGroup.ControlStun, DiminishingClassifier.GetGroup(SpellTestKit.Spell(id), true)); // charge / intercept stuns
        }
    }

    [Fact]
    public void GroupTypes_FollowVmangos()
    {
        Assert.Equal(DiminishingType.All, DiminishingGroups.TypeOf(DiminishingGroup.ControlStun));
        Assert.Equal(DiminishingType.All, DiminishingGroups.TypeOf(DiminishingGroup.KidneyShot));
        Assert.Equal(DiminishingType.Player, DiminishingGroups.TypeOf(DiminishingGroup.Fear));
        Assert.Equal(DiminishingType.Player, DiminishingGroups.TypeOf(DiminishingGroup.Freeze));
        Assert.Equal(DiminishingType.None, DiminishingGroups.TypeOf(DiminishingGroup.LimitOnly));
        Assert.Equal(DiminishingType.None, DiminishingGroups.TypeOf(DiminishingGroup.None));
        Assert.Equal([1.0f, 0.5f, 0.25f, 0.0f], new[] { DiminishingLevel.Level1, DiminishingLevel.Level2, DiminishingLevel.Level3, DiminishingLevel.Immune }.Select(DiminishingGroups.RateOf));
    }

    // --- tracker ----------------------------------------------------------------------------------

    [Fact]
    public void Tracker_FirstHitCreatesTheEntryAtLevelTwo_AndStopsAtImmune()
    {
        var tracker = new DiminishingTracker();
        const DiminishingGroup g = DiminishingGroup.ControlStun;

        Assert.Equal(DiminishingLevel.Level1, tracker.GetLevel(g, 1000, 15_000));
        tracker.Increment(g, 1000);
        Assert.Equal(DiminishingLevel.Level2, tracker.GetLevel(g, 2000, 15_000));
        tracker.Increment(g, 2000);
        Assert.Equal(DiminishingLevel.Level3, tracker.GetLevel(g, 3000, 15_000));
        tracker.Increment(g, 3000);
        Assert.Equal(DiminishingLevel.Immune, tracker.GetLevel(g, 4000, 15_000));
        tracker.Increment(g, 4000);
        Assert.Equal(DiminishingLevel.Immune, tracker.GetLevel(g, 5000, 15_000));
    }

    [Fact]
    public void Tracker_TheResetWindowStartsWhenTheLastAuraOfTheGroupEnds()
    {
        var tracker = new DiminishingTracker();
        const DiminishingGroup g = DiminishingGroup.ControlStun;
        tracker.Increment(g, 0);
        tracker.AuraChanged(g, applied: true, 0);

        // The aura is still up for 10 s: no reset however long ago the hit was.
        Assert.Equal(DiminishingLevel.Level2, tracker.GetLevel(g, 40_000, 15_000));
        tracker.AuraChanged(g, applied: false, 10_000);
        Assert.Equal(DiminishingLevel.Level2, tracker.GetLevel(g, 24_900, 15_000));
        Assert.Equal(DiminishingLevel.Level1, tracker.GetLevel(g, 25_100, 15_000));
        tracker.Increment(g, 25_100);
        tracker.AuraChanged(g, applied: true, 25_100); // as the added holder does; the entry keeps the old stamp until the aura ends
        Assert.Equal(DiminishingLevel.Level2, tracker.GetLevel(g, 25_200, 15_000)); // a reset entry starts again at level 2
    }

    [Fact]
    public void Tracker_TwoAurasOfAGroup_NeedBothRemovedToStartTheWindow_AndGroupsAreIndependent()
    {
        var tracker = new DiminishingTracker();
        tracker.Increment(DiminishingGroup.Fear, 0);
        tracker.AuraChanged(DiminishingGroup.Fear, true, 0);
        tracker.AuraChanged(DiminishingGroup.Fear, true, 0);
        tracker.AuraChanged(DiminishingGroup.Fear, false, 1000);
        Assert.Equal(DiminishingLevel.Level2, tracker.GetLevel(DiminishingGroup.Fear, 60_000, 15_000)); // one still up
        tracker.AuraChanged(DiminishingGroup.Fear, false, 61_000);
        Assert.Equal(DiminishingLevel.Level1, tracker.GetLevel(DiminishingGroup.Fear, 80_000, 15_000));
        Assert.Equal(DiminishingLevel.Level1, tracker.GetLevel(DiminishingGroup.Silence, 1, 15_000));
        tracker.Increment(DiminishingGroup.Silence, 1);
        tracker.Clear();
        Assert.Equal(DiminishingLevel.Level1, tracker.GetLevel(DiminishingGroup.Silence, 2, 15_000));
    }

    // --- through the spell system -----------------------------------------------------------------

    [Fact]
    public void ControlledStuns_OnAPlayer_GoFullHalfQuarterThenNothing()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim, _) = Setup(kit);

        Hit(kit, caster, victim, Stun);
        Assert.Equal(4000, DurationOf(kit, victim, Stun));
        kit.Advance(100);
        Hit(kit, caster, victim, Stun);
        Assert.Equal(2000, DurationOf(kit, victim, Stun));
        kit.Advance(100);
        Hit(kit, caster, victim, Stun);
        Assert.Equal(1000, DurationOf(kit, victim, Stun));
        kit.Advance(100);
        var added = new List<SpellAuraHolder>();
        kit.System.HolderAdded += added.Add;
        Hit(kit, caster, victim, Stun);

        Assert.Empty(added); // fully diminished: no new holder
        Assert.True(DurationOf(kit, victim, Stun) <= 1000); // the third stun was not refreshed
    }

    [Fact]
    public void AFullyDiminishedStun_StillDealsItsDamage()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim, DiminishingRule rule) = Setup(kit);
        rule.GetTracker(victim).Increment(DiminishingGroup.TriggerStun, kit.Now);
        rule.GetTracker(victim).Increment(DiminishingGroup.TriggerStun, kit.Now);
        rule.GetTracker(victim).Increment(DiminishingGroup.TriggerStun, kit.Now); // immune level
        uint before = victim.Health;

        Hit(kit, caster, victim, StunAndDamage);

        Assert.Equal(before - 6, victim.Health);
        Assert.DoesNotContain(kit.System.GetAuras(victim), h => h.Spell.Id == StunAndDamage);
        Assert.Equal(UnitFlags.None, victim.UnitFlags & UnitFlags.Stunned);
    }

    [Fact]
    public void TheLevelResets_FifteenSecondsAfterTheStunEnds_NotAfterTheHit()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim, _) = Setup(kit);

        Hit(kit, caster, victim, Stun);
        kit.Advance(4100); // the 4 s stun expires at about t = 4 s
        Assert.DoesNotContain(kit.System.GetAuras(victim), h => h.Spell.Id == Stun);
        kit.Advance(10_000);
        Hit(kit, caster, victim, Stun);
        Assert.Equal(2000, DurationOf(kit, victim, Stun)); // 10 s after the end: still level 2

        kit.Advance(4100);
        kit.Advance(15_500);
        Hit(kit, caster, victim, Stun);
        Assert.Equal(4000, DurationOf(kit, victim, Stun)); // more than 15 s after the end: reset
    }

    [Fact]
    public void DifferentGroups_DoNotInteract_AndKidneyShotIsItsOwnGroup()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim, _) = Setup(kit);

        Hit(kit, caster, victim, Stun);
        Hit(kit, caster, victim, KidneyShotShape);
        Hit(kit, caster, victim, Silence);

        Assert.Equal(4000, DurationOf(kit, victim, KidneyShotShape));
        Assert.Equal(4000, DurationOf(kit, victim, Silence));
    }

    [Fact]
    public void Death_ClearsTheDiminishing()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim, DiminishingRule rule) = Setup(kit);
        Hit(kit, caster, victim, Stun);
        Assert.Equal(DiminishingLevel.Level2, rule.GetTracker(victim).GetLevel(DiminishingGroup.TriggerStun, kit.Now, 15_000));

        victim.Health = 0;
        victim.Combat.DeathState = Game.Combat.DeathState.Dead;
        kit.System.OnUnitDied(victim);

        Assert.Equal(DiminishingLevel.Level1, rule.GetTracker(victim).GetLevel(DiminishingGroup.TriggerStun, kit.Now, 15_000));
    }

    [Fact]
    public void FriendlyCasters_AreNotDiminished_ButStillCountAsAHit()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 2);
        new DiminishingRule().Attach(kit.System); // default relations: same-team players are friendly

        Hit(kit, caster, ally, Stun);
        kit.System.RemoveAuras(ally, Stun);
        Hit(kit, caster, ally, Stun);

        Assert.Equal(4000, DurationOf(kit, ally, Stun));
    }

    [Fact]
    public void HolderEvents_FireOncePerAddAndRemove_AndStackRefreshDoesNotDoubleCount()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player victim, _) = Setup(kit);
        int added = 0;
        int removed = 0;
        kit.System.HolderAdded += _ => added++;
        kit.System.HolderRemoved += _ => removed++;

        Hit(kit, caster, victim, Silence);
        Assert.Equal((1, 0), (added, removed));
        kit.System.RemoveAuras(victim, Silence);
        Assert.Equal((1, 1), (added, removed));
    }

    // --- the rule on non-player targets and callers -----------------------------------------------

    private static (SpellAuraHolder Holder, SpellApplication App) Apply(SpellTestKit kit, DiminishingRule rule, Unit caster, Unit target, uint spellId)
    {
        SpellInfo spell = kit.Store.Get(spellId)!;
        var cast = new SpellCast(spell, caster, SpellCastTargets.ForUnit(target.Guid), triggered: false, 0, 0, 0);
        var app = new SpellApplication(kit.System, cast, target, 1);
        rule.Begin(app);
        var holder = new SpellAuraHolder(spell, target, caster, new AuraCasterOwner(caster), 4000);
        return (holder, app);
    }

    [Fact]
    public void PlayerOnlyGroups_AreNeverDiminished_OnACreature_AndDoNotAdvance()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player _, DiminishingRule rule) = Setup(kit);
        Creature wolf = FoundationTests.MakeCreature(0, 5);
        var relations = new FakeRelations();
        relations.Hostile.Add(wolf.Guid);
        kit.System.Relations = relations;

        for (int i = 0; i < 4; i++)
        {
            (SpellAuraHolder holder, SpellApplication app) = Apply(kit, rule, caster, wolf, Fear);
            Assert.True(rule.AcceptHolder(app, holder));
            Assert.Equal(4000, holder.MaxDuration);
        }

        Assert.Equal(DiminishingLevel.Level1, rule.GetTracker(wolf).GetLevel(DiminishingGroup.Fear, kit.Now, 15_000));
    }

    [Fact]
    public void Stuns_AreDiminished_OnACreature_TooBecauseTheirGroupIsTypeAll()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player _, DiminishingRule rule) = Setup(kit);
        Creature wolf = FoundationTests.MakeCreature(0, 5);
        var relations = new FakeRelations();
        relations.Hostile.Add(wolf.Guid);
        kit.System.Relations = relations;

        (SpellAuraHolder first, SpellApplication firstApp) = Apply(kit, rule, caster, wolf, Stun);
        Assert.True(rule.AcceptHolder(firstApp, first));
        Assert.Equal(4000, first.MaxDuration);
        (SpellAuraHolder second, SpellApplication secondApp) = Apply(kit, rule, caster, wolf, Stun);
        Assert.True(rule.AcceptHolder(secondApp, second));
        Assert.Equal(2000, second.MaxDuration);
    }

    [Fact]
    public void ACreatureCastersFear_OnAPlayer_AdvancesTheLevelButIsNotScaled()
    {
        using SpellTestKit kit = Kit();
        (Player _, Player victim, DiminishingRule rule) = Setup(kit);
        Creature wolf = FoundationTests.MakeCreature(0, 5);
        var relations = new FakeRelations();
        relations.Hostile.Add(victim.Guid);
        kit.System.Relations = relations;

        (SpellAuraHolder holder, SpellApplication app) = Apply(kit, rule, wolf, victim, Fear);

        Assert.True(rule.AcceptHolder(app, holder));
        Assert.Equal(4000, holder.MaxDuration); // not player-like on both sides (Unit.cpp:7660)
        Assert.Equal(DiminishingLevel.Level2, rule.GetTracker(victim).GetLevel(DiminishingGroup.Fear, kit.Now, 15_000)); // Spell.cpp:1744 only needs a player target
    }
}

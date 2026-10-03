using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Totems;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>
/// The threat core against vmangos Threat/ThreatManager.cpp, Threat/HostileRefManager.cpp and Unit::CanHaveThreatList
/// (Objects/Unit.cpp:7377-7405). Pure Game tests: no clock, no database, no client.
/// </summary>
public sealed class ThreatListCoreTests
{
    private static readonly Func<Unit, bool> Valid = _ => true;
    private static readonly Func<Unit, bool> NobodyInMelee = _ => false;

    private static Creature RealCreature(CreatureTemplate? template = null)
    {
        CreatureTemplate t = template ?? Template();
        CreatureContent content = Content([t], []);
        return new Creature(1, t, null, content, new Random(1));
    }

    // --- 110% / 130% rule (ThreatManager.cpp:340-356) ------------------------------------------

    [Fact]
    public void MeleeTarget_TakesAggroOnlyAbove110Percent()
    {
        var owner = new CombatTestUnit();
        var tank = new CombatTestUnit();
        var rival = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(tank, 100);
        Assert.Same(tank, list.SelectVictim(Valid, NobodyInMelee));

        list.AddThreat(rival, 110);
        Assert.Same(tank, list.SelectVictim(Valid, _ => true)); // exactly 110%: keeps

        list.AddThreat(rival, 0.01f);
        Assert.Same(rival, list.SelectVictim(Valid, _ => true)); // above 110%: switches
    }

    [Fact]
    public void RangedTarget_NeedsMoreThan130PercentAndMeleeRangeDoesNotMatter()
    {
        var owner = new CombatTestUnit();
        var tank = new CombatTestUnit();
        var rival = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(tank, 100);
        Assert.Same(tank, list.SelectVictim(Valid, NobodyInMelee));

        list.AddThreat(rival, 129.9f);
        Assert.Same(tank, list.SelectVictim(Valid, NobodyInMelee)); // out of melee reach: 110% is not enough

        list.AddThreat(rival, 0.2f);
        Assert.Same(rival, list.SelectVictim(Valid, NobodyInMelee));
    }

    // --- taunt (ThreatManager.cpp:481-500) -----------------------------------------------------

    [Fact]
    public void TauntApply_LiftsTheTaunterToTheVictimsThreat_AndFadeOutRestoresIt()
    {
        var owner = new CombatTestUnit();
        var tank = new CombatTestUnit();
        var taunter = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(tank, 100);
        list.AddThreat(taunter, 20);
        Assert.Same(tank, list.SelectVictim(Valid, _ => true));

        list.TauntApply(taunter);
        Assert.Equal(100f, list.GetThreat(taunter));
        Assert.Equal(80f, list.FindEntry(taunter)!.TempThreat);

        // a second application while the modifier is in use changes nothing
        list.AddThreat(tank, 50); // victim at 150
        list.TauntApply(taunter);
        Assert.Equal(100f, list.GetThreat(taunter));

        list.TauntFadeOut(taunter);
        Assert.Equal(20f, list.GetThreat(taunter));
        Assert.Equal(0f, list.FindEntry(taunter)!.TempThreat);
    }

    [Fact]
    public void TauntApply_ChangesNothingWhenTheTaunterIsAlreadyAhead_OrHasNoEntry()
    {
        var owner = new CombatTestUnit();
        var tank = new CombatTestUnit();
        var ahead = new CombatTestUnit();
        var stranger = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(tank, 100);
        Assert.Same(tank, list.SelectVictim(Valid, _ => true));
        list.AddThreat(ahead, 105);

        list.TauntApply(ahead);
        list.TauntApply(stranger);

        Assert.Equal(105f, list.GetThreat(ahead));
        Assert.Equal(0f, list.FindEntry(ahead)!.TempThreat);
        Assert.False(list.Contains(stranger));
    }

    [Fact]
    public void TauntCasters_TheLatestValidOneWins_AndAnInvalidOneIsSkipped()
    {
        var owner = new CombatTestUnit();
        var first = new CombatTestUnit();
        var second = new CombatTestUnit();
        Unit? Resolve(ObjectGuid guid) => guid == first.Guid ? first : guid == second.Guid ? second : null;
        ThreatList list = owner.Combat.Threat;
        Assert.False(list.HasTauntCasters);
        Assert.Null(list.GetTauntTarget(Resolve, Valid));

        list.AddTauntCaster(first.Guid);
        list.AddTauntCaster(second.Guid);
        Assert.True(list.HasTauntCasters);
        Assert.Same(second, list.GetTauntTarget(Resolve, Valid));
        Assert.Same(first, list.GetTauntTarget(Resolve, u => !ReferenceEquals(u, second)));

        list.RemoveTauntCaster(second.Guid);
        Assert.Same(first, list.GetTauntTarget(Resolve, Valid));
        // a taunter that left the world is skipped but still counts until its aura is removed
        var gone = ObjectGuid.WithEntry(HighGuid.Unit, 9, 9);
        list.AddTauntCaster(gone);
        Assert.Same(first, list.GetTauntTarget(Resolve, Valid));
        list.RemoveTauntCaster(first.Guid);
        Assert.True(list.HasTauntCasters);
        Assert.Null(list.GetTauntTarget(Resolve, Valid));
        list.RemoveTauntCaster(gone);
        Assert.False(list.HasTauntCasters);
    }

    // --- percent changes (ThreatManager.cpp:250-262, ThreatManager.h addThreatPercent) --------------

    [Fact]
    public void ModifyThreatPercent_BelowMinus100Removes_Minus100Zeroes_OtherwiseScales()
    {
        var owner = new CombatTestUnit();
        var a = new CombatTestUnit();
        var b = new CombatTestUnit();
        var c = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(a, 200);
        list.AddThreat(b, 200);
        list.AddThreat(c, 200);

        list.ModifyThreatPercent(a, -101);
        Assert.False(list.Contains(a));
        Assert.DoesNotContain(owner, a.Combat.ThreatenedBy);

        list.ModifyThreatPercent(b, -100);
        Assert.True(list.Contains(b));
        Assert.Equal(0f, list.GetThreat(b));

        list.ModifyThreatPercent(c, 50);
        Assert.Equal(300f, list.GetThreat(c));
        list.ModifyThreatPercent(c, -50);
        Assert.Equal(150f, list.GetThreat(c));

        list.ModifyThreatPercent(new CombatTestUnit(), 50); // no entry: nothing happens
        Assert.Equal(2, list.Entries.Count);
    }

    [Fact]
    public void HostileRefs_ScaleTempAndDeleteActOnEveryListHoldingTheTarget()
    {
        var one = new CombatTestUnit();
        var two = new CombatTestUnit();
        var target = new CombatTestUnit();
        one.Combat.Threat.AddThreat(target, 100);
        two.Combat.Threat.AddThreat(target, 40);

        HostileRefs.AddThreatPercent(target, -50);
        Assert.Equal(50f, one.Combat.Threat.GetThreat(target));
        Assert.Equal(20f, two.Combat.Threat.GetThreat(target));

        HostileRefs.AddTempThreat(target, -30, apply: true);
        Assert.Equal(20f, one.Combat.Threat.GetThreat(target));
        Assert.Equal(0f, two.Combat.Threat.GetThreat(target)); // clamped at zero like HostileReference::addThreat

        // a second application does not stack
        HostileRefs.AddTempThreat(target, -30, apply: true);
        Assert.Equal(20f, one.Combat.Threat.GetThreat(target));

        HostileRefs.AddTempThreat(target, 0, apply: false);
        Assert.Equal(50f, one.Combat.Threat.GetThreat(target));

        HostileRefs.DeleteReferences(target);
        Assert.True(one.Combat.Threat.IsEmpty);
        Assert.True(two.Combat.Threat.IsEmpty);
        Assert.Empty(target.Combat.ThreatenedBy);
    }

    // --- who can hold threat (Unit.cpp:7377-7405) ---------------------------------------------

    [Fact]
    public void AnOrdinaryCreatureHoldsThreat()
    {
        Creature wolf = RealCreature();
        var attacker = new CombatTestUnit();
        Assert.True(ThreatRules.CanHaveThreatList(wolf));
        wolf.Combat.Threat.AddThreat(attacker, 10);
        Assert.Equal(10f, wolf.Combat.Threat.GetThreat(attacker));
    }

    [Fact]
    public void ANoThreatListCreature_IgnoresThreat()
    {
        Creature creature = RealCreature(Template() with { ExtraFlags = 0x800, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        var attacker = new CombatTestUnit();

        creature.Combat.Threat.AddThreat(attacker, 10);

        Assert.False(ThreatRules.CanHaveThreatList(creature));
        Assert.True(creature.Combat.Threat.IsEmpty);
        Assert.DoesNotContain(creature, attacker.Combat.ThreatenedBy);
    }

    [Fact]
    public void ATotem_IgnoresThreat()
    {
        Creature totem = RealCreature();
        var owner = new CombatTestUnit();
        var info = new TotemInfo(totem, owner, TotemSlot.Fire, 1, 1, 10000);
        TotemQuery.Add(info);
        try
        {
            var attacker = new CombatTestUnit();
            totem.Combat.Threat.AddThreat(attacker, 10);
            Assert.False(ThreatRules.CanHaveThreatList(totem));
            Assert.True(totem.Combat.Threat.IsEmpty);
        }
        finally
        {
            TotemQuery.Remove(info);
        }
    }

    [Fact]
    public void APlayerOwnedPet_IgnoresThreat_ButACreatureOwnedOneHoldsIt()
    {
        var (world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime runtime = world;
        Player owner = CombatTestKit.AddPlayer(runtime, 1, 0, 0, new FakeSession(1));
        var attacker = new CombatTestUnit();

        Creature pet = RealCreature();
        pet.Summon = new SummonLinks(SummonKind.Pet, owner.Guid, 1, 0, 0);
        pet.SetOwnerGuid(owner.Guid);
        pet.Combat.Threat.AddThreat(attacker, 10);
        Assert.False(ThreatRules.CanHaveThreatList(pet));
        Assert.True(pet.Combat.Threat.IsEmpty);

        Creature npcPet = RealCreature();
        npcPet.Summon = new SummonLinks(SummonKind.Guardian, ObjectGuid.WithEntry(HighGuid.Unit, 5, 5), 1, 0, 0);
        npcPet.SetOwnerGuid(ObjectGuid.WithEntry(HighGuid.Unit, 5, 5));
        npcPet.Combat.Threat.AddThreat(attacker, 10);
        Assert.True(ThreatRules.CanHaveThreatList(npcPet));
        Assert.Equal(10f, npcPet.Combat.Threat.GetThreat(attacker));
    }

    [Fact]
    public void AUnitCharmedByAPlayer_IgnoresThreat()
    {
        var (world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime runtime = world;
        Player charmer = CombatTestKit.AddPlayer(runtime, 1, 0, 0, new FakeSession(1));
        Creature creature = RealCreature();
        creature.SetUInt64(UpdateFields.UnitFieldCharmedby, charmer.Guid.Value);

        creature.Combat.Threat.AddThreat(new CombatTestUnit(), 10);

        Assert.False(ThreatRules.CanHaveThreatList(creature));
        Assert.True(creature.Combat.Threat.IsEmpty);
    }

    [Fact]
    public void ADeadOwnerAndAPlayerOwnerHoldNoThreat_AndSoDoDeadTargetsAndTheOwnerItself()
    {
        var owner = new CombatTestUnit();
        var dead = new CombatTestUnit { Health = 0 };
        owner.Combat.Threat.AddThreat(dead, 10);
        owner.Combat.Threat.AddThreat(owner, 10);
        Assert.True(owner.Combat.Threat.IsEmpty);

        var corpse = new CombatTestUnit { Health = 0 };
        corpse.Combat.Threat.AddThreat(new CombatTestUnit(), 10);
        Assert.True(corpse.Combat.Threat.IsEmpty);

        var (world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime runtime = world;
        Player player = CombatTestKit.AddPlayer(runtime, 1, 0, 0, new FakeSession(1));
        player.Combat.Threat.AddThreat(new CombatTestUnit(), 10);
        Assert.True(player.Combat.Threat.IsEmpty);
    }

    // --- pet owner reference (ThreatManager.cpp:117-122) -------------------------------------

    [Fact]
    public void APetThatAttacks_PullsItsOwnerOntoTheList_WithZeroThreat_AndANegativeChangeDoesNot()
    {
        var (world, map, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime runtime = world;
        Player master = CombatTestKit.AddPlayer(runtime, 1, 0, 0, new FakeSession(1));
        var pet = new CombatTestUnit();
        pet.Spawn(map, 1, 0);
        pet.SetOwnerGuid(master.Guid);
        var mob = new CombatTestUnit();
        mob.Spawn(map, 3, 0);

        mob.Combat.Threat.AddThreat(pet, 30);
        Assert.Equal(30f, mob.Combat.Threat.GetThreat(pet));
        Assert.True(mob.Combat.Threat.Contains(master));
        Assert.Equal(0f, mob.Combat.Threat.GetThreat(master));

        var mob2 = new CombatTestUnit();
        mob2.Spawn(map, 4, 0);
        mob2.Combat.Threat.AddThreat(pet, 30);
        mob2.Combat.Threat.Remove(master);
        mob2.Combat.Threat.AddThreat(pet, -10); // a reduction never creates the owner entry
        Assert.False(mob2.Combat.Threat.Contains(master));
    }

    // --- assist threat (ThreatManager.cpp:414-421) -------------------------------------------

    [Fact]
    public void AssistThreat_IsZeroWhileTheOwnerIsConfusedOrFleeing_ButTheEntryExists()
    {
        var owner = new CombatTestUnit();
        var healer = new CombatTestUnit();
        var healer2 = new CombatTestUnit();
        owner.UnitFlags |= UnitFlags.Confused;

        owner.Combat.Threat.AddThreat(healer, 50, new ThreatContext(IsAssist: true));
        Assert.True(owner.Combat.Threat.Contains(healer));
        Assert.Equal(0f, owner.Combat.Threat.GetThreat(healer));

        owner.UnitFlags &= ~UnitFlags.Confused;
        owner.UnitFlags |= UnitFlags.Fleeing;
        owner.Combat.Threat.AddThreat(healer, 50, new ThreatContext(IsAssist: true));
        Assert.Equal(0f, owner.Combat.Threat.GetThreat(healer));

        owner.UnitFlags &= ~UnitFlags.Fleeing;
        owner.Combat.Threat.AddThreat(healer, 50, new ThreatContext(IsAssist: true));
        Assert.Equal(50f, owner.Combat.Threat.GetThreat(healer));

        // plain (non-assist) threat is never zeroed
        owner.UnitFlags |= UnitFlags.Confused;
        owner.Combat.Threat.AddThreat(healer2, 50);
        Assert.Equal(50f, owner.Combat.Threat.GetThreat(healer2));
    }

    [Fact]
    public void NoNewEntry_RaisesAnExistingEntryButNeverCreatesOne()
    {
        var owner = new CombatTestUnit();
        var known = new CombatTestUnit();
        var stranger = new CombatTestUnit();
        owner.Combat.Threat.AddThreat(known, 10);

        owner.Combat.Threat.AddThreat(known, 5, new ThreatContext(NoNewEntry: true));
        owner.Combat.Threat.AddThreat(stranger, 5, new ThreatContext(NoNewEntry: true));

        Assert.Equal(15f, owner.Combat.Threat.GetThreat(known));
        Assert.False(owner.Combat.Threat.Contains(stranger));
    }

    // --- offline entries (ThreatManager.cpp:128-149, :526-543) -------------------------------

    [Fact]
    public void AnEntryWhoseTargetBecomesAGameMaster_GoesOffline_AndIsNeverSelected_UntilItIsBack()
    {
        var (world, _, _, _) = CombatTestKit.CreateWorld();
        using WorldRuntime runtime = world;
        Player target = CombatTestKit.AddPlayer(runtime, 1, 0, 0, new FakeSession(1));
        var other = new CombatTestUnit();
        var owner = new CombatTestUnit();
        owner.Combat.Threat.AddThreat(target, 100);
        owner.Combat.Threat.AddThreat(other, 10);
        Assert.Same(target, owner.Combat.Threat.SelectVictim(Valid, _ => true));

        target.Flags |= PlayerFlags.Gm;
        owner.Combat.Threat.UpdateOnlineStatus(target);

        Assert.Same(target, Assert.Single(owner.Combat.Threat.OfflineEntries).Target);
        Assert.DoesNotContain(owner.Combat.Threat.Entries, e => ReferenceEquals(e.Target, target));
        Assert.Equal(100f, owner.Combat.Threat.GetThreat(target)); // threat is kept
        Assert.Equal(0f, owner.Combat.Threat.GetOnlineThreat(target));
        Assert.Same(other, owner.Combat.Threat.SelectVictim(Valid, _ => true));
        Assert.False(owner.Combat.Threat.IsEmpty);

        target.Flags &= ~PlayerFlags.Gm;
        owner.Combat.Threat.UpdateOnlineStatus(target);

        Assert.Empty(owner.Combat.Threat.OfflineEntries);
        Assert.Same(target, owner.Combat.Threat.SelectVictim(Valid, _ => true));
    }

    // --- two-pass selection (ThreatManager.cpp:286-370) --------------------------------------

    [Fact]
    public void LowPriorityTargets_AreOnlyPickedWhenNobodyElseIsEligible()
    {
        var owner = new CombatTestUnit();
        var feared = new CombatTestUnit();
        var plain = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(feared, 500);
        list.AddThreat(plain, 10);
        Func<Unit, bool> secondChoice = u => ReferenceEquals(u, feared);

        Assert.Same(plain, list.SelectVictim(Valid, _ => true, null, secondChoice));

        list.Remove(plain);
        Assert.Same(feared, list.SelectVictim(Valid, _ => true, null, secondChoice));
    }

    [Fact]
    public void ALowPriorityCurrentVictim_DropsOutOfThe110PercentComparison()
    {
        var owner = new CombatTestUnit();
        var feared = new CombatTestUnit();
        var plain = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(feared, 100);
        Assert.Same(feared, list.SelectVictim(Valid, _ => true)); // current victim

        list.AddThreat(plain, 5);
        Func<Unit, bool> secondChoice = u => ReferenceEquals(u, feared);

        // 5 is far below 110% of 100, yet the feared current victim is no longer the baseline
        Assert.Same(plain, list.SelectVictim(Valid, _ => true, null, secondChoice));
    }

    [Fact]
    public void OutOfArea_StillAbandonsTheSelection_AndAnInvalidCurrentVictimIsReplaced()
    {
        var owner = new CombatTestUnit();
        var a = new CombatTestUnit();
        var b = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(a, 100);
        list.AddThreat(b, 50);
        Assert.Same(a, list.SelectVictim(Valid, _ => true));

        // an out-of-area target behind the chosen victim is never reached (ThreatManager.cpp:301-312 walks in list order)
        Assert.Same(a, list.SelectVictim(Valid, _ => true, u => ReferenceEquals(u, b)));

        // one reached before a victim is found abandons the selection
        Assert.Null(list.SelectVictim(Valid, _ => true, u => ReferenceEquals(u, a)));
        Assert.Null(list.CurrentVictim);

        // an invalid current victim stops being the baseline
        Assert.Same(a, list.SelectVictim(Valid, _ => true));
        Assert.Same(b, list.SelectVictim(u => !ReferenceEquals(u, a), _ => true));
    }

    [Fact]
    public void SetCurrentVictimIfCan_PicksAnEntryWithoutComparison_AndIgnoresStrangers()
    {
        var owner = new CombatTestUnit();
        var top = new CombatTestUnit();
        var low = new CombatTestUnit();
        ThreatList list = owner.Combat.Threat;
        list.AddThreat(top, 100);
        list.AddThreat(low, 10);

        list.SetCurrentVictimIfCan(low);
        Assert.Same(low, list.CurrentVictim);
        list.SetCurrentVictimIfCan(new CombatTestUnit());
        Assert.Same(low, list.CurrentVictim);
        // the next selection compares against the chosen victim: 100 > 110% of 10 in melee
        Assert.Same(top, list.SelectVictim(Valid, _ => true));
    }

    [Fact]
    public void EqualThreat_KeepsInsertionOrder_AndTheListStaysSortedUnderManyChanges()
    {
        var owner = new CombatTestUnit();
        var units = Enumerable.Range(0, 40).Select(_ => new CombatTestUnit()).ToArray();
        ThreatList list = owner.Combat.Threat;
        foreach (CombatTestUnit u in units)
        {
            list.AddThreat(u, 10);
        }

        Assert.Equal(units, list.Entries.Select(e => e.Target));

        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < units.Length; i++)
            {
                list.AddThreat(units[i], (i * 7 + round * 3) % 11);
            }
        }

        float previous = float.MaxValue;
        foreach (ThreatEntry entry in list.Entries)
        {
            Assert.True(entry.Threat <= previous);
            previous = entry.Threat;
        }

        Assert.Equal(40, list.Entries.Count);
    }
}


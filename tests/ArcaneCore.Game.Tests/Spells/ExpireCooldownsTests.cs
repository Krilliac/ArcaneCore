using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.TestSupport;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// <c>SpellSystem.ExpireCooldowns</c> (driven through the internal <c>ExpireCooldownsOf</c> seam): an entry goes at exactly
/// <c>until &lt;= now</c> in each of the four cooldown collections, nothing else is touched, and a pass over unexpired entries allocates nothing
/// (it runs for every tracked unit on every spell tick).
/// </summary>
public sealed class ExpireCooldownsTests
{
    private const uint Now = 50_000;

    /// <summary>A player whose spell state exists (a triggered cast creates it); every cooldown collection is then emptied for the test to fill.</summary>
    private static (Player Player, UnitSpellState State) StateWithEmptyCooldowns(SpellTestKit kit)
    {
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, CooldownSpell, SpellCastTargets.ForSelf(), triggered: true));
        UnitSpellState state = Assert.IsType<UnitSpellState>(kit.System.GetState(player.Guid));
        state.SpellCooldowns.Clear();
        state.CategoryCooldowns.Clear();
        state.GlobalCooldowns.Clear();
        state.SchoolLockouts.Clear();
        state.CategoryOwners.Clear();
        state.CooldownOwners.Clear();
        return (player, state);
    }

    [Fact]
    public void ExpireCooldowns_RemovesEntriesAtUntilLessOrEqualNow_InEveryCollection()
    {
        using var kit = new SpellTestKit();
        (Player player, UnitSpellState state) = StateWithEmptyCooldowns(kit);
        foreach (Dictionary<uint, uint> dictionary in new[] { state.SpellCooldowns, state.CategoryCooldowns, state.GlobalCooldowns })
        {
            dictionary[1] = Now - 1;     // expired
            dictionary[2] = Now;         // expires exactly now: gone (until <= now)
            dictionary[3] = Now + 1;     // one ms left: kept
            dictionary[4] = 0;           // long past: gone
            dictionary[5] = uint.MaxValue;
        }

        state.SchoolLockouts[SpellSchool.Holy] = Now - 1;
        state.SchoolLockouts[SpellSchool.Fire] = Now;
        state.SchoolLockouts[SpellSchool.Nature] = Now + 1;
        state.SchoolLockouts[SpellSchool.Frost] = 0;
        state.SchoolLockouts[SpellSchool.Shadow] = uint.MaxValue;

        kit.System.ExpireCooldownsOf(player, Now);

        foreach (Dictionary<uint, uint> dictionary in new[] { state.SpellCooldowns, state.CategoryCooldowns, state.GlobalCooldowns })
        {
            Assert.Equal([3u, 5u], dictionary.Keys.OrderBy(k => k));
            Assert.Equal(Now + 1, dictionary[3]);
            Assert.Equal(uint.MaxValue, dictionary[5]);
        }

        Assert.Equal([SpellSchool.Nature, SpellSchool.Shadow], state.SchoolLockouts.Keys.OrderBy(k => k));
        Assert.Equal(Now + 1, state.SchoolLockouts[SpellSchool.Nature]);
    }

    [Fact]
    public void ExpireCooldowns_AllExpired_EmptiesEveryCollection()
    {
        using var kit = new SpellTestKit();
        (Player player, UnitSpellState state) = StateWithEmptyCooldowns(kit);
        for (uint i = 1; i <= 20; i++)
        {
            state.SpellCooldowns[i] = Now - i;
            state.CategoryCooldowns[i] = Now;
            state.GlobalCooldowns[i] = Now - 1;
        }

        state.SchoolLockouts[SpellSchool.Fire] = Now;
        state.SchoolLockouts[SpellSchool.Frost] = Now - 5;

        kit.System.ExpireCooldownsOf(player, Now);

        Assert.Empty(state.SpellCooldowns);
        Assert.Empty(state.CategoryCooldowns);
        Assert.Empty(state.GlobalCooldowns);
        Assert.Empty(state.SchoolLockouts);
    }

    [Fact]
    public void ExpireCooldowns_NothingExpired_KeepsEverythingAndOwners()
    {
        using var kit = new SpellTestKit();
        (Player player, UnitSpellState state) = StateWithEmptyCooldowns(kit);
        state.SpellCooldowns[10] = Now + 100;
        state.CategoryCooldowns[11] = Now + 200;
        state.GlobalCooldowns[133] = Now + 300;
        state.SchoolLockouts[SpellSchool.Arcane] = Now + 400;
        state.CategoryOwners[11] = 10;

        kit.System.ExpireCooldownsOf(player, Now);

        Assert.Equal(Now + 100, Assert.Single(state.SpellCooldowns).Value);
        Assert.Equal(Now + 200, Assert.Single(state.CategoryCooldowns).Value);
        Assert.Equal(Now + 300, Assert.Single(state.GlobalCooldowns).Value);
        Assert.Equal(Now + 400, Assert.Single(state.SchoolLockouts).Value);
        Assert.Equal(10u, Assert.Single(state.CategoryOwners).Value);
    }

    [Fact]
    public void ExpireCooldowns_DoesNotTouchOwnerMaps_WhenTheirCooldownExpires()
    {
        // Current behaviour, pinned so the in-place rewrite does not widen the pass: only the four expiry maps are pruned here.
        using var kit = new SpellTestKit();
        (Player player, UnitSpellState state) = StateWithEmptyCooldowns(kit);
        state.SpellCooldowns[10] = Now - 1;
        state.CategoryCooldowns[11] = Now - 1;
        state.CategoryOwners[11] = 10;
        state.CooldownOwners[10] = new ItemCooldownOwner(555, 11, 10);

        kit.System.ExpireCooldownsOf(player, Now);

        Assert.Empty(state.SpellCooldowns);
        Assert.Empty(state.CategoryCooldowns);
        Assert.Single(state.CategoryOwners);
        Assert.Single(state.CooldownOwners);
    }

    [Fact]
    public void ExpireCooldowns_EmptyCollections_AreANoOp()
    {
        using var kit = new SpellTestKit();
        (Player player, UnitSpellState state) = StateWithEmptyCooldowns(kit);

        kit.System.ExpireCooldownsOf(player, Now);

        Assert.Empty(state.SpellCooldowns);
        Assert.Empty(state.CategoryCooldowns);
        Assert.Empty(state.GlobalCooldowns);
        Assert.Empty(state.SchoolLockouts);
    }

    [Fact]
    public void ExpireCooldowns_ThroughUpdate_DropsExpiredCooldownsAndKeepsRunningOnes()
    {
        // The seam must agree with the real tick: SpellSystem.Update calls the same pass with its own clock.
        using var kit = new SpellTestKit();
        (Player player, UnitSpellState state) = StateWithEmptyCooldowns(kit);
        state.SpellCooldowns[1] = kit.Now + 100; // gone after the 100 ms advance (until <= now)
        state.SpellCooldowns[2] = kit.Now + 101; // one ms longer: kept
        state.SchoolLockouts[SpellSchool.Fire] = kit.Now + 100;
        state.GlobalCooldowns[133] = kit.Now + 101;

        kit.Advance(100);

        UnitSpellState after = Assert.IsType<UnitSpellState>(kit.System.GetState(player.Guid));
        Assert.Equal([2u], after.SpellCooldowns.Keys.ToArray());
        Assert.Empty(after.SchoolLockouts);
        Assert.Equal([133u], after.GlobalCooldowns.Keys.ToArray());
    }

    [Fact]
    public void ExpireCooldowns_UnitWithoutState_IsANoOp()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        Assert.Null(kit.System.GetState(player.Guid));

        kit.System.ExpireCooldownsOf(player, Now);

        Assert.Null(kit.System.GetState(player.Guid));
    }

    [Fact]
    public void ExpireCooldowns_UnexpiredSpellCategoryGlobalAndSchoolEntries_AllocateNothing()
    {
        using var kit = new SpellTestKit();
        (Player player, UnitSpellState state) = StateWithEmptyCooldowns(kit);
        const uint far = 4_000_000_000; // never expires at Now
        for (uint i = 1; i <= 8; i++)
        {
            state.SpellCooldowns[i] = far + i;
            state.CategoryCooldowns[i] = far + i;
        }

        state.GlobalCooldowns[133] = far;
        state.GlobalCooldowns[134] = far;
        state.SchoolLockouts[SpellSchool.Fire] = far;
        state.SchoolLockouts[SpellSchool.Frost] = far;
        SpellSystem system = kit.System;

        void Window() { for (int i = 0; i < 1000; i++) { system.ExpireCooldownsOf(player, Now); } }

        AllocationProbe.AssertEveryCleanWindowAtMost(Window, 0, "ExpireCooldowns over unexpired entries");
        Assert.Equal(8, state.SpellCooldowns.Count); // the pass ran and kept everything
        Assert.Equal(2, state.SchoolLockouts.Count);
    }
}

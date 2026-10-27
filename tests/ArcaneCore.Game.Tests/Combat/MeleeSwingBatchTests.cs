using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.CombatCore;

/// <summary>
/// White swings in the spell batch (MapCombat.SwingBatch.cs): with a window set, a ready swing is held and resolves at the next boundary,
/// so two units that kill each other in one window both land their swing (Kaivax, Blizzard, 2019, https://us.forums.blizzard.com/en/wow/t/137118/1).
/// </summary>
public sealed class MeleeSwingBatchTests
{
    private const uint Window = 400;

    private static (WorldRuntime World, Map Map, Player A, Player V) Duel(uint window, bool batchMelee = true)
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        world.UseManualClock(); // RunTick(diff) moves NowMs by diff, so the batch boundaries follow the swing timers
        Player a = CombatTestKit.AddPlayer(world, 1, 0, 0, new FakeSession(1));
        Player v = CombatTestKit.AddPlayer(world, 2, 2f, 0, new FakeSession(2), Race.Orc);
        v.Relocate(2f, 0, 83.5f, MathF.PI, 0); // facing the attacker, so both can swing
        v.Map!.Combat.TogglePvp(v, true);
        a.Map!.Combat.TogglePvp(a, true);
        foreach (Player p in new[] { a, v })
        {
            p.SetFloat(UpdateFields.UnitFieldMindamage, 50);
            p.SetFloat(UpdateFields.UnitFieldMaxdamage, 50);
        }

        map.Combat.SpellMitigation = new SpellSystem(new SpellStore([], [], []), () => world.NowMs)
        {
            SpellBatchWindowMs = window,
            SpellBatchMelee = batchMelee,
        };
        world.RunTick(1);
        return (world, map, a, v);
    }

    [Fact]
    public void WithAWindow_ASwingIsHeldUntilTheBoundary_ThenLands()
    {
        var t = Duel(Window);
        using WorldRuntime world = t.World;
        t.Map.Combat.Attack(t.A, t.V);

        world.RunTick(50);
        Assert.Equal(1000u, t.V.Health);
        Assert.Equal(1, t.Map.Combat.PendingSwings);
        Assert.Equal(2000u, t.A.Combat.GetAttackTimer(WeaponAttackType.BaseAttack)); // the timer restarts when the swing is held

        uint heldAt = world.NowMs;
        while (t.Map.Combat.PendingSwings > 0 && world.NowMs - heldAt <= Window + 100)
        {
            Assert.Equal(1000u, t.V.Health); // untouched while it is held
            world.RunTick(50);
        }

        Assert.Equal(0, t.Map.Combat.PendingSwings); // released within one window (the clock is the world's)
        Assert.Equal(950u, t.V.Health);
    }

    [Fact]
    public void WithoutMeleeBatching_TheSwingLandsAtOnce()
    {
        var t = Duel(Window, batchMelee: false);
        using WorldRuntime world = t.World;
        t.Map.Combat.Attack(t.A, t.V);

        world.RunTick(50);

        Assert.Equal(0, t.Map.Combat.PendingSwings);
        Assert.Equal(950u, t.V.Health);
    }

    [Fact]
    public void TwoPlayersWhoKillEachOtherWithOneSwingInOneWindow_BothDie()
    {
        var t = Duel(Window);
        using WorldRuntime world = t.World;
        t.A.Health = 40;
        t.V.Health = 40;
        t.Map.Combat.Attack(t.A, t.V);
        t.Map.Combat.Attack(t.V, t.A);

        world.RunTick(50); // both swings are held in the same batch
        Assert.Equal(2, t.Map.Combat.PendingSwings);
        uint heldAt = world.NowMs;
        while (t.Map.Combat.PendingSwings > 0 && world.NowMs - heldAt <= Window + 100)
        {
            world.RunTick(50);
        }

        Assert.False(t.A.IsAlive);
        Assert.False(t.V.IsAlive);
    }

    [Fact]
    public void WithoutAWindow_TheFirstSwingKills_AndTheDeadPlayerNeverSwings()
    {
        var t = Duel(0);
        using WorldRuntime world = t.World;
        t.A.Health = 40;
        t.V.Health = 40;
        t.Map.Combat.Attack(t.A, t.V);
        t.Map.Combat.Attack(t.V, t.A);

        world.RunTick(50);

        Assert.True(t.A.IsAlive ^ t.V.IsAlive); // exactly one survives
    }
}

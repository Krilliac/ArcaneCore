using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>H1 outcome checks of map combat (subsystem combat).</summary>
public sealed class MapCombatAnomalyTests
{
    private static AnomalyCapture CaptureFor(Unit unit)
    {
        string subject = MapCombat.SubjectOf(unit);
        return AnomalyLedger.Shared.Capture(n => n.Subject == subject);
    }

    private static CombatTestUnit Spawn(Map map, bool regen = false)
    {
        var unit = new CombatTestUnit { RegeneratesHealth = regen };
        unit.Spawn(map, 10, 10);
        return unit;
    }

    /// <summary>Whole sweeps of one second each: a single huge frame is capped (a hang is one observation), so long spans are stepped like the real tick.</summary>
    private static void Advance(Map map, int totalMs)
    {
        for (int elapsed = 0; elapsed < totalMs; elapsed += 1000)
        {
            map.Combat.Update(1000);
        }
    }

    [Fact]
    public void HealthZeroButAlive_IsReportedOnceAfterTheBudget_NotBefore()
    {
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        CombatTestUnit unit = Spawn(map);
        using AnomalyCapture cap = CaptureFor(unit);
        unit.Health = 0;
        map.Combat.Update(1000);
        map.Combat.Update(1000);
        Assert.False(cap.Any("combat/death-not-processed"));
        map.Combat.Update(1000);
        map.Combat.Update(1000);
        Assert.Equal(1, cap.Count("combat/death-not-processed"));
    }

    [Fact]
    public void AHungWorldFrame_IsNotChargedAsTimeSpentWithTheStateWrong()
    {
        // A 12 s world-tick hang is one frame: a unit seen at health 0 for the first time in it has not been wrong for the 3 s budget.
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        CombatTestUnit unit = Spawn(map);
        using AnomalyCapture cap = CaptureFor(unit);
        unit.Health = 0;

        map.Combat.Update(12_000);

        Assert.False(cap.Any("combat/death-not-processed"), cap.Describe());
    }

    [Fact]
    public void ProperKill_AndAHealthRestoredInTime_AreSilent()
    {
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        CombatTestUnit dead = Spawn(map);
        CombatTestUnit healed = Spawn(map);
        using AnomalyCapture a = CaptureFor(dead);
        using AnomalyCapture b = CaptureFor(healed);
        map.Combat.Kill(null, dead);
        healed.Health = 0;
        map.Combat.Update(1000);
        healed.Health = 500;
        for (int i = 0; i < 6; i++) map.Combat.Update(1000);
        Assert.False(a.Any("combat/death-not-processed"), a.Describe());
        Assert.False(b.Any("combat/death-not-processed"), b.Describe());
    }

    [Fact]
    public void ThreatListHoldingADeadUnit_IsReported_AliveTargetIsNot()
    {
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        CombatTestUnit holder = Spawn(map);
        CombatTestUnit target = Spawn(map);
        CombatTestUnit other = Spawn(map);
        holder.Combat.Threat.AddThreat(target, 10);
        other.Combat.Threat.AddThreat(Spawn(map), 10);
        using AnomalyCapture cap = CaptureFor(holder);
        using AnomalyCapture quiet = CaptureFor(other);
        for (int i = 0; i < 5; i++) map.Combat.Update(1000);
        Assert.False(cap.Any("combat/stale-threat-entry"));
        target.Combat.DeathState = DeathState.Corpse;
        for (int i = 0; i < 5; i++) map.Combat.Update(1000);
        Assert.Equal(1, cap.Count("combat/stale-threat-entry"));
        Assert.False(quiet.Any("combat/stale-threat-entry"));
    }

    [Fact]
    public void CombatFlagWithNothingToFight_IsReported_ButNotWithAThreatList()
    {
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        CombatTestUnit stuck = Spawn(map);
        CombatTestUnit busy = Spawn(map);
        stuck.UnitFlags |= UnitFlags.InCombat;
        busy.UnitFlags |= UnitFlags.InCombat;
        busy.Combat.Threat.AddThreat(Spawn(map), 5);
        using AnomalyCapture s = CaptureFor(stuck);
        using AnomalyCapture b = CaptureFor(busy);
        Advance(map, 60_000);
        Assert.False(s.Any("combat/combat-never-left"));
        Advance(map, 60_000);
        Assert.Equal(1, s.Count("combat/combat-never-left"));
        Assert.False(b.Any("combat/combat-never-left"));
    }

    /// <summary>
    /// Live 2026-10-18: combat-never-left kept firing on player pets after the combat-timer fix, with nothing in the report to say what
    /// put them in combat. The report now names the last <see cref="MapCombat.SetInCombatState"/> caller, how often since the flag was
    /// cleared, and whether the combat timer can end it.
    /// </summary>
    [Fact]
    public void CombatNeverLeft_NamesWhatPutTheUnitInCombat()
    {
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        CombatTestUnit stuck = Spawn(map);
        map.Combat.SetInCombatState(stuck, 0);
        map.Combat.SetInCombatState(stuck, 0);
        using AnomalyCapture s = CaptureFor(stuck);
        Advance(map, 60_000);
        Advance(map, 60_000);
        string detail = Assert.Single(s.Notices).Detail;
        Assert.Contains($"set by {nameof(CombatNeverLeft_NamesWhatPutTheUnitInCombat)} x2", detail);
        Assert.Contains("pvp timer False", detail);
    }

    [Fact]
    public void PlayerInCombatOnlyWithAFarHolder_IsReported_NearHolderIsNot()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player far = CombatTestKit.AddPlayer(world, 9101, 0, 0, new FakeSession());
        Player near = CombatTestKit.AddPlayer(world, 9102, 0, 0, new FakeSession());
        var farHolder = new CombatTestUnit { RegeneratesHealth = false };
        farHolder.Spawn(map, 500, 0);
        var nearHolder = new CombatTestUnit { RegeneratesHealth = false };
        nearHolder.Spawn(map, 10, 0);
        farHolder.Combat.Threat.AddThreat(far, 1);
        nearHolder.Combat.Threat.AddThreat(near, 1);
        map.Combat.SetInCombatState(far, 0);
        map.Combat.SetInCombatState(near, 0);
        using AnomalyCapture f = CaptureFor(far);
        using AnomalyCapture n = CaptureFor(near);
        Advance(map, 60_000);
        Advance(map, 60_000);
        Assert.Equal(1, f.Count("combat/combat-never-left"));
        Assert.False(n.Any("combat/combat-never-left"));
    }

    [Fact]
    public void RegenStall_IsReported_NormalRegenAndDisabledRegenAreNot()
    {
        (_, Map map, _, _) = CombatTestKit.CreateWorld();
        CombatTestUnit stalled = Spawn(map, regen: true);
        CombatTestUnit healthy = Spawn(map, regen: true);
        CombatTestUnit noRegen = Spawn(map, regen: false);
        foreach (CombatTestUnit u in new[] { stalled, healthy, noRegen }) u.Health = 400;
        stalled.Combat.RegenTimer = int.MaxValue / 2;
        noRegen.Combat.RegenTimer = int.MaxValue / 2;
        using AnomalyCapture s = CaptureFor(stalled);
        using AnomalyCapture h = CaptureFor(healthy);
        using AnomalyCapture q = CaptureFor(noRegen);
        Advance(map, 50_000);
        Assert.Equal(1, s.Count("combat/regen-stalled"));
        Assert.False(h.Any("combat/regen-stalled"), h.Describe());
        Assert.False(q.Any("combat/regen-stalled"));
    }

    [Fact]
    public void HealedLifeStateInvariant_IsReported_HealthyPlayerIsNot()
    {
        (WorldRuntime world, Map map, _, _) = CombatTestKit.CreateWorld();
        Player bad = CombatTestKit.AddPlayer(world, 9111, 0, 0, new FakeSession());
        Player good = CombatTestKit.AddPlayer(world, 9112, 0, 0, new FakeSession());
        using AnomalyCapture b = CaptureFor(bad);
        using AnomalyCapture g = CaptureFor(good);
        bad.Combat.DeathState = DeathState.Dead;
        Assert.True(map.Combat.VerifyPlayerLifeInvariants(bad));
        Assert.False(map.Combat.VerifyPlayerLifeInvariants(good));
        Assert.True(b.Any("combat/invariant-healed-ghost-flag"), b.Describe());
        Assert.Empty(g.Notices);
    }
}

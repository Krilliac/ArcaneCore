using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>H1 outcome checks of <see cref="DeathOutcomeWatcher"/>: each failure is reported once under <c>death/*</c>, a correct death is silent.</summary>
public sealed class DeathAnomalyTests
{
    private static readonly string Stuck = ServerAnomalies.Key("death", "stuck-dying");
    private static readonly string Overdue = ServerAnomalies.Key("death", "release-overdue");
    private static readonly string NoCorpse = ServerAnomalies.Key("death", "corpse-missing");
    private static readonly string Repop = ServerAnomalies.Key("death", "repop-stuck");
    private static readonly string Durability = ServerAnomalies.Key("death", "durability-not-applied");

    private static readonly MapContent Content = new(
        [new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", "")],
        [], [], [], []);

    private sealed class Rig : IDisposable
    {
        public Rig(uint guid)
        {
            (World, _, _, TestCombatHooks hooks) = CombatTestKit.CreateWorld();
            WorldMaps.Of(World).Load(Content);
            World.GetMap(0).Combat.Hooks = hooks;
            Watcher = new DeathOutcomeWatcher(World);
            Session = new FakeSession((int)guid);
            Victim = CombatTestKit.AddPlayer(World, guid, 100, 100, Session);
            Wire(Victim.Inventory);
            Victim.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData { Guid = guid * 10, Entry = WornShortsword, Durability = 20 })]);
            Killer = new CombatTestUnit();
            Killer.Spawn(World.GetMap(0), 100, 101);
            Cap = ServerAnomalies.CaptureFor(Victim);
        }

        public WorldRuntime World { get; }

        public DeathOutcomeWatcher Watcher { get; }

        public FakeSession Session { get; }

        public Player Victim { get; }

        public CombatTestUnit Killer { get; }

        public AnomalyCapture Cap { get; }

        public MapCombat Combat => World.GetMap(0).Combat;

        public void Dispose()
        {
            Cap.Dispose();
            Watcher.Dispose();
            World.Dispose();
        }
    }

    [Fact]
    public void ADeathStillJustDied_ThirtySecondsLater_IsReportedOnce_ButNotBeforeThen()
    {
        using var r = new Rig(9201);
        r.Combat.Kill(r.Killer, r.Victim);
        Assert.Equal(1, r.Watcher.WatchedCount);

        r.Watcher.Sweep(r.World.NowMs + DeathOutcomeWatcher.StuckDyingBudgetMs - 1000);
        Assert.False(r.Cap.Any(Stuck));
        r.Watcher.Sweep(r.World.NowMs + DeathOutcomeWatcher.StuckDyingBudgetMs + 1000);
        r.Watcher.Sweep(r.World.NowMs + DeathOutcomeWatcher.StuckDyingBudgetMs + 2000);

        Assert.Equal(1, r.Cap.Count(Stuck));
    }

    [Fact]
    public void AnUnreleasedBody_PastTheAutoRelease_IsReported_AndAReleasedOneIsNot()
    {
        using var r = new Rig(9202);
        r.Combat.Kill(r.Killer, r.Victim);
        r.Combat.KillPlayer(r.Victim);

        r.Watcher.Sweep(r.World.NowMs + CombatConstants.CorpseRepopTimeMs);
        Assert.False(r.Cap.Any(Overdue));
        r.Watcher.Sweep(r.World.NowMs + CombatConstants.CorpseRepopTimeMs + DeathOutcomeWatcher.ReleaseGraceMs + 1000);
        Assert.Equal(1, r.Cap.Count(Overdue));
    }

    [Fact]
    public void AReleasedSpirit_WithItsCorpse_IsSilent_AndWithoutOne_IsReported()
    {
        using var r = new Rig(9203);
        r.Combat.Kill(r.Killer, r.Victim);
        r.Combat.KillPlayer(r.Victim);
        Assert.True(r.Combat.RepopPlayer(r.Victim));
        r.Victim.RepopDestinationResolved = true;

        r.Watcher.Sweep(r.World.NowMs);
        Assert.DoesNotContain(r.Cap.Notices, n => n.Key != Durability);

        r.Combat.RemoveResurrectedCorpse(r.Victim); // the body vanishes while the player is still a ghost (SpawnCorpseBones only acts on the alive)
        r.Watcher.Sweep(r.World.NowMs + 1000);
        r.Watcher.Sweep(r.World.NowMs + 2000);

        Assert.Equal(1, r.Cap.Count(NoCorpse));
    }

    [Fact]
    public void AGhostWhoseGraveyardTripNeverRan_IsReported_AfterTheBudget()
    {
        using var r = new Rig(9204);
        r.Combat.Kill(r.Killer, r.Victim);
        r.Combat.KillPlayer(r.Victim);
        Assert.True(r.Combat.RepopPlayer(r.Victim));
        Assert.False(r.Victim.RepopDestinationResolved);

        r.Watcher.Sweep(r.World.NowMs);
        r.Watcher.Sweep(r.World.NowMs + DeathOutcomeWatcher.RepopBudgetMs - 1000);
        Assert.False(r.Cap.Any(Repop));
        r.Watcher.Sweep(r.World.NowMs + DeathOutcomeWatcher.RepopBudgetMs + 1000);

        Assert.Equal(1, r.Cap.Count(Repop));
    }

    [Fact]
    public void AResurrectedPlayer_IsNoLongerWatched_AndNothingIsReported()
    {
        using var r = new Rig(9205);
        r.Combat.Kill(r.Killer, r.Victim);
        r.Combat.KillPlayer(r.Victim);
        r.Combat.RepopPlayer(r.Victim);
        r.Combat.ResurrectPlayer(r.Victim, 1.0f, applySickness: false);
        r.Combat.SpawnCorpseBones(r.Victim);

        r.Watcher.Sweep(r.World.NowMs + 600_000);

        Assert.Equal(0, r.Watcher.WatchedCount);
        Assert.DoesNotContain(r.Cap.Notices, n => n.Key != Durability);
    }

    [Fact]
    public void ACreatureKill_ThatLeavesEveryItemAtFullDurability_IsReported_AndOneThatWearsThemIsNot()
    {
        using (var worn = new Rig(9206))
        {
            worn.Combat.Kill(worn.Killer, worn.Victim);
            Assert.False(worn.Cap.Any(Durability));
        }

        using var r = new Rig(9207);
        Item sword = r.Victim.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!;
        Assert.Equal(sword.MaxDurability, sword.Durability);

        r.Combat.Kill(r.Killer, r.Victim, durabilityLoss: false); // the loss did not happen

        Assert.Equal(1, r.Cap.Count(Durability));
        Assert.Equal(AnomalySeverity.Info, Assert.Single(r.Cap.Notices, n => n.Key == Durability).Severity);

        using var channeled = new Rig(9208);
        channeled.Killer.SetUInt32(UpdateFields.UnitChannelSpell, 689);
        channeled.Killer.SetUInt64(UpdateFields.UnitFieldChannelObject, channeled.Victim.Guid.Value);
        channeled.Combat.Kill(channeled.Killer, channeled.Victim, durabilityLoss: false);
        Assert.False(channeled.Cap.Any(Durability));
    }
}

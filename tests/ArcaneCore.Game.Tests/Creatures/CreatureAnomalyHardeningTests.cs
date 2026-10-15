using System.Reflection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// The respawn-stuck checks judge only what the server could have spawned: a dormant respawn is overdue by design while its grid is unloaded,
/// and a corpse in an inactive cell does not respawn until the cell wakes. A dormant respawn on a LOADED grid that never spawns is reported.
/// </summary>
public sealed class CreatureAnomalyHardeningTests
{
    private const uint MissingTemplateEntry = 99_999;

    private static void InjectDormantRespawn(CreatureMapSystem system, uint spawnGuid, long respawnAtMs)
    {
        FieldInfo field = typeof(CreatureMapSystem).GetField("_respawnAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((Dictionary<uint, long>)field.GetValue(system)!)[spawnGuid] = respawnAtMs;
    }

    [Fact]
    public void ADormantRespawnOnALoadedGrid_ThatNeverSpawns_IsReported()
    {
        // Spawn 2 has no creature_template, so it is skipped at grid load: a dormant entry with its grid loaded and nothing standing on it.
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0), Spawn(2, MissingTemplateEntry, 31, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        using (AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "respawn/stuck" && n.Subject.Contains($"entry {MissingTemplateEntry} ")))
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            InjectDormantRespawn(system, 2, system.ClockMs + 10);

            for (int i = 0; i < 20; i++) world.RunTick(5_000);

            Assert.True(capture.Any("respawn/stuck"), "an overdue dormant respawn on a loaded grid was not reported");
        }
    }

    [Fact]
    public void ADormantRespawnOnAnUnloadedGrid_IsOverdueByDesign_AndNeverReported()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 1 };
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0, respawnSeconds: 600)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content, options);
        using (world)
        using (AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "respawn/stuck" && n.Subject.EndsWith("spawn 1", StringComparison.Ordinal)))
        {
            map.Grids.Options.GridCleanUpDelayMs = 60_000;
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            world.AddPlayer(player);
            world.RunTick(50);
            system.KillCreature(Assert.Single(system.Creatures));
            long respawnAt = system.PendingRespawnAt(1) ?? system.ClockMs + 600_000;

            player.Relocate(5000, 5000, 83.5f, 0, 0);
            world.RunTick(6000);
            world.RunTick(1);
            world.RunTick(60_000);
            Assert.False(system.IsGridLoaded(30, 0));
            Assert.NotNull(system.PendingRespawnAt(1));

            while (system.ClockMs < respawnAt + 5 * 60_000) world.RunTick(30_000);

            Assert.True(system.ClockMs > system.PendingRespawnAt(1)!.Value + 60_000);
            Assert.False(capture.Any("respawn/stuck"), capture.Describe());
        }
    }

    [Fact]
    public void ACorpseInAnInactiveCell_ThatIsPastItsRespawnTime_IsNotReportedStuck()
    {
        var options = new CreatureOptions { CorpseDecayNormalSeconds = 600 };
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0, respawnSeconds: 10)]);
        (WorldRuntime world, Map map, CreatureMapSystem system) = CreateSystem(content, options);
        using (world)
        using (AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "respawn/stuck" && n.Subject.EndsWith("spawn 1", StringComparison.Ordinal)))
        {
            Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
            world.AddPlayer(player);
            world.RunTick(50);
            Creature wolf = Assert.Single(system.Creatures);
            system.KillCreature(wolf);
            player.Relocate(-240, 0, 83.5f, 0, 0); // same grid, 260 yards away: the corpse's cell is inactive
            for (int i = 0; i < 150; i++) world.RunTick(1_000);

            Assert.True(map.UpdatesActiveCellsOnly);
            Assert.True(system.ClockMs > wolf.RespawnAtMs + 60_000);
            Assert.False(capture.Any("respawn/stuck"), capture.Describe());
        }
    }

    [Fact]
    public void EvadeWithoutHomeGenerator_IsReported_ButNotHealedByTheCheck_AndAHealthyCreatureIsSilent()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 20, 0), Spawn(2, WolfEntry, 22, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        using (AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "creature-script/boss-state-inconsistent"))
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            Creature broken = system.Creatures.Single(c => c.Spawn!.Guid == 1);
            world.RunTick(100);
            Assert.False(capture.Any("creature-script/boss-state-inconsistent"), capture.Describe());

            broken.IsEvading = true;
            world.RunTick(100);

            Assert.True(capture.Any("creature-script/boss-state-inconsistent"), capture.Describe());
            Assert.Single(capture.Notices, n => n.Subject.EndsWith("spawn 1", StringComparison.Ordinal));
            Assert.True(broken.IsEvading, "the check must report only; it must not clear the flag");
        }
    }
}

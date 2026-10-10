using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.BlackwingLair;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>mangos-classic blackwing_lair.cpp go_ai_suppression: fume cadence and the rearm window before and after Lashlayer.</summary>
public sealed class BlackwingSuppressionDeviceTests
{
    private static DungeonScriptHarness Run() => new(map => new BlackwingLairInstance(map), [], [],
        (SuppressionDeviceAI.Entry, GameObjectType.Trap));

    [Fact]
    public void A_used_device_rearms_within_thirty_seconds_to_two_minutes_while_Lashlayer_lives()
    {
        using DungeonScriptHarness run = Run();
        GameObject device = run.Object(SuppressionDeviceAI.Entry);
        run.Tick();
        Assert.True(run.Objects.DespawnForRespawn(device));
        run.Tick();
        run.Tick();
        Assert.False(device.IsSpawned);
        long delay = device.RespawnAtMs - run.Objects.ClockMs;
        Assert.InRange(delay, (SuppressionDeviceAI.RearmMinSeconds * 1000L) - 200, SuppressionDeviceAI.RearmMaxSeconds * 1000L);
        for (int i = 0; i < 125; i++) run.Tick(1000);
        Assert.True(device.IsSpawned);
    }

    [Fact]
    public void After_Lashlayer_dies_a_used_device_stays_down_for_the_instance_lifetime()
    {
        using DungeonScriptHarness run = Run();
        GameObject device = run.Object(SuppressionDeviceAI.Entry);
        run.Tick();
        run.Data.SetData(2, EncounterState.Done);
        Assert.True(run.Objects.DespawnForRespawn(device));
        run.Tick();
        run.Tick();
        Assert.False(device.IsSpawned);
        Assert.True(device.RespawnAtMs - run.Objects.ClockMs > (SuppressionDeviceAI.DeadRearmSeconds - 1) * 1000L);
    }

    [Fact]
    public void A_ready_device_plays_its_fumes_every_five_seconds()
    {
        using DungeonScriptHarness run = Run();
        var session = Assert.IsType<FakeSession>(run.Player.Session);
        run.Tick(SuppressionDeviceAI.FumeIntervalMs + 100); // the random first fume (0-5 s) is due
        // Packets drains what the session got so far, so each count is of the interval since the last read.
        Assert.NotEmpty(GameObjectTestKit.Packets(session, WorldOpcode.SmsgGameobjectCustomAnim));
        run.Tick(SuppressionDeviceAI.FumeIntervalMs - 1000);
        Assert.Empty(GameObjectTestKit.Packets(session, WorldOpcode.SmsgGameobjectCustomAnim));
        run.Tick(1000);
        Assert.Single(GameObjectTestKit.Packets(session, WorldOpcode.SmsgGameobjectCustomAnim));
    }
}

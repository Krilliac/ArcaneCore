using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// An evading creature that is feared or confused keeps its fear / confuse generator on top (MotionMaster.SyncCrowdControl re-pushes it over
/// Home on purpose), so <c>creature-script/boss-state-inconsistent</c> must not call that stack inconsistent. Evading with no Home and no
/// crowd-control generator still is.
/// </summary>
public sealed class EvadeCrowdControlConsistencyTests
{
    private const string Key = "creature-script/boss-state-inconsistent";

    private static (WorldRuntime World, CreatureMapSystem System, Creature Wolf) Start(uint spawn)
    {
        CreatureContent content = Content([Template()], [Spawn(spawn, WolfEntry, 20, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
        world.RunTick(50);
        Creature wolf = system.Creatures.Single(c => c.Spawn!.Guid == spawn);
        world.RunTick(100);
        return (world, system, wolf);
    }

    private static AnomalyCapture CaptureFor(uint spawn)
        => AnomalyLedger.Shared.Capture(n => n.Key == Key && n.Subject.EndsWith($"spawn {spawn}", StringComparison.Ordinal));

    [Theory]
    [InlineData(UnitFlags.Fleeing, true)]
    [InlineData(UnitFlags.Confused, false)]
    public void AnEvadingCreature_UnderFearOrConfusion_IsConsistent_AndSilent(UnitFlags flag, bool fear)
    {
        const uint spawn = 61;
        (WorldRuntime world, _, Creature wolf) = Start(spawn);
        using (world)
        using (AnomalyCapture capture = CaptureFor(spawn))
        {
            wolf.IsEvading = true;
            wolf.UnitFlags |= flag;
            world.RunTick(100);
            world.RunTick(100);

            Assert.Equal(fear ? CrowdControlMovement.Fear : CrowdControlMovement.Confuse, wolf.Motion.ActiveCrowdControl);
            Assert.False(capture.Any(Key), capture.Describe());
        }
    }

    [Fact]
    public void AnEvadingCreature_WithoutHomeOrCrowdControl_StillFires()
    {
        const uint spawn = 62;
        (WorldRuntime world, _, Creature wolf) = Start(spawn);
        using (world)
        using (AnomalyCapture capture = CaptureFor(spawn))
        {
            wolf.IsEvading = true;
            world.RunTick(100);

            Assert.True(capture.Any(Key), capture.Describe());
        }
    }
}

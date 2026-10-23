using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Non-finite creature coordinates (hardening wave H1). One declared fix: <see cref="Creature.StartSpline(IReadOnlyList{Vector3}, bool, SplineFacing, uint, long)"/>
/// never stores a NaN point, because a spline's points are written into SMSG_MONSTER_MOVE for every viewer. Everything else is report-only:
/// a creature whose coordinates are not finite is reported, not moved home, and a path request to a non-finite destination is reported, not refused.
/// </summary>
public sealed class CreatureNonFiniteReportTests
{
    private static Creature MakeCreature(uint counter)
    {
        var tpl = Template();
        var spawn = Spawn(counter, WolfEntry, 0f, 0f, 0f);
        return new Creature(counter, tpl, spawn, Content([tpl], [spawn]), new Random((int)counter));
    }

    private static AnomalyCapture CaptureFor(Creature creature)
        => AnomalyLedger.Shared.Capture(n => n.Key == "creature/non-finite" && n.Subject == CreatureAnomalies.Subject(creature));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASplineThroughANonFinitePoint_IsStoredFinite_AndReported(bool nanFirst)
    {
        Creature wolf = MakeCreature(501);
        using AnomalyCapture capture = CaptureFor(wolf);
        Vector3[] path = nanFirst ? [new(float.NaN, 5f, 0f), new(10f, 10f, 0f)] : [new(5f, 5f, 0f), new(10f, float.PositiveInfinity, 0f)];

        CreatureSpline spline = wolf.StartSpline(path, run: true, SplineFacing.None, splineId: 1, clockMs: 0);

        Assert.True(capture.Any("creature/non-finite"), capture.Describe());
        Assert.True(float.IsFinite(spline.EndX) && float.IsFinite(spline.EndY) && float.IsFinite(spline.EndZ), "the spline end is not finite");
        Assert.All(spline.Path, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z), $"stored point {p}"));
        Assert.True(spline.DurationMs >= 1);
    }

    [Fact]
    public void ASplineWithANonFiniteFinalAngle_DropsTheFacing_AndIsReported()
    {
        Creature wolf = MakeCreature(502);
        using AnomalyCapture capture = CaptureFor(wolf);

        CreatureSpline spline = wolf.StartSpline([new Vector3(10f, 0f, 0f)], run: false, SplineFacing.ToAngle(float.NaN), splineId: 1, clockMs: 0);

        Assert.True(capture.Any("creature/non-finite"), capture.Describe());
        Assert.True(spline.FinalOrientation is null || float.IsFinite(spline.FinalOrientation.Value));
    }

    [Fact]
    public void AFiniteSpline_IsSilent_AndKeepsItsPoints()
    {
        Creature wolf = MakeCreature(503);
        using AnomalyCapture capture = CaptureFor(wolf);
        Vector3[] path = [new(5f, 5f, 0f), new(10f, 10f, 0f)];

        CreatureSpline spline = wolf.StartSpline(path, run: true, SplineFacing.None, splineId: 1, clockMs: 0);

        Assert.False(capture.Any("creature/non-finite"), capture.Describe());
        Assert.Equal(path, spline.Path);
        Assert.Equal(10f, spline.EndX);
    }

    [Fact]
    public void ACreatureWithNonFiniteCoordinates_IsReported_AndNotMovedHome()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(ArcaneCore.Game.Tests.TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            Creature wolf = Assert.Single(system.Creatures);
            using AnomalyCapture capture = CaptureFor(wolf);
            float homeZ = wolf.Home.Z;
            wolf.Z = float.NaN;

            system.CheckNonFinitePosition(wolf);

            Assert.True(capture.Any("creature/non-finite"), capture.Describe());
            Assert.True(float.IsNaN(wolf.Z), "the check moved the creature; it must only report");
            Assert.True(float.IsFinite(homeZ));
        }
    }

    [Fact]
    public void ARejectedNonFiniteRelocation_IsReported_AndAFiniteCreatureIsSilent()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(ArcaneCore.Game.Tests.TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            Creature wolf = Assert.Single(system.Creatures);
            using AnomalyCapture capture = CaptureFor(wolf);

            system.CheckNonFinitePosition(wolf);
            Assert.False(capture.Any("creature/non-finite"), capture.Describe());

            wolf.SetPosition(float.NaN, 0f, 0f, 0f);
            system.CheckNonFinitePosition(wolf);

            Assert.True(capture.Any("creature/non-finite"), capture.Describe());
            Assert.True(float.IsFinite(wolf.X), "a rejected relocation must leave the position as it was");
        }
    }

    [Fact]
    public void APathToANonFiniteDestination_IsReported_NotRefusedBeforeThePlanner()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(ArcaneCore.Game.Tests.TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            Creature wolf = Assert.Single(system.Creatures);
            using AnomalyCapture capture = CaptureFor(wolf);

            PlannedPath planned = system.PlanPath(wolf, new Vector3(float.NaN, 0f, 0f));
            Assert.NotEqual(PathOutcome.NoPath, planned.Outcome);
            Assert.True(capture.Any("creature/non-finite"), capture.Describe());

            using AnomalyCapture silent = CaptureFor(wolf);
            ClearEpisode(wolf);
            _ = system.PlanPath(wolf, new Vector3(35f, 0f, 83.5f));
            Assert.False(silent.Any("creature/non-finite"), silent.Describe());
        }
    }

    private static void ClearEpisode(Creature wolf) => wolf.ClearNonFiniteEpisode();
}

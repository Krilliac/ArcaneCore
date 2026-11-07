using System.Numerics;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The rule the three progress clocks share (<see cref="PlayerbotProgressAim"/>): the aim is the goal or the landing of the lift the
/// walk heads for, a change of aim is reported once, and past <see cref="PlayerbotProgressAim.MaxChanges"/> on one goal a walk to a lift
/// no longer moves the aim.
/// </summary>
public sealed class PlayerbotProgressAimTests
{
    private static readonly Vector3 LandingA = new(-4700f, -1800f, -44f), LandingB = new(-4680f, -1850f, -44f);

    private static PlayerbotRoute LiftRoute(Vector3 landing)
        => new([new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f)], 1f, [true]) { ToLiftLanding = true, LiftLanding = landing };

    private static PlayerbotRoute OrdinaryRoute() => new([new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f)], 1f, [true]);

    [Fact]
    public void AChangeOfAim_IsReportedOnce_AndAnotherLandingIsAnotherAim()
    {
        var aim = new PlayerbotProgressAim();
        Assert.False(aim.Retarget(null));
        Assert.Null(aim.Landing);
        Assert.Equal(0, aim.Changes);

        Assert.True(aim.Retarget(LiftRoute(LandingA)));
        Assert.Equal(LandingA, aim.Landing);
        Assert.Equal(1, aim.Changes);
        Assert.False(aim.Retarget(LiftRoute(LandingA)));

        Assert.True(aim.Retarget(LiftRoute(LandingB)));
        Assert.Equal(LandingB, aim.Landing);
        Assert.Equal(2, aim.Changes);

        Assert.True(aim.Retarget(OrdinaryRoute()));
        Assert.Null(aim.Landing);
        Assert.Equal(3, aim.Changes);
        Assert.False(aim.Retarget(null));
    }

    [Fact]
    public void PastTheBound_AWalkToALiftIsMeasuredToTheGoal_AndResetStartsOver()
    {
        var aim = new PlayerbotProgressAim();
        for (int i = 0; i < 2; i++)
        {
            Assert.True(aim.Retarget(LiftRoute(LandingA)));
            Assert.True(aim.Retarget(OrdinaryRoute()));
        }

        Assert.True(aim.Retarget(LiftRoute(LandingA)));
        Assert.True(aim.Retarget(LiftRoute(LandingB)));
        Assert.Equal(PlayerbotProgressAim.MaxChanges, aim.Changes);
        Assert.Equal(LandingB, aim.Landing);

        // The aim was a landing when the bound was reached: the move back to the goal is the one change still due, then none.
        Assert.True(aim.Retarget(LiftRoute(LandingA)));
        Assert.Null(aim.Landing);
        Assert.False(aim.Retarget(LiftRoute(LandingA)));
        Assert.False(aim.Retarget(LiftRoute(LandingB)));
        Assert.Null(aim.Landing);

        aim.Reset();
        Assert.Equal(0, aim.Changes);
        Assert.Null(aim.Landing);
        Assert.True(aim.Retarget(LiftRoute(LandingA)));
        Assert.Equal(LandingA, aim.Landing);
    }

    [Fact]
    public void AnAimAtTheBound_ThatWasTheGoal_StaysTheGoal()
    {
        var aim = new PlayerbotProgressAim();
        for (int i = 0; i < PlayerbotProgressAim.MaxChanges / 2; i++)
        {
            Assert.True(aim.Retarget(LiftRoute(LandingA)));
            Assert.True(aim.Retarget(OrdinaryRoute()));
        }

        Assert.Equal(PlayerbotProgressAim.MaxChanges, aim.Changes);
        Assert.Null(aim.Landing);
        Assert.False(aim.Retarget(LiftRoute(LandingA)));
        Assert.Null(aim.Landing);
        Assert.Equal(PlayerbotProgressAim.MaxChanges, aim.Changes);
    }
}

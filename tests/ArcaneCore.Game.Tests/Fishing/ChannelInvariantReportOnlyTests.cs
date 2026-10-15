using ArcaneCore.Protocol;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Game.Tests.Fishing;

/// <summary>
/// The spell-state invariant sweep reports a channel whose bookkeeping is wrong but never cancels it, and it does not mistake a channel
/// object that is not a unit (a fishing bobber, a ground effect, a Far Sight eye) for a missing target.
/// </summary>
public sealed class ChannelInvariantReportOnlyTests
{
    private static bool IsChannelReport(AnomalyNotice n) => n.Key is "spell/channel-orphaned" or "spell/cast-stuck";

    [Fact]
    public void AFishingChannel_WhoseObjectIsABobber_StaysCasting_AndReportsNothing()
    {
        using var rig = new FishingRig();
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(IsChannelReport);

        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        Assert.NotNull(rig.Bobber);
        rig.Step(500);

        Assert.Equal(SpellCastState.Casting, rig.Kit.System.GetState(rig.Player.Guid)?.CurrentCast?.State);
        Assert.False(capture.Any("spell/channel-orphaned"), capture.Describe());
        Assert.False(capture.Any("spell/cast-stuck"), capture.Describe());
    }

    [Fact]
    public void AChannelWhoseSpellFieldWasWiped_IsReported_ButNotCancelled()
    {
        using var rig = new FishingRig();
        Assert.Equal(SpellCastResult.CastOk, rig.Cast());
        using AnomalyCapture capture = AnomalyLedger.Shared.Capture(IsChannelReport);

        rig.Player.SetUInt32(UpdateFields.UnitChannelSpell, 0);
        rig.Step(100);

        Assert.True(capture.Any("spell/channel-orphaned"), capture.Describe());
        Assert.Equal(SpellCastState.Casting, rig.Kit.System.GetState(rig.Player.Guid)?.CurrentCast?.State);
    }
}

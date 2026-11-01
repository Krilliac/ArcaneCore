using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>H1 outcome checks of instances (subsystem instance).</summary>
public sealed class InstanceAnomalyTests
{
    private static AnomalyCapture CaptureSave(uint id)
        => AnomalyLedger.Shared.Capture(n => n.Subsystem == "instance" && n.Subject == $"instance {id} of map {Dungeon}");

    [Fact]
    public void NormalEntry_ReportsNothing_ButARefusedEntryIsCounted()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(8001);
        string subject = $"player (guid {a.Guid.Low})";
        using AnomalyCapture cap = AnomalyLedger.Shared.Capture(n => n.Subsystem == "instance" && n.Subject == subject);
        using AnomalyCapture saves = AnomalyLedger.Shared.Capture(n => n.Subsystem == "instance" && n.Subject.StartsWith("instance "));
        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        Assert.Empty(cap.Notices);
        Assert.DoesNotContain(saves.Notices, n => n.Subject == $"instance {id} of map {Dungeon}");

        Player b = f.AddPlayer(8002);
        string raidSubject = $"player (guid {b.Guid.Low})";
        using AnomalyCapture refused = AnomalyLedger.Shared.Capture(n => n.Subject == raidSubject);
        Assert.False(f.EnterRaid(b)); // no raid group
        Assert.True(refused.Any("instance/enter-refused"), refused.Describe());
    }

    [Fact]
    public void SaveResetWithAPlayerStillInside_IsReported_EmptyResetIsNot()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(8011);
        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        using AnomalyCapture cap = CaptureSave(id);
        InstanceSave save = f.Manager.FindSave(id)!;
        f.Manager.ResetSaveForTest(save);
        Assert.True(cap.Any("instance/reset-with-players-inside"), cap.Describe());

        Player b = f.AddPlayer(8012);
        Assert.True(f.EnterDungeon(b));
        uint id2 = b.Map!.InstanceId;
        Assert.True(f.LeaveToContinent(b));
        using AnomalyCapture quiet = CaptureSave(id2);
        f.Manager.ResetSaveForTest(f.Manager.FindSave(id2)!);
        Assert.False(quiet.Any("instance/reset-with-players-inside"));
    }

    [Fact]
    public void BindToADeletedSave_IsReported_BindToALiveSaveIsNot()
    {
        using var f = new InstanceFixture();
        Player a = f.AddPlayer(8021);
        Assert.True(f.EnterDungeon(a));
        uint id = a.Map!.InstanceId;
        InstanceSave save = f.Manager.FindSave(id)!;
        using AnomalyCapture cap = CaptureSave(id);
        f.Manager.BindForTest(a.Guid, save);
        Assert.False(cap.Any("instance/bind-without-save"));
        save.IsDeleted = true;
        f.Manager.BindForTest(a.Guid, save);
        Assert.True(cap.Any("instance/bind-without-save"), cap.Describe());
    }
}

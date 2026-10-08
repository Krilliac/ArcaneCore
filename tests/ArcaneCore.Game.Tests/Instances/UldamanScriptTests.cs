using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Uldaman;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class UldamanScriptTests
{
    [Fact]
    public void StoneKeeperDeath_CompletesAltar_AndArchaedasHasAwakeningAi()
    {
        using DungeonScriptHarness run = new(map => new UldamanInstance(map),
            [UldamanInstance.Archaedas, UldamanInstance.StoneKeeper],
            [UldamanInstance.Archaedas, UldamanInstance.StoneKeeper]);
        var data = Assert.IsType<UldamanInstance>(run.Data);
        Assert.IsType<ArchaedasAi>(run.Creature(UldamanInstance.Archaedas).AI);
        data.StartEvent(UldamanInstance.AltarKeeperEvent, run.Player);
        Assert.Equal(EncounterState.InProgress, data.GetData(UldamanInstance.TypeAltar));
        Assert.Equal(run.Player.Guid.Value, data.GetData64(UldamanInstance.DataEventStarter));
        run.Kill(UldamanInstance.StoneKeeper);
        Assert.Equal(EncounterState.Done, data.GetData(UldamanInstance.TypeAltar));
        Assert.Equal("3 0", run.SavedData);
    }

    [Fact]
    public void ArchaedasAltarEvent_AdvancesAwakeningOnMapTicks()
    {
        using DungeonScriptHarness run = new(map => new UldamanInstance(map),
            [UldamanInstance.Archaedas], [UldamanInstance.Archaedas]);
        var data = Assert.IsType<UldamanInstance>(run.Data);
        data.StartEvent(UldamanInstance.AltarArchaedasEvent, run.Player);
        Assert.Equal(EncounterState.Special, data.GetData(UldamanInstance.TypeArchaedas));
        run.Tick(1000);
        run.Tick(2000);
        run.Tick(3000);
        Assert.Equal(EncounterState.InProgress, data.GetData(UldamanInstance.TypeArchaedas));
    }
}

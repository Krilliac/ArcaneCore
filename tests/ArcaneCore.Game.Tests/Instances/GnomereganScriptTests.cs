using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Instances;

public sealed class GnomereganScriptTests
{
    [Fact]
    public void GrubbisAndThermaplugg_KeepSeparateStates_AndBombFacesResetAfterFailure()
    {
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map),
            [7998, 7800, 7850], [7998, 7800, 7850]);
        var data = Assert.IsType<GnomereganInstance>(run.Data);
        Assert.IsType<EmiShortfuseAi>(run.Creature(7998).AI);
        Assert.IsType<ThermapluggAi>(run.Creature(7800).AI);
        Assert.IsType<KernobeeAi>(run.Creature(7850).AI);

        data.SetData(GnomereganInstance.TypeGrubbis, EncounterState.Done);
        data.SetData(GnomereganInstance.TypeThermaplugg, EncounterState.InProgress);
        Assert.True(data.FaceActive(2));
        data.SetData(GnomereganInstance.TypeThermaplugg, EncounterState.Fail);
        Assert.False(data.FaceActive(2));
        Assert.Equal("3 0", run.SavedData); // only DONE persisted, before Thermaplugg began
        Assert.Equal(EncounterState.Fail, data.GetData(GnomereganInstance.TypeThermaplugg));
    }

    [Fact]
    public void EmiGossip_OffersTheClassicDbLine_AndStartsTheEscortEvent()
    {
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map), [7998], [7998]);
        var emi = run.Creature(7998);
        var npc = new NpcInfo(emi.Guid, emi.Template.Entry, emi.Spawn!.Guid, NpcFlags.Gossip,
            run.Map.MapId, emi.X, emi.Y, emi.Z, emi.BoundingRadius, true, false, false, false, 0);
        var gossip = new EmiGossipScript();
        ScriptedGossipMenu menu = Assert.IsType<ScriptedGossipMenu>(gossip.Hello(run.Player, npc));
        Assert.Equal("I am ready to begin.", Assert.Single(menu.Items).Text);
        Assert.True(gossip.SelectReply(run.Player, npc, 1, EmiGossipScript.StartAction).Close);
        Assert.Equal(EncounterState.InProgress, run.Data.GetData(GnomereganInstance.TypeGrubbis));
    }

    [Fact]
    public void Thermaplugg_AggroAndDeath_LockThenFinishTheEncounter()
    {
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map), [7800], [7800]);
        var data = Assert.IsType<GnomereganInstance>(run.Data);
        Assert.True(run.Creatures.AttackStart(run.Creature(7800), run.Player));
        Assert.Equal(EncounterState.InProgress, data.GetData(GnomereganInstance.TypeThermaplugg));
        Assert.True(data.FaceActive(2));
        run.Kill(7800);
        Assert.Equal(EncounterState.Done, data.GetData(GnomereganInstance.TypeThermaplugg));
        Assert.False(data.FaceActive(2));
        Assert.Equal("0 3", run.SavedData);
    }
}

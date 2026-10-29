using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Instances.Scripts.Gnomeregan;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
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
    public void Thermaplugg_ActiveBombFace_SummonsAWalkingBombThreeSecondsAfterItsActivation()
    {
        // instance_gnomeregan.cpp DoActivateBombFace sets m_uiBombTimer = 3000; boss_thermaplugg.cpp UpdateAI summons NPC_WALKING_BOMB
        // when that timer runs out (`m_uiBombTimer < uiDiff`). The map ticks in 50 ms steps, a divisor of 3000.
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map),
            [ThermapluggAi.Entry, ThermapluggAi.WalkingBomb], [ThermapluggAi.Entry],
            (GnomereganInstance.FaceEntries[2], ArcaneCore.Game.GameObjects.GameObjectType.Door));
        var data = Assert.IsType<GnomereganInstance>(run.Data);
        Assert.True(run.Creatures.AttackStart(run.Creature(ThermapluggAi.Entry), run.Player));
        Assert.True(data.FaceActive(2));

        for (int i = 0; i < 58; i++) run.Tick(50); // 2.9 s
        Assert.DoesNotContain(run.Creatures.Creatures, c => c.Template.Entry == ThermapluggAi.WalkingBomb);
        for (int i = 0; i < 4; i++) run.Tick(50); // 3.1 s
        Assert.Contains(run.Creatures.Creatures, c => c.Template.Entry == ThermapluggAi.WalkingBomb);
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
    [Fact]
    public void ActivateBomb_TheSpellsDummyActivatesARandomFace()
    {
        // Spell.dbc 5875 11511 Activate Bomb A: effect 0 DUMMY at TARGET_LOCATION_DATABASE (17). vmangos
        // EffectDummyCreature_spell_boss_thermaplugg: DoActivateBombFace(urand(0, MAX_GNOME_FACES - 1)).
        SpellInfo bombA = SpellTestKit.Spell(ThermapluggAi.ActivateBombA,
            SpellTestKit.Effect(SpellEffectName.Dummy, 1, SpellImplicitTarget.LocationDatabase));
        var spells = new SpellSystem(new SpellStore([bombA], [], []), () => 10000);
        SpellScriptDispatcher.Install(spells, new SpellScriptRegistry([new ThermapluggActivateBombScript()]));
        using DungeonScriptHarness run = new(map => new GnomereganInstance(map), [ThermapluggAi.Entry], [ThermapluggAi.Entry],
            (RelayScriptCatalog?)null, new CreatureAiServices { Spells = new SpellSystemCreatureCaster(spells) });
        var data = Assert.IsType<GnomereganInstance>(run.Data);
        Assert.Equal(0, Enumerable.Range(0, 6).Count(data.FaceActive));

        Assert.Equal(SpellCastResult.CastOk,
            spells.CastSpell(run.Creature(ThermapluggAi.Entry), ThermapluggAi.ActivateBombA, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(1, Enumerable.Range(0, 6).Count(data.FaceActive));
    }
}

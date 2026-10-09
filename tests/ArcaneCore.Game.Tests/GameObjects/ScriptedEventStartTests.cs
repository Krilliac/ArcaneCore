using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTypeRig;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// cmangos StartEvents_Event (DBScripts/ScriptMgr.cpp:3445-3482): the ScriptDev2 handler bound in scripted_event_id answers before the
/// dbscripts_on_event rows, and a started event is unique by its unit or game-object source. Every event here has one delayed kill-credit
/// step, so a started run shows as a pending step.
/// </summary>
public sealed class ScriptedEventStartTests
{
    private static (GameObjectTypeRig Rig, CreatureMapSystem Creatures) Create(params GameObjectSpawn[] spawns)
    {
        GameObjectTypeRig rig = GameObjectTypeRig.Create(spawns);
        uint[] events = [ChestEvent, ScriptedEvents.PurifyFood, ScriptedEvents.RazorgorePossess, ScriptedEvents.SummonJeevee,
            ScriptedEvents.SummonDreadsteed, ScriptedEvents.GluthDecimate];
        var ai = new CreatureAiContent([], [])
        {
            DbScripts = new DbScriptCatalog([.. events.Select(id => (DbScriptKind.Event,
                new RelayScriptStep(id, 5000, 0, 8, 2044, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)))]),
        };
        var creatures = new CreatureMapSystem(rig.Map, new CreatureContent([Template(ScriptedEvents.Gluth)], [], [], [], [], ai));
        rig.Map.AddUpdater(creatures);
        return (rig, creatures);
    }

    [Fact]
    public void UnportedScriptDev2Handlers_LetTheDbScriptRunOnlyWhereCmangosDoes()
    {
        (GameObjectTypeRig rig, CreatureMapSystem creatures) = Create(GoSpawn(1, EventGoober, 3, 0));
        (Player player, _) = rig.Join(1);
        GameObject bonfire = rig.Single(EventGoober);

        // event_razorgore_possess always returns true; event_spells_warlock_dreadsteed needs a Dreadsteed ritual state the port lacks.
        Assert.Equal(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, ScriptedEvents.RazorgorePossess, player, null));
        Assert.Equal(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, ScriptedEvents.SummonJeevee, player, null));
        Assert.Equal(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, ScriptedEvents.SummonDreadsteed, player, bonfire));
        // event_purify_food: only a player at a game object reaches the DB script.
        Assert.Equal(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, ScriptedEvents.PurifyFood, player, null));
        Assert.Equal(0, creatures.PendingDbScriptSteps);
        Assert.Equal(ScriptedEventResult.Started, ScriptedEvents.Start(rig.Map, ScriptedEvents.PurifyFood, player, bonfire));
        Assert.Equal(1, creatures.PendingDbScriptSteps);

        // event_naxxramas (Decimate): true without Gluth in the instance, false with him.
        Assert.Equal(ScriptedEventResult.Handled, ScriptedEvents.Start(rig.Map, ScriptedEvents.GluthDecimate, player, null));
        Creature gluth = creatures.SpawnTemporary(Template(ScriptedEvents.Gluth), 1, 1, 0, 0);
        Assert.Equal(ScriptedEventResult.Started, ScriptedEvents.Start(rig.Map, ScriptedEvents.GluthDecimate, gluth, null));
        Assert.Equal(2, creatures.PendingDbScriptSteps);

        Assert.Equal(ScriptedEventResult.NoScript, ScriptedEvents.Start(rig.Map, 9999, player, null));
    }

    [Fact]
    public void GooberEvent_RunsOncePerPlayer_AndEachPlayerStartsItsOwn()
    {
        // A player is a unit, so the run is SCRIPT_EXEC_PARAM_UNIQUE_BY_SOURCE (TYPEMASK_CREATURE_OR_GAMEOBJECT includes TYPEMASK_UNIT):
        // the same player at a second object with the same event is the same run; another player's use is a run of its own.
        (GameObjectTypeRig rig, CreatureMapSystem creatures) = Create(GoSpawn(1, EventGoober, 3, 0), GoSpawn(2, EventGoober, 0, 3));
        (Player first, _) = rig.Join(1);
        (Player second, _) = rig.Join(2);
        GameObject[] goobers = [.. rig.System.GameObjects.Where(g => g.Entry == EventGoober)];

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(first, goobers[0].Guid));
        Assert.Equal(1, creatures.PendingDbScriptSteps);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(first, goobers[0].Guid));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(first, goobers[1].Guid));
        Assert.Equal(1, creatures.PendingDbScriptSteps);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(second, goobers[0].Guid));
        Assert.Equal(2, creatures.PendingDbScriptSteps);
    }

    [Fact]
    public void GooberInUse_RefusesAnotherUse_BeforeItsEventStarts()
    {
        (GameObjectTypeRig rig, CreatureMapSystem creatures) = Create(GoSpawn(1, InUseEventGoober, 3, 0));
        (Player first, _) = rig.Join(1);
        (Player second, _) = rig.Join(2);
        GameObject goober = rig.Single(InUseEventGoober);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(first, goober.Guid));
        Assert.Equal(1, creatures.PendingDbScriptSteps);
        Assert.Equal(GameObjectUseResult.InUse, rig.System.Use(second, goober.Guid));
        Assert.Equal(1, creatures.PendingDbScriptSteps);
    }
}

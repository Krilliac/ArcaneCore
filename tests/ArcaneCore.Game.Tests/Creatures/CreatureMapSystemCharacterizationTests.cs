using System.Reflection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectUseRig;

namespace ArcaneCore.Game.Tests.Creatures;

/// <summary>
/// Characterisation of the seams an extraction of collaborators from the map systems must keep (refactor audit, wave 3 follow-up): the
/// private members other code reaches by reflection, the type-name strings MapUpdateDiagnostics matches, the slow-line wording, the
/// gameobject use-handler precedence and the viewer-filter identity. They describe today's behaviour and must pass before and after.
/// </summary>
public sealed class CreatureMapSystemCharacterizationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    // --- reflection seams (CreatureAnomalyHardeningTests, PlayerbotSiegeStopAndRetreatWorldTests) ---

    [Fact]
    public void RespawnAt_IsAPrivateInstanceDictionaryOfSpawnGuidToClockMs_BehindPendingRespawnAt()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            FieldInfo field = typeof(CreatureMapSystem).GetField("_respawnAt", Private)!;
            Assert.NotNull(field);
            Assert.Equal(typeof(Dictionary<uint, long>), field.FieldType);

            var dormant = (Dictionary<uint, long>)field.GetValue(system)!;
            dormant[77] = 12_345;

            Assert.Equal(12_345L, system.PendingRespawnAt(77));
            Assert.Equal(dormant.Count, system.DormantRespawnCount);
            Assert.Null(system.PendingRespawnAt(78));
        }
    }

    [Fact]
    public void UnloadGrid_IsAPrivateInstanceMethodOfOneGridCoord_ThatDropsTheGridAndItsCreatures()
    {
        MethodInfo method = typeof(CreatureMapSystem).GetMethod("UnloadGrid", Private)!;
        Assert.NotNull(method);
        ParameterInfo parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(GridCoord), parameter.ParameterType);
        Assert.Equal(typeof(void), method.ReturnType);

        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);
            Assert.True(system.IsGridLoaded(30, 0));
            Assert.Single(system.Creatures);

            method.Invoke(system, [GridDefines.ComputeGridCoord(30, 0)]);

            Assert.False(system.IsGridLoaded(30, 0));
            Assert.Empty(system.Creatures);
        }
    }

    // --- MapUpdateDiagnostics matches updater type names as strings ---

    private sealed class FakeUpdater : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
        }
    }

    // Stand-ins that only carry the matched names.
    private sealed class MapLocomotion
    {
    }

    private class InstanceData
    {
    }

    private sealed class BossScript : InstanceData
    {
    }

    [Fact]
    public void SubsystemOf_LabelsTheMapSystemsByTheirTypeNames()
    {
        // The label is chosen by Type.Name, so renaming or moving the types silently changes the slow-map-update lines.
        Assert.Equal("CreatureMapSystem", typeof(CreatureMapSystem).Name);
        Assert.Equal("GameObjectMapSystem", typeof(GameObjectMapSystem).Name);
        Assert.Equal("creature AI (CreatureMapSystem)", MapUpdateDiagnostics.SubsystemOf(typeof(CreatureMapSystem)));
        Assert.Equal("gameobjects (GameObjectMapSystem)", MapUpdateDiagnostics.SubsystemOf(typeof(GameObjectMapSystem)));
        Assert.Equal("movement (MapLocomotion)", MapUpdateDiagnostics.SubsystemOf(typeof(MapLocomotion)));
        Assert.Equal("FakeUpdater", MapUpdateDiagnostics.SubsystemOf(new FakeUpdater()));
    }

    [Fact]
    public void SubsystemOf_GivesEveryInstanceDataSubclassOneLabel()
    {
        Assert.Equal("instance scripts (InstanceData)", MapUpdateDiagnostics.SubsystemOf(typeof(InstanceData)));
        Assert.Equal("instance scripts (InstanceData)", MapUpdateDiagnostics.SubsystemOf(typeof(BossScript)));
    }

    // --- the slow-map-update line ---

    [Fact]
    public void CreatureUpdateReport_Describe_KeepsItsWording()
    {
        string text = CreatureUpdateReport.Describe(default, 1, 2, 3, 4, 0);

        Assert.Equal(
            "creatures 0.0 ms over 0/0 active (ai 0.0, motion 0.0), path searches 0.0 ms in 0 searches from any stage, "
            + "catch-up moves 0.0, linked followers 0.0, spawn groups 0.0, pending ai 0.0, "
            + "relay scripts 0.0, forced despawns 0.0, observers 0.0, "
            + "path outcomes so far: 1 no-path, 2 straight authored routes, 3 deferred, 4 home teleports",
            text);
    }

    [Fact]
    public void CreatureUpdateReport_Describe_AddsTheOptionalSections()
    {
        var timing = new UpdateTiming
        {
            SplinePass = 1,
            UngatedWanderers = 2,
            SlowestCreatureTicks = 1,
            SlowestEntry = 299,
            JobSearches = 1,
        };

        string text = CreatureUpdateReport.Describe(timing, 0, 0, 0, 0, 5);

        Assert.Contains(", spline pass ", text, StringComparison.Ordinal);
        Assert.Contains(", 2 wanderers outside the gate", text, StringComparison.Ordinal);
        Assert.Contains("; slowest creature entry 299 ", text, StringComparison.Ordinal);
        Assert.Contains(", top generator none", text, StringComparison.Ordinal);
        Assert.Contains("; path jobs applied 1 (worker time ", text, StringComparison.Ordinal);
        Assert.EndsWith("in flight 5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("slowest path search", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeLastUpdate_OfALiveSystem_StartsWithCreaturesAndEndsWithThePathOutcomes()
    {
        CreatureContent content = Content([Template()], [Spawn(1, WolfEntry, 30, 0)]);
        (WorldRuntime world, _, CreatureMapSystem system) = CreateSystem(content);
        using (world)
        {
            world.AddPlayer(TestWorld.CreatePlayer(1, 0, 0, new FakeSession()));
            world.RunTick(50);

            string text = ((IMapUpdateBreakdown)system).DescribeLastUpdate();

            Assert.StartsWith("creatures ", text, StringComparison.Ordinal);
            Assert.Contains(" active (ai ", text, StringComparison.Ordinal);
            Assert.EndsWith(
                $"path outcomes so far: {system.NoPathPaths} no-path, {system.DirectFallbackPaths} straight authored routes, "
                + $"{system.DeferredPathSearches} deferred, {system.HomePathTeleports} home teleports",
                text,
                StringComparison.Ordinal);
        }
    }

    // --- GameObjectMapSystem seams ---

    [Fact]
    public void RegisteredUseHandler_OverridesTheBuiltInUseOfItsType()
    {
        GameObjectUseRig rig = Create([GoSpawn(2, MailboxEntry, 2, 0)]);
        (Player player, _) = rig.Join(1);
        GameObject mailbox = rig.Single(MailboxEntry);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, mailbox.Guid));

        var calls = new List<GameObject>();
        rig.System.RegisterUseHandler(GameObjectType.Mailbox, (_, go) =>
        {
            calls.Add(go);
            return GameObjectUseResult.NotUsable;
        });

        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(player, mailbox.Guid));
        Assert.Same(mailbox, Assert.Single(calls));

        // A later registration replaces the earlier one.
        rig.System.RegisterUseHandler(GameObjectType.Mailbox, (_, _) => GameObjectUseResult.Ok);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, mailbox.Guid));
        Assert.Single(calls);
    }

    [Fact]
    public void QuestRelatedObjects_CarryTheMapSystemItselfAsTheirViewerFieldFilter()
    {
        GameObjectUseRig rig = Create([GoSpawn(1, QuestHerbEntry, 3, 0), GoSpawn(2, MailboxEntry, 2, 0)]);
        rig.Join(1);

        Assert.True(ReferenceEquals(rig.Single(QuestHerbEntry).ViewerFieldFilter, rig.System));
        Assert.Null(rig.Single(MailboxEntry).ViewerFieldFilter);
    }
}

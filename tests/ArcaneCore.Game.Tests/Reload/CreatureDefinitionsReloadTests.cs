using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Reload;

/// <summary>
/// The creature definitions (templates, models, addons, waypoints, EventAI) of a <see cref="CreatureContent"/>
/// can be swapped in place, like vmangos' <c>ObjectMgr::LoadCreatureTemplates</c> overwriting the
/// <c>CreatureInfo</c> table that live creatures point into (ObjectMgr.cpp:1190): every holder of the
/// content, and every creature that reads <see cref="Creature.Template"/>, sees the new definition.
/// </summary>
public sealed class CreatureDefinitionsReloadTests
{
    private static CreatureTemplate Wolf(string name = "Young Wolf", uint rank = 0) => new() { Entry = 299, Name = name, DisplayIds = [30], Rank = rank };

    private static CreatureSpawn SpawnOf(uint guid, uint entry = 299) => new() { Guid = guid, Entry = entry, MapId = 0, X = 1, Y = 2, Z = 3 };

    [Fact]
    public void SwapDefinitions_ReplacesTemplatesModelsAddonsAndWaypoints_ButNotTheSpawns()
    {
        var content = new CreatureContent([Wolf()], [SpawnOf(10)], [(10u, new CreatureWaypoint(1, 1, 1, 1, 0, 0))], [new CreatureModelInfo(30, 1, 1, 0, 0)], [new CreatureAddon(10, 0, 1, 0, 0)]);
        var fresh = new CreatureContent([Wolf("Dire Wolf")], [SpawnOf(99)], [(10u, new CreatureWaypoint(1, 5, 5, 5, 0, 0)), (10u, new CreatureWaypoint(2, 6, 6, 6, 0, 0))], [new CreatureModelInfo(30, 9, 9, 0, 0)], []);

        CreatureDefinitions previous = content.SwapDefinitions(fresh);

        Assert.Equal("Dire Wolf", content.FindTemplate(299)?.Name);
        Assert.Equal(9f, content.FindModel(30)?.BoundingRadius);
        Assert.Null(content.FindAddon(10));
        Assert.Equal(2, content.GetWaypoints(10).Count);
        Assert.Equal(10u, Assert.Single(content.GetSpawns(0)).Guid);
        Assert.Equal(1, content.SpawnCount);

        content.RestoreDefinitions(previous);

        Assert.Equal("Young Wolf", content.FindTemplate(299)?.Name);
        Assert.NotNull(content.FindAddon(10));
        Assert.Single(content.GetWaypoints(10));
    }

    [Fact]
    public void SwapDefinitions_BumpsTheVersion_AndSwapsTheAiContent()
    {
        var content = new CreatureContent([Wolf()], [], [], [], []);
        var ai = new CreatureAiContent([], []);
        var fresh = new CreatureContent([Wolf()], [], [], [], [], ai);
        int before = content.DefinitionsVersion;

        content.SwapDefinitions(fresh);

        Assert.True(content.DefinitionsVersion > before);
        Assert.Same(ai, content.Ai);
        Assert.Equal(1, content.TemplateCount);
    }

    [Fact]
    public void TheSharedEmptyContent_CannotBeSwapped()
    {
        var fresh = new CreatureContent([Wolf()], [], [], [], []);

        Assert.Throws<InvalidOperationException>(() => CreatureContent.Empty.SwapDefinitions(fresh));
        Assert.Equal(0, CreatureContent.Empty.TemplateCount);
    }

    [Fact]
    public void ALiveCreature_ReadsTheNewTemplate_AfterTheSwap()
    {
        var content = new CreatureContent([Wolf()], [SpawnOf(10)], [], [], []);
        var creature = new Creature(10, Wolf(), SpawnOf(10), content, new Random(1));
        Assert.Equal("Young Wolf", creature.Template.Name);

        content.SwapDefinitions(new CreatureContent([Wolf("Dire Wolf", rank: 1)], [], [], [], []));

        Assert.Equal("Dire Wolf", creature.Template.Name);
        Assert.Equal(1u, creature.Template.Rank);
    }

    [Fact]
    public void ACreature_WhoseTemplateWasRemoved_KeepsTheLastOneItKnew()
    {
        var content = new CreatureContent([Wolf()], [SpawnOf(10)], [], [], []);
        var creature = new Creature(10, Wolf(), SpawnOf(10), content, new Random(1));

        content.SwapDefinitions(new CreatureContent([new CreatureTemplate { Entry = 1, Name = "Other", DisplayIds = [1] }], [], [], [], []));

        Assert.Equal("Young Wolf", creature.Template.Name);
    }

    [Fact]
    public void ACreature_BuiltFromATemplateTheContentNeverHad_IsUnaffected()
    {
        var creature = new Creature(1, Wolf(), null, CreatureContent.Empty, new Random(1));

        Assert.Equal("Young Wolf", creature.Template.Name);
    }
}

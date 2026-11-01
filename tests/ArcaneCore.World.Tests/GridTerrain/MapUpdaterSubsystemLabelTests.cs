
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Npc;
using Xunit;

namespace ArcaneCore.World.Tests.GridTerrain;

/// <summary>Every map updater type in the Game and World assemblies gets a subsystem label in the perf and slow-map "top" lines.</summary>
public sealed class MapUpdaterSubsystemLabelTests
{
    [Fact]
    public void EveryMapUpdaterType_HasANonFallbackSubsystemLabel()
    {
        Type[] updaters = [.. new[] { typeof(IMapUpdater).Assembly, typeof(NpcServicesFeature).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && typeof(IMapUpdater).IsAssignableFrom(t))];

        Assert.Contains(updaters, t => t.Name == "FlightUpdater");
        Assert.Contains(updaters, t => t.Name == "TransportMapUpdater");
        Assert.Contains(updaters, t => t.Name == "SkillPersistenceUpdater");
        Assert.Contains(updaters, t => t.BaseType?.Name == "InstanceData");

        string[] unlabelled = [.. updaters.Where(t => MapUpdateDiagnostics.SubsystemOf(t) == t.Name).Select(t => t.FullName ?? t.Name)];
        Assert.True(unlabelled.Length == 0, "map updaters with no subsystem label: " + string.Join(", ", unlabelled));
    }

    [Fact]
    public void InstanceDataSubclasses_ShareOneLabel()
    {
        string[] labels = [.. typeof(IMapUpdater).Assembly.GetTypes()
            .Where(t => t.IsClass && t.BaseType is { } b && (b.Name == "InstanceData" || b.Name == "ScriptedInstance"))
            .Select(MapUpdateDiagnostics.SubsystemOf)
            .Distinct()];

        Assert.Equal(["instance scripts (InstanceData)"], labels);
    }
}

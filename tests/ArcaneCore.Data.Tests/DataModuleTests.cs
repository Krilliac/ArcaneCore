using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Schema composition from <see cref="IDataModule"/>s (docs/integration/seams.md).</summary>
public sealed class DataModuleTests
{
    [Fact]
    public void Compose_AppendsModuleSteps_InVersionOrder()
    {
        SchemaDefinition schema = DataModules.Compose(
            DatabaseComponent.World, "world", ["t1"], [],
            [new FakeModule(DatabaseComponent.World, 3, "c"), new FakeModule(DatabaseComponent.World, 2, "b")]);

        Assert.Equal(3, schema.CurrentVersion);
        Assert.Equal([2, 3], schema.Steps.Select(s => s.Version));
        Assert.Equal(new CreateTableChange("b"), schema.Steps[0].Changes.Single());
        Assert.Equal("world_schema", schema.VersionTable);
    }

    [Fact]
    public void Compose_KeepsInlineSteps_AndContinuesAfterThem()
    {
        SchemaDefinition schema = DataModules.Compose(
            DatabaseComponent.Characters, "characters", ["characters"],
            [new SchemaStep(2, [new CreateTableChange("a")])],
            [new FakeModule(DatabaseComponent.Characters, 3, "b")]);

        Assert.Equal(3, schema.CurrentVersion);
        Assert.Equal([2, 3], schema.Steps.Select(s => s.Version));
    }

    [Theory]
    [InlineData(3)] // gap: version 2 missing
    [InlineData(2)] // duplicate of the inline step
    [InlineData(1)] // reserved base version
    public void Compose_FailsClosed_OnBadVersions(int version)
    {
        IReadOnlyList<SchemaStep> inline = version == 2 ? [new SchemaStep(2, [])] : [];
        Assert.Throws<InvalidOperationException>(() => DataModules.Compose(
            DatabaseComponent.World, "world", ["t1"], inline, [new FakeModule(DatabaseComponent.World, version, "x")]));
    }

    [Fact]
    public void Compose_RejectsAModuleOfAnotherComponent()
        => Assert.Throws<InvalidOperationException>(() => DataModules.Compose(
            DatabaseComponent.World, "world", ["t1"], [], [new FakeModule(DatabaseComponent.Auth, 2, "x")]));

    [Fact]
    public void ProductionSchemas_ComposeFromTheDiscoveredModules()
    {
        Assert.True(AuthDbContext.Schema.CurrentVersion >= 2);
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= 2);
        Assert.True(WorldDbContext.Schema.CurrentVersion >= 1);
        Assert.All(DataModules.All, m => Assert.True(m.SchemaVersion >= 2));
    }

    private sealed class FakeModule(DatabaseComponent component, int version, string table) : IDataModule
    {
        public DatabaseComponent Component { get; } = component;

        public int SchemaVersion { get; } = version;

        public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(table)];

        public void ConfigureModel(ModelBuilder modelBuilder)
        {
        }

        public void AddServices(IServiceCollection services)
        {
        }
    }
}

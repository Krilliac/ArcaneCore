using System.Reflection;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DbUpgradeExitCodes = ArcaneCore.Data.Schema.Upgrade.Cli.DbUpgradeExitCodes;
using HostExitCodes = ArcaneCore.Kernel.Ops.ExitCodes;
using ImporterExitCodes = ArcaneCore.Data.Content.Import.ExitCodes;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// docs/reference/schema.md and exit-codes.md are generated from the live definitions. These read metadata only: they run no
/// database, so they prove nothing about MariaDB or PostgreSQL behaviour (the provider tests of the data project do).
/// </summary>
public sealed class SchemaReferenceTests
{
    private sealed class FixtureModule(DatabaseComponent component, int version, params SchemaChange[] changes) : IDataModule
    {
        public DatabaseComponent Component { get; } = component;

        public int SchemaVersion { get; } = version;

        public IReadOnlyList<SchemaChange> SchemaChanges { get; } = changes;

        public void ConfigureModel(ModelBuilder modelBuilder)
        {
        }

        public void AddServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }
    }

    [Fact]
    public void Build_LabelsInlineStepsAndModules_AndDescribesTheChanges()
    {
        var module = new FixtureModule(DatabaseComponent.World, 3, new CreateTableChange("fixture_a"), new AddColumnChange("fixture_a", "Col"));
        SchemaDefinition schema = DataModules.Compose(
            DatabaseComponent.World,
            "world",
            ["base"],
            [new SchemaStep(2, [new EnsureIndexesChange("base")])],
            [module]);
        SchemaComponent component = SchemaReferenceRenderer.Build(DatabaseComponent.World, schema, [module]);

        Assert.Equal(3, component.CurrentVersion);
        Assert.Equal([2, 3], component.Rows.Select(r => r.Version));
        Assert.Equal("inline step of the database context", component.Rows[0].Owner);
        Assert.Equal("repairs the model's indexes of `base`", component.Rows[0].Changes);
        Assert.Equal("creates `fixture_a`; adds columns `fixture_a.Col`", component.Rows[1].Changes);
    }

    [Fact]
    public void Components_HighestVersionEqualsTheContextSchemaVersion_AndVersionsAreContiguousFromTwo()
    {
        (DatabaseComponent Component, SchemaDefinition Schema)[] expected =
        [
            (DatabaseComponent.Auth, AuthDbContext.Schema),
            (DatabaseComponent.Characters, CharacterDbContext.Schema),
            (DatabaseComponent.World, WorldDbContext.Schema),
        ];
        IReadOnlyList<SchemaComponent> components = SchemaReferenceRenderer.Components();
        Assert.Equal(expected.Select(e => e.Component), components.Select(c => c.Component));
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Schema.CurrentVersion, components[i].CurrentVersion);
            Assert.Equal(components[i].CurrentVersion, components[i].Rows.Max(r => r.Version));
            Assert.Equal(Enumerable.Range(2, components[i].CurrentVersion - 1), components[i].Rows.Select(r => r.Version));
        }
    }

    [Fact]
    public void EveryModule_AppearsExactlyOnce_InThePageOfItsComponent()
    {
        string page = SchemaReferenceRenderer.RenderSchema(SchemaReferenceRenderer.Components());
        Assert.NotEmpty(DataModules.All);
        foreach (IDataModule module in DataModules.All)
        {
            string name = module.GetType().FullName!["ArcaneCore.Data.".Length..];
            int count = page.Split("| `" + name + "` |").Length - 1;
            Assert.True(count == 1, $"{name} appears {count} times in docs/reference/schema.md");
        }
    }

    [Fact]
    public void CharactersCleanupColumn_AgreesWithWhatTheModulesRegister()
    {
        SchemaComponent characters = SchemaReferenceRenderer.Components().Single(c => c.Component == DatabaseComponent.Characters);
        foreach (IDataModule module in DataModules.For(DatabaseComponent.Characters))
        {
            SchemaRow row = characters.Rows.Single(r => r.Version == module.SchemaVersion);
            Assert.Equal(SchemaReferenceRenderer.HasCleanup(module), row.HasCharacterCleanup);
        }

        Assert.Contains(characters.Rows, r => r.HasCharacterCleanup == true);
    }

    [Fact]
    public void ExitCodes_ListEveryConstant_WithItsSummary()
    {
        string page = SchemaReferenceRenderer.RenderExitCodes();
        foreach (FieldInfo field in typeof(HostExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.Name != "MaxRequested"))
        {
            Assert.Contains($"| {(int)field.GetRawConstantValue()!} | `{field.Name}` |", page, StringComparison.Ordinal);
        }

        foreach (FieldInfo field in typeof(ImporterExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            Assert.Contains($"| {(int)field.GetRawConstantValue()!} | `{field.Name}` |", page, StringComparison.Ordinal);
        }

        Assert.Contains($"| {DbUpgradeExitCodes.Refused} | `Refused` |", page, StringComparison.Ordinal);
        Assert.Contains($"| {DbUpgradeExitCodes.LockTimeout} | `LockTimeout` |", page, StringComparison.Ordinal);
        Assert.Contains("RestartPreventExitStatus=78", page, StringComparison.Ordinal);
        Assert.Contains(HostExitCodes.InvalidConfiguration.ToString(), page, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaPage_MatchesTheCommittedPage() =>
        DocsGolden.Verify("docs/reference/schema.md", SchemaReferenceRenderer.RenderSchema(SchemaReferenceRenderer.Components()));

    [Fact]
    public void ExitCodesPage_MatchesTheCommittedPage() =>
        DocsGolden.Verify("docs/reference/exit-codes.md", SchemaReferenceRenderer.RenderExitCodes());

    [Fact]
    public void ExitCodesPage_LinksTheRunbook_ThatNamesEveryDbUpgradeCode()
    {
        string runbook = RepoRoot.ReadText("docs/ops/database-upgrade.md");
        foreach (FieldInfo field in typeof(ArcaneCore.Data.Schema.Upgrade.Cli.DbUpgradeExitCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            Assert.Contains($"| {(int)field.GetRawConstantValue()!} |", runbook, StringComparison.Ordinal);
        }

        Assert.Contains("(../ops/database-upgrade.md)", SchemaReferenceRenderer.RenderExitCodes(), StringComparison.Ordinal);
    }
}

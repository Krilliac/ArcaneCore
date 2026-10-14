using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

/// <summary>Declared row seeds (<see cref="EnsureRowChange"/>) in the update snapshot: recorded, frozen, and no schema object.</summary>
public sealed class SqlUpdateSeedTests
{
    [Fact]
    public void Apply_SkipsRowSeedActions_AndStillRefusesAnOperationlessTableCreate()
    {
        var seed = new PlannedAction(ChangeObject.Row, "character_ownership", "world", ChangeDecision.Create, "seed", Script: ["INSERT ..."]);
        var catalog = SnapshotCatalog.Empty();
        catalog.Apply(new PlannedStep(60, "step", [seed]), SnapshotCatalog.Empty());
        Assert.False(catalog.HasTable("character_ownership"));

        var table = new PlannedAction(ChangeObject.Table, "character_ownership", null, ChangeDecision.Create, "create");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SnapshotCatalog.Empty().Apply(new PlannedStep(60, "step", [table]), SnapshotCatalog.Empty()));
        Assert.Contains("carries no operation", ex.Message);
    }

    [Fact]
    public void TheCharactersSnapshot_RecordsTheWorldFenceSeed()
    {
        SchemaSnapshot snapshot = SqlUpdateSupport.ReadSnapshot("characters");
        SnapshotSeed seed = Assert.Single(snapshot.Seeds ?? [], s => s.Table == "character_ownership");
        Assert.Equal("scope=s:world", seed.Key);
        Assert.Equal(SnapshotSeed.Of(UpdateComponent.For("characters").Definition), snapshot.Seeds);
    }

    [Fact]
    public async Task ASeedWhoseValuesChangedWithoutAStep_IsRefused()
    {
        SchemaSnapshot committed = SqlUpdateSupport.ReadSnapshot("characters");
        SchemaSnapshot tampered = committed with
        {
            Seeds = [.. committed.Seeds!.Select(s => s.Table == "character_ownership" ? s with { Hash = new string('0', 64) } : s)],
        };
        UpdateExportException ex = await Assert.ThrowsAsync<UpdateExportException>(
            () => UpdateScriptWriter.GenerateAsync(UpdateComponent.For("characters"), tampered));
        Assert.Contains("changed", ex.Message);
        Assert.Contains("character_ownership", ex.Message);
    }

    [Fact]
    public async Task ASeedTheSnapshotLacks_AtTheSnapshotsOwnVersion_IsRefused()
    {
        SchemaSnapshot committed = SqlUpdateSupport.ReadSnapshot("characters");
        SchemaSnapshot withoutSeed = committed with { Seeds = [] };
        UpdateExportException ex = await Assert.ThrowsAsync<UpdateExportException>(
            () => UpdateScriptWriter.GenerateAsync(UpdateComponent.For("characters"), withoutSeed));
        Assert.Contains("no schema step accounts for", ex.Message);
    }

    [Fact]
    public async Task ASnapshotWrittenBeforeSeedsWereRecorded_AdoptsTheDeclaredOnes()
    {
        SchemaSnapshot committed = SqlUpdateSupport.ReadSnapshot("characters");
        UpdateSet set = await UpdateScriptWriter.GenerateAsync(UpdateComponent.For("characters"), committed with { Seeds = null });
        Assert.Equal(committed.Seeds, set.Snapshot.Seeds);
    }
}

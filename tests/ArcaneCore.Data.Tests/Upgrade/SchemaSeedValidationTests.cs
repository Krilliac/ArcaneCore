using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Upgrade;

public sealed class SchemaSeedValidationTests
{
    [Fact]
    public async Task DeclaredOwnershipSeed_IsAccepted_WhileMismatchedDefinitionsAreRejected()
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(new DatabaseConnectionOptions
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=:memory:"
        });
        await db.Database.OpenConnectionAsync();

        EnsureRowChange seed = new(CharacterOwnershipDataModule.Table, "scope", "world", new Dictionary<string, object?>
        {
            ["owner_node"] = null,
            ["owner_incarnation"] = null,
            ["epoch"] = 0L,
            ["write_sequence"] = 0L,
            ["active"] = false,
        });

        SchemaDefinition accepted = Definition(seed);
        Assert.NotNull(SchemaChangeDecider.ReadModel(db, accepted));

        EnsureRowChange wrongValue = seed with { Values = new Dictionary<string, object?>(seed.Values) { ["epoch"] = 1L } };
        Assert.Throws<InvalidOperationException>(() => SchemaChangeDecider.ReadModel(db, Definition(wrongValue)));

        EnsureRowChange wrongColumns = seed with { Values = new Dictionary<string, object?>(seed.Values) { ["unexpected"] = 1L } };
        Assert.Throws<InvalidOperationException>(() => SchemaChangeDecider.ReadModel(db, Definition(wrongColumns)));
    }

    // The model is the whole characters model, so every seeded row it declares (the ownership row and the realm authority lease scopes) must be declared by a step.
    private static SchemaDefinition Definition(EnsureRowChange seed) => new()
    {
        Component = "characters",
        CurrentVersion = RealmAuthorityLeaseDataModule.Version,
        Version1Tables = ["characters"],
        Steps =
        [
            new SchemaStep(CharacterOwnershipDataModule.Version, [seed]),
            new SchemaStep(RealmAuthorityLeaseDataModule.Version, [.. new RealmAuthorityLeaseDataModule().SchemaChanges.OfType<EnsureRowChange>()]),
        ],
    };
}

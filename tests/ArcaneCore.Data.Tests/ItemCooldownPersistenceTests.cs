using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Cold-store owner metadata coverage; provider matrix includes SQLite.</summary>
public sealed class ItemCooldownPersistenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    [Fact]
    public async Task Sqlite_RoundTripsOwnerIdentityAndIndependentCategoryExpiry_ThenDeletes()
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            SchemaDefinition current = CharacterDbContext.Schema;
            var legacy = new SchemaDefinition
            {
                Component = current.Component, CurrentVersion = 21, Version1Tables = current.Version1Tables,
                Steps = current.Steps.Where(step => step.Version <= 21).ToArray(),
            };
            await SchemaBootstrapper.EnsureAsync(db, legacy);
            db.Set<CharacterSpellCooldownRow>().Add(new CharacterSpellCooldownRow
            {
                CharacterId = 91, Kind = 0, Id = 117, EndsAtUnixMs = 700,
            });
            await db.SaveChangesAsync();
            await SchemaBootstrapper.EnsureAsync(db, current);
            CharacterSpellCooldownRow preserved = await db.Set<CharacterSpellCooldownRow>().AsNoTracking()
                .SingleAsync(row => row.CharacterId == 91);
            Assert.Equal((117u, 700L), (preserved.Id, preserved.EndsAtUnixMs));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            var store = new EfCharacterSpellStateStore(db);
            await store.SaveAsync(1, new CharacterSpellState(
                [new CharacterSpellCooldownRow { CharacterId = 1, Kind = 0, Id = 9901, EndsAtUnixMs = 100 }],
                [],
                [new CharacterSpellCooldownOwnerRow { CharacterId = 1, SpellId = 9901, ItemId = 6948, Category = 77,
                    SpellEndsAtUnixMs = 100, CategoryEndsAtUnixMs = 200 }]));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            CharacterSpellState loaded = await new EfCharacterSpellStateStore(db).LoadAsync(1);
            CharacterSpellCooldownOwnerRow owner = Assert.Single(loaded.CooldownOwners!);
            Assert.Equal((6948u, 77u, 100L, 200L), (owner.ItemId, owner.Category, owner.SpellEndsAtUnixMs, owner.CategoryEndsAtUnixMs));
            await new EfCharacterSpellStateStore(db).DeleteCharacterAsync(1);
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.Empty((await new EfCharacterSpellStateStore(db).LoadAsync(1)).CooldownOwners!);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

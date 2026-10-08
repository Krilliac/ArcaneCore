using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Transports;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests.Transports;

/// <summary>
/// Characters schema 41 (<see cref="CharacterTransportDataModule"/>): vmangos characters.transport_guid and transport_x..o, written
/// with every character snapshot and cleared on land.
/// </summary>
public sealed class CharacterTransportSeatTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Module_IsCharactersVersion41_AddingTheFiveVmangosColumns()
    {
        var module = new CharacterTransportDataModule();

        Assert.Equal(41, CharacterTransportDataModule.Version);
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(
            ["transport_guid", "transport_x", "transport_y", "transport_z", "transport_o"],
            module.SchemaChanges.Cast<AddColumnChange>().Where(c => c.Table == "characters").Select(c => c.Column));
        Assert.IsAssignableFrom<ICharacterDataCleanup>(module);
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= CharacterTransportDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Seat_RoundTripsWithTheSnapshot_AndLandClearsIt(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        CharacterRecord created = await store.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Sailor" });
        Assert.Equal(0u, created.TransportGuid);

        await store.SaveStateAsync(new CharacterState(created.Id, 1, 0, 1100, 5, 6, 0.5f, 1, 10,
            Transport: new TransportSeat(176244, 3.5f, -1.25f, 6f, 1.5f)));
        CharacterRecord aboard = (await store.GetByIdAsync(created.Id))!;
        Assert.Equal((176244u, 3.5f, -1.25f, 6f, 1.5f),
            (aboard.TransportGuid, aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation));

        await store.SaveStateAsync(new CharacterState(created.Id, 1, 0, 1100, 5, 6, 0.5f, 1, 11));
        CharacterRecord ashore = (await store.GetByIdAsync(created.Id))!;
        Assert.Equal((0u, 0f, 0f, 0f, 0f),
            (ashore.TransportGuid, ashore.TransportX, ashore.TransportY, ashore.TransportZ, ashore.TransportOrientation));
    }


    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeFromVersion40_AddsTheColumns_AndKeepsCharactersOnLand(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int id;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            id = (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 5, Name = "Oldtimer", Level = 7 })).Id;

            // A database from before the step: no transport columns, version below it. Fixed identifiers only.
            ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
            foreach (string column in new[] { "transport_guid", "transport_x", "transport_y", "transport_z", "transport_o" })
            {
                string dropColumn = $"ALTER TABLE {sql.DelimitIdentifier("characters")} DROP COLUMN {sql.DelimitIdentifier(column)}";
                await db.Database.ExecuteSqlRawAsync(dropColumn);
            }

            await db.Set<SchemaVersionRow>().ExecuteUpdateAsync(s => s.SetProperty(r => r.Version, CharacterTransportDataModule.Version - 1));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
            await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema); // a second start changes nothing
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);
            CharacterRecord old = (await new EfCharacterStore(db).GetByIdAsync(id))!;
            Assert.Equal(("Oldtimer", (byte)7, 0u), (old.Name, old.Level, old.TransportGuid));
        }
    }
}

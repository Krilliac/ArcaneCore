using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Data.World.Rest;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The rested-experience row, the at-login flags with the rename they allow, and the <c>areatrigger_tavern</c> table, on every
/// available engine (SQLite always; MariaDB and PostgreSQL when their test servers exist). The rename is one transaction on the shared
/// <c>characters</c> row, so the unique name index is exercised for real.
/// </summary>
public sealed class CharacterRestAndRenameStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<CharacterDbContext> NewContextAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return db;
    }

    private static async Task<int> CreateAsync(CharacterDbContext db, int account, string name)
        => (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = account, Name = name, Race = 1, Class = 1, Level = 1 })).Id;

    // --- rested state -----------------------------------------------------------------

    [Fact]
    public void RestModule_IsAnAllocatedCharactersVersion_WithItsOwnCleanup_AndTheStoreRegistered()
    {
        var module = new CharacterRestDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(CharacterRestDataModule.Version, module.SchemaVersion);
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterRestDataModule);
        Assert.Equal([CharacterRestDataModule.Table], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(CharacterDbContext.Schema.CurrentVersion >= CharacterRestDataModule.Version);
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.Characters);
        Assert.Contains(services, d => d.ServiceType == typeof(ICharacterRestStore) && d.ImplementationType == typeof(EfCharacterRestStore));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RestState_RoundTrips_AndTheLatestWriteWins(DatabaseProvider provider)
    {
        await using CharacterDbContext db = await NewContextAsync(provider);
        int id = await CreateAsync(db, 3, "Resty");
        var store = new EfCharacterRestStore(db);
        Assert.Null(await store.LoadAsync(id)); // never saved: a fresh character

        await store.SaveAsync(id, new CharacterRestState(123.5f, 1_700_000_123, true));
        CharacterRestState back = (await store.LoadAsync(id))!.Value;
        Assert.Equal((123.5f, 1_700_000_123L, true), (back.RestBonus, back.LogoutUnixSeconds, back.WasResting));

        await store.SaveAsync(id, new CharacterRestState(0f, 1_700_009_999, false));
        back = (await store.LoadAsync(id))!.Value;
        Assert.Equal((0f, 1_700_009_999L, false), (back.RestBonus, back.LogoutUnixSeconds, back.WasResting));
        Assert.Equal(1, await db.Set<CharacterRestRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RestState_OfAMissingCharacter_IsIgnored_AndDeleteAndCharacterDeletionClearIt(DatabaseProvider provider)
    {
        await using CharacterDbContext db = await NewContextAsync(provider);
        var store = new EfCharacterRestStore(db);
        await store.SaveAsync(404, new CharacterRestState(5f, 1, true)); // deleted meanwhile: no row, no error
        Assert.Equal(0, await db.Set<CharacterRestRow>().CountAsync());

        int id = await CreateAsync(db, 3, "Restdel");
        int other = await CreateAsync(db, 3, "Restkeep");
        await store.SaveAsync(id, new CharacterRestState(5f, 1, true));
        await store.SaveAsync(other, new CharacterRestState(6f, 2, false));
        await store.DeleteAsync(id);
        Assert.Null(await store.LoadAsync(id));
        Assert.NotNull(await store.LoadAsync(other));

        await store.SaveAsync(id, new CharacterRestState(5f, 1, true));
        Assert.True(await new EfCharacterStore(db).DeleteAsync(id, 3));
        Assert.Null(await store.LoadAsync(id));
        Assert.Equal(6f, (await store.LoadAsync(other))!.Value.RestBonus); // another character's row is left alone
    }

    // --- at-login flags and rename -------------------------------------------------------

    [Fact]
    public void RenameModule_IsAnAllocatedCharactersVersion_WithItsOwnCleanup_AndTheStoreRegistered()
    {
        var module = new CharacterRenameDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(CharacterRenameDataModule.Version, module.SchemaVersion);
        Assert.NotEqual(CharacterRestDataModule.Version, CharacterRenameDataModule.Version);
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterRenameDataModule);
        Assert.Equal([CharacterRenameDataModule.Table], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.Characters);
        Assert.Contains(services, d => d.ServiceType == typeof(ICharacterRenameStore) && d.ImplementationType == typeof(EfCharacterRenameStore));
        Assert.Equal(1u, CharacterAtLoginFlags.Rename);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ARenameOfAFlaggedCharacter_ChangesTheNameAndClearsTheFlagInOneCommit(DatabaseProvider provider)
    {
        await using CharacterDbContext db = await NewContextAsync(provider);
        int id = await CreateAsync(db, 3, "Oldname");
        var store = new EfCharacterRenameStore(db);
        Assert.True(await store.SetFlagAsync(id, CharacterAtLoginFlags.Rename));
        Assert.True(await store.SetFlagAsync(id, CharacterAtLoginFlags.Rename)); // idempotent
        Assert.Equal(CharacterAtLoginFlags.Rename, (await store.GetFlagsAsync(3))[id]);

        CharacterRenameResult result = await store.RenameAsync(id, 3, "Newname");

        Assert.Equal(new CharacterRenameResult(CharacterRenameOutcome.Renamed, "Oldname"), result);
        Assert.Equal("Newname", (await new EfCharacterStore(db).GetByIdAsync(id))!.Name);
        Assert.Empty(await store.GetFlagsAsync(3)); // cleared
        Assert.Equal(CharacterRenameOutcome.NotAllowed, (await store.RenameAsync(id, 3, "Another")).Outcome); // and so no second rename
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ADuplicateName_IsRefused_InAnyCase_AndNothingChanges(DatabaseProvider provider)
    {
        await using CharacterDbContext db = await NewContextAsync(provider);
        int id = await CreateAsync(db, 3, "Renameme");
        await CreateAsync(db, 4, "Taken");
        var store = new EfCharacterRenameStore(db);
        await store.SetFlagAsync(id, CharacterAtLoginFlags.Rename);

        foreach (string name in new[] { "Taken", "taken", "TAKEN", "Renameme" })
        {
            Assert.Equal(CharacterRenameOutcome.NameTaken, (await store.RenameAsync(id, 3, name)).Outcome);
        }

        Assert.Equal("Renameme", (await new EfCharacterStore(db).GetByIdAsync(id))!.Name);
        Assert.Equal(CharacterAtLoginFlags.Rename, (await store.GetFlagsAsync(3))[id]); // the flag stays: the player can try another name
        Assert.Equal(CharacterRenameOutcome.Renamed, (await store.RenameAsync(id, 3, "Freename")).Outcome);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ARename_NeedsTheFlag_TheAccount_AndARealCharacter(DatabaseProvider provider)
    {
        await using CharacterDbContext db = await NewContextAsync(provider);
        int unflagged = await CreateAsync(db, 3, "Unflagged");
        int foreign = await CreateAsync(db, 4, "Foreign");
        var store = new EfCharacterRenameStore(db);
        await store.SetFlagAsync(foreign, CharacterAtLoginFlags.Rename);

        Assert.Equal(CharacterRenameOutcome.NotAllowed, (await store.RenameAsync(unflagged, 3, "Newone")).Outcome);
        Assert.Equal(CharacterRenameOutcome.NotAllowed, (await store.RenameAsync(foreign, 3, "Stolen")).Outcome); // account 3 does not own it
        Assert.Equal(CharacterRenameOutcome.NotAllowed, (await store.RenameAsync(9999, 3, "Nobody")).Outcome);
        Assert.False(await store.SetFlagAsync(9999, CharacterAtLoginFlags.Rename));
        Assert.Equal("Foreign", (await new EfCharacterStore(db).GetByIdAsync(foreign))!.Name);
        Assert.Equal(CharacterAtLoginFlags.Rename, (await store.GetFlagsAsync(4))[foreign]);
        Assert.Empty(await store.GetFlagsAsync(3));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentRenamesToOneName_LeaveExactlyOneWinner_AndTheOthersKeepTheirFlags(DatabaseProvider provider)
    {
        // The unique index on characters.name decides when two renames pass the pre-check together; the loser is "taken", not a fault.
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        int[] ids = new int[6];
        await using (CharacterDbContext boot = TestContexts.Create<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(boot, CharacterDbContext.Schema);
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = await CreateAsync(boot, 10 + i, $"Racer{i}");
                await new EfCharacterRenameStore(boot).SetFlagAsync(ids[i], CharacterAtLoginFlags.Rename);
            }
        }

        Task<CharacterRenameOutcome>[] attempts = [.. ids.Select((id, i) => Task.Run(async () =>
        {
            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            return (await new EfCharacterRenameStore(db).RenameAsync(id, 10 + i, "Winner")).Outcome;
        }))];

        CharacterRenameOutcome[] outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(o => o == CharacterRenameOutcome.Renamed));
        Assert.Equal(ids.Length - 1, outcomes.Count(o => o == CharacterRenameOutcome.NameTaken));
        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await check.Characters.CountAsync(c => c.Name == "Winner"));
        Assert.Equal(ids.Length - 1, (await check.Set<CharacterAtLoginRow>().Where(r => r.Flags != 0).CountAsync())); // only the winner's flag was cleared
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeletingTheCharacter_RemovesItsFlags(DatabaseProvider provider)
    {
        await using CharacterDbContext db = await NewContextAsync(provider);
        int id = await CreateAsync(db, 3, "Flagdel");
        var store = new EfCharacterRenameStore(db);
        await store.SetFlagAsync(id, CharacterAtLoginFlags.Rename);
        Assert.Equal(1, await db.Set<CharacterAtLoginRow>().CountAsync());

        Assert.True(await new EfCharacterStore(db).DeleteAsync(id, 3));

        Assert.Equal(0, await db.Set<CharacterAtLoginRow>().CountAsync());
    }

    // --- areatrigger_tavern ----------------------------------------------------------------

    [Fact]
    public void TavernModule_IsAnAllocatedWorldVersion_AndRegistersTheStore()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.World), m => m is AreaTriggerTavernDataModule);
        Assert.Equal(AreaTriggerTavernDataModule.Version, module.SchemaVersion);
        Assert.Equal([AreaTriggerTavernDataModule.Table], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.True(WorldDbContext.Schema.CurrentVersion >= AreaTriggerTavernDataModule.Version);
        var services = new ServiceCollection();
        DataModules.AddServices(services, DatabaseComponent.World);
        Assert.Contains(services, d => d.ServiceType == typeof(IAreaTriggerTavernStore) && d.ImplementationType == typeof(EfAreaTriggerTavernStore));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task TavernTriggers_AreReadAscending_IncludingIdsAboveInt32(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using WorldDbContext db = TestContexts.Create<WorldDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        var store = new EfAreaTriggerTavernStore(db);
        Assert.Empty(await store.LoadAsync());

        db.Set<AreaTriggerTavernRow>().AddRange(new AreaTriggerTavernRow { Id = 982 }, new AreaTriggerTavernRow { Id = 4 }, new AreaTriggerTavernRow { Id = 3_000_000_000 });
        await db.SaveChangesAsync();

        Assert.Equal([4u, 982u, 3_000_000_000u], await store.LoadAsync());
    }
}

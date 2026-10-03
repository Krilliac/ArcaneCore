using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// M6 persistence on every engine: the v1 → v2 upgrades of the auth and characters schemas
/// from real M5-shaped databases, and round trips through the stores that use the new columns
/// and tables (GM level, action bars, bind point, account data, tutorials).
/// </summary>
public sealed class M6StoreTests : IAsyncLifetime
{
    // vmangos ActionButtonType values (the Game assembly is not referenced here).
    private const byte Spell = 0x00;
    private const byte Macro = 0x40;
    private const byte Item = 0x80;

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task M5CharactersDatabase_UpgradesToVersion2(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharactersM5Context m5 = TestContexts.Create<CharactersM5Context>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(m5, CharactersM5Context.Schema);
            m5.Characters.Add(new CharacterV1Row { AccountId = 7, Name = "Veteran", Level = 12, X = 5.5f, PlayedTime = 3600 });
            await m5.SaveChangesAsync();
        }

        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);

        CharacterRecord veteran = await db.Characters.AsNoTracking().SingleAsync();
        Assert.Equal(("Veteran", (byte)12, 5.5f, 3600u), (veteran.Name, veteran.Level, veteran.X, veteran.PlayedTime));
        Assert.Equal((0u, 0u, (byte)0), (veteran.Money, veteran.LevelPlayedTime, veteran.ActionBarToggles));
        Assert.True(new HomeBind(veteran.HomeMapId, veteran.HomeZoneId, veteran.HomeX, veteran.HomeY, veteran.HomeZ).IsUnset);

        // The new tables are usable.
        var characters = new EfCharacterStore(db);
        await characters.SaveStateAsync(new CharacterState(veteran.Id, 0, 12, 1, 2, 3, 0, 12, 3700,
            ActionButtons: [new ActionButton(0, 6603, Spell)]));
        Assert.Single(await characters.GetActionButtonsAsync(veteran.Id));
        var accountData = new EfAccountDataStore(db);
        await accountData.SaveTutorialsAsync(7, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal(8u, (await accountData.GetAsync(7)).Tutorials[7]);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task M5AuthDatabase_UpgradesToVersion2_WithPlayerSecurity(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (AuthM5Context m5 = TestContexts.Create<AuthM5Context>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(m5, AuthM5Context.Schema);
            m5.Accounts.Add(new AccountV1Row { Username = "OLDTIMER", Salt = new byte[32], Verifier = new byte[32] });
            await m5.SaveChangesAsync();
        }

        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
        Assert.Equal(2, (await db.Set<SchemaVersionRow>().SingleAsync()).Version);

        var accounts = new EfAccountStore(db);
        Assert.Equal(AccountSecurity.Player, (await accounts.FindByUsernameAsync("oldtimer"))!.Security);
        Assert.True(await accounts.UpdateSecurityAsync("oldtimer", AccountSecurity.GameMaster));
        Assert.False(await accounts.UpdateSecurityAsync("nobody", AccountSecurity.GameMaster));
        db.ChangeTracker.Clear();
        Assert.Equal(AccountSecurity.GameMaster, (await accounts.FindByUsernameAsync("OLDTIMER"))!.Security);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterStore_ActionButtonsHomeAndMoney_RoundTrip(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);

        CharacterRecord created = await store.CreateAsync(new CharacterRecord { AccountId = 4, Name = "Banker" });
        var home = new HomeBind(1, 14, -618.5f, -4251.6f, 38.7f);
        await store.SaveStateAsync(new CharacterState(created.Id, 1, 14, 0, 0, 0, 0, 1, 10, 9, 12345, 0b11,
            [new ActionButton(0, 6603, Spell), new ActionButton(119, 0xFFFFFF, Item)], home));

        CharacterRecord reloaded = (await store.GetByIdAsync(created.Id))!;
        Assert.Equal((12345u, (byte)0b11, 9u), (reloaded.Money, reloaded.ActionBarToggles, reloaded.LevelPlayedTime));
        Assert.Equal(home, new HomeBind(reloaded.HomeMapId, reloaded.HomeZoneId, reloaded.HomeX, reloaded.HomeY, reloaded.HomeZ));
        Assert.Equal(
            new[] { new ActionButton(0, 6603, Spell), new ActionButton(119, 0xFFFFFF, Item) },
            await store.GetActionButtonsAsync(created.Id));

        // Unchanged buttons (null) are left alone; a new list replaces the old one.
        await store.SaveStateAsync(new CharacterState(created.Id, 1, 14, 0, 0, 0, 0, 1, 11));
        Assert.Equal(2, (await store.GetActionButtonsAsync(created.Id)).Count);
        await store.SaveStateAsync(new CharacterState(created.Id, 1, 14, 0, 0, 0, 0, 1, 12, ActionButtons: [new ActionButton(5, 133, Macro)]));
        Assert.Equal(new[] { new ActionButton(5, 133, Macro) }, await store.GetActionButtonsAsync(created.Id));

        // Identities for the name cache; deleting removes the character and its buttons.
        Assert.Contains(await store.GetAllIdentitiesAsync(), i => i.Id == created.Id && i.Name == "Banker" && i.AccountId == 4);
        Assert.True(await store.DeleteAsync(created.Id, 4));
        Assert.Empty(await store.GetActionButtonsAsync(created.Id));
        Assert.Empty(await store.GetAllIdentitiesAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AccountDataStore_KeepsExactBytes_AndErases(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfAccountDataStore(db);

        // Not valid UTF-8 on purpose: the MD5 the client checks must hash what it uploaded.
        byte[] blob = [0x53, 0x45, 0x54, 0xFF, 0xFE, 0x00, 0x80, 0x0A];
        await store.SaveDataAsync(9, 4, new AccountDataEntry(1700000000, blob));
        await store.SaveDataAsync(9, 7, new AccountDataEntry(1700000001, [1]));
        await store.SaveTutorialsAsync(9, [0xFFFFFFFF, 0, 1, 0, 0, 0, 0, 0x80000000]);

        AccountSettings loaded = await store.GetAsync(9);
        Assert.Equal(blob, loaded.Data[4]!.Data);
        Assert.Equal(1700000000u, loaded.Data[4]!.Time);
        Assert.Equal(new byte[] { 1 }, loaded.Data[7]!.Data);
        Assert.Null(loaded.Data[0]);
        Assert.Equal(new uint[] { 0xFFFFFFFF, 0, 1, 0, 0, 0, 0, 0x80000000 }, loaded.Tutorials);

        // Overwrite, erase, and other accounts stay separate.
        await store.SaveDataAsync(9, 4, new AccountDataEntry(1700000002, [2, 3]));
        await store.SaveDataAsync(9, 7, new AccountDataEntry(1700000003, []));
        loaded = await store.GetAsync(9);
        Assert.Equal(new byte[] { 2, 3 }, loaded.Data[4]!.Data);
        Assert.Null(loaded.Data[7]);
        Assert.All((await store.GetAsync(10)).Data, Assert.Null);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

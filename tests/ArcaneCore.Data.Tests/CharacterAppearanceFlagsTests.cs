using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The helm and cloak show toggles in characters.character_flags (vmangos 0e3ff01 Player.cpp:16339-16340, 14678-14681).</summary>
public sealed class CharacterAppearanceFlagsTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task HideBits_RoundTrip_AndOnlyTheHideBitsAreWritten(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        CharacterRecord created = await store.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Hoodless" });

        await store.SaveStateAsync(new CharacterState(created.Id, 0, 0, 0, 0, 0, 0, 1, 10, CharacterFlags: 0xC00));
        Assert.Equal(0xC00u, (await store.GetByIdAsync(created.Id))!.CharacterFlags);

        await store.SaveStateAsync(new CharacterState(created.Id, 0, 0, 0, 0, 0, 0, 1, 10, CharacterFlags: 0x400 | 0x2));
        Assert.Equal(0x400u, (await store.GetByIdAsync(created.Id))!.CharacterFlags);
    }

    [Fact]
    public void Module_AddsTheCharacterFlagsColumn_AndOwnsDeletionCleanup()
    {
        var module = new CharacterAppearanceFlagsDataModule();
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(67, module.SchemaVersion);
        Assert.Contains(module.SchemaChanges, change => change is AddColumnChange
        {
            Table: "characters", Column: "character_flags",
        });
        Assert.IsAssignableFrom<ICharacterDataCleanup>(module);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

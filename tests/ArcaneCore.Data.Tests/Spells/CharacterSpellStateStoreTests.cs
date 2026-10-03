using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Schema;
using Xunit;

namespace ArcaneCore.Data.Tests.Spells;

/// <summary>
/// <c>character_spell_cooldown</c> and <c>character_aura</c> (<see cref="CharacterSpellStateDataModule"/>)
/// on every engine: a save round trips every column (including caster GUIDs above 2^63 and
/// permanent -1 durations), a second save replaces the first, characters are isolated, and
/// delete clears only the deleted character.
/// </summary>
public sealed class CharacterSpellStateStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void Module_IsOneCharactersStep_WithBothTables()
    {
        IDataModule module = Assert.Single(DataModules.For(DatabaseComponent.Characters), m => m is CharacterSpellStateDataModule);
        Assert.Equal(CharacterSpellStateDataModule.Version, module.SchemaVersion);
        Assert.Equal(
            [CharacterSpellStateDataModule.CooldownTable, CharacterSpellStateDataModule.AuraTable],
            module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(CharacterDbContext.Schema.Steps, s => s.Version == module.SchemaVersion && s.Changes.SequenceEqual(module.SchemaChanges));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Save_RoundTrips_ReplacesAndIsolatesCharacters(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        CharacterAuraRow foreign = Aura(spell: 172, caster: 0xF130_0000_0000_0042UL, remaining: 9_000);
        foreign.Amount0 = 12;
        foreign.PeriodicTimer0 = 1_500;
        foreign.EffectMask = 0b001;
        CharacterAuraRow permanent = Aura(spell: 465, caster: 5UL, remaining: -1);
        permanent.MaxDurationMs = -1;
        permanent.Amount1 = 55;
        permanent.EffectMask = 0b010;
        permanent.StackCount = 3;
        permanent.Charges = 2;
        var first = new CharacterSpellState(
            [Cooldown(0, 133, 1_800_000_000_000), Cooldown(1, 76, 1_800_000_005_000)],
            [foreign, permanent]);

        await WithStore(cs, async store =>
        {
            await store.SaveAsync(1, first);
            await store.SaveAsync(2, new CharacterSpellState([Cooldown(0, 99, 1)], [Aura(1, 2UL, 3)]));
        });

        await WithStore(cs, async store =>
        {
            CharacterSpellState loaded = await store.LoadAsync(1);
            Assert.Equal(
                [(0, 133u, 1_800_000_000_000L), (1, 76u, 1_800_000_005_000L)],
                loaded.Cooldowns.Select(c => ((int)c.Kind, c.Id, c.EndsAtUnixMs)));
            Assert.All(loaded.Cooldowns, c => Assert.Equal(1, c.CharacterId));
            Assert.Equal(2, loaded.Auras.Count);
            AssertAura(foreign, loaded.Auras[0], seq: 0);
            AssertAura(permanent, loaded.Auras[1], seq: 1);
            Assert.Single((await store.LoadAsync(2)).Cooldowns);
        });

        // A second save replaces everything of that character (cooldown expired, aura gone).
        await WithStore(cs, store => store.SaveAsync(1, new CharacterSpellState([Cooldown(0, 133, 7), Cooldown(0, 133, 8)], [permanent])));
        await WithStore(cs, async store =>
        {
            CharacterSpellState loaded = await store.LoadAsync(1);
            Assert.Equal(7, Assert.Single(loaded.Cooldowns).EndsAtUnixMs); // duplicate keys keep the first
            AssertAura(permanent, Assert.Single(loaded.Auras), seq: 0);
            await store.DeleteCharacterAsync(1);
        });

        await WithStore(cs, async store =>
        {
            CharacterSpellState deleted = await store.LoadAsync(1);
            Assert.Empty(deleted.Cooldowns);
            Assert.Empty(deleted.Auras);
            CharacterSpellState other = await store.LoadAsync(2);
            Assert.Single(other.Cooldowns);
            Assert.Single(other.Auras);
            await store.DeleteCharacterAsync(42); // deleting nothing is a no-op
            await store.SaveAsync(2, new CharacterSpellState([], []));
            Assert.Empty((await store.LoadAsync(2)).Auras);
        });
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletionCleanup_RemovesOnlyTheDeletedCharactersRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await CreateAsync(provider);
        await WithStore(cs, async store =>
        {
            await store.SaveAsync(1, new CharacterSpellState([Cooldown(0, 133, 5), Cooldown(1, 76, 6)], [Aura(1, 2UL, 3), Aura(4, 5UL, 6)]));
            await store.SaveAsync(2, new CharacterSpellState([Cooldown(0, 99, 1)], [Aura(7, 1UL, 8)]));
        });

        ICharacterDataCleanup cleanup = Assert.Single(CharacterDataCleanups.All.OfType<CharacterSpellStateDataModule>());
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            await cleanup.DeleteCharacterDataAsync(db, 1, CancellationToken.None);
            await cleanup.DeleteCharacterDataAsync(db, 42, CancellationToken.None); // nothing to delete
        }

        await WithStore(cs, async store =>
        {
            CharacterSpellState deleted = await store.LoadAsync(1);
            Assert.Empty(deleted.Cooldowns);
            Assert.Empty(deleted.Auras);
            CharacterSpellState other = await store.LoadAsync(2);
            Assert.Equal(99u, Assert.Single(other.Cooldowns).Id);
            Assert.Equal(1UL, Assert.Single(other.Auras).CasterGuid); // an aura cast by the deleted character stays
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private async Task<DatabaseConnectionOptions> CreateAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        return cs;
    }

    private static async Task WithStore(DatabaseConnectionOptions cs, Func<EfCharacterSpellStateStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfCharacterSpellStateStore(db));
    }

    private static CharacterSpellCooldownRow Cooldown(byte kind, uint id, long ends)
        => new() { CharacterId = 999, Kind = kind, Id = id, EndsAtUnixMs = ends };

    private static CharacterAuraRow Aura(uint spell, ulong caster, int remaining) => new()
    {
        CharacterId = 999,
        Seq = 77,
        Spell = spell,
        CasterGuid = caster,
        CasterLevel = 60,
        StackCount = 1,
        MaxDurationMs = remaining < 0 ? -1 : remaining + 1_000,
        RemainingMs = remaining,
        EffectMask = 1,
        Amount0 = -3,
        Amount2 = int.MaxValue,
        PeriodicTimer1 = 2_000,
        PeriodicTimer2 = 3_000,
        SavedAtUnixMs = 1_800_000_000_123,
    };

    private static void AssertAura(CharacterAuraRow expected, CharacterAuraRow actual, int seq)
    {
        Assert.Equal(seq, actual.Seq);
        Assert.Equal(
            (expected.Spell, expected.CasterGuid, expected.CasterLevel, expected.StackCount, expected.Charges, expected.MaxDurationMs, expected.RemainingMs, expected.EffectMask),
            (actual.Spell, actual.CasterGuid, actual.CasterLevel, actual.StackCount, actual.Charges, actual.MaxDurationMs, actual.RemainingMs, actual.EffectMask));
        Assert.Equal(
            (expected.Amount0, expected.Amount1, expected.Amount2, expected.PeriodicTimer0, expected.PeriodicTimer1, expected.PeriodicTimer2, expected.SavedAtUnixMs),
            (actual.Amount0, actual.Amount1, actual.Amount2, actual.PeriodicTimer0, actual.PeriodicTimer1, actual.PeriodicTimer2, actual.SavedAtUnixMs));
    }
}

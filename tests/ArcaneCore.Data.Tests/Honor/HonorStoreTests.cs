using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Honor;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Honor;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Honor;

/// <summary>
/// Honor state and contribution point rows (<see cref="CharacterHonorDataModule"/>) on every engine the
/// environment provides. Only SQLite runs on a developer machine; MariaDB and PostgreSQL run on hosted CI
/// (ARCANECORE_TEST_MARIADB / ARCANECORE_TEST_POSTGRES).
/// </summary>
public sealed class HonorStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task StateAndRows_RoundTrip_WithTheirOneDecimalRounding(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("Honored", 1, 60, 1));
        int id = await HonorTestSupport.IdOfAsync(cs, "Honored");
        AssertEmpty(await LoadAsync(cs, id));

        var state = new CharacterHonorState(12345.678f, 12, 3_000_000_000u, 4_000_000_000u, 188.34f, 70_000, 3, 0x03, true);
        await WriteAsync(cs, s => s.SaveStateAsync(id, state));
        await WriteAsync(cs, s => s.AppendCpAsync(id,
        [
            new HonorCpRecord(4, 3_500_000_000u, 188.3f, 20_000, (byte)HonorKind.Honorable),
            new HonorCpRecord(3, 3057, 488f, 20_000, (byte)HonorKind.Honorable),
            new HonorCpRecord(0, 5, 11.54f, 20_001, (byte)HonorKind.Bonus),
        ]));

        CharacterHonorData back = await LoadAsync(cs, id);
        Assert.Equal(state with { RankPoints = 12345.7f, LastWeekCp = 188.3f }, back.State);
        Assert.Equal(
            [
                new HonorCpRecord(4, 3_500_000_000u, 188.3f, 20_000, 1),
                new HonorCpRecord(3, 3057, 488f, 20_000, 1),
                new HonorCpRecord(0, 5, 11.5f, 20_001, 3),
            ],
            back.Cp);

        // A second save replaces in place.
        await WriteAsync(cs, s => s.SaveStateAsync(id, state with { RankPoints = 0f, StoredHk = -1 }));
        Assert.Equal(0f, (await LoadAsync(cs, id)).State.RankPoints);
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(1, await verify.Set<CharacterHonorRow>().CountAsync());
    }

    [Fact]
    public void OneDecimal_RoundsExactTiesToEven_AndRefusesNonFiniteValues()
    {
        Assert.Equal(0.2f, HonorRounding.OneDecimal(0.25f));
        Assert.Equal(0.8f, HonorRounding.OneDecimal(0.75f));
        Assert.Equal(-0.2f, HonorRounding.OneDecimal(-0.25f));
        Assert.Equal(188.3f, HonorRounding.OneDecimal(188.3f));
        Assert.Equal(0f, HonorRounding.OneDecimal(float.PositiveInfinity));
        Assert.Equal(0f, HonorRounding.OneDecimal(float.NaN));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MissingCharacter_IsIgnored(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("Someone", 1, 10, 1));
        await WriteAsync(cs, s => s.SaveStateAsync(9999, CharacterHonorState.Empty with { RankPoints = 5f }));
        await WriteAsync(cs, s => s.AppendCpAsync(9999, [new HonorCpRecord(0, 0, 1f, 1, 1)]));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(cs);
        Assert.Equal(0, await verify.Set<CharacterHonorRow>().CountAsync());
        Assert.Equal(0, await verify.Set<HonorCpRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Reset_ClearsTheNumbersAndRows_ButKeepsTheCharacterFlags(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("Reset", 1, 60, 1), ("Other", 1, 60, 1));
        int id = await HonorTestSupport.IdOfAsync(cs, "Reset");
        int other = await HonorTestSupport.IdOfAsync(cs, "Other");
        foreach (int c in new[] { id, other })
        {
            await WriteAsync(cs, s => s.SaveStateAsync(c, new CharacterHonorState(9000f, 9, 4, 12, 50f, 100, 2, 0x01, true)));
            await WriteAsync(cs, s => s.AppendCpAsync(c, [new HonorCpRecord(4, 1, 10f, 5, 1)]));
        }

        await WriteAsync(cs, s => s.ResetAsync(id));
        CharacterHonorData back = await LoadAsync(cs, id);
        Assert.Equal(new CharacterHonorState(0f, 0, 0, 0, 0f, 0, 0, 0x01, true), back.State);
        Assert.Empty(back.Cp);
        Assert.Single((await LoadAsync(cs, other)).Cp);
        Assert.Equal(9000f, (await LoadAsync(cs, other)).State.RankPoints);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletion_RemovesOnlyThatCharactersHonor(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("Gone", 1, 60, 1), ("Stays", 2, 60, 1));
        int gone = await HonorTestSupport.IdOfAsync(cs, "Gone");
        int stays = await HonorTestSupport.IdOfAsync(cs, "Stays");
        foreach (int c in new[] { gone, stays })
        {
            await WriteAsync(cs, s => s.SaveStateAsync(c, CharacterHonorState.Empty with { RankPoints = 100f }));
            await WriteAsync(cs, s => s.AppendCpAsync(c, [new HonorCpRecord(4, 1, 10f, 5, 1)]));
        }

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(gone, accountId: 1));
        }

        AssertEmpty(await LoadAsync(cs, gone));
        Assert.Single((await LoadAsync(cs, stays)).Cp);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task QueuedRemovalAfterDeletion_DoesNotWipeARecreatedCharacter(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("Reborn", 1, 60, 1));
        int id = await HonorTestSupport.IdOfAsync(cs, "Reborn");
        await WriteAsync(cs, s => s.AppendCpAsync(id, [new HonorCpRecord(4, 1, 10f, 5, 1)]));

        // The character id still exists (a recreated character): the deleted-id purge must not touch it.
        await WriteAsync(cs, s => s.DeleteDeletedCharacterAsync(id));
        Assert.Single((await LoadAsync(cs, id)).Cp);

        // Explicit removal at creation does clear it.
        await WriteAsync(cs, s => s.DeleteCharacterAsync(id));
        Assert.Empty((await LoadAsync(cs, id)).Cp);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ConcurrentAppends_FromTwoScopes_KeepEveryRow(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await HonorTestSupport.CreateWithCharactersAsync(_databases, provider, ("Busy", 1, 60, 1));
        int id = await HonorTestSupport.IdOfAsync(cs, "Busy");
        await Task.WhenAll(
            Task.Run(async () =>
            {
                for (uint i = 0; i < 20; i++)
                {
                    await WriteAsync(cs, s => s.AppendCpAsync(id, [new HonorCpRecord(4, i, 1f, 100, 1)]));
                }
            }),
            Task.Run(async () =>
            {
                for (uint i = 100; i < 120; i++)
                {
                    await WriteAsync(cs, s => s.AppendCpAsync(id, [new HonorCpRecord(3, i, 2f, 100, 1)]));
                }
            }));
        CharacterHonorData back = await LoadAsync(cs, id);
        Assert.Equal(40, back.Cp.Count);
        Assert.Equal(40, back.Cp.Select(r => (r.VictimType, r.VictimId)).Distinct().Count());
    }

    [Fact]
    public void TheModule_DeclaresItsCleanup_AndItsIndexes()
    {
        Assert.Contains(CharacterDataCleanups.All, c => c is CharacterHonorDataModule);
        Assert.DoesNotContain(CharacterDataCleanups.Missing, m => m is CharacterHonorDataModule);
    }

    private static void AssertEmpty(CharacterHonorData data)
    {
        Assert.Equal(CharacterHonorState.Empty, data.State);
        Assert.Empty(data.Cp);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static async Task<CharacterHonorData> LoadAsync(DatabaseConnectionOptions cs, int id)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        return await new EfHonorStore(db).LoadAsync(id);
    }

    private static async Task WriteAsync(DatabaseConnectionOptions cs, Func<EfHonorStore, Task> action)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await action(new EfHonorStore(db));
    }
}

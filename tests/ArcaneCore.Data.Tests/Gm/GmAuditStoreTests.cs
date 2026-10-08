using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Gm;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Gm;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Gm;

/// <summary>The GM audit lane's mute and ticket tables (<see cref="GmAuditDataModule"/>) on the SQLite / MariaDB / PostgreSQL matrix.</summary>
public sealed class GmAuditStoreTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Fact]
    public void Module_HasItsConstantVersion_TwoTables_AndACleanup()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is GmAuditDataModule);
        Assert.Equal(DatabaseComponent.Characters, module.Component);
        Assert.Equal(GmAuditDataModule.Version, module.SchemaVersion);
        Assert.Equal([GmAuditDataModule.MuteTable, GmAuditDataModule.TicketTable], module.SchemaChanges.OfType<CreateTableChange>().Select(c => c.Table));
        Assert.Contains(CharacterDataCleanups.All, c => c is GmAuditDataModule);
        Assert.Equal(GmAuditDataModule.Version, CharacterDbContext.Schema.Steps.Single(s => s.Version == GmAuditDataModule.Version).Version);
    }

    private static AccountMuteRecord Mute(int account, long until, string reason = "spam") => new(account, until, until - 600, "Staffer", 1, reason);

    private static GmTicketRecord Ticket(int id, int character, string text = "help", GmTicketStatus status = GmTicketStatus.Open)
        => new(id, character, text, 1, 0, 1.5f, -2.25f, 3.75f, 1000, 1100, status, string.Empty, string.Empty, 0);

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Mutes_RoundTrip_ReplaceInPlace_AndLoadOnlyThoseStillInForce(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, _) = await CreateAsync(provider);
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(1, 5000, "first")));
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(2, 900)));    // already over at 1000
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(3, 6000)));

        IReadOnlyList<AccountMuteRecord> active = await ReadAsync(connection, s => s.LoadActiveMutesAsync(1000));
        Assert.Equal([1, 3], active.Select(m => m.AccountId));
        Assert.Equal(Mute(1, 5000, "first"), active[0]);

        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(1, 7000, "second")));   // replaces, never duplicates
        active = await ReadAsync(connection, s => s.LoadActiveMutesAsync(1000));
        Assert.Equal((7000, "second"), (active[0].MutedUntil, active[0].Reason));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal(3, await verify.Set<AccountMuteRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteMute_RemovesTheRow_AndIsIdempotent(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, _) = await CreateAsync(provider);
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(1, 5000)));

        await WriteAsync(connection, s => s.DeleteMuteAsync(1));
        await WriteAsync(connection, s => s.DeleteMuteAsync(1));
        await WriteAsync(connection, s => s.DeleteMuteAsync(404));

        Assert.Empty(await ReadAsync(connection, s => s.LoadActiveMutesAsync(0)));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task DeleteExpiredMutes_RemovesOnlyRowsThatEndedByThen(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, _) = await CreateAsync(provider);
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(1, 900)));
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(2, 1000)));   // ends exactly now: over (CanSpeak is mutetime <= now)
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(3, 1001)));

        Assert.Equal(2, await ReadAsync(connection, s => s.DeleteExpiredMutesAsync(1000)));
        Assert.Equal(0, await ReadAsync(connection, s => s.DeleteExpiredMutesAsync(1000)));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal([3], await verify.Set<AccountMuteRow>().Select(r => r.AccountId).ToListAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Tickets_RoundTrip_UpdateInPlace_AndOnlyOpenOnesLoad(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int[] ids) = await CreateAsync(provider, "Alpha", "Bravo", "Charlie");
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(1, ids[0], "stuck in a wall")));
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(2, ids[1], "closed already", GmTicketStatus.Closed) with { Response = "fixed", ClosedBy = "Gm", ClosedAt = 1200 }));
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(5, ids[2], "third")));

        IReadOnlyList<GmTicketRecord> open = await ReadAsync(connection, s => s.LoadOpenTicketsAsync());
        Assert.Equal([1, 5], open.Select(t => t.Id));
        GmTicketRecord first = open[0];
        Assert.Equal((ids[0], "stuck in a wall", (byte)1, 0u, 1000L, 1100L, GmTicketStatus.Open), (first.CharacterId, first.Text, first.Category, first.MapId, first.CreatedAt, first.UpdatedAt, first.Status));
        Assert.Equal((1.5f, -2.25f, 3.75f), (first.X, first.Y, first.Z));   // exactly representable floats survive every provider
        Assert.Equal(5, await ReadAsync(connection, s => s.GetMaxTicketIdAsync()));   // the closed ticket counts too

        GmTicketRecord updated = first with { Text = "still stuck", Response = "looking", UpdatedAt = 1300 };
        await WriteAsync(connection, s => s.SaveTicketAsync(updated));
        Assert.Equal(("still stuck", "looking"), ((await ReadAsync(connection, s => s.LoadOpenTicketsAsync()))[0].Text, (await ReadAsync(connection, s => s.LoadOpenTicketsAsync()))[0].Response));
        await WriteAsync(connection, s => s.SaveTicketAsync(updated with { Status = GmTicketStatus.Closed, ClosedBy = "Gm", ClosedAt = 1400 }));
        open = await ReadAsync(connection, s => s.LoadOpenTicketsAsync());
        Assert.Equal([5], open.Select(t => t.Id));
        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        GmTicketRow closed = await verify.Set<GmTicketRow>().AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(("still stuck", (byte)GmTicketStatus.Closed, "Gm", 1400L), (closed.Text, closed.Status, closed.ClosedBy, closed.ClosedAt));
        Assert.Equal(3, await verify.Set<GmTicketRow>().CountAsync());
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task MaxTicketId_IsZeroWithNoTickets(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, _) = await CreateAsync(provider);
        Assert.Equal(0, await ReadAsync(connection, s => s.GetMaxTicketIdAsync()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Tickets_OfAMissingCharacter_AreIgnored_AndDeleteIsIdempotent(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int[] ids) = await CreateAsync(provider, "Alpha");
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(1, 9999)));   // deleted while queued: not written
        Assert.Empty(await ReadAsync(connection, s => s.LoadOpenTicketsAsync()));

        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(2, ids[0])));
        await WriteAsync(connection, s => s.DeleteTicketAsync(2));
        await WriteAsync(connection, s => s.DeleteTicketAsync(2));
        await WriteAsync(connection, s => s.DeleteTicketAsync(77));

        Assert.Empty(await ReadAsync(connection, s => s.LoadOpenTicketsAsync()));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LongTexts_AreStoredAtTheColumnLimit(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int[] ids) = await CreateAsync(provider, "Alpha");
        string text = new('t', GmAuditLimits.MaxTextLength);
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(1, ids[0], text) with { Response = text }));

        GmTicketRecord stored = Assert.Single(await ReadAsync(connection, s => s.LoadOpenTicketsAsync()));
        Assert.Equal((text, text), (stored.Text, stored.Response));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterDeletion_RemovesItsTickets_ButNotAnAccountsMute(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions connection, int[] ids) = await CreateAsync(provider, "Gone", "Kept");
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(1, ids[0])));
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(2, ids[0], "history", GmTicketStatus.Closed)));
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(3, ids[1])));
        await WriteAsync(connection, s => s.SaveMuteAsync(Mute(31, 5000)));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(ids[0], accountId: 31));
        }

        await using CharacterDbContext verify = TestContexts.Create<CharacterDbContext>(connection);
        Assert.Equal([3], await verify.Set<GmTicketRow>().AsNoTracking().Select(r => r.Id).ToListAsync());
        Assert.Single(await ReadAsync(connection, s => s.LoadActiveMutesAsync(0)));   // the mute belongs to the account (31), which still has "Kept"

        // a replayed write for the deleted character is ignored, not resurrected
        await WriteAsync(connection, s => s.SaveTicketAsync(Ticket(1, ids[0])));
        Assert.Equal([3], (await ReadAsync(connection, s => s.LoadOpenTicketsAsync())).Select(t => t.Id));
    }

    private async Task<(DatabaseConnectionOptions Connection, int[] Ids)> CreateAsync(DatabaseProvider provider, params string[] names)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);
        var ids = new List<int>();
        foreach (string name in names)
        {
            ids.Add((await store.CreateAsync(new CharacterRecord { AccountId = 31, Name = name, Race = 1, Class = 1, Level = 10 })).Id);
        }

        return (connection, [.. ids]);
    }

    private static async Task WriteAsync(DatabaseConnectionOptions connection, Func<IGmAuditStore, Task> write)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        await write(new EfGmAuditStore(db));
    }

    private static async Task<T> ReadAsync<T>(DatabaseConnectionOptions connection, Func<IGmAuditStore, Task<T>> read)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return await read(new EfGmAuditStore(db));
    }
}

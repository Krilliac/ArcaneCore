using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Login;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace ArcaneCore.Data.Tests.Login;

/// <summary>End-to-end service checks over the real auth database store and a hand-driven UTC clock.</summary>
public sealed class SessionTokenServiceTests : IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 6, 18, 30, 0, DateTimeKind.Utc);
    private readonly TestDatabases _databases = new();

    [Fact]
    public async Task IssueLoginThenConsumeLoginAndResume_UsesConfiguredTtlsAndFreshContexts()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);
        var clock = new TestClock(Start);
        byte[] seed;
        SessionTokenTicket ticket;
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            ticket = await new SessionTokenService(new EfSessionTokenStore(db), clock)
                .IssueLoginAsync(7, 5875, "::ffff:192.0.2.4");
            seed = ticket.IntegritySeed;
            Assert.Equal(Start.AddSeconds(60), ticket.ExpiresUtc);
            Assert.Equal(16, ticket.TokenId.Length);
            Assert.Equal(32, ticket.Token.Length);
            SessionTokenRecord stored = await db.Set<SessionTokenRecord>().SingleAsync();
            Assert.Equal(System.Security.Cryptography.SHA256.HashData(ticket.Token), stored.TokenSha256);
            Assert.NotEqual(ticket.Token, stored.TokenSha256);
        }

        byte[] tokenId = ticket.TokenId;
        byte[] token = ticket.Token;
        SessionTokenAuthentication authentication;
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            authentication = (await new SessionTokenService(new EfSessionTokenStore(db), clock)
                .ConsumeLoginAsync(tokenId, token, 5875, "192.0.2.4", 7))!;
            Assert.Equal(7, authentication.Record.AccountId);
            Assert.Equal(seed, authentication.Record.IntegritySeed);
            Assert.Equal(Start.AddMinutes(5), authentication.Record.ExpiresUtc);
        }

        byte[] resume = authentication.ResumeToken;
        clock.UtcNow = Start.AddMinutes(1);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            SessionTokenAuthentication? resumed = await new SessionTokenService(new EfSessionTokenStore(db), clock)
                .ConsumeResumeAsync(resume, 5875, "::ffff:192.0.2.4", 7);
            Assert.NotNull(resumed);
            Assert.Equal(7, resumed!.Record.AccountId);
            resumed.Dispose();
        }
        authentication.Dispose();
        ticket.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ticket.Token.ToString());
        Assert.Throws<ObjectDisposedException>(() => authentication.ResumeToken.ToString());
    }

    [Fact]
    public async Task ServiceRejectsMalformedTokensAndExactExpiryBoundary()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);
        var clock = new TestClock(Start);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            SessionTokenService service = new(new EfSessionTokenStore(db), clock);
            Assert.Null(await service.ConsumeLoginAsync(new byte[15], new byte[32], 5875, "192.0.2.4", 7));
            Assert.Null(await service.ConsumeLoginAsync(new byte[16], new byte[31], 5875, "192.0.2.4", 7));
            Assert.Null(await service.ConsumeResumeAsync(new byte[31], 5875, "192.0.2.4", 7));
            Assert.Null(await service.ConsumeResumeAsync(new byte[32], 0, "192.0.2.4", 7));

            SessionTokenTicket ticket = await service.IssueLoginAsync(7, 5875, "192.0.2.4");
            byte[] tokenId = ticket.TokenId;
            byte[] token = ticket.Token;
            clock.UtcNow = ticket.ExpiresUtc;
            Assert.Null(await service.ConsumeLoginAsync(tokenId, token, 5875, "192.0.2.4", 7));
            ticket.Dispose();
        }
    }

    [Fact]
    public async Task ServiceStoreRetriesBusyTransactionAndThenSucceeds()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);
        var clock = new TestClock(Start);
        SessionTokenTicket ticket;
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
            ticket = await new SessionTokenService(new EfSessionTokenStore(db), clock).IssueLoginAsync(7, 5875, "192.0.2.4");

        var busy = new BusyTransactions(2);
        await using (AuthDbContext db = CreateWithInterceptor(connection, busy))
        {
            SessionTokenAuthentication? result = await new SessionTokenService(new EfSessionTokenStore(db), clock)
                .ConsumeLoginAsync(ticket.TokenId, ticket.Token, 5875, "192.0.2.4", 7);
            Assert.NotNull(result);
            result!.Dispose();
        }
        Assert.Equal(3, busy.Attempts);
        ticket.Dispose();
    }

    [Fact]
    public async Task ServiceStoreExhaustsBusyRetriesAndSurfacesTheDatabaseFailure()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);
        var clock = new TestClock(Start);
        SessionTokenTicket ticket;
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
            ticket = await new SessionTokenService(new EfSessionTokenStore(db), clock).IssueLoginAsync(7, 5875, "192.0.2.4");

        var busy = new BusyTransactions(int.MaxValue);
        await using (AuthDbContext db = CreateWithInterceptor(connection, busy))
        {
            await Assert.ThrowsAsync<SqliteException>(() => new SessionTokenService(new EfSessionTokenStore(db), clock)
                .ConsumeLoginAsync(ticket.TokenId, ticket.Token, 5875, "192.0.2.4", 7));
        }
        Assert.Equal(3, busy.Attempts);
        ticket.Dispose();
    }

    private static byte[] Bytes(byte value, int length)
    {
        byte[] result = new byte[length];
        Array.Fill(result, value);
        return result;
    }

    private sealed class TestClock(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    private static AuthDbContext CreateWithInterceptor(DatabaseConnectionOptions connection, BusyTransactions busy)
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, connection);
        builder.AddInterceptors(busy);
        return new AuthDbContext(builder.Options);
    }

    private sealed class BusyTransactions(int failures) : DbTransactionInterceptor
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            if (attempt <= failures)
                throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
            return ValueTask.FromResult(result);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

using System.Data.Common;
using System.Security.Cryptography;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Login;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ArcaneCore.Data.Tests.Login;

/// <summary>Verifies that a failed replacement insert rolls back the earlier conditional claim and cleanup.</summary>
public sealed class SessionTokenRollbackTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 6, 19, 0, 0, DateTimeKind.Utc);
    private readonly TestDatabases _databases = new();

    [Fact]
    public async Task ReplacementInsertFailure_RollsBackClaimAndPreservesOlderResume()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await SchemaBootstrapper.EnsureAsync(setup, AuthDbContext.Schema);

        byte[] firstId = Bytes(0x71, 16), firstSecret = Bytes(0x72, 32);
        byte[] secondId = Bytes(0x73, 16), secondSecret = Bytes(0x74, 32);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(db);
            await store.InsertLoginAsync(Login(firstId, firstSecret, accountId: 7));
            await store.InsertLoginAsync(Login(secondId, secondSecret, accountId: 7));
            Assert.NotNull(await store.TryConsumeAndRotateAsync(Request(firstId, firstSecret, 0x75, 0x76)));
        }

        var fault = new ResumeInsertFailure();
        await using (AuthDbContext db = CreateWithInterceptor(connection, fault))
        {
            Assert.Null(await new EfSessionTokenStore(db).TryConsumeAndRotateAsync(Request(secondId, secondSecret, 0x77, 0x78)));
            Assert.True(fault.Fired);
        }

        await using (AuthDbContext verify = TestContexts.Create<AuthDbContext>(connection))
        {
            SessionTokenRecord second = await verify.Set<SessionTokenRecord>().SingleAsync(t => t.TokenId == secondId);
            Assert.Null(second.ConsumedUtc);
            Assert.Equal(1, await verify.Set<SessionTokenRecord>().CountAsync(t => t.Kind == SessionTokenKind.Resume && t.ConsumedUtc == null));
        }

        await using (AuthDbContext retry = TestContexts.Create<AuthDbContext>(connection))
        {
            SessionTokenRecord? result = await new EfSessionTokenStore(retry).TryConsumeAndRotateAsync(Request(secondId, secondSecret, 0x79, 0x7A));
            Assert.NotNull(result);
        }
    }

    private static AuthDbContext CreateWithInterceptor(DatabaseConnectionOptions connection, ResumeInsertFailure interceptor)
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, connection);
        builder.AddInterceptors(interceptor);
        return new AuthDbContext(builder.Options);
    }

    private static SessionTokenRecord Login(byte[] id, byte[] secret, int accountId)
        => new()
        {
            TokenId = id, Kind = SessionTokenKind.Login, AccountId = accountId,
            TokenSha256 = SHA256.HashData(secret), OriginTokenId = id.ToArray(), IntegritySeed = Bytes(0x80, 32),
            ClientBuild = 5875, Ip = "192.0.2.4", CreatedUtc = Now, ExpiresUtc = Now.AddSeconds(60),
        };

    private static SessionTokenConsumeRequest Request(byte[] id, byte[] secret, byte idMarker, byte secretMarker)
        => new(SessionTokenKind.Login, id, SHA256.HashData(secret), 5875, "192.0.2.4", 7, Now,
            Bytes(idMarker, 16), SHA256.HashData(Bytes(secretMarker, 32)));

    private static byte[] Bytes(byte value, int length)
    {
        byte[] result = new byte[length];
        Array.Fill(result, value);
        return result;
    }

    private sealed class ResumeInsertFailure : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<SessionTokenRecord>().Any(e =>
                    e.State == EntityState.Added && e.Entity.Kind == SessionTokenKind.Resume) == true && !Fired)
            {
                Fired = true;
                throw new DbUpdateException("injected resume replacement conflict", new SqliteException("constraint", 2067));
            }
            return ValueTask.FromResult(result);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

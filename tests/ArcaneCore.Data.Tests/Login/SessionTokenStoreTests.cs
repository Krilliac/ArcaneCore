using System.Security.Cryptography;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Login;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests.Login;

/// <summary>Provider contract for the persistent one-time world-entry token table.</summary>
public sealed class SessionTokenStoreTests : IAsyncLifetime
{
    private static readonly DateTime Created = new(2026, 11, 6, 18, 0, 0, DateTimeKind.Utc);
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void SessionTokenModule_IsAuthVersion8_AndDiscovered()
    {
        Assert.Equal(8, SessionTokenDataModule.Version);
        Assert.Contains(DataModules.For(DatabaseComponent.Auth), m => m is SessionTokenDataModule);
        Assert.True(AuthDbContext.Schema.CurrentVersion >= SessionTokenDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task LoginToken_Persists_ConsumesOnce_AndRotatesResume(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await EnsureSchemaAsync(connection);
        byte[] loginId = Bytes(0x10, 16), loginSecret = Bytes(0x20, 32), seed = Bytes(0x30, 32);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
            await new EfSessionTokenStore(db).InsertLoginAsync(Login(loginId, loginSecret, seed));

        SessionTokenRecord? resume;
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
            resume = await new EfSessionTokenStore(db).TryConsumeAndRotateAsync(
                ConsumeLogin(loginId, loginSecret, Bytes(0x40, 16), Bytes(0x50, 32)));

        Assert.NotNull(resume);
        Assert.Equal(SessionTokenKind.Resume, resume!.Kind);
        Assert.Equal(loginId, resume.OriginTokenId);
        Assert.Equal(seed, resume.IntegritySeed);
        Assert.Equal(Created.AddMinutes(5), resume.ExpiresUtc);
        Assert.Null(resume.ConsumedUtc);

        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
            Assert.Null(await new EfSessionTokenStore(db).TryConsumeAndRotateAsync(
                ConsumeLogin(loginId, loginSecret, Bytes(0x60, 16), Bytes(0x70, 32))));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task InvalidClaims_DoNotBurnValidLoginToken(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await EnsureSchemaAsync(connection);
        byte[] id = Bytes(0x80, 16), secret = Bytes(0x90, 32);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(db);
            await store.InsertLoginAsync(Login(id, secret, Bytes(0xA0, 32), ip: "192.0.2.4"));
            Assert.Null(await store.TryConsumeAndRotateAsync(ConsumeLogin(id, Bytes(0x91, 32), Bytes(0xB0, 16), Bytes(0xC0, 32))));
            Assert.Null(await store.TryConsumeAndRotateAsync(ConsumeLogin(id, secret, Bytes(0xB1, 16), Bytes(0xC1, 32), build: 9)));
            Assert.Null(await store.TryConsumeAndRotateAsync(ConsumeLogin(id, secret, Bytes(0xB2, 16), Bytes(0xC2, 32), ip: "192.0.2.5")));
            Assert.Null(await store.TryConsumeAndRotateAsync(new SessionTokenConsumeRequest(
                SessionTokenKind.Resume, id, Sha256(secret), 5875, "192.0.2.4", 7, Created,
                Bytes(0xB3, 16), Sha256(Bytes(0xC3, 32)))));
            Assert.NotNull(await store.TryConsumeAndRotateAsync(ConsumeLogin(id, secret, Bytes(0xD0, 16), Bytes(0xE0, 32), ip: "::ffff:192.0.2.4")));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExpiredLoginToken_IsRejected(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await EnsureSchemaAsync(connection);
        byte[] id = Bytes(0x11, 16), secret = Bytes(0x12, 32);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(db);
            await store.InsertLoginAsync(Login(id, secret, Bytes(0x13, 32), created: Created.AddSeconds(-61), expires: Created.AddSeconds(-1)));
            Assert.Null(await store.TryConsumeAndRotateAsync(ConsumeLogin(id, secret, Bytes(0x14, 16), Bytes(0x15, 32))));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ResumeWrongRealmBuildAndIp_DoNotBurnValidResume(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await EnsureSchemaAsync(connection);
        byte[] loginId = Bytes(0x16, 16), loginSecret = Bytes(0x17, 32);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(db);
            await store.InsertLoginAsync(Login(loginId, loginSecret, Bytes(0x18, 32)));
            SessionTokenRecord resume = (await store.TryConsumeAndRotateAsync(
                ConsumeLogin(loginId, loginSecret, Bytes(0x19, 16), Bytes(0x1A, 32))))!;
            SessionTokenConsumeRequest Wrong(int realm, uint build, string ip) => new(
                SessionTokenKind.Resume, null, resume.TokenSha256, build, ip, realm, Created.AddMinutes(1),
                Bytes(0x1B, 16), Sha256(Bytes(0x1C, 32)));
            Assert.Null(await store.TryConsumeAndRotateAsync(Wrong(8, 5875, "192.0.2.4")));
            Assert.Null(await store.TryConsumeAndRotateAsync(Wrong(7, 5876, "192.0.2.4")));
            Assert.Null(await store.TryConsumeAndRotateAsync(Wrong(7, 5875, "192.0.2.5")));
            Assert.NotNull(await store.TryConsumeAndRotateAsync(Wrong(7, 5875, "::ffff:192.0.2.4")));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExpiryAtTheExactBoundary_IsDenied(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await EnsureSchemaAsync(connection);
        byte[] id = Bytes(0x1D, 16), secret = Bytes(0x1E, 32);
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
        EfSessionTokenStore store = new(db);
        await store.InsertLoginAsync(Login(id, secret, Bytes(0x1F, 32), created: Created.AddSeconds(-60), expires: Created));
        Assert.Null(await store.TryConsumeAndRotateAsync(ConsumeLogin(id, secret, Bytes(0x2A, 16), Bytes(0x2B, 32))));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ResumeRotation_RemovesOlderResumeAndCopiesMetadata(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await EnsureSchemaAsync(connection);
        byte[] id = Bytes(0x21, 16), secret = Bytes(0x22, 32), seed = Bytes(0x23, 32);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(db);
            await store.InsertLoginAsync(Login(id, secret, seed));
            SessionTokenRecord first = (await store.TryConsumeAndRotateAsync(ConsumeLogin(id, secret, Bytes(0x24, 16), Bytes(0x25, 32))))!;
            SessionTokenRecord second = (await store.TryConsumeAndRotateAsync(new SessionTokenConsumeRequest(
                SessionTokenKind.Resume, null, first.TokenSha256, 5875, "192.0.2.4", 7, Created.AddMinutes(1),
                Bytes(0x26, 16), Sha256(Bytes(0x27, 32)))))!;
            Assert.Equal(first.OriginTokenId, second.OriginTokenId);
            Assert.Equal(seed, second.IntegritySeed);
            Assert.Equal(Created.AddMinutes(6), second.ExpiresUtc);
            Assert.Equal(2, await db.Set<SessionTokenRecord>().CountAsync(t => t.Kind == SessionTokenKind.Resume));
            Assert.Equal(1, await db.Set<SessionTokenRecord>().CountAsync(t => t.Kind == SessionTokenKind.Resume && t.ConsumedUtc == null));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RevokeAndPurge_RemoveOnlyTheirIntendedRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await EnsureSchemaAsync(connection);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(db);
            await store.InsertLoginAsync(Login(Bytes(0x31, 16), Bytes(0x32, 32), Bytes(0x33, 32), accountId: 41));
            await store.InsertLoginAsync(Login(Bytes(0x34, 16), Bytes(0x35, 32), Bytes(0x36, 32), accountId: 42,
                created: Created.AddDays(-1).AddSeconds(-60), expires: Created.AddDays(-1)));
            Assert.Equal(1, await store.RevokeUnconsumedAsync(41));
            Assert.Equal(1, await store.PurgeExpiredAsync(Created));
            Assert.Empty(await db.Set<SessionTokenRecord>().Where(t => t.AccountId == 41 || t.AccountId == 42).ToListAsync());
        }
    }

    [Fact]
    public async Task ConcurrentFreshContexts_HaveOneWinnerForTheSameLoginToken()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await EnsureSchemaAsync(connection);
        byte[] id = Bytes(0x41, 16), secret = Bytes(0x42, 32);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
            await new EfSessionTokenStore(setup).InsertLoginAsync(Login(id, secret, Bytes(0x43, 32)));

        var gate = new ConcurrentGate(2);
        Task<SessionTokenRecord?> Claim(byte idByte) => ClaimFreshAsync(connection,
            ConsumeLogin(id, secret, Bytes(idByte, 16), Bytes((byte)(idByte + 1), 32)), gate);
        SessionTokenRecord?[] results = await Task.WhenAll(
            Task.Run(() => Claim(0x44)), Task.Run(() => Claim(0x46)));
        Assert.Single(results, result => result is not null);
    }

    [Fact]
    public async Task ResumeHashCollision_RollsBackTheLoginConsumption()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await EnsureSchemaAsync(connection);
        byte[] loginId = Bytes(0x51, 16), loginSecret = Bytes(0x52, 32);
        byte[] resumeId = Bytes(0x53, 16), resumeSecret = Bytes(0x54, 32);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(db);
            await store.InsertLoginAsync(Login(loginId, loginSecret, Bytes(0x55, 32)));
            db.Set<SessionTokenRecord>().Add(new SessionTokenRecord
            {
                TokenId = Bytes(0x56, 16),
                Kind = SessionTokenKind.Resume,
                AccountId = 99,
                TokenSha256 = Sha256(resumeSecret),
                OriginTokenId = Bytes(0x57, 16),
                IntegritySeed = Bytes(0x58, 32),
                ClientBuild = 5875,
                Ip = "192.0.2.99",
                CreatedUtc = Created,
                ExpiresUtc = Created.AddMinutes(5),
                IssuedRealmId = 7,
            });
            await db.SaveChangesAsync();

            Assert.Null(await store.TryConsumeAndRotateAsync(
                ConsumeLogin(loginId, loginSecret, resumeId, resumeSecret)));
        }

        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            // The failed replacement must leave the login row unconsumed, so a
            // caller can retry after resolving the collision rather than losing auth.
            SessionTokenRecord? retry = await new EfSessionTokenStore(db).TryConsumeAndRotateAsync(
                ConsumeLogin(loginId, loginSecret, Bytes(0x59, 16), Bytes(0x5A, 32)));
            Assert.NotNull(retry);
        }
    }

    [Fact]
    public async Task ConcurrentDistinctLoginTokensForOneAccount_LeaveAtMostOneActiveResume()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await EnsureSchemaAsync(connection);
        byte[] firstId = Bytes(0x61, 16), firstSecret = Bytes(0x62, 32);
        byte[] secondId = Bytes(0x63, 16), secondSecret = Bytes(0x64, 32);
        await using (AuthDbContext setup = TestContexts.Create<AuthDbContext>(connection))
        {
            EfSessionTokenStore store = new(setup);
            await store.InsertLoginAsync(Login(firstId, firstSecret, Bytes(0x65, 32), accountId: 77));
            await store.InsertLoginAsync(Login(secondId, secondSecret, Bytes(0x66, 32), accountId: 77));
        }
        var gate = new ConcurrentGate(2);
        Task<SessionTokenRecord?> Claim(byte[] id, byte[] secret, byte marker) => ClaimFreshAsync(connection,
            ConsumeLogin(id, secret, Bytes(marker, 16), Bytes((byte)(marker + 1), 32)), gate);
        await Task.WhenAll(Task.Run(() => Claim(firstId, firstSecret, 0x67)), Task.Run(() => Claim(secondId, secondSecret, 0x69)));
        await using (AuthDbContext verify = TestContexts.Create<AuthDbContext>(connection))
            Assert.InRange(await verify.Set<SessionTokenRecord>().CountAsync(t =>
                t.AccountId == 77 && t.Kind == SessionTokenKind.Resume && t.ConsumedUtc == null), 0, 1);
    }

    private static async Task<SessionTokenRecord?> ClaimFreshAsync(DatabaseConnectionOptions connection, SessionTokenConsumeRequest request, ConcurrentGate? gate = null)
    {
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
        gate?.ArriveAndWait();
        return await new EfSessionTokenStore(db).TryConsumeAndRotateAsync(request);
    }

    private sealed class ConcurrentGate(int parties)
    {
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;
        public void ArriveAndWait()
        {
            if (Interlocked.Increment(ref _arrived) == parties)
                _released.TrySetResult(true);
            _released.Task.GetAwaiter().GetResult();
        }
    }

    private async Task EnsureSchemaAsync(DatabaseConnectionOptions connection)
    {
        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
        await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
    }

    private static SessionTokenRecord Login(byte[] id, byte[] secret, byte[] seed, int accountId = 7,
        string ip = "192.0.2.4", uint build = 5875, DateTime? created = null, DateTime? expires = null)
        => new()
        {
            TokenId = id,
            Kind = SessionTokenKind.Login,
            AccountId = accountId,
            TokenSha256 = Sha256(secret),
            OriginTokenId = id.ToArray(),
            IntegritySeed = seed,
            ClientBuild = build,
            Ip = ip,
            CreatedUtc = created ?? Created,
            ExpiresUtc = expires ?? Created.AddSeconds(60),
        };

    private static SessionTokenConsumeRequest ConsumeLogin(byte[] id, byte[] secret, byte[] resumeId, byte[] resumeSecret,
        uint build = 5875, string ip = "192.0.2.4", DateTime? now = null)
        => new(SessionTokenKind.Login, id, Sha256(secret), build, ip, 7, now ?? Created,
            resumeId, Sha256(resumeSecret));

    private static byte[] Bytes(byte value, int count)
    {
        byte[] result = new byte[count];
        Array.Fill(result, value);
        return result;
    }

    private static byte[] Sha256(byte[] value) => SHA256.HashData(value);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}

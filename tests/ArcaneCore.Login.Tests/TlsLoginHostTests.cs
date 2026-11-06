using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ArcaneCore.Cryptography;
using ArcaneCore.Cryptography.Login;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Login;
using ArcaneCore.Kernel.Realms;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Login.Tests;

public sealed class TlsLoginHostTests
{
    [Fact]
    public async Task RealLoginRouteEnforcesCredentialsBuildsAndTotpOverPinnedTls()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        string directory = Path.Combine(Path.GetTempPath(), "login-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "server.pfx");
        await File.WriteAllBytesAsync(path, certificate.Export(X509ContentType.Pfx));
        byte[] totpKey = RandomNumberGenerator.GetBytes(32);
        byte[] totpSecret = RandomNumberGenerator.GetBytes(20);
        (int port, int httpPort) = FreePorts();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            await using WebApplication app = LoginApplication.Create([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                ["Login:BindAddress"] = "127.0.0.1", ["Login:Port"] = port.ToString(CultureInfo.InvariantCulture),
                ["Login:CertificatePath"] = path, ["Login:TotpKeys:1"] = Convert.ToBase64String(totpKey),
                ["Kestrel:Endpoints:InjectedHttp:Url"] = $"http://127.0.0.1:{httpPort}",
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = $"Data Source={Path.Combine(directory, "auth.db")};Pooling=False",
                });
                builder.Services.AddSingleton<TimeProvider>(clock);
            });
            await app.StartAsync();
            Assert.Single(app.Urls);
            Assert.Contains($"https://127.0.0.1:{port}", app.Urls);
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                var account = new Account { Username = "TLSLOGIN", Salt = new byte[32], Verifier = new byte[32] };
                db.Accounts.Add(account);
                var realm = new RealmEntry { Name = "Test Realm", Address = "legacy:8085", ProtocolVersion = 7,
                    Host = "localhost", Port = 8443, State = RealmRuntimeState.Online, HeartbeatUtc = clock.GetUtcNow().UtcDateTime };
                db.Realms.Add(realm);
                await db.SaveChangesAsync();
                db.Set<RealmClientBuildRow>().AddRange(
                    new RealmClientBuildRow { RealmId = realm.Id, BuildId = 42, Status = LanternfallBuildStatus.Active, ChangedUtc = DateTime.UtcNow },
                    new RealmClientBuildRow { RealmId = realm.Id, BuildId = 43, Status = LanternfallBuildStatus.Retired, ChangedUtc = DateTime.UtcNow });
                var credential = new AccountCredential { AccountId = account.Id, Email = "Player@example.com", EmailNormalized = "player@example.com",
                    EmailVerifiedUtc = DateTime.UtcNow, SecurityStamp = RandomNumberGenerator.GetBytes(16), PasswordHash = new Argon2idPasswordHasher().Hash("correct-password") };
                db.Set<AccountCredential>().Add(credential);
                await db.SaveChangesAsync();
                using (HttpClient plaintext = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
                {
                    await Assert.ThrowsAsync<HttpRequestException>(() => plaintext.PostAsync($"http://127.0.0.1:{httpPort}/v1/login",
                        new StringContent("{\"account\":\"test@example.invalid\",\"password\":\"test-password\",\"protocol_version\":7,\"build_id\":42}", Encoding.UTF8, "application/json")));
                }
                using HttpClient client = new(new HttpClientHandler
                {
                    UseProxy = false,
                    ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented is not null && presented.GetCertHashString() == certificate.GetCertHashString(),
                }) { BaseAddress = new Uri($"https://127.0.0.1:{port}") };

                using (HttpResponseMessage success = await Post(client, "correct-password"))
                {
                    Assert.Equal(HttpStatusCode.OK, success.StatusCode);
                    using JsonDocument body = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
                    Assert.Equal(16, Convert.FromBase64String(body.RootElement.GetProperty("token_id").GetString()!).Length);
                    Assert.Equal(32, Convert.FromBase64String(body.RootElement.GetProperty("token").GetString()!).Length);
                    Assert.Equal(32, Convert.FromBase64String(body.RootElement.GetProperty("integrity_seed").GetString()!).Length);
                    Assert.Equal(60, body.RootElement.GetProperty("expires_in").GetInt32());
                    Assert.Equal("Test Realm", body.RootElement.GetProperty("realms")[0].GetProperty("name").GetString());
                }
                using (HttpResponseMessage bad = await Post(client, "incorrect-password")) Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
                using (HttpResponseMessage malformed = await client.PostAsync("/v1/login", new StringContent("{\"account\":\"x\",\"unexpected\":1}", Encoding.UTF8, "application/json")))
                    Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
                using (HttpResponseMessage oversized = await client.PostAsync("/v1/login", new StringContent(new string('x', 5000), Encoding.UTF8, "application/json")))
                    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
                using (HttpResponseMessage wrongBuild = await Post(client, "correct-password", build: 99)) Assert.Equal(HttpStatusCode.Conflict, wrongBuild.StatusCode);
                using (HttpResponseMessage retired = await Post(client, "correct-password", build: 43)) Assert.Equal(HttpStatusCode.Conflict, retired.StatusCode);
                var secondRealm = new RealmEntry { Name = "Other Realm", Address = "legacy:8086", ProtocolVersion = 7,
                    Host = "localhost", Port = 8444, State = RealmRuntimeState.Online, HeartbeatUtc = clock.GetUtcNow().UtcDateTime };
                db.Realms.Add(secondRealm); await db.SaveChangesAsync();
                db.Set<RealmClientBuildRow>().Add(new RealmClientBuildRow { RealmId = secondRealm.Id, BuildId = 42, Status = LanternfallBuildStatus.Active, ChangedUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
                IBanStore bans = scope.ServiceProvider.GetRequiredService<IBanStore>();
                await bans.BanAccountAsync(new BanRequest(account.Id, 0, "test", "test", Realm: realm.Id));
                using (HttpResponseMessage filtered = await Post(client, "correct-password"))
                {
                    Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
                    using JsonDocument body = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
                    Assert.Equal("Other Realm", Assert.Single(body.RootElement.GetProperty("realms").EnumerateArray()).GetProperty("name").GetString());
                }
                await bans.UnbanAccountAsync(account.Id, "test", "test");
                await bans.BanAccountAsync(new BanRequest(account.Id, 0, "test", "test", Realm: 0));
                using (HttpResponseMessage globallyBanned = await Post(client, "correct-password")) Assert.Equal(HttpStatusCode.Unauthorized, globallyBanned.StatusCode);
                await bans.UnbanAccountAsync(account.Id, "test", "test");
                await bans.BanIpAsync(new IpBanRequest("127.0.0.1", 0, "test", "test"));
                using (HttpResponseMessage ipBanned = await Post(client, "correct-password")) Assert.Equal(HttpStatusCode.Unauthorized, ipBanned.StatusCode);
                await bans.UnbanIpAsync("127.0.0.1");
                // Unban clears the shared context's tracker; reload before mutating these rows.
                credential = await db.Set<AccountCredential>().SingleAsync(x => x.AccountId == account.Id);
                account = await db.Accounts.SingleAsync(x => x.Id == account.Id);
                credential.EmailVerifiedUtc = null; await db.SaveChangesAsync();
                using (HttpResponseMessage unverified = await Post(client, "correct-password")) Assert.Equal(HttpStatusCode.Unauthorized, unverified.StatusCode);
                credential.EmailVerifiedUtc = DateTime.UtcNow; credential.LockedUntilUtc = DateTime.UtcNow.AddMinutes(5); await db.SaveChangesAsync();
                using (HttpResponseMessage locked = await Post(client, "correct-password")) Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
                credential.LockedUntilUtc = null; account.Security = AccountSecurity.Moderator; await db.SaveChangesAsync();
                using (HttpResponseMessage staff = await Post(client, "correct-password")) Assert.Equal(HttpStatusCode.Unauthorized, staff.StatusCode);
                using var protector = new AesGcmTotpSecretProtector(new Dictionary<byte, byte[]> { [1] = totpKey });
                credential.TotpKeyId = 1; credential.TotpSecretEncrypted = protector.Encrypt(1, totpSecret); credential.TotpEnabledUtc = DateTime.UtcNow; await db.SaveChangesAsync();
                using (HttpResponseMessage invalidTotp = await Post(client, "correct-password", "abcdef")) Assert.Equal(HttpStatusCode.BadRequest, invalidTotp.StatusCode);
                string code = Totp.Generate(totpSecret, clock.GetUtcNow().ToUnixTimeSeconds()).ToString("D6", CultureInfo.InvariantCulture);
                using (HttpResponseMessage accepted = await Post(client, "correct-password", code)) Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                using (HttpResponseMessage replay = await Post(client, "correct-password", code)) Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(totpKey); CryptographicOperations.ZeroMemory(totpSecret);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task MissingCertificateFailsClosed()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using WebApplication app = LoginApplication.Create([], builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Login:CertificatePath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pfx"),
                ["Database:Provider"] = "Sqlite", ["Database:ConnectionString"] = "Data Source=:memory:",
            }));
            await app.StartAsync();
        });
        Assert.Contains("certificate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string password, string totp = "", uint build = 42)
    {
        string json = JsonSerializer.Serialize(new { account = " PLAYER@example.com ", password, totp, protocol_version = 7, build_id = build });
        return client.PostAsync("/v1/login", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private static (int Https, int Http) FreePorts()
    {
        using var https = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        using var http = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        https.Start();
        http.Start();
        return (((IPEndPoint)https.LocalEndpoint).Port, ((IPEndPoint)http.LocalEndpoint).Port);
    }
}

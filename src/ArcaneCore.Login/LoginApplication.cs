using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Globalization;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Login;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Cryptography;
using ArcaneCore.Cryptography.Login;
using ArcaneCore.Kernel.Accounts;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace ArcaneCore.Login;

public static class LoginApplication
{
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAuthDatabase(builder.Configuration);
        builder.Services.AddScoped<IAccountCredentialStore, EfAccountCredentialStore>();
        builder.Services.AddScoped<ILanternfallRealmStore, EfLanternfallRealmStore>();
        builder.Services.AddScoped<SessionTokenService>();
        builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        builder.Services.AddSingleton<LoginGuards>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ITotpSecretProtector>(_ => LoginOptions.BuildTotpProtector(builder.Configuration));
        builder.Services.AddHostedService<AuthDatabaseStartup>();
        configure?.Invoke(builder);
        X509Certificate2? serverCertificate = null;
        builder.WebHost.ConfigureKestrel((context, kestrel) =>
        {
            // Replace the host's default Kestrel:Endpoints loader; only the TLS listener below is allowed.
            kestrel.Configure(new ConfigurationBuilder().Build(), reloadOnChange: false);
            LoginOptions options = context.Configuration.GetSection(LoginOptions.Section).Get<LoginOptions>() ?? new LoginOptions();
            X509Certificate2 certificate = LoginCertificate.Load(options.CertificatePath, options.CertificatePassword);
            serverCertificate = certificate;
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = LoginOptions.MaxBodyBytes;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            kestrel.Listen(IPAddress.Parse(options.BindAddress), options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(https => { https.ServerCertificate = certificate; https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13; });
            });
        });
        WebApplication app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(() => serverCertificate?.Dispose());
        app.Use(async (context, next) =>
        {
            if (!context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await next(context);
        });
app.MapPost("/v1/login", async (HttpContext context, LoginRequest request, IServiceScopeFactory scopeFactory, CancellationToken cancellationToken) =>
{
    if (!request.IsValid()) return Results.Json(new LoginError("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
    string? ip = AccountBanEvaluator.NormalizeIp(context.Connection.RemoteIpAddress?.ToString());
    if (string.IsNullOrWhiteSpace(ip)) return Results.Json(new LoginError("invalid_request"), statusCode: StatusCodes.Status400BadRequest);
    using IServiceScope scope = scopeFactory.CreateScope();
    IAccountCredentialStore credentials = scope.ServiceProvider.GetRequiredService<IAccountCredentialStore>();
    IPasswordHasher hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
    SessionTokenService tokens = scope.ServiceProvider.GetRequiredService<SessionTokenService>();
    ILanternfallRealmStore realmStore = scope.ServiceProvider.GetRequiredService<ILanternfallRealmStore>();
    IBanStore bans = scope.ServiceProvider.GetRequiredService<IBanStore>();
    ITotpSecretProtector protector = scope.ServiceProvider.GetRequiredService<ITotpSecretProtector>();
    LoginGuards guards = scope.ServiceProvider.GetRequiredService<LoginGuards>();
    string email = IAccountCredentialStore.NormalizeEmail(request.Account);
    if (!guards.TryEnter(ip)) return Results.Json(new LoginError("rate_limited"), statusCode: StatusCodes.Status429TooManyRequests);
    try
    {
    AccountCredential? credential = await credentials.FindByEmailNormalizedAsync(email, cancellationToken);
    bool valid = credential is not null && credential.PasswordHash is not null
        ? hasher.Verify(request.Password, credential.PasswordHash)
        : hasher.Verify(request.Password, LoginGuards.DummyHash);
    if (credential is null || !valid || credential.EmailVerifiedUtc is null)
    {
        if (credential is not null) await credentials.TryRecordFailureAsync(credential.AccountId, credential.SecurityStamp, scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime, 5, TimeSpan.FromMinutes(15), cancellationToken);
        return Results.Json(new LoginError("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
    }
    DateTime now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
    if (credential.LockedUntilUtc is { } locked && locked > now)
        return Results.Json(new LoginError("temporarily_locked"), statusCode: StatusCodes.Status429TooManyRequests);
    LoginAccountInfo? account = await credentials.FindAccountAsync(credential.AccountId, cancellationToken);
    if (account is null || account.Status != ArcaneCore.Kernel.Accounts.AccountStatus.Active)
        return Results.Json(new LoginError("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
    long? totpStep = null;
    bool needsTotp = credential.TotpSecretEncrypted is not null || account.Security > AccountSecurity.Player;
    if (needsTotp)
    {
        if (credential.TotpSecretEncrypted is null || string.IsNullOrEmpty(request.Totp))
            return Results.Json(new LoginError("totp_required"), statusCode: StatusCodes.Status401Unauthorized);
        if (!protector.TryDecrypt(credential.TotpKeyId, credential.TotpSecretEncrypted, out byte[] secret))
            return Results.Json(new LoginError("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
        try
        {
            long currentStep = new DateTimeOffset(now).ToUnixTimeSeconds() / 30;
            for (int offset = -1; offset <= 1; offset++)
            {
                if (Totp.Generate(secret, currentStep * 30, offset).ToString("D6", CultureInfo.InvariantCulture) == request.Totp)
                { totpStep = currentStep + offset; break; }
            }
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
        if (totpStep is null)
        {
            await credentials.TryRecordFailureAsync(account.AccountId, credential.SecurityStamp, now, 5, TimeSpan.FromMinutes(15), cancellationToken);
            return Results.Json(new LoginError("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
        }
    }
    if (await bans.GetActiveIpBanAsync(ip, cancellationToken) is not null)
        return Results.Json(new LoginError("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
    IReadOnlyList<LanternfallRealmInfo> realms = await realmStore.GetEligibleAsync(request.ProtocolVersion, request.BuildId, account.Security, now, TimeSpan.FromSeconds(30), cancellationToken);
    IReadOnlyList<AccountBanRecord> history = await bans.GetHistoryAsync(account.AccountId, cancellationToken);
    long unixNow = new DateTimeOffset(now).ToUnixTimeSeconds();
    var activeBans = history.Where(b => AccountBanEvaluator.IsActive(b, unixNow)).ToArray();
    if (activeBans.Any(b => b.Realm == 0))
        return Results.Json(new LoginError("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
    realms = realms.Where(r => !activeBans.Any(b => b.Realm == r.Id)).ToArray();
    if (realms.Count == 0)
        return Results.Json(new LoginError("realm_unavailable"), statusCode: StatusCodes.Status409Conflict);
    if (!await credentials.TryCompleteLoginAsync(account.AccountId, credential.SecurityStamp, now, totpStep, cancellationToken))
        return Results.Json(new LoginError("invalid_credentials"), statusCode: StatusCodes.Status401Unauthorized);
    SessionTokenTicket ticket = await tokens.IssueLoginAsync(account.AccountId, request.BuildId, ip, cancellationToken);
    using (ticket) return Results.Ok(new LoginResponse(ticket, realms));
    }
    finally { guards.Exit(); }
});
        return app;
    }
}

internal sealed class AuthDatabaseStartup(AuthDbInitializer initializer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => initializer.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class LoginOptions
{
    public const string Section = "Login";
    public const int MaxBodyBytes = 4096;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8790;
    public string CertificatePath { get; set; } = string.Empty;
    public string? CertificatePassword { get; set; }
    public static ITotpSecretProtector BuildTotpProtector(IConfiguration configuration)
    {
        var keys = new Dictionary<byte, byte[]>();
        foreach (IConfigurationSection entry in configuration.GetSection($"{Section}:TotpKeys").GetChildren())
        {
            if (!byte.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out byte id) || id == 0 || string.IsNullOrEmpty(entry.Value))
                throw new InvalidOperationException("Invalid TOTP key configuration.");
            byte[] key;
            try { key = Convert.FromBase64String(entry.Value); }
            catch (FormatException ex) { throw new InvalidOperationException("Invalid TOTP key configuration.", ex); }
            keys.Add(id, key);
        }
        try { return new AesGcmTotpSecretProtector(keys); }
        finally { foreach (byte[] key in keys.Values) CryptographicOperations.ZeroMemory(key); }
    }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record LoginRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("account")] string Account,
    [property: System.Text.Json.Serialization.JsonPropertyName("password")] string Password,
    [property: System.Text.Json.Serialization.JsonPropertyName("totp")] string? Totp,
    [property: System.Text.Json.Serialization.JsonPropertyName("protocol_version")] uint ProtocolVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("build_id")] uint BuildId)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Account) && Account.Length <= 254
        && Password is { Length: >= 10 and <= 256 } && ProtocolVersion != 0 && BuildId != 0
        && (string.IsNullOrEmpty(Totp) || Totp.Length == 6 && Totp.All(c => c >= '0' && c <= '9'));
}

public sealed record LoginError(string Error);

public sealed class LoginResponse
{
    public string token_id { get; }
    public string token { get; }
    public int expires_in { get; } = 60;
    public string integrity_seed { get; }
    public IReadOnlyList<LoginRealm> realms { get; }
    public LoginResponse(SessionTokenTicket ticket, IReadOnlyList<LanternfallRealmInfo> eligible)
    {
        token_id = Convert.ToBase64String(ticket.TokenId);
        token = Convert.ToBase64String(ticket.Token);
        integrity_seed = Convert.ToBase64String(ticket.IntegritySeed);
        realms = eligible.Select(x => new LoginRealm(x.Name, x.Host, x.Port, x.Population.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
    }
}

public sealed record LoginRealm(string name, string host, ushort port, string population);

internal static class LoginCertificate
{
    public static X509Certificate2 Load(string path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException("Login TLS certificate is missing.");
        // Schannel cannot use ephemeral private keys for SslStream on Windows.
        X509KeyStorageFlags flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
        if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() || DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime())
        {
            certificate.Dispose();
            throw new InvalidOperationException("Login TLS certificate is not currently valid or has no private key.");
        }
        return certificate;
    }
}

public sealed class LoginGuards
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Window, int Count)> _ip = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _hashSlots = new(2, 2);
    private DateTimeOffset _window = DateTimeOffset.UtcNow;
    private int _globalCount;
    public static string DummyHash { get; } = new Argon2idPasswordHasher().Hash("LanternfallDummyPassword-DoNotUse");
    public bool TryEnter(string ip)
    {
        lock (_gate)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now - _window >= TimeSpan.FromMinutes(1)) { _window = now; _globalCount = 0; _ip.Clear(); }
            if (++_globalCount > 1000) return false;
            if (!_ip.TryGetValue(ip, out var value))
            {
                if (_ip.Count >= 1000) return false;
                value = (now, 0);
            }
            _ip[ip] = (value.Window, value.Count + 1);
            return value.Count < 30 && _hashSlots.Wait(0);
        }
    }
    public void Exit() => _hashSlots.Release();
}

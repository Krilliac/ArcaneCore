using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Kernel.Resilience;
using ArcaneCore.Realm.Protocol;
using ArcaneCore.Realm.Protocol.Versioning;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// Handles one logon connection: the SRP6 challenge/proof exchange and the realm-list
/// reply. Drives the command loop documented in docs/M1_ACCEPTANCE.md.
///
/// Flow and packet shapes verified against vmangos src/realmd/AuthSocket.cpp; the
/// auto-create-on-login behavior follows WCell Services/WCell.AuthServer/Authentication.cs.
/// <para>
/// Transport protections (docs/ops/netguard.md): the unauthenticated lifetime and the frame read
/// deadline come from <c>Net:Protection</c> (through the <see cref="NetGuard"/> of the listener,
/// or the defaults when a host constructs the session without one, so they are never off by
/// accident); the per-address failure budget needs the guard's table and is skipped without it.
/// </para>
/// </summary>
public sealed class LogonSession(
    NetworkStream stream,
    IAccountStore accountStore,
    IRealmStore realmStore,
    AuthOptions options,
    ILogger logger,
    string remoteEndpoint,
    IBanStore? banStore = null,
    NetGuard? guard = null,
    RealmIpBanCache? ipBanCache = null,
    TimeProvider? timeProvider = null,
    PatchCatalog? patches = null)
{
    private static readonly NetProtectionOptions DefaultProtection = new();

    private readonly IBanStore? _banStore = banStore; // optional: null keeps every pre-ban call site unchanged
    private readonly RealmIpBanCache? _ipBanCache = ipBanCache; // optional: null reads the row on every challenge (vmangos realmd)
    private readonly NetProtectionOptions _protection = guard?.Options ?? DefaultProtection;
    private readonly IpKey? _address = IpKey.TryParse(remoteEndpoint, out IpKey parsedAddress) ? parsedAddress : null;

    // The client build's logon protocol (multi-version design S0), bound by the logon or reconnect challenge.
    // Only build 5875 is registered, so this is the 5875 protocol on every path that reaches the proof.
    private IAuthProtocol _authProtocol = Build5875AuthProtocol.Instance;
    private string _username = string.Empty;
    private Srp6Server? _srp;
    private Account? _pendingAccount;
    private bool _promptPin;
    private uint _gridSeed;
    private byte[]? _pinSalt;
    private string _clientOs = string.Empty;
    private string _clientPlatform = string.Empty;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private bool _isAutocreate;
    private byte[]? _autocreateVerifier;
    private bool _authenticated;
    private byte[]? _reconnectChallenge;
    private byte[]? _reconnectSessionKey;
    private bool _closeRequested;
    private int _challenges;
    private CancellationToken _sessionToken;
    private CancellationTokenSource? _unauthenticatedLifetime;
    private ReadDeadline? _deadline;
    private ClientPatch? _offeredPatch; // chosen at the challenge of a non-5875 build, sent at its proof
    private ClientPatch? _xferPatch;
    private CancellationTokenSource? _sessionCap; // Auth:MaxSessionDurationSeconds; re-armed per patch chunk so only an idle transfer hits it    // offered by XFER_INITIATE; XFER_ACCEPT/RESUME stream it

    /// <summary>XFER_DATA payload size (vmangos XFER_DATA_CHUNK::data[4096], AuthPackets.h:135-140).</summary>
    public const int XferChunkSize = 4096;

    // vmangos AuthSocket.cpp:248-262: the challenge body is sizeof(sAuthLogonChallengeBody) = 47 at
    // most and 47 - AUTH_LOGON_MAX_NAME (16) = 31 at least; username_len above 16 is dropped.
    private const int ChallengeMinBody = 31;
    private const int ChallengeMaxBody = 47;
    private const int MaxUsernameLength = 16;

    /// <summary>
    /// Most logon and reconnect challenges one connection may send. vmangos realmd accepts exactly one: a challenge is
    /// dispatched only in STATUS_CHALLENGE (AuthSocket.cpp:125-150), the initial state no handler returns to, and an
    /// out-of-state command closes the socket without a reply. ArcaneCore still answers a retry after a refused challenge
    /// or a failed proof on the same connection, so the cap is a backstop that holds with or without a
    /// <see cref="NetGuard"/>: past it the connection is closed without a reply and the attempt is charged.
    /// </summary>
    public const int MaxChallengesPerConnection = 8;

    // Locales realmd accepts (AuthSocket.cpp:213-230); the client sends them byte-reversed.
    private static readonly HashSet<string> AllowedLocales = new(StringComparer.Ordinal)
    {
        "enUS", "enGB", "koKR", "frFR", "deDE", "zhCN", "zhTW", "esES", "esMX", "ruRU",
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // vmangos MaxSessionDuration (AuthSocket.cpp:76-82): a hard cap on one connection's life.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.MaxSessionDurationSeconds > 0)
        {
            session.CancelAfter(TimeSpan.FromSeconds(options.MaxSessionDurationSeconds));
        }

        // Net:Protection:LogonUnauthenticatedLifetime: a connection without a successful proof is
        // closed well before the 300 s session cap. Disarmed (never re-armed) by the first good proof.
        using var unauthenticated = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
        TimeSpan lifetime = _protection.LogonUnauthenticatedLifetime;
        if (lifetime > TimeSpan.Zero)
        {
            unauthenticated.CancelAfter(lifetime);
        }

        _unauthenticatedLifetime = unauthenticated;
        _sessionCap = session;

        // Net:Protection:FrameReadTimeout (Auth:ReadTimeoutSeconds, when set, wins): one deadline per
        // connection, re-armed per packet, so no timer or token source is allocated per read.
        TimeSpan frameTimeout = options.ReadTimeoutSeconds > 0 ? TimeSpan.FromSeconds(options.ReadTimeoutSeconds) : _protection.FrameReadTimeout;
        using var deadline = new ReadDeadline(unauthenticated.Token, frameTimeout);
        _deadline = deadline;

        try
        {
            await RunCommandsAsync(unauthenticated.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (deadline.Expired)
            {
                if (guard is not null)
                {
                    guard.ReportFrameTimeout(remoteEndpoint);
                }
                else
                {
                    logger.LogWarning("[{Endpoint}] packet not completed within {Timeout}; closing", remoteEndpoint, frameTimeout);
                }
            }
            else if (unauthenticated.IsCancellationRequested && !session.IsCancellationRequested)
            {
                if (guard is not null)
                {
                    guard.ReportUnauthenticatedTimeout(remoteEndpoint, lifetime);
                }
                else
                {
                    logger.LogInformation("[{Endpoint}] not authenticated within {Lifetime}; closing", remoteEndpoint, lifetime);
                }
            }
            else
            {
                logger.LogInformation("[{Endpoint}] logon connection timed out; closing", remoteEndpoint);
            }
        }
        finally
        {
            _deadline = null;
            _unauthenticatedLifetime = null;
            _sessionCap = null;
        }
    }

    private async Task RunCommandsAsync(CancellationToken cancellationToken)
    {
        _sessionToken = cancellationToken;
        byte[] commandBuffer = new byte[1];
        while (!cancellationToken.IsCancellationRequested && !_closeRequested)
        {
            int read = await stream.ReadAsync(commandBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break; // client closed the connection
            }

            var command = (AuthCommand)commandBuffer[0];
            try
            {
                switch (command)
                {
                    case AuthCommand.LogonChallenge:
                        await HandleChallengeAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.LogonProof:
                        await HandleProofAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.ReconnectChallenge:
                        await HandleReconnectChallengeAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.ReconnectProof:
                        await HandleReconnectProofAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.RealmList:
                        await HandleRealmListAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case AuthCommand.XferAccept or AuthCommand.XferResume or AuthCommand.XferCancel when _xferPatch is not null:
                        if (!await HandleXferAsync(command, _xferPatch, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }

                        break;

                    default:
                        logger.LogWarning("[{Endpoint}] unsupported logon command 0x{Command:X2}; closing",
                            remoteEndpoint, commandBuffer[0]);
                        return;
                }
            }
            catch (ResilienceException ex)
            {
                await RefuseDatabaseUnavailableAsync(command, ex, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>
    /// The auth database is down, too slow, or its circuit is open (docs/ops/resilience.md): fail closed. Nothing was
    /// authenticated, the SRP state is dropped, the client gets WOW_FAIL_DB_BUSY so it shows a message instead of a
    /// silent disconnect, and the connection closes. One line per refused connection without a stack; the breaker
    /// logged the cause once when it opened. vmangos realmd has no equivalent: a dead database there fails every query.
    /// </summary>
    private async Task RefuseDatabaseUnavailableAsync(AuthCommand command, ResilienceException reason, CancellationToken cancellationToken)
    {
        ResetChallengeState();
        _closeRequested = true;
        logger.LogWarning("[{Endpoint}] auth database unavailable ({Reason}); refusing logon (fail closed)", remoteEndpoint, reason.Message);
        switch (command)
        {
            case AuthCommand.LogonChallenge:
                await SendChallengeFailureAsync(AuthResult.FailDbBusy, cancellationToken).ConfigureAwait(false);
                break;
            case AuthCommand.LogonProof:
                await SendProofFailureAsync(AuthResult.FailDbBusy, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleChallengeAsync(CancellationToken cancellationToken)
    {
        // Metered before anything is read or looked up (security finding S1).
        if (!BeginChallenge())
        {
            return;
        }

        // vmangos sets m_status = STATUS_INVALID on handler entry (AuthSocket.cpp:327): a new
        // challenge discards every piece of the previous one, so a proof can never be mixed with
        // another challenge's SRP state, username or auto-create flag.
        ResetChallengeState();

        // header after the command byte: protocol_version(1) + size(2 LE), then size bytes of body.
        byte[] header = new byte[3];
        await ReadPacketPartAsync(header, cancellationToken).ConfigureAwait(false);
        ushort bodySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1, 2));

        // Out-of-window sizes are dropped without a reply, as realmd does, before anything is allocated.
        if (bodySize < ChallengeMinBody || bodySize > ChallengeMaxBody)
        {
            logger.LogInformation("[{Endpoint}] challenge body size {Size} outside {Min}..{Max}; closing",
                remoteEndpoint, bodySize, ChallengeMinBody, ChallengeMaxBody);
            RecordFailure();
            _closeRequested = true;
            return;
        }

        byte[] body = new byte[bodySize];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);

        // The size window above (31..47) is what makes the fixed offsets below (locale at 17..20,
        // username length at 29) in range; keep the two in step.
        Invariant.Assert(body.Length >= ChallengeMinBody && ChallengeMinBody > 29, $"challenge body of {body.Length} bytes is shorter than the fixed fields it is indexed by");

        // Net:Protection:AuthFailureBurstPerIp: an address whose failure budget is spent is refused
        // before the locale check, the ban lookup and the account lookup (no query for a guesser).
        // FAIL_NOACCESS is what realmd answers a refused address (AuthSocket.cpp:338-352); then close.
        if (guard is not null && !guard.AllowsAuthAttempt(_address))
        {
            await SendChallengeFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            _closeRequested = true;
            return;
        }

        if (body[29] > MaxUsernameLength || !AllowedLocales.Contains(ReadLocale(body)))
        {
            logger.LogInformation("[{Endpoint}] challenge with bad username length or locale; closing", remoteEndpoint);
            RecordFailure();
            _closeRequested = true;
            return;
        }

        if (!LogonChallengeRequest.TryParse(body, out LogonChallengeRequest? request) || request is null)
        {
            logger.LogWarning("[{Endpoint}] malformed logon challenge", remoteEndpoint);
            RecordFailure();
            await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        ClientPatch? offeredPatch = null;
        if (!AuthProtocols.TryGet(request.Build, out IAuthProtocol authProtocol))
        {
            // Auth:AutoPatch (off by default): vmangos lets a wrong build finish the challenge and answers its proof with the patch
            // (AuthSocket.cpp:665-676, _HandleLogonProof__PostRecv_HandleInvalidVersion).
            offeredPatch = patches?.Find(request.Build, ReadLocale(body));
            if (offeredPatch is null)
            {
                logger.LogInformation("[{Endpoint}] rejected build {Build} (only {Supported} is supported)",
                    remoteEndpoint, request.Build, ClientBuild.Vanilla1121);
                await SendChallengeFailureAsync(AuthResult.VersionInvalid, cancellationToken).ConfigureAwait(false);
                return;
            }

            logger.LogInformation("[{Endpoint}] build {Build} will be offered patch {Patch}",
                remoteEndpoint, request.Build, Path.GetFileName(offeredPatch.Path));

            // A patched build is answered with the transfer at its proof and never reaches the integrity check or the realm list,
            // so it keeps the default protocol.
            authProtocol = Build5875AuthProtocol.Instance;
        }

        _authProtocol = authProtocol;

        string username = request.Username.ToUpperInvariant();
        if (options.StrictUsernameCharset && !IsPrintableAscii(username))
        {
            logger.LogInformation("[{Endpoint}] rejected account name '{Account}' (not printable ASCII)",
                remoteEndpoint, LogSafe.Escape(username));
            RecordFailure();
            await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        // IP ban: FAIL_NOACCESS before the account is even looked up and before any SRP state exists
        // (vmangos AuthSocket.cpp:338-352). A store error propagates and closes the connection (fail closed).
        // ArcaneCore validates the build and name above first, so a wrong-build client sees VersionInvalid.
        string? address = AccountBanEvaluator.AddressOfEndpoint(remoteEndpoint);
        if (_banStore is not null && address is not null && await IsIpBannedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation("[{Endpoint}] banned address tried to log in as '{Account}'",
                remoteEndpoint, LogSafe.Escape(username));
            RecordFailure();
            await SendChallengeFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        Account? account = await accountStore.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            if (!options.AutocreateAccounts)
            {
                logger.LogInformation("[{Endpoint}] unknown account '{Account}'", remoteEndpoint, LogSafe.Escape(username));
                RecordFailure();
                await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
                return;
            }

            // WCell-style auto-create: build the verifier as if password == username.
            // The account is only persisted later if the client's proof validates against it.
            byte[] salt = WowSrp6.GenerateSalt();
            BigInteger verifier = WowSrp6.ComputeVerifier(salt, username, username);
            _srp = new Srp6Server(salt, verifier);
            _isAutocreate = true;
            _autocreateVerifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength);
            logger.LogInformation("[{Endpoint}] auto-create candidate '{Account}' (proof pending)",
                remoteEndpoint, LogSafe.Escape(username));
        }
        else
        {
            switch (account.Status)
            {
                case AccountStatus.Banned:
                    RecordFailure();
                    await SendChallengeFailureAsync(AuthResult.Banned, cancellationToken).ConfigureAwait(false);
                    return;
                case AccountStatus.Suspended:
                    RecordFailure();
                    await SendChallengeFailureAsync(AuthResult.Suspended, cancellationToken).ConfigureAwait(false);
                    return;
            }

            // Ban rows: a permanent one answers FAIL_BANNED, a temporary one FAIL_SUSPENDED
            // (AuthSocket.cpp:464-476). The status column above stays an always-honoured override.
            if (_banStore is not null
                && await _banStore.GetActiveAccountBanAsync(account.Id, cancellationToken).ConfigureAwait(false) is { } ban)
            {
                logger.LogInformation("[{Endpoint}] banned account '{Account}' tried to log in ({Kind})",
                    remoteEndpoint, LogSafe.Escape(username), ban.IsPermanent ? "permanent" : "temporary");
                RecordFailure();
                await SendChallengeFailureAsync(
                    ban.IsPermanent ? AuthResult.Banned : AuthResult.Suspended, cancellationToken).ConfigureAwait(false);
                return;
            }

            // A zero or degenerate salt/verifier would let anyone forge the proof (vmangos
            // SRP6.cpp:188-199 refuses them; answer FAIL_NOACCESS).
            _srp = Srp6Server.TryCreate(account.Salt, WowSrp6.FromLittleEndian(account.Verifier));
            if (_srp is null)
            {
                logger.LogWarning("[{Endpoint}] account '{Account}' has a degenerate SRP salt or verifier; refusing",
                    remoteEndpoint, LogSafe.Escape(username));
                await SendChallengeFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
                return;
            }

            // vmangos AuthSocket.cpp:393-414,497-517: IP_LOCK from a different address
            // requires a configured factor, while ALWAYS_ENFORCE prompts even at the usual IP.
            bool moved = account.LockFlags.HasFlag(AccountLockFlags.IpLock)
                && !string.Equals(account.LastIp, address, StringComparison.OrdinalIgnoreCase);
            bool hasFactor = (account.LockFlags & (AccountLockFlags.FixedPin | AccountLockFlags.Totp)) != 0;
            if (moved && !hasFactor)
            {
                RecordFailure();
                await SendChallengeFailureAsync(AuthResult.Suspended, cancellationToken).ConfigureAwait(false);
                return;
            }
            _promptPin = moved || account.LockFlags.HasFlag(AccountLockFlags.AlwaysEnforce);
            _pendingAccount = account;

            _isAutocreate = false;
        }

        // Only now, after every early-out, does the connection commit to this account.
        _offeredPatch = offeredPatch;
        _username = username;
        _clientOs = request.Os;
        _clientPlatform = request.Platform;
        if (_promptPin)
        {
            Span<byte> seedBytes = stackalloc byte[4];
            RandomNumberGenerator.Fill(seedBytes);
            _gridSeed = BinaryPrimitives.ReadUInt32LittleEndian(seedBytes);
            _pinSalt = RandomNumberGenerator.GetBytes(PinHash.SaltLength);
        }

        await SendChallengeSuccessAsync(_srp, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProofAsync(CancellationToken cancellationToken)
    {
        byte[] body = new byte[LogonProofRequest.BodyLength];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);
        // AuthSocket.cpp:578-587: the 36-byte PINData immediately follows the fixed proof
        // whenever the client sets securityFlags bit 0 (SECURITY_FLAG_PIN), whatever other bits are set,
        // so the stream stays framed even when the flags are then refused below.
        byte[]? pinData = null;
        if ((body[^1] & 1) != 0)
        {
            pinData = new byte[PinHash.SaltLength + PinHash.HashLength];
            await ReadPacketPartAsync(pinData, cancellationToken).ConfigureAwait(false);
        }

        // One proof per challenge (vmangos STATUS_INVALID on entry, AuthSocket.cpp:555): the SRP
        // state is consumed whether the proof succeeds or fails, so it cannot be retried or replayed.
        Srp6Server? srp = _srp;
        string username = _username;
        bool isAutocreate = _isAutocreate;
        byte[]? autocreateVerifier = _autocreateVerifier;
        Account? account = _pendingAccount;
        bool promptPin = _promptPin;
        uint gridSeed = _gridSeed;
        byte[]? pinSalt = _pinSalt;
        string clientOs = _clientOs;
        string clientPlatform = _clientPlatform;
        ClientPatch? offeredPatch = _offeredPatch;
        ResetChallengeState();

        // The state machine: a proof consumes the challenge, so from here the session holds no SRP state
        // and is not authenticated until this very proof says so (vmangos STATUS_INVALID on entry).
        Invariant.Assert(_srp is null && !_authenticated && _username.Length == 0, "a logon proof consumes the challenge state before it is judged");

        if (srp is null
            || !LogonProofRequest.TryParse(body, out LogonProofRequest? request)
            || request is null)
        {
            logger.LogWarning("[{Endpoint}] logon proof without a valid challenge", remoteEndpoint);
            RecordFailure();
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        // vmangos checks the build before any SRP work (AuthSocket.cpp:669-676): a patched build gets WOW_FAIL_VERSION_UPDATE and the
        // XFER_INITIATE, nothing is authenticated, and the connection may only accept, resume or cancel the transfer.
        if (offeredPatch is not null)
        {
            await SendPatchOfferAsync(offeredPatch, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.SecurityFlags is not (0 or 1) || (promptPin && (request.SecurityFlags != 1 || pinData is null)))
        {
            logger.LogInformation("[{Endpoint}] unsupported security flags 0x{Flags:X2}",
                remoteEndpoint, request.SecurityFlags);
            RecordFailure();
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!srp.TryAcceptProof(username, request.ClientPublicKey, request.ClientProof)
            || srp.SessionKey is null || srp.ServerProof is null)
        {
            logger.LogInformation("[{Endpoint}] invalid proof for '{Account}'", remoteEndpoint, LogSafe.Escape(username));
            RecordFailure();
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (promptPin && (account is null || pinSalt is null
            || !VerifyPin(account, gridSeed, pinSalt, pinData!)))
        {
            RecordFailure();
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!VerifyVersion(request.ClientPublicKey, request.CrcHash, clientOs, clientPlatform))
        {
            RecordFailure();
            await SendProofFailureAsync(AuthResult.VersionInvalid, cancellationToken).ConfigureAwait(false);
            return;
        }

        // What is about to be persisted: K is the 40-byte key the world daemon keys its header cipher
        // with (account.sessionkey), and an auto-create candidate carries the verifier its challenge was
        // built from. Either missing means the challenge handler and this handler disagree; refuse rather
        // than store a row the world cannot use or an account with no verifier.
        if (!Invariant.Check(srp.SessionKey.Length == WowSrp6.SessionKeyLength, $"session key of {srp.SessionKey.Length} bytes about to be stored for an account")
            || !Invariant.Check(!isAutocreate || autocreateVerifier is not null, "auto-create proof without the verifier its challenge was built from"))
        {
            await SendProofFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (isAutocreate)
        {
            await accountStore.CreateAsync(
                new Account
                {
                    Username = username,
                    Salt = srp.Salt,
                    Verifier = autocreateVerifier!,
                    SessionKey = srp.SessionKey,
                    Status = AccountStatus.Active,
                },
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[{Endpoint}] auto-created account '{Account}'", remoteEndpoint, LogSafe.Escape(username));
        }
        else
        {
            await accountStore.UpdateLoginAsync(username, srp.SessionKey,
                    AccountBanEvaluator.AddressOfEndpoint(remoteEndpoint), cancellationToken)
                .ConfigureAwait(false);
        }

        _authenticated = true;
        _unauthenticatedLifetime?.CancelAfter(Timeout.InfiniteTimeSpan); // proven: the pre-proof lifetime no longer applies
        logger.LogInformation("[{Endpoint}] '{Account}' authenticated", remoteEndpoint, LogSafe.Escape(username));
        await SendProofSuccessAsync(srp.ServerProof, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReconnectChallengeAsync(CancellationToken cancellationToken)
    {
        if (!BeginChallenge())
        {
            return;
        }

        ResetChallengeState();
        byte[] header = new byte[3];
        await ReadPacketPartAsync(header, cancellationToken).ConfigureAwait(false);
        ushort bodySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1, 2));
        if (bodySize < ChallengeMinBody || bodySize > ChallengeMaxBody)
        {
            RecordFailure();
            _closeRequested = true;
            return;
        }

        byte[] body = new byte[bodySize];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);

        // Net:Protection:AuthFailureBurstPerIp applies to a reconnect exactly as to a logon challenge: a spent
        // address is refused before the ban and account lookups (see HandleChallengeAsync).
        if (guard is not null && !guard.AllowsAuthAttempt(_address))
        {
            await SendReconnectFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (body[29] > MaxUsernameLength || !AllowedLocales.Contains(ReadLocale(body))
            || !LogonChallengeRequest.TryParse(body, out LogonChallengeRequest? request) || request is null
            || !AuthProtocols.TryGet(request.Build, out IAuthProtocol reconnectProtocol))
        {
            RecordFailure();
            await SendReconnectFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        _authProtocol = reconnectProtocol;

        string username = request.Username.ToUpperInvariant();
        if (options.StrictUsernameCharset && !IsPrintableAscii(username))
        {
            RecordFailure();
            await SendReconnectFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? address = AccountBanEvaluator.AddressOfEndpoint(remoteEndpoint);
        if (_banStore is not null && address is not null && await IsIpBannedAsync(address, cancellationToken).ConfigureAwait(false))
        {
            RecordFailure();
            await SendReconnectFailureAsync(AuthResult.FailNoAccess, cancellationToken).ConfigureAwait(false);
            return;
        }

        Account? account = await accountStore.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        bool accountBanned = account?.Status == AccountStatus.Banned
            || account is not null && _banStore is not null
            && await _banStore.GetActiveAccountBanAsync(account.Id, cancellationToken).ConfigureAwait(false) is not null;
        if (account is null || account.Status != AccountStatus.Active
            || accountBanned
            || account.SessionKey is not { Length: 40 } sessionKey)
        {
            AuthResult result = accountBanned ? AuthResult.Banned
                : account?.Status == AccountStatus.Suspended ? AuthResult.Suspended : AuthResult.UnknownAccount;
            RecordFailure();
            await SendReconnectFailureAsync(result, cancellationToken).ConfigureAwait(false);
            return;
        }

        _username = username;
        _reconnectSessionKey = sessionKey.ToArray();
        _reconnectChallenge = RandomNumberGenerator.GetBytes(16);
        var writer = new PacketWriter(34);
        writer.WriteByte((byte)AuthCommand.ReconnectChallenge);
        writer.WriteByte((byte)AuthResult.Success);
        writer.WriteBytes(_reconnectChallenge);
        writer.WriteBytes(AuthConstants.VersionChallenge);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleReconnectProofAsync(CancellationToken cancellationToken)
    {
        const int bodyLength = 16 + 20 + 20 + 1;
        byte[] body = new byte[bodyLength];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);
        byte[]? challenge = _reconnectChallenge;
        byte[]? sessionKey = _reconnectSessionKey;
        string username = _username;
        _authenticated = false;
        _reconnectChallenge = null;
        _reconnectSessionKey = null;

        if (challenge is null || sessionKey is null || username.Length == 0 || body[^1] != 0)
        {
            CloseReconnectProof();
            return;
        }

        byte[] input = new byte[username.Length + 16 + 16 + sessionKey.Length];
        int offset = 0;
        offset += System.Text.Encoding.ASCII.GetBytes(username, input.AsSpan(offset));
        body.AsSpan(0, 16).CopyTo(input.AsSpan(offset)); offset += 16;
        challenge.CopyTo(input, offset); offset += challenge.Length;
        sessionKey.CopyTo(input, offset);
        byte[] expected = SHA1.HashData(input);
        bool valid = CryptographicOperations.FixedTimeEquals(expected, body.AsSpan(16, 20));
        if (!valid)
        {
            CloseReconnectProof();
            return;
        }

        // vmangos AuthSocket.cpp:944 and VerifyVersion: reconnects use SHA1(R1 || 20 zero
        // bytes), regardless of the configured client-file hash and of the client's OS/platform.
        if (!VerifyVersion(body.AsSpan(0, 16), body.AsSpan(36, 20), os: string.Empty, platform: string.Empty,
                isReconnect: true))
        {
            RecordFailure();
            var failure = new PacketWriter(2);
            failure.WriteByte((byte)AuthCommand.ReconnectProof);
            failure.WriteByte((byte)AuthResult.VersionInvalid);
            await stream.WriteAsync(failure.AsMemory(), cancellationToken).ConfigureAwait(false);
            _closeRequested = true;
            return;
        }

        _authenticated = true;
        var writer = new PacketWriter(2);
        writer.WriteByte((byte)AuthCommand.ReconnectProof);
        writer.WriteByte((byte)AuthResult.Success);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Charge one failed attempt to this connection's address (Net:Protection:AuthFailureBurstPerIp).</summary>
    private void RecordFailure() => guard?.RecordAuthFailure(_address);

    private bool VerifyPin(Account account, uint gridSeed, ReadOnlySpan<byte> serverSalt, ReadOnlySpan<byte> pinData)
    {
        if (pinData.Length != PinHash.SaltLength + PinHash.HashLength) return false;
        ReadOnlySpan<byte> clientSalt = pinData[..PinHash.SaltLength];
        ReadOnlySpan<byte> clientHash = pinData[PinHash.SaltLength..];
        // AuthSocket.cpp:695-731 tests FIXED_PIN first, then TOTP. Preserve that
        // priority even for a legacy row with both bits set.
        if (account.LockFlags.HasFlag(AccountLockFlags.FixedPin))
        {
            string text = account.SecurityInfo;
            if (text.Length is < 4 or > 10 || text.Any(c => c is < '0' or > '9')) return false;
            byte[] digits = text.Select(c => (byte)(c - '0')).ToArray();
            return PinHash.Verify(digits, gridSeed, serverSalt, clientSalt, clientHash);
        }

        if (account.LockFlags.HasFlag(AccountLockFlags.Totp)
            && Totp.TryDecodeSecret(account.SecurityInfo, out byte[] key))
        {
            // vmangos AuthSocket.cpp:719-729: four windows, [-2, -1, 0, +1].
            long now = _time.GetUtcNow().ToUnixTimeSeconds();
            for (int offset = -2; offset <= 1; offset++)
            {
                if (now / 30 + offset < 0) continue;
                byte[] digits = Totp.Generate(key, now, offset).ToString("D6", System.Globalization.CultureInfo.InvariantCulture)
                    .Select(c => (byte)(c - '0')).ToArray();
                if (PinHash.Verify(digits, gridSeed, serverSalt, clientSalt, clientHash)) return true;
            }
        }
        return false;
    }

    private bool VerifyVersion(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> proof, string os, string platform,
        bool isReconnect = false)
    {
        if (!options.StrictVersionCheck) return true;

        // vmangos VerifyVersion: a reconnect always proves against 20 zero bytes, with no build
        // lookup, so it needs no configured entry for the client tuple.
        if (isReconnect)
            return CryptographicOperations.FixedTimeEquals(ClientIntegrity.VersionProof(publicKey, new byte[20]), proof);

        foreach (ClientIntegrityHashOptions entry in options.IntegrityHashes)
        {
            if (entry.Build != _authProtocol.Build
                || !string.Equals(entry.Os, os, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(entry.Platform, platform, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Hash.Length != 40) continue;
            byte[] hash;
            try { hash = Convert.FromHexString(entry.Hash); }
            catch (FormatException) { continue; }
            if (hash.AsSpan().IndexOfAnyExcept((byte)0) < 0) return true; // zero = not filled server side
            if (CryptographicOperations.FixedTimeEquals(ClientIntegrity.VersionProof(publicKey, hash), proof))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Meter a logon or reconnect challenge before it costs anything (security finding S1). Every challenge does an
    /// IP-ban read, an account read, a ban-row read and builds SRP state, and only failures were charged, so one
    /// connection could repeat valid challenges without ever proving. Two rules, both before the body is read:
    /// <list type="bullet">
    /// <item>past <see cref="MaxChallengesPerConnection"/> the connection is closed without a reply (vmangos closes on any
    /// second challenge) and the attempt is charged;</item>
    /// <item>a challenge that abandons an issued, still unproven one charges the abandoned one as a failed attempt, so
    /// the per-address budget refuses the flood before the lookups (the existing check after the body read).</item>
    /// </list>
    /// A refused challenge leaves no state (its refusal was charged where the client is to blame), and a proof, good or
    /// bad, consumes its challenge, so a retry after either is not charged here; one challenge per connection, which is
    /// what the client sends, is never charged.
    /// </summary>
    /// <returns>False when the connection is closing and the challenge must not be handled.</returns>
    private bool BeginChallenge()
    {
        if (++_challenges > MaxChallengesPerConnection)
        {
            logger.LogInformation("[{Endpoint}] more than {Max} challenges on one connection; closing",
                remoteEndpoint, MaxChallengesPerConnection);
            RecordFailure();
            ResetChallengeState();
            _closeRequested = true;
            return false;
        }

        if (_srp is not null || _reconnectChallenge is not null)
        {
            RecordFailure();
        }

        return true;
    }

    /// <summary>
    /// Read the rest of a packet whose command byte has arrived, bounded by the frame deadline
    /// (Auth:ReadTimeoutSeconds, else Net:Protection:FrameReadTimeout). The deadline is armed for the
    /// read and disarmed after it; its expiry surfaces as the OperationCanceledException RunAsync reports.
    /// </summary>
    private async Task ReadPacketPartAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        ReadDeadline? deadline = _deadline;
        if (deadline is null || !deadline.Enabled)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        deadline.Arm();
        await stream.ReadExactlyAsync(buffer, deadline.Token).ConfigureAwait(false);
        deadline.Disarm();
    }

    /// <summary>The country field (body offset 17), reversed on the wire (AuthSocket.cpp:301-304).</summary>
    private static string ReadLocale(byte[] body)
    {
        char[] chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            chars[3 - i] = (char)body[17 + i];
        }

        return new string(chars);
    }

    /// <summary>
    /// The IP-ban check of a challenge or a reconnect challenge: through the listener's <see cref="RealmIpBanCache"/>
    /// when there is one, otherwise one row read per call (vmangos AuthSocket.cpp:338-352). A store error propagates.
    /// </summary>
    private async ValueTask<bool> IsIpBannedAsync(string address, CancellationToken cancellationToken)
        => _ipBanCache is not null
            ? await _ipBanCache.IsBannedAsync(address, _banStore!, cancellationToken).ConfigureAwait(false)
            : await _banStore!.GetActiveIpBanAsync(address, cancellationToken).ConfigureAwait(false) is not null;

    private static bool IsPrintableAscii(string value)
    {
        foreach (char c in value)
        {
            if (c < 0x21 || c > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// vmangos _HandleLogonProof__PostRecv_HandleInvalidVersion (AuthSocket.cpp:632-660): <c>[CMD_AUTH_LOGON_PROOF, WOW_FAIL_VERSION_UPDATE]</c>
    /// then XFER_INIT <c>{0x30, u8 5, "Patch", u64 size, md5[16]}</c> (AuthPackets.h:126-133).
    /// </summary>
    private async Task SendPatchOfferAsync(ClientPatch patch, CancellationToken cancellationToken)
    {
        byte[] packet = new byte[2 + 1 + 1 + 5 + 8 + 16];
        packet[0] = (byte)AuthCommand.LogonProof;
        packet[1] = (byte)AuthResult.VersionUpdate;
        packet[2] = (byte)AuthCommand.XferInitiate;
        packet[3] = 5;
        "Patch"u8.CopyTo(packet.AsSpan(4));
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(9), (ulong)patch.Size);
        patch.Md5.CopyTo(packet.AsSpan(17));
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        _xferPatch = patch;
        // The download outlives the pre-proof lifetime; the session cap (Auth:MaxSessionDurationSeconds) still applies and the client resumes.
        _unauthenticatedLifetime?.CancelAfter(Timeout.InfiniteTimeSpan);
        logger.LogInformation("[{Endpoint}] offered patch {Patch} ({Size} bytes)", remoteEndpoint, Path.GetFileName(patch.Path), patch.Size);
    }

    /// <summary>
    /// XFER_ACCEPT streams from the start, XFER_RESUME (+ u64 offset) from that byte, XFER_CANCEL closes (vmangos AuthSocket.cpp:1152-1200);
    /// false closes the connection. Chunks are <c>{0x31, u16 size, data}</c> (RepeatInternalXferLoop, AuthSocket.cpp:1306-1329).
    /// </summary>
    private async Task<bool> HandleXferAsync(AuthCommand command, ClientPatch patch, CancellationToken cancellationToken)
    {
        long offset = 0;
        if (command == AuthCommand.XferCancel)
        {
            return false;
        }

        if (command == AuthCommand.XferResume)
        {
            byte[] start = new byte[8];
            await ReadPacketPartAsync(start, cancellationToken).ConfigureAwait(false);
            ulong requested = BinaryPrimitives.ReadUInt64LittleEndian(start);
            if (requested >= (ulong)patch.Size)
            {
                logger.LogInformation("[{Endpoint}] patch resume outside the file ({Offset})", remoteEndpoint, requested);
                return false;
            }

            offset = (long)requested;
        }

        await using FileStream file = new(patch.Path, FileMode.Open, FileAccess.Read, FileShare.Read, XferChunkSize, useAsync: true);
        if (file.Length != patch.Size)
        {
            logger.LogWarning("[{Endpoint}] patch {Patch} changed on disk during the offer; closing", remoteEndpoint, patch.Path);
            return false;
        }

        file.Seek(offset, SeekOrigin.Begin);
        byte[] chunk = new byte[3 + XferChunkSize];
        chunk[0] = (byte)AuthCommand.XferData;
        int read;
        while ((read = await file.ReadAsync(chunk.AsMemory(3, XferChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(1, 2), (ushort)read);
            ExtendSessionCap(); // bytes are flowing: the cap counts from the last chunk, so a stalled download still closes
            await stream.WriteAsync(chunk.AsMemory(0, 3 + read), cancellationToken).ConfigureAwait(false);
        }

        ExtendSessionCap(); // the idle cap runs again from the end of the transfer
        logger.LogInformation("[{Endpoint}] patch sent from byte {Offset}", remoteEndpoint, offset);
        return true;
    }

    /// <summary>
    /// An active patch download is exempt from the absolute session cap: each chunk restarts the
    /// <c>Auth:MaxSessionDurationSeconds</c> timer, so the cap becomes an idle limit while the transfer runs (0 keeps it off).
    /// </summary>
    private void ExtendSessionCap()
    {
        if (options.MaxSessionDurationSeconds > 0)
        {
            _sessionCap?.CancelAfter(TimeSpan.FromSeconds(options.MaxSessionDurationSeconds));
        }
    }

    private void ResetChallengeState()
    {
        _offeredPatch = null;
        _xferPatch = null;
        _srp = null;
        _pendingAccount = null;
        _promptPin = false;
        _gridSeed = 0;
        _pinSalt = null;
        _clientOs = string.Empty;
        _clientPlatform = string.Empty;
        _isAutocreate = false;
        _autocreateVerifier = null;
        _username = string.Empty;
        _authenticated = false; // a new challenge or proof always revokes the previous authentication
        _reconnectChallenge = null;
        _reconnectSessionKey = null;
    }

    private async Task HandleRealmListAsync(CancellationToken cancellationToken)
    {
        // request body is a single unused uint32.
        byte[] discard = new byte[4];
        await ReadPacketPartAsync(discard, cancellationToken).ConfigureAwait(false);

        if (!_authenticated)
        {
            logger.LogWarning("[{Endpoint}] realm list requested before authentication", remoteEndpoint);
            return;
        }

        // Authenticated means the proof consumed the SRP state; a session that is both authenticated and
        // holding an Srp6Server could answer a second proof against a stale challenge.
        Invariant.Assert(_srp is null, "an authenticated logon session holds no SRP state");

        IReadOnlyList<RealmEntry> realms = await realmStore.GetRealmsAsync(cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> packet = RealmListWriter.Build(realms, charactersPerRealm: 0);
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("[{Endpoint}] sent realm list ({Count} realm(s))", remoteEndpoint, realms.Count);
    }

    // --- reply builders ----------------------------------------------------------

    private async Task SendChallengeSuccessAsync(Srp6Server srp, CancellationToken cancellationToken)
    {
        var writer = new PacketWriter(128);
        writer.WriteByte((byte)AuthCommand.LogonChallenge);
        writer.WriteByte(0x00); // protocol/unk
        writer.WriteByte((byte)AuthResult.Success);
        writer.WriteBytes(srp.PublicEphemeral);          // B (32, LE)
        writer.WriteByte(1);                             // g length
        writer.WriteByte(WowSrp6.Generator);             // g = 7
        writer.WriteByte((byte)WowSrp6.KeyLength);       // N length = 32
        writer.WriteBytes(WowSrp6.NLittleEndian);        // N (32, LE)
        writer.WriteBytes(srp.Salt);                     // s (32)
        writer.WriteBytes(AuthConstants.VersionChallenge); // crc_salt (16)
        writer.WriteByte(_promptPin ? (byte)1 : (byte)0);
        if (_promptPin)
        {
            writer.WriteUInt32(_gridSeed);
            writer.WriteBytes(_pinSalt!);
        }
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendChallengeFailureAsync(AuthResult result, CancellationToken cancellationToken)
    {
        var writer = new PacketWriter(3);
        writer.WriteByte((byte)AuthCommand.LogonChallenge);
        writer.WriteByte(0x00);
        writer.WriteByte((byte)result);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendProofSuccessAsync(byte[] serverProof, CancellationToken cancellationToken)
    {
        // AUTH_LOGON_PROOF_S (build < 6299): cmd, error=0, M2[20], surveyId(u32)=0.
        var writer = new PacketWriter(26);
        writer.WriteByte((byte)AuthCommand.LogonProof);
        writer.WriteByte((byte)AuthResult.Success);
        writer.WriteBytes(serverProof);
        writer.WriteUInt32(0); // survey id
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendProofFailureAsync(AuthResult result, CancellationToken cancellationToken)
    {
        // Build 5875: vmangos AuthSocket.cpp:745-756, 854-864 sends only cmd and
        // error. Its two padding bytes apply to builds newer than 6005.
        var writer = new PacketWriter(2);
        writer.WriteByte((byte)AuthCommand.LogonProof);
        writer.WriteByte((byte)result);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendReconnectFailureAsync(AuthResult result, CancellationToken cancellationToken)
    {
        var writer = new PacketWriter(2);
        writer.WriteByte((byte)AuthCommand.ReconnectChallenge);
        writer.WriteByte((byte)result);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
        _closeRequested = true;
    }

    /// <summary>A failed or out-of-order reconnect proof is a failed authentication attempt (Net:Protection:AuthFailureBurstPerIp).</summary>
    private void CloseReconnectProof()
    {
        RecordFailure();
        _closeRequested = true;
    }
}

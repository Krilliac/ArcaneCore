using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// Handles one logon connection: the SRP6 challenge/proof exchange and the realm-list
/// reply. Drives the command loop documented in docs/M1_ACCEPTANCE.md.
///
/// Flow and packet shapes verified against vmangos src/realmd/AuthSocket.cpp; the
/// auto-create-on-login behavior follows WCell Services/WCell.AuthServer/Authentication.cs.
/// </summary>
public sealed class LogonSession(
    NetworkStream stream,
    IAccountStore accountStore,
    IRealmStore realmStore,
    AuthOptions options,
    ILogger logger,
    string remoteEndpoint)
{
    private string _username = string.Empty;
    private Srp6Server? _srp;
    private bool _isAutocreate;
    private byte[]? _autocreateVerifier;
    private bool _authenticated;
    private bool _closeRequested;
    private CancellationToken _sessionToken;

    // vmangos AuthSocket.cpp:248-262: the challenge body is sizeof(sAuthLogonChallengeBody) = 47 at
    // most and 47 - AUTH_LOGON_MAX_NAME (16) = 31 at least; username_len above 16 is dropped.
    private const int ChallengeMinBody = 31;
    private const int ChallengeMaxBody = 47;
    private const int MaxUsernameLength = 16;

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

        try
        {
            await RunCommandsAsync(session.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("[{Endpoint}] logon connection timed out; closing", remoteEndpoint);
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
            switch (command)
            {
                case AuthCommand.LogonChallenge:
                    await HandleChallengeAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case AuthCommand.LogonProof:
                    await HandleProofAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case AuthCommand.RealmList:
                    await HandleRealmListAsync(cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    logger.LogWarning("[{Endpoint}] unsupported logon command 0x{Command:X2}; closing",
                        remoteEndpoint, commandBuffer[0]);
                    return;
            }
        }
    }

    private async Task HandleChallengeAsync(CancellationToken cancellationToken)
    {
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
            _closeRequested = true;
            return;
        }

        byte[] body = new byte[bodySize];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);

        if (body[29] > MaxUsernameLength || !AllowedLocales.Contains(ReadLocale(body)))
        {
            logger.LogInformation("[{Endpoint}] challenge with bad username length or locale; closing", remoteEndpoint);
            _closeRequested = true;
            return;
        }

        if (!LogonChallengeRequest.TryParse(body, out LogonChallengeRequest? request) || request is null)
        {
            logger.LogWarning("[{Endpoint}] malformed logon challenge", remoteEndpoint);
            await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Build != ClientBuild.Vanilla1121)
        {
            logger.LogInformation("[{Endpoint}] rejected build {Build} (only {Supported} is supported)",
                remoteEndpoint, request.Build, ClientBuild.Vanilla1121);
            await SendChallengeFailureAsync(AuthResult.VersionInvalid, cancellationToken).ConfigureAwait(false);
            return;
        }

        string username = request.Username.ToUpperInvariant();
        if (options.StrictUsernameCharset && !IsPrintableAscii(username))
        {
            logger.LogInformation("[{Endpoint}] rejected account name '{Account}' (not printable ASCII)",
                remoteEndpoint, LogSafe.Escape(username));
            await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        Account? account = await accountStore.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            if (!options.AutocreateAccounts)
            {
                logger.LogInformation("[{Endpoint}] unknown account '{Account}'", remoteEndpoint, LogSafe.Escape(username));
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
                    await SendChallengeFailureAsync(AuthResult.Banned, cancellationToken).ConfigureAwait(false);
                    return;
                case AccountStatus.Suspended:
                    await SendChallengeFailureAsync(AuthResult.Suspended, cancellationToken).ConfigureAwait(false);
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

            _isAutocreate = false;
        }

        // Only now, after every early-out, does the connection commit to this account.
        _username = username;

        await SendChallengeSuccessAsync(_srp, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProofAsync(CancellationToken cancellationToken)
    {
        byte[] body = new byte[LogonProofRequest.BodyLength];
        await ReadPacketPartAsync(body, cancellationToken).ConfigureAwait(false);

        // One proof per challenge (vmangos STATUS_INVALID on entry, AuthSocket.cpp:555): the SRP
        // state is consumed whether the proof succeeds or fails, so it cannot be retried or replayed.
        Srp6Server? srp = _srp;
        string username = _username;
        bool isAutocreate = _isAutocreate;
        byte[]? autocreateVerifier = _autocreateVerifier;
        ResetChallengeState();

        if (srp is null
            || !LogonProofRequest.TryParse(body, out LogonProofRequest? request)
            || request is null)
        {
            logger.LogWarning("[{Endpoint}] logon proof without a valid challenge", remoteEndpoint);
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        // PIN / authenticator security flags are not supported in M1.
        if (request.SecurityFlags != 0)
        {
            logger.LogInformation("[{Endpoint}] unsupported security flags 0x{Flags:X2}",
                remoteEndpoint, request.SecurityFlags);
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!srp.TryAcceptProof(username, request.ClientPublicKey, request.ClientProof)
            || srp.SessionKey is null || srp.ServerProof is null)
        {
            logger.LogInformation("[{Endpoint}] invalid proof for '{Account}'", remoteEndpoint, LogSafe.Escape(username));
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
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
            await accountStore.UpdateSessionKeyAsync(username, srp.SessionKey, cancellationToken)
                .ConfigureAwait(false);
        }

        _authenticated = true;
        logger.LogInformation("[{Endpoint}] '{Account}' authenticated", remoteEndpoint, LogSafe.Escape(username));
        await SendProofSuccessAsync(srp.ServerProof, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Read the rest of a packet whose command byte has arrived, bounded by the read timeout.</summary>
    private async Task ReadPacketPartAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (options.ReadTimeoutSeconds <= 0)
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.ReadTimeoutSeconds));
        await stream.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
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

    private void ResetChallengeState()
    {
        _srp = null;
        _isAutocreate = false;
        _autocreateVerifier = null;
        _username = string.Empty;
        _authenticated = false; // a new challenge or proof always revokes the previous authentication
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
        writer.WriteByte(0x00);                          // security flag (none)
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
        // vmangos failure path: cmd, error, uint16(0).
        var writer = new PacketWriter(4);
        writer.WriteByte((byte)AuthCommand.LogonProof);
        writer.WriteByte((byte)result);
        writer.WriteUInt16(0);
        await stream.WriteAsync(writer.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}

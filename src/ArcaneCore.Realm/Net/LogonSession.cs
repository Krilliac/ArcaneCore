using System.Buffers.Binary;
using System.Net.Sockets;
using System.Numerics;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] commandBuffer = new byte[1];
        while (!cancellationToken.IsCancellationRequested)
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
        // header after the command byte: protocol_version(1) + size(2 LE), then size bytes of body.
        byte[] header = new byte[3];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        ushort bodySize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1, 2));

        byte[] body = new byte[bodySize];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

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

        _username = request.Username.ToUpperInvariant();
        Account? account = await accountStore.FindByUsernameAsync(_username, cancellationToken).ConfigureAwait(false);

        if (account is null)
        {
            if (!options.AutocreateAccounts)
            {
                logger.LogInformation("[{Endpoint}] unknown account '{Account}'", remoteEndpoint, _username);
                await SendChallengeFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
                return;
            }

            // WCell-style auto-create: build the verifier as if password == username.
            // The account is only persisted later if the client's proof validates against it.
            byte[] salt = WowSrp6.GenerateSalt();
            BigInteger verifier = WowSrp6.ComputeVerifier(salt, _username, _username);
            _srp = new Srp6Server(salt, verifier);
            _isAutocreate = true;
            _autocreateVerifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength);
            logger.LogInformation("[{Endpoint}] auto-create candidate '{Account}' (proof pending)",
                remoteEndpoint, _username);
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

            _srp = new Srp6Server(account.Salt, WowSrp6.FromLittleEndian(account.Verifier));
            _isAutocreate = false;
        }

        await SendChallengeSuccessAsync(_srp, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleProofAsync(CancellationToken cancellationToken)
    {
        byte[] body = new byte[LogonProofRequest.BodyLength];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);

        if (_srp is null
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

        if (!_srp.TryAcceptProof(_username, request.ClientPublicKey, request.ClientProof)
            || _srp.SessionKey is null || _srp.ServerProof is null)
        {
            logger.LogInformation("[{Endpoint}] invalid proof for '{Account}'", remoteEndpoint, _username);
            await SendProofFailureAsync(AuthResult.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_isAutocreate)
        {
            await accountStore.CreateAsync(
                new Account
                {
                    Username = _username,
                    Salt = _srp.Salt,
                    Verifier = _autocreateVerifier!,
                    SessionKey = _srp.SessionKey,
                    Status = AccountStatus.Active,
                },
                cancellationToken).ConfigureAwait(false);
            logger.LogInformation("[{Endpoint}] auto-created account '{Account}'", remoteEndpoint, _username);
        }
        else
        {
            await accountStore.UpdateSessionKeyAsync(_username, _srp.SessionKey, cancellationToken)
                .ConfigureAwait(false);
        }

        _authenticated = true;
        logger.LogInformation("[{Endpoint}] '{Account}' authenticated", remoteEndpoint, _username);
        await SendProofSuccessAsync(_srp.ServerProof, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleRealmListAsync(CancellationToken cancellationToken)
    {
        // request body is a single unused uint32.
        byte[] discard = new byte[4];
        await stream.ReadExactlyAsync(discard, cancellationToken).ConfigureAwait(false);

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

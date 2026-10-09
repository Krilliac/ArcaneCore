using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ArcaneCore.MockClient.Protocol;

public sealed class WorldClient : IAsyncDisposable
{
    private const ushort AuthChallenge = 0x01EC;
    private const ushort AuthSession = 0x01ED;
    private const ushort AuthResponse = 0x01EE;
    private const ushort AddonInfo = 0x02EF;
    private const byte AuthOk = 0x0C;
    private const int MaximumClientPayload = 0x2800 - 4;
    private const int MaximumServerPayload = 32 * 1024;
    private const int MaximumSkippedBytes = 1024 * 1024;
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(10);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientHeaderCipher? _cipher;
    private int _reading;
    private int _state; // 0 connected, 1 authenticating, 2 authenticated, 3 closed

    /// <summary>The deadline of each bounded operation of this client (<see cref="ProtocolIO.OperationTimeout"/> when it was created).</summary>
    internal TimeSpan OperationTimeout { get; set; } = ProtocolIO.OperationTimeout;

    private WorldClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <summary>The local end of this connection (its port identifies the connection), or null once closed.</summary>
    public EndPoint? LocalEndPoint
    {
        get
        {
            try
            {
                return _client.Client.LocalEndPoint;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }
    }

    public static async Task<WorldClient> ConnectAsync(IPEndPoint endpoint, CancellationToken ct = default)
        => new(await ProtocolIO.ConnectAsync(endpoint, ct).ConfigureAwait(false));

    public async Task<byte> AuthenticateAsync(string account, byte[] sessionKey, CancellationToken ct = default, uint build = 5875)
    {
        string username = ProtocolPackets.NormalizeAccount(account);
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (sessionKey.Length != 40)
        {
            throw new ArgumentException("A vanilla world session key must contain exactly 40 bytes.", nameof(sessionKey));
        }

        EnterReader();
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            ExitReader();
            throw new InvalidOperationException("World authentication requires a fresh connected client.");
        }

        try
        {
            return await ProtocolIO.BoundedAsync("World authentication", OperationTimeout, ct, async token =>
            {
                await _sendLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    WorldFrame challenge = await ReadFrameAsync(token).ConfigureAwait(false);
                    ExpectOpcode(challenge, AuthChallenge);
                    if (challenge.Payload.Length != 4)
                    {
                        throw new MockProtocolException("World auth challenge must contain exactly one uint32 seed.");
                    }

                    uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(challenge.Payload);
                    uint clientSeed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
                    byte[] payload = ProtocolPackets.WorldAuth(username, sessionKey, clientSeed, serverSeed, build);
                    await SendFrameAsync(AuthSession, payload, token).ConfigureAwait(false);
                    _cipher = new ClientHeaderCipher(sessionKey);

                    byte[] rawHeader = await ProtocolIO.ReadExactAsync(_stream, 4, "world auth response header", token).ConfigureAwait(false);
                    // This owned server sends its one-byte rejection before initializing encryption.
                    // Match the exact plaintext frame; all normal success headers use the cipher.
                    if (rawHeader.AsSpan().SequenceEqual(new byte[] { 0, 3, 0xEE, 1 }))
                    {
                        byte[] failure = await ProtocolIO.ReadExactAsync(_stream, 1, "world auth rejection", token).ConfigureAwait(false);
                        if (failure[0] == AuthOk)
                        {
                            throw new MockProtocolException("World server reported success with a plaintext auth response.");
                        }

                        Close();
                        return failure[0];
                    }

                    WorldFrame response = await ReadFrameAsync(token, rawHeader).ConfigureAwait(false);
                    ExpectOpcode(response, AuthResponse);
                    if (response.Payload.Length == 0)
                    {
                        throw new MockProtocolException("World auth response has no result byte.");
                    }

                    byte result = response.Payload[0];
                    if (result != AuthOk)
                    {
                        Close();
                        return result;
                    }

                    WorldFrame addon = await ReadFrameAsync(token).ConfigureAwait(false);
                    ExpectOpcode(addon, AddonInfo);
                    Volatile.Write(ref _state, 2);
                    return result;
                }
                finally
                {
                    _sendLock.Release();
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            Close();
            throw;
        }
        finally
        {
            ExitReader();
        }
    }

    /// <summary>Send a bounded raw packet for the owned fixture, including pre-authentication state tests.</summary>
    public async Task SendAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaximumClientPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), $"Client payload exceeds {MaximumClientPayload} bytes.");
        }

        RequireUsable();
        try
        {
            await ProtocolIO.BoundedAsync("World packet send", OperationTimeout, ct, async token =>
            {
                await _sendLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    RequireUsable();
                    await SendFrameAsync(opcode, payload, token).ConfigureAwait(false);
                    return true;
                }
                finally
                {
                    _sendLock.Release();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or TimeoutException)
        {
            Close();
            throw;
        }
    }

    public Task<WorldFrame> ReadAsync(CancellationToken ct = default)
        => ReadOperationAsync("World packet read", ct, ReadFrameAsync);

    /// <summary>
    /// Waits until the server has sent something (or closed the connection) without consuming a
    /// byte and without the per-operation deadline that bounds <see cref="ReadAsync"/>. A reader
    /// that is legitimately quiet for a long stretch, such as a held settlement, calls this before
    /// each read so only the arrival of a frame is held to that deadline, never the silence before
    /// it. A timed-out read cannot be resumed: it closes the connection.
    /// </summary>
    public async Task WaitForTrafficAsync(CancellationToken ct = default)
    {
        RequireUsable();
        EnterReader();
        try
        {
            while (!_client.Client.Poll(0, SelectMode.SelectRead))
            {
                await Task.Delay(IdlePollInterval, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ExitReader();
        }
    }

    public Task<WorldFrame> ReadUntilAsync(ushort opcode, CancellationToken ct = default, int maxPackets = 128)
    {
        if (maxPackets is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPackets), "Packet search limit must be 1..1024.");
        }

        return ReadOperationAsync($"World packet search for 0x{opcode:X4}", ct, async token =>
        {
            int skippedBytes = 0;
            for (int index = 0; index < maxPackets; index++)
            {
                WorldFrame frame = await ReadFrameAsync(token).ConfigureAwait(false);
                if (frame.Opcode == opcode)
                {
                    return frame;
                }

                skippedBytes += frame.Payload.Length + 4;
                if (skippedBytes > MaximumSkippedBytes)
                {
                    throw new MockProtocolException($"Packet search exceeded {MaximumSkippedBytes} skipped bytes.");
                }
            }

            throw new MockProtocolException($"Opcode 0x{opcode:X4} was not found within {maxPackets} packets.");
        });
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private async Task<WorldFrame> ReadOperationAsync(
        string operation, CancellationToken ct, Func<CancellationToken, Task<WorldFrame>> action)
    {
        RequireUsable();
        EnterReader();
        try
        {
            return await ProtocolIO.BoundedAsync(operation, OperationTimeout, ct, action).ConfigureAwait(false);
        }
        catch
        {
            Close();
            throw;
        }
        finally
        {
            ExitReader();
        }
    }

    private Task<WorldFrame> ReadFrameAsync(CancellationToken token) => ReadFrameAsync(token, null);

    private async Task<WorldFrame> ReadFrameAsync(CancellationToken token, byte[]? existingHeader)
    {
        byte[] header = existingHeader ?? await ProtocolIO.ReadExactAsync(_stream, 4, "world packet header", token).ConfigureAwait(false);
        _cipher?.Decrypt(header);
        int size = BinaryPrimitives.ReadUInt16BigEndian(header);
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        int bodyLength = size - 2;
        if (bodyLength is < 0 or > MaximumServerPayload)
        {
            throw new MockProtocolException($"World frame 0x{opcode:X4} declares invalid size {size}; maximum body is {MaximumServerPayload} bytes.");
        }

        byte[] body = await ProtocolIO.ReadExactAsync(_stream, bodyLength, $"world packet 0x{opcode:X4} body", token).ConfigureAwait(false);
        return new WorldFrame(opcode, body);
    }

    private async Task SendFrameAsync(ushort opcode, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        byte[] header = ProtocolPackets.ClientHeader(opcode, payload.Length);
        _cipher?.Encrypt(header);
        await _stream.WriteAsync(header, token).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await _stream.WriteAsync(payload, token).ConfigureAwait(false);
        }
    }

    private void EnterReader()
    {
        if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            throw new InvalidOperationException("Only one world reader may be active at a time.");
        }
    }

    private void ExitReader() => Volatile.Write(ref _reading, 0);

    private void RequireUsable()
    {
        int state = Volatile.Read(ref _state);
        if (state == 3)
        {
            throw new ObjectDisposedException(nameof(WorldClient));
        }

        if (state == 1)
        {
            throw new InvalidOperationException("World authentication is in progress.");
        }
    }

    private void Close()
    {
        Interlocked.Exchange(ref _state, 3);
        _client.Dispose();
        _cipher?.Clear();
    }

    private static void ExpectOpcode(WorldFrame frame, ushort expected)
    {
        if (frame.Opcode != expected)
        {
            throw new MockProtocolException($"Expected world opcode 0x{expected:X4}, received 0x{frame.Opcode:X4}.");
        }
    }
}

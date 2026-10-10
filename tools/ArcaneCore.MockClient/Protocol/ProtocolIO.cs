using System.Net;
using System.Net.Sockets;

namespace ArcaneCore.MockClient.Protocol;

internal static class ProtocolIO
{
    /// <summary>The tool's deadline for one network operation (a read, a send, a logon step): 5 s.</summary>
    internal static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The deadline <see cref="BoundedAsync{T}(string, CancellationToken, Func{CancellationToken, Task{T}})"/> applies, and a new
    /// <see cref="WorldClient"/>'s. The tool keeps <see cref="DefaultOperationTimeout"/> against a live server. The in-process test
    /// suite raises it once, before any test runs: there it is only a hang bound, because a loaded machine answered a real SQLite
    /// character create later than 5 s and failed healthy scenarios; each test's own deadline still ends a hung one.
    /// </summary>
    internal static TimeSpan OperationTimeout { get; set; } = DefaultOperationTimeout;

    internal static Task<T> BoundedAsync<T>(
        string operation, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> action)
        => BoundedAsync(operation, OperationTimeout, cancellationToken, action);

    internal static async Task<T> BoundedAsync<T>(
        string operation, TimeSpan timeout, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await action(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{operation} exceeded the {timeout.TotalSeconds:0}-second deadline.", exception);
        }
    }

    internal static async Task<TcpClient> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        LoopbackOnly.Validate(endpoint);
        var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        try
        {
            await BoundedAsync("Loopback connection", cancellationToken, async token =>
            {
                await client.ConnectAsync(endpoint.Address, endpoint.Port, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static async Task<byte[]> ReadExactAsync(
        Stream stream, int count, string field, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[count];
        int received = 0;
        while (received < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(received), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Peer closed while reading {field}: received {received} of {count} bytes.");
            }

            received += read;
        }

        return buffer;
    }
}

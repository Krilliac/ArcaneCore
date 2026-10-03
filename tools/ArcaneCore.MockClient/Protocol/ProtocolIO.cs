using System.Net;
using System.Net.Sockets;

namespace ArcaneCore.MockClient.Protocol;

internal static class ProtocolIO
{
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);

    internal static async Task<T> BoundedAsync<T>(
        string operation, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        try
        {
            return await action(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{operation} exceeded the {OperationTimeout.TotalSeconds:0}-second deadline.", exception);
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

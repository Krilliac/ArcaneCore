using System.Net;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ArcaneCore.MockClient.Tests")]

namespace ArcaneCore.MockClient.Protocol;

public sealed record LogonResult(byte[] SessionKey, IReadOnlyList<MockRealm> Realms);

public sealed record MockRealm(string Name, string Address)
{
    /// <summary>Resolve only a numeric loopback address. Realm names never trigger DNS or remote traffic.</summary>
    public IPEndPoint GetLoopbackEndpoint()
    {
        if (!IPEndPoint.TryParse(Address, out IPEndPoint? endpoint))
        {
            throw new MockProtocolException($"Realm '{Name}' has an invalid numeric endpoint '{Address}'.");
        }

        LoopbackOnly.Validate(endpoint);
        return endpoint;
    }
}

public sealed record WorldFrame(ushort Opcode, byte[] Payload);

public sealed class MockAuthException : IOException
{
    public MockAuthException(byte result, string stage)
        : base($"Logon {stage} was rejected with result 0x{result:X2}.") => Result = result;

    public byte Result { get; }
}

public sealed class MockProtocolException(string message) : IOException(message);

internal static class LoopbackOnly
{
    internal static void Validate(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        IPAddress address = endpoint.Address.IsIPv4MappedToIPv6
            ? endpoint.Address.MapToIPv4()
            : endpoint.Address;
        if (!IPAddress.IsLoopback(address) || endpoint.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Mock clients require a numeric loopback endpoint with a nonzero port.", nameof(endpoint));
        }
    }
}

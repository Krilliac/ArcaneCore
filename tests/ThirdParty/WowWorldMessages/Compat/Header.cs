using System.Buffers.Binary;

// The oracle reads plaintext payloads captured before ArcaneCore encrypts a socket header.
// These adapters provide only the unencrypted vanilla header API used by the generated models.
namespace WowSrp.Header;

public struct HeaderData(uint size, uint opcode)
{
    public uint Size { get; set; } = size;
    public uint Opcode { get; set; } = opcode;
}

public interface IServerEncrypter
{
    Task WriteServerHeaderAsync(Stream stream, uint size, uint opcode, CancellationToken cancellationToken = default);
}

public interface IClientEncrypter
{
    Task WriteClientHeaderAsync(Stream stream, uint size, uint opcode, CancellationToken cancellationToken = default);
}

public class NullCrypter : IServerEncrypter, IClientEncrypter
{
    public async Task<HeaderData> ReadServerHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        return new HeaderData(BinaryPrimitives.ReadUInt16BigEndian(header), BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)));
    }

    public async Task<HeaderData> ReadClientHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[6];
        await stream.ReadExactlyAsync(header, cancellationToken);
        return new HeaderData(BinaryPrimitives.ReadUInt16BigEndian(header), BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2)));
    }

    public Task WriteServerHeaderAsync(Stream stream, uint size, uint opcode, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(header, checked((ushort)size));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), checked((ushort)opcode));
        return stream.WriteAsync(header, cancellationToken).AsTask();
    }

    public Task WriteClientHeaderAsync(Stream stream, uint size, uint opcode, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header, checked((ushort)size));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), opcode);
        return stream.WriteAsync(header, cancellationToken).AsTask();
    }
}

public sealed class VanillaDecryption : NullCrypter { }

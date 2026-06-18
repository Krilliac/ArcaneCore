using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Verifies the vanilla header cipher is self-consistent: what the server encrypts on its
/// outbound SMSG headers, a peer with the same key decrypts back, and vice versa — across
/// multiple packets (the rolling state must stay in lock-step).
/// </summary>
public sealed class WorldHeaderCryptTests
{
    private static readonly byte[] Key = CreateKey();

    [Fact]
    public void ServerEncryptedSmsgHeaders_DecryptOnClient()
    {
        var server = new WorldHeaderCrypt();
        var client = new WorldHeaderCrypt();
        server.Initialize(Key);
        client.Initialize(Key);

        for (int packet = 0; packet < 50; packet++)
        {
            byte[] original = [(byte)packet, 0x12, (byte)(packet * 7), 0x34];
            byte[] header = (byte[])original.Clone();

            server.EncryptHeader(header);            // server sends a 4-byte SMSG header
            Assert.NotEqual(original, header);       // actually encrypted
            client.DecryptHeader(header);            // client receives and decrypts

            Assert.Equal(original, header);
        }
    }

    [Fact]
    public void ClientEncryptedCmsgHeaders_DecryptOnServer()
    {
        var server = new WorldHeaderCrypt();
        var client = new WorldHeaderCrypt();
        server.Initialize(Key);
        client.Initialize(Key);

        for (int packet = 0; packet < 50; packet++)
        {
            byte[] original = [(byte)packet, 0x9A, 0x00, 0x04, 0x00, 0x00];
            byte[] header = (byte[])original.Clone();

            client.EncryptHeader(header);            // client sends a 6-byte CMSG header
            server.DecryptHeader(header);            // server receives and decrypts

            Assert.Equal(original, header);
        }
    }

    [Fact]
    public void BeforeInitialization_HeadersAreUntouched()
    {
        var crypt = new WorldHeaderCrypt();
        byte[] header = [1, 2, 3, 4];
        byte[] original = (byte[])header.Clone();

        crypt.EncryptHeader(header);

        Assert.Equal(original, header);
    }

    private static byte[] CreateKey()
    {
        byte[] key = new byte[40];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i * 3 + 1);
        }

        return key;
    }
}

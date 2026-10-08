using WowSrp.Header;

namespace WowWorldMessages;

public interface IWorldClientMessage : IWorldMessage
{
    public Task WriteEncryptedClientAsync(Stream w, IClientEncrypter enc,
        CancellationToken cancellationToken = default);
}
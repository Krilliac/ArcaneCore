namespace WowWorldMessages;

public interface IWorldMessage
{
    public Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default);
}
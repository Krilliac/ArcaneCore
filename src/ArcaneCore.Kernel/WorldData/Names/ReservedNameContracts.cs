namespace ArcaneCore.Kernel.WorldData.Names;

public interface IReservedNameStore
{
    Task<IReadOnlySet<string>> LoadAsync(CancellationToken cancellationToken = default);
}

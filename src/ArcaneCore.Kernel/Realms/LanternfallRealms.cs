using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Kernel.Realms;

public sealed record LanternfallRealmInfo(int Id, string Name, string Host, ushort Port, float Population);

public interface ILanternfallRealmStore
{
    Task<IReadOnlyList<LanternfallRealmInfo>> GetEligibleAsync(
        uint protocolVersion, uint buildId, AccountSecurity security, DateTime utcNow,
        TimeSpan maxHeartbeatAge, CancellationToken cancellationToken = default);
}

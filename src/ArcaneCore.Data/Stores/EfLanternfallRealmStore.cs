using System.Net;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Stores;

public sealed class EfLanternfallRealmStore(AuthDbContext db) : ILanternfallRealmStore
{
    public async Task<IReadOnlyList<LanternfallRealmInfo>> GetEligibleAsync(
        uint protocolVersion, uint buildId, AccountSecurity security, DateTime utcNow,
        TimeSpan maxHeartbeatAge, CancellationToken cancellationToken = default)
    {
        if (protocolVersion == 0 || buildId == 0 || utcNow.Kind != DateTimeKind.Utc || maxHeartbeatAge < TimeSpan.Zero)
            throw new ArgumentException("Invalid realm eligibility query.");
        DateTime minimumHeartbeat = utcNow - maxHeartbeatAge;
        byte securityLevel = (byte)security;
        List<RealmEntry> realms = await db.Realms.AsNoTracking()
            .Where(r => r.ProtocolVersion == protocolVersion && r.State == RealmRuntimeState.Online
                && r.HeartbeatUtc != null && r.HeartbeatUtc >= minimumHeartbeat && r.HeartbeatUtc <= utcNow
                && r.AllowedSecurityLevel <= securityLevel && r.Host != string.Empty && r.Port != 0)
            .OrderBy(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        HashSet<int> ids = realms.Select(r => r.Id).ToHashSet();
        HashSet<int> accepted = await db.Set<RealmClientBuildRow>().AsNoTracking()
            .Where(b => ids.Contains(b.RealmId) && b.BuildId == buildId
                && (b.Status == LanternfallBuildStatus.Active || b.Status == LanternfallBuildStatus.Grace))
            .Select(b => b.RealmId).ToHashSetAsync(cancellationToken).ConfigureAwait(false);
        return realms.Where(r => accepted.Contains(r.Id) && IsHost(r.Host))
            .Select(r => new LanternfallRealmInfo(r.Id, r.Name, r.Host, r.Port, r.Population)).ToArray();
    }

    private static bool IsHost(string host)
    {
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains(':')) return false;
        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.Basic;
    }
}

namespace ArcaneCore.TestSupport;

/// <summary>A clock a test steps by hand (whole Unix seconds).</summary>
public sealed class ManualTimeProvider(long unixSeconds) : TimeProvider
{
    private long _unix = unixSeconds;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _unix));

    public void Advance(long seconds) => Interlocked.Add(ref _unix, seconds);
}

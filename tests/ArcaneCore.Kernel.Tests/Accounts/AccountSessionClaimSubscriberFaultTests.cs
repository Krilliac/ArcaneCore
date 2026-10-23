using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Accounts;

/// <summary>
/// Bug #116: a throwing revocation subscriber must not abort <see cref="InMemoryAccountSessionClaimStore.Claim"/> /
/// <see cref="InMemoryAccountSessionClaimStore.Invalidate"/> nor starve the subscribers registered after it.
/// </summary>
public sealed class AccountSessionClaimSubscriberFaultTests
{
    private const string Site = "accountclaims.subscriber";

    [Fact]
    public void Claim_FirstSubscriberThrows_ReturnsClaimAndStillNotifiesSecondSubscriber()
    {
        var store = new InMemoryAccountSessionClaimStore();
        var seen = new List<AccountSessionClaim>();
        string message = "claim-subscriber-fault-" + Guid.NewGuid();
        using IDisposable first = store.SubscribeRevocations(_ => throw new InvalidOperationException(message));
        using IDisposable second = store.SubscribeRevocations(seen.Add);

        store.Claim(7, Guid.NewGuid());
        AccountSessionClaim superseding = store.Claim(7, Guid.NewGuid());

        Assert.Equal(2, superseding.Generation);
        Assert.Equal(superseding, store.GetActive(7));
        Assert.Equal(superseding, Assert.Single(seen));
        Assert.Contains(
            FaultLedger.Shared.Snapshot(),
            fault => fault.Site == Site && fault.SampleMessage.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Invalidate_FirstSubscriberThrows_ReturnsGenerationAndStillNotifiesSecondSubscriber()
    {
        var store = new InMemoryAccountSessionClaimStore();
        var seen = new List<AccountSessionClaim>();
        using IDisposable first = store.SubscribeRevocations(_ => throw new InvalidOperationException("invalidate-subscriber-fault"));
        using IDisposable second = store.SubscribeRevocations(seen.Add);

        long generation = store.Invalidate(9);

        Assert.Equal(1, generation);
        Assert.Null(store.GetActive(9));
        AccountSessionClaim tombstone = Assert.Single(seen);
        Assert.Equal(new AccountSessionClaim(9, 1, Guid.Empty), tombstone);
    }

    [Fact]
    public void Claim_SubscriberThrowsOutOfMemory_StillPropagates()
    {
        var store = new InMemoryAccountSessionClaimStore();
        using IDisposable sub = store.SubscribeRevocations(_ => throw new OutOfMemoryException());

        store.Claim(11, Guid.NewGuid());

        Assert.Throws<OutOfMemoryException>(() => store.Claim(11, Guid.NewGuid()));
    }
}

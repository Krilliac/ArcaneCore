using System.Numerics;
using ArcaneCore.World.Playerbots.Events;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// The festival claims on the board (P7a H2, design 4.2 and review item I9): a bot takes a place at an anchor at decision time, so the
/// caps hold when many bots decide in the same pass; a held think renews the claim, it expires 60 seconds after its last renewal, and
/// leaving frees the place at once. The board's claims alone: no world, no brains.
/// </summary>
public sealed class PlayerbotFestivalClaimsTests
{
    private const string Town = "town:stormwind";
    private const uint Alliance = 1, Horde = 2;

    private static readonly PlayerbotFestivalCaps Caps = new(PerGossipAnchor: 6, PerServiceAnchor: 3, PerTown: 24);

    private static PlayerbotFestivalAnchor Service(ulong guid, ushort root = 2) => new(guid, 15310, new Vector3(guid, 0f, 0f), PlayerbotAnchorKind.Service, root);

    private static PlayerbotFestivalAnchor Gossip(ulong guid, ushort root = 2) => new(guid, 15311, new Vector3(guid, 0f, 0f), PlayerbotAnchorKind.Gossip, root);

    private static Guid Bot(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static bool Claim(PlayerbotFestivalClaims claims, PlayerbotFestivalAnchor anchor, int bot, long nowMs, PlayerbotFestivalCaps? caps = null,
        string town = Town, uint team = Alliance)
        => claims.TryClaim(anchor, anchor.Position + new Vector3(10f, 0f, 0f), Bot(bot), town, team, caps ?? Caps, nowMs);

    // --- the caps at decision time ---------------------------------------------------------------------------------------

    [Fact]
    public void TenBotsDecidingInOnePass_GetExactlyThreePlacesAtAServiceAnchor()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor giver = Service(1);

        bool[] results = [.. Enumerable.Range(1, 10).Select(bot => Claim(claims, giver, bot, nowMs: 1_000))];

        Assert.Equal(3, results.Count(r => r));
        Assert.Equal([true, true, true, false, false, false, false, false, false, false], results); // first come, first served
        Assert.Equal(3, claims.CountAt(giver.Guid, 1_000));
        Assert.Equal(3, claims.Active(1_000).Count);
    }

    [Fact]
    public void TenBotsDecidingInOnePass_GetExactlySixPlacesAtAGossipAnchor()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor reveler = Gossip(2);

        int granted = Enumerable.Range(1, 10).Count(bot => Claim(claims, reveler, bot, nowMs: 1_000));

        Assert.Equal(6, granted);
        Assert.Equal(6, claims.CountAt(reveler.Guid, 1_000));
    }

    [Fact]
    public void ARefusedBot_HoldsNothing()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor giver = Service(1);
        for (int bot = 1; bot <= 3; bot++) Assert.True(Claim(claims, giver, bot, 0));

        Assert.False(Claim(claims, giver, 4, 0));

        Assert.Null(claims.ClaimOf(Bot(4), 0));
        Assert.Equal(3, claims.CountInTown(Town, Alliance, 0));
        Assert.DoesNotContain(claims.Active(0), c => c.Bot == Bot(4));
    }

    [Fact]
    public void TheTownCap_HoldsAcrossAnchors_PerTeam()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalCaps caps = new(PerGossipAnchor: 6, PerServiceAnchor: 3, PerTown: 24);
        PlayerbotFestivalAnchor[] anchors = [.. Enumerable.Range(1, 6).Select(n => Gossip((ulong)n))]; // room for 36 bots

        int granted = 0;
        for (int bot = 1; bot <= 40; bot++)
            if (Claim(claims, anchors[(bot - 1) % anchors.Length], bot, 0, caps)) granted++;

        Assert.Equal(24, granted);
        Assert.Equal(24, claims.CountInTown(Town, Alliance, 0));
        Assert.All(anchors, anchor => Assert.InRange(claims.CountAt(anchor.Guid, 0), 0, 6));

        // The cap is the team's own: the same town still has room for the other team (and another town for the same team).
        Assert.True(Claim(claims, anchors[0], 100, 0, caps, team: Horde)); // the anchor holds 4 of its 6 places
        Assert.Equal(1, claims.CountInTown(Town, Horde, 0));
        Assert.Equal(24, claims.CountInTown(Town, Alliance, 0));
        Assert.True(Claim(claims, Gossip(50), 101, 0, caps, town: "town:ironforge"));
    }

    [Fact]
    public void AServiceCapOfZero_NeverClaimsAQuestGiver_ButStillClaimsAGossipNpc()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalCaps none = Caps with { PerServiceAnchor = 0 };

        Assert.False(Claim(claims, Service(1), 1, 0, none));
        Assert.False(Claim(claims, Service(2), 2, 0, none));
        Assert.True(Claim(claims, Gossip(3), 3, 0, none));
        Assert.Single(claims.Active(0));
    }

    [Fact]
    public void ABotHoldsOnePlace_AClaimAtAnotherAnchorMovesIt_AndAClaimAtTheSameAnchorKeepsIt()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor first = Service(1), second = Service(2);
        Assert.True(Claim(claims, first, 1, 0));

        // The same anchor again, even when it is full for everyone else: still its own place.
        Assert.True(Claim(claims, first, 2, 0));
        Assert.True(Claim(claims, first, 3, 0));
        Assert.False(Claim(claims, first, 4, 0));
        Assert.True(Claim(claims, first, 1, 0));
        Assert.Equal(3, claims.CountAt(first.Guid, 0));

        // Another anchor: the old place is given up, the count there falls.
        Assert.True(Claim(claims, second, 1, 0));
        Assert.Equal(2, claims.CountAt(first.Guid, 0));
        Assert.Equal(1, claims.CountAt(second.Guid, 0));
        Assert.Equal(second.Guid, claims.ClaimOf(Bot(1), 0)!.Value.Anchor.Guid);
        Assert.Equal(3, claims.Active(0).Count);
        Assert.True(Claim(claims, first, 4, 0)); // the freed place is free
    }

    [Fact]
    public void AClaim_RecordsTheSpotTheTownAndTheTeam_ForTheLanesWorkList()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor giver = Service(7, root: 12);
        var spot = new Vector3(3f, 4f, 5f);

        Assert.True(claims.TryClaim(giver, spot, Bot(9), Town, Horde, Caps, 5_000));

        PlayerbotFestivalClaim claim = Assert.Single(claims.Active(5_000));
        Assert.Equal((Bot(9), giver, spot, Town, Horde), (claim.Bot, claim.Anchor, claim.Spot, claim.Town, claim.Team));
        Assert.Equal(5_000, claim.RenewedAtMs);
        Assert.Equal((ushort)12, claim.Anchor.Root);
    }

    // --- release, renewal and expiry -------------------------------------------------------------------------------------

    [Fact]
    public void AReleasedClaim_FreesThePlaceAtOnce()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor giver = Service(1);
        for (int bot = 1; bot <= 3; bot++) Assert.True(Claim(claims, giver, bot, 0));
        Assert.False(Claim(claims, giver, 4, 0));

        claims.Release(Bot(2));

        Assert.Null(claims.ClaimOf(Bot(2), 0));
        Assert.Equal(2, claims.CountAt(giver.Guid, 0));
        Assert.Equal(2, claims.CountInTown(Town, Alliance, 0));
        Assert.True(Claim(claims, giver, 4, 0));
        Assert.Equal(3, claims.CountAt(giver.Guid, 0));

        claims.Release(Bot(2)); // releasing twice, or a bot that never claimed, is harmless
        claims.Release(Bot(99));
        Assert.Equal(3, claims.CountAt(giver.Guid, 0));
    }

    [Fact]
    public void AClaimThatIsNotRenewed_ExpiresSixtySecondsAfterItsLastRenewal()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor giver = Service(1);
        Assert.Equal(60_000, PlayerbotFestivalClaims.ExpiryMs);
        for (int bot = 1; bot <= 3; bot++) Assert.True(Claim(claims, giver, bot, 10_000));

        // Just inside the minute: still held, the anchor is still full.
        Assert.Equal(3, claims.CountAt(giver.Guid, 69_999));
        Assert.False(Claim(claims, giver, 4, 69_999));

        // Just past it: gone, with no release call, so a bot that logged out frees its place.
        Assert.Equal(0, claims.CountAt(giver.Guid, 70_001));
        Assert.Null(claims.ClaimOf(Bot(1), 70_001));
        Assert.Empty(claims.Active(70_001));
        Assert.Equal(0, claims.CountInTown(Town, Alliance, 70_001));
        Assert.True(Claim(claims, giver, 4, 70_001));
    }

    [Fact]
    public void ARenewal_ExtendsTheClaimByAnotherMinute_AndOnlyForTheBotThatRenews()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor giver = Service(1);
        Assert.True(Claim(claims, giver, 1, 0));
        Assert.True(Claim(claims, giver, 2, 0));

        Assert.True(claims.Renew(Bot(1), 50_000)); // a held think
        Assert.Equal(50_000, claims.ClaimOf(Bot(1), 50_000)!.Value.RenewedAtMs);
        Assert.True(claims.Renew(Bot(1), 100_000)); // 100 s after the claim was taken: renewed in time, so alive

        Assert.NotNull(claims.ClaimOf(Bot(1), 150_000));
        Assert.Null(claims.ClaimOf(Bot(2), 100_000)); // never renewed: expired at 60 s
        Assert.Equal(1, claims.CountAt(giver.Guid, 100_000));
        Assert.Null(claims.ClaimOf(Bot(1), 160_001));
    }

    [Fact]
    public void ARenewalAfterTheExpiry_Fails_AndDoesNotRevive()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalAnchor giver = Service(1);
        Assert.True(Claim(claims, giver, 1, 0));

        Assert.False(claims.Renew(Bot(1), 61_000));
        Assert.False(claims.Renew(Bot(99), 1_000)); // never claimed
        Assert.Null(claims.ClaimOf(Bot(1), 61_000));
        Assert.Equal(0, claims.CountAt(giver.Guid, 61_000));
    }

    [Fact]
    public void AnExpiredPlace_CountsFreeForTheCapEvenBeforeAnythingSweepsIt()
    {
        var claims = new PlayerbotFestivalClaims();
        PlayerbotFestivalCaps caps = Caps with { PerTown = 2 };
        Assert.True(Claim(claims, Gossip(1), 1, 0, caps));
        Assert.True(Claim(claims, Gossip(2), 2, 0, caps));
        Assert.False(Claim(claims, Gossip(3), 3, 30_000, caps)); // the town is at its cap

        Assert.True(Claim(claims, Gossip(3), 3, 61_000, caps)); // both have expired
        Assert.Equal(1, claims.CountInTown(Town, Alliance, 61_000));
    }
}

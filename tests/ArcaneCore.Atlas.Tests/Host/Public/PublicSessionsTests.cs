using ArcaneCore.Atlas.Host.Public;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>Cookie sessions (design 0.4 steps 5 and 6): bounds, expiry, rotation at login and logout, kick.</summary>
public sealed class PublicSessionsTests
{
    private static PublicOptions Options(Action<PublicOptions>? edit = null)
    {
        var options = new PublicOptions();
        edit?.Invoke(options);
        return options;
    }

    [Fact]
    public void AGuestSession_HasARandomCookie_ACsrfToken_AndAFindableHash()
    {
        var sessions = new PublicSessions(Options(), new ManualTime());

        IssuedSession a = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;
        IssuedSession b = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;

        Assert.Equal(43, a.Cookie.Length);
        Assert.NotEqual(a.Cookie, b.Cookie);
        Assert.NotEqual(a.Session.Csrf, b.Session.Csrf);
        Assert.False(a.Session.OpaqueKey.AsSpan().SequenceEqual(b.Session.OpaqueKey));
        Assert.Same(a.Session, sessions.Find(a.Cookie));
        Assert.DoesNotContain(a.Cookie, a.Session.HashKey, StringComparison.Ordinal);
        Assert.Null(sessions.Find(null));
        Assert.Null(sessions.Find("short"));
        Assert.Null(sessions.Find(new string('A', 43)));
        Assert.True(a.Session.CsrfMatches(a.Session.Csrf));
        Assert.False(a.Session.CsrfMatches(b.Session.Csrf));
        Assert.False(a.Session.CsrfMatches(null));
    }

    [Fact]
    public void GuestSessions_AreCappedPerAddress_ByEvictingThatAddressesIdlestSession()
    {
        var time = new ManualTime();
        var sessions = new PublicSessions(Options(o => o.Guest.MaxSessionsPerIp = 3), time);
        IssuedSession first = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;
        time.Advance(TimeSpan.FromSeconds(1));
        IssuedSession second = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;
        time.Advance(TimeSpan.FromSeconds(1));
        IssuedSession third = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;
        IssuedSession other = sessions.CreateGuest("2.2.2.2", "2.2.2.2")!;
        time.Advance(TimeSpan.FromSeconds(1));
        sessions.Find(first.Cookie); // touched: now the idlest is the second

        IssuedSession fourth = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;

        Assert.Null(sessions.Find(second.Cookie));
        Assert.NotNull(sessions.Find(first.Cookie));
        Assert.NotNull(sessions.Find(third.Cookie));
        Assert.NotNull(sessions.Find(fourth.Cookie));
        Assert.NotNull(sessions.Find(other.Cookie));
    }

    [Fact]
    public void GuestSessions_AreCappedGlobally_NeverEvictingAnAccountSession()
    {
        var time = new ManualTime();
        var sessions = new PublicSessions(Options(o => { o.Guest.MaxSessions = 3; o.Guest.MaxSessionsPerIp = 10; }), time);
        IssuedSession guest = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;
        (IssuedSession account, _) = sessions.Login(sessions.CreateGuest("2.2.2.2", "2.2.2.2")!.Session, "2.2.2.2", "2.2.2.2", "STAFF", 2, "t1");
        time.Advance(TimeSpan.FromSeconds(1));
        IssuedSession third = sessions.CreateGuest("3.3.3.3", "3.3.3.3")!;

        IssuedSession fourth = sessions.CreateGuest("4.4.4.4", "4.4.4.4")!; // evicts the idlest guest, which is the first

        Assert.Null(sessions.Find(guest.Cookie));
        Assert.NotNull(sessions.Find(account.Cookie));
        Assert.NotNull(sessions.Find(third.Cookie));
        Assert.NotNull(sessions.Find(fourth.Cookie));
        Assert.Equal(3, sessions.Count);

        var onlyAccounts = new PublicSessions(Options(o => o.Guest.MaxSessions = 1), time);
        onlyAccounts.Login(null, "1.1.1.1", "1.1.1.1", "A", 1, "t");
        Assert.Null(onlyAccounts.CreateGuest("5.5.5.5", "5.5.5.5"));
    }

    [Fact]
    public void Sessions_ExpireWhenIdle_AndAtTheirAbsoluteLifetime()
    {
        var time = new ManualTime();
        var sessions = new PublicSessions(Options(), time);
        IssuedSession idle = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;
        IssuedSession busy = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;

        for (int i = 0; i < 24; i++)
        {
            time.Advance(TimeSpan.FromMinutes(29));
            Assert.NotNull(sessions.Find(busy.Cookie)); // keeps it alive
            if (i == 0)
            {
                Assert.NotNull(sessions.Find(idle.Cookie));
            }
        }

        time.Advance(TimeSpan.FromMinutes(29)); // 12 hours and 5 minutes since creation
        Assert.Null(sessions.Find(idle.Cookie)); // idle for over 30 minutes
        Assert.Null(sessions.Find(busy.Cookie)); // past the absolute lifetime although it was just in use
    }

    [Fact]
    public void AnAccountSessionThatExpiresInsideFind_StillHasItsWorldTicketEnded_BySweep()
    {
        var time = new ManualTime();
        var sessions = new PublicSessions(Options(), time);
        (IssuedSession account, _) = sessions.Login(null, "1.1.1.1", "1.1.1.1", "GM", 1, "ticket-1");
        IssuedSession guest = sessions.CreateGuest("2.2.2.2", "2.2.2.2")!;

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Null(sessions.Find(account.Cookie)); // expired by the lookup, not by a sweep
        IReadOnlyList<EndedSession> ended = sessions.Sweep();

        EndedSession end = Assert.Single(ended);
        Assert.Equal("ticket-1", end.Ticket);
        Assert.Empty(sessions.Sweep()); // reported once
        Assert.Null(sessions.Find(guest.Cookie));
    }

    [Fact]
    public void Login_RotatesTheSession_TheOldCookieDies_ItsSocketsCloseAndItsTicketIsReturned()
    {
        var sessions = new PublicSessions(Options(), new ManualTime());
        IssuedSession guest = sessions.CreateGuest("1.1.1.1", "1.1.1.1")!;
        var closed = new List<string>();
        using IDisposable registration = guest.Session.RegisterSocket(closed.Add);

        (IssuedSession account, EndedSession? ended) = sessions.Login(guest.Session, "1.1.1.1", "1.1.1.1", "STAFF", 2, "ticket-1");

        Assert.NotEqual(guest.Cookie, account.Cookie);
        Assert.NotEqual(guest.Session.Csrf, account.Session.Csrf);
        Assert.False(guest.Session.OpaqueKey.AsSpan().SequenceEqual(account.Session.OpaqueKey));
        Assert.Null(sessions.Find(guest.Cookie));
        Assert.Same(account.Session, sessions.Find(account.Cookie));
        Assert.Equal(PublicTier.Account, account.Session.Tier);
        Assert.Equal("STAFF", account.Session.Account);
        Assert.Equal(["login"], closed);
        Assert.NotNull(ended);
        Assert.Null(ended!.Ticket); // a guest had none

        (IssuedSession second, EndedSession? secondEnded) = sessions.Login(account.Session, "1.1.1.1", "1.1.1.1", "STAFF", 2, "ticket-2");
        Assert.Equal("ticket-1", secondEnded!.Ticket); // logging in over an account session ends its ticket
        Assert.Null(sessions.Find(account.Cookie));
        Assert.NotNull(sessions.Find(second.Cookie));
    }

    [Fact]
    public void Logout_RotatesToAFreshGuestSession_AndReturnsTheTicket()
    {
        var sessions = new PublicSessions(Options(), new ManualTime());
        (IssuedSession account, _) = sessions.Login(null, "1.1.1.1", "1.1.1.1", "STAFF", 2, "ticket-1");

        (IssuedSession issued, EndedSession ended) = sessions.Logout(account.Session)!.Value;

        Assert.Equal("ticket-1", ended.Ticket);
        Assert.Equal(PublicTier.Guest, issued.Session.Tier);
        Assert.NotEqual(account.Session.Csrf, issued.Session.Csrf);
        Assert.False(account.Session.OpaqueKey.AsSpan().SequenceEqual(issued.Session.OpaqueKey));
        Assert.Null(sessions.Find(account.Cookie));
        Assert.Null(sessions.Logout(account.Session)); // already gone
    }

    [Fact]
    public void End_ClosesSockets_RemovesTheMatchingSessions_AndReturnsTheirTickets()
    {
        var sessions = new PublicSessions(Options(), new ManualTime());
        (IssuedSession a, _) = sessions.Login(null, "1.1.1.1", "1.1.1.1", "STAFF", 2, "t-a");
        (IssuedSession b, _) = sessions.Login(null, "2.2.2.2", "2.2.2.2", "staff", 2, "t-b");
        (IssuedSession c, _) = sessions.Login(null, "3.3.3.3", "3.3.3.3", "OTHER", 1, "t-c");
        var closed = new List<string>();
        using IDisposable registration = a.Session.RegisterSocket(closed.Add);

        IReadOnlyList<EndedSession> ended = sessions.End(s => string.Equals(s.Account, "STAFF", StringComparison.OrdinalIgnoreCase), "kicked");

        Assert.Equal(["t-a", "t-b"], ended.Select(e => e.Ticket!).Order().ToArray());
        Assert.Equal(["kicked"], closed);
        Assert.Null(sessions.Find(a.Cookie));
        Assert.Null(sessions.Find(b.Cookie));
        Assert.NotNull(sessions.Find(c.Cookie));
        Assert.Same(c.Session, sessions.FindByRef(c.Session.Ref));
    }

    [Fact]
    public void Counts_SplitAccountsByTheActionLevel()
    {
        var sessions = new PublicSessions(Options(), new ManualTime());
        sessions.CreateGuest("1.1.1.1", "1.1.1.1");
        sessions.Login(null, "1.1.1.1", "1.1.1.1", "PLAYER", 0, "t0");
        sessions.Login(null, "1.1.1.1", "1.1.1.1", "GM", 2, "t2");

        Assert.Equal((1, 2, 1), sessions.Counts());
    }
}

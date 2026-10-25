using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Host.Public;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host.Public;

/// <summary>
/// The public listener end to end (design 0.1 to 0.4, 0.8, section 4): a real Kestrel listener on a free loopback port, a fake world and an HTTP client
/// that holds cookies by hand. Proves the tier is the listener's, TLS is required, cookies, CSRF, Host and Origin checks, generic errors, session
/// rotation, the login rules and the guest answers.
/// </summary>
public sealed class PublicServerTests
{
    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static void RaiseLoginLimits(PublicOptions o)
    {
        o.Login.PerIpPerMinute = 1000;
        o.Login.PerAccountPer15Min = 1000;
    }

    // ---- tier ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestFromLoopback_WithForwardedForLoopback_OnThePublicPort_IsAGuest()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        Visitor visitor = await rig.VisitAsync(forwardedFor: "127.0.0.1");
        Reply me = await rig.GetAsync("/api/public/me", visitor, r =>
        {
            r.Headers.TryAddWithoutValidation("X-Forwarded-For", "127.0.0.1");
            r.Headers.TryAddWithoutValidation("X-Atlas-Tier", "local");
            r.Headers.TryAddWithoutValidation("X-Atlas-Actor", "atlas-local:admin");
        });

        Assert.Equal(200, me.Status);
        Assert.Equal("guest", me.Json!["tier"]!.GetValue<string>());
        Assert.Null(me.Json["account"]);
        PublicSession session = Assert.Single(rig.Runtime.Sessions.Snapshot());
        Assert.Equal(PublicTier.Guest, session.Tier);
        Assert.Equal(0, session.Security);

        // What a Local caller may run is not available to this visitor, whatever the headers claim.
        foreach (string name in new[] { "status", "help", "maps.list", "server.console", "live.inject", "live.send", "live.tap.start", "view.set", "camera.goto", "capture", "export.tiles", "query.los", "public.status", "public.kick" })
        {
            JsonNode result = await rig.CommandAsync(visitor, name);
            Assert.False(result["ok"]!.GetValue<bool>(), name);
            Assert.Equal("not-available", result["error"]!["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task ATierCannotBeSetByAHeader_OrByACookieWeNeverIssued()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        Reply forged = await rig.SendAsync(HttpMethod.Get, "/api/public/me", new Visitor("A" + new string('b', 42), "x"));
        Reply none = await rig.GetAsync("/api/public/me");

        Assert.Equal(401, forged.Status);
        Assert.Equal("no-session", forged.Code);
        Assert.Equal(401, none.Status);
    }

    // ---- start-up -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheListener_RefusesToStartWithoutTls_AndLogsWhy_AndBindsNothing()
    {
        var logs = new CapturingLoggers();
        int port = FreePort.Next();
        var options = new PublicOptions { Enabled = true, Bind = $"127.0.0.1:{port}" };

        PublicStartException e = await Assert.ThrowsAsync<PublicStartException>(() => PublicServer.StartAsync(
            new PublicStartup { Options = options, Loggers = logs, World = new FakeWorld(), Assets = new PublicAssets() },
            CancellationToken.None));

        Assert.Contains("refuses to start without TLS", e.Message, StringComparison.Ordinal);
        Assert.Contains(logs.Lines, l => l.Level == LogLevel.Error && l.Message.Contains("refuses to start without TLS", StringComparison.Ordinal));
        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start(); // the port is free: nothing was left listening
    }

    [Fact]
    public async Task AnInsecureDevelopmentBind_OnAnAddressThatIsNeitherLoopbackNorPrivate_IsRefused()
    {
        var logs = new CapturingLoggers();
        var options = new PublicOptions { Enabled = true, Bind = $"[::]:{FreePort.Next()}", AllowInsecureDev = true, Origin = "http://atlas.test" };

        PublicStartException e = await Assert.ThrowsAsync<PublicStartException>(() => PublicServer.StartAsync(
            new PublicStartup { Options = options, Loggers = logs, World = new FakeWorld(), Assets = new PublicAssets() },
            CancellationToken.None));

        Assert.Contains("AllowInsecureDev", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListener_LogsItsListenerLine_AndTheInsecureModeWarning()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        LogLine line = Assert.Single(rig.Logs.Lines, l => l.Message.StartsWith("Atlas public listener:", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains($"http://127.0.0.1:{rig.Port}", line.Message, StringComparison.Ordinal);
        Assert.Contains("tiers Guest+Account", line.Message, StringComparison.Ordinal);
        Assert.Contains("process=separate", line.Message, StringComparison.Ordinal);
        Assert.Contains("tls=INSECURE-DEV", line.Message, StringComparison.Ordinal);
        Assert.Contains("guest players=hidden, names=off", line.Message, StringComparison.Ordinal);
        Assert.Contains("login min=Moderator", line.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Logs.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("INSECURE-DEV", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Logs.Lines, l => l.Message.Contains("real players", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheListenerLine_SaysUnwired_WhenAccountActionsAreOnButNothingRunsThem()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        LogLine line = Assert.Single(rig.Logs.Lines, l => l.Message.StartsWith("Atlas public listener:", StringComparison.Ordinal));
        Assert.Contains("account actions=unwired", line.Message, StringComparison.Ordinal);
        Assert.Equal("off", PublicServer.AccountActionsWord(enabled: false, wired: true));
        Assert.Equal("on", PublicServer.AccountActionsWord(enabled: true, wired: true));
        Assert.False(GuestStatusActions(await rig.GetAsync("/api/public/status")));
    }

    private static bool GuestStatusActions(Reply status) => status.Json!["accountActions"]!.GetValue<bool>();

    [Fact]
    public async Task ShowingPlayers_IsWarnedAboutInTheLog()
    {
        await using PublicRig rig = await PublicRig.StartAsync(o => o.Guest.ShowPlayers = true);

        Assert.Contains(rig.Logs.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains("real players", StringComparison.Ordinal));
        LogLine line = Assert.Single(rig.Logs.Lines, l => l.Message.StartsWith("Atlas public listener:", StringComparison.Ordinal));
        Assert.Contains("guest players=shown", line.Message, StringComparison.Ordinal);
    }

    // ---- in-process mode asks the world --------------------------------------------------------------------------------------------

    private static Action<PublicOptions> InProcess => o =>
    {
        o.Feed.WorldActionsEnabled = false;
        o.Feed.LiveServer = false;
        o.Feed.WorldMaxConnections = 3;
    };

    [Fact]
    public async Task InProcessMode_StartsAgainstAWorldWhoseCapsSayActionsOff_AndRefusesOneWhoseCapsSayOn()
    {
        var quiet = new FakeFeed();
        await using (PublicRig rig = await PublicRig.StartAsync(InProcess, connector: quiet, mode: PublicProcessMode.InProcess))
        {
            Assert.Equal(["role", "caps"], Assert.Single(quiet.Opened).Ops);
            Assert.Contains(rig.Logs.Lines, l => l.Message.Contains("process=in-process", StringComparison.Ordinal));
        }

        // The operator's settings say the action channel is off; the world says otherwise. The world wins.
        var actionsOn = new FakeFeed { CapsActions = true };
        PublicStartException e = await Assert.ThrowsAsync<PublicStartException>(() => PublicRig.StartAsync(InProcess, connector: actionsOn, mode: PublicProcessMode.InProcess));
        Assert.Contains("actions:true", e.Message, StringComparison.Ordinal);

        var live = new FakeFeed { CapsLiveServer = true };
        e = await Assert.ThrowsAsync<PublicStartException>(() => PublicRig.StartAsync(InProcess, connector: live, mode: PublicProcessMode.InProcess));
        Assert.Contains("liveServer:true", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListener_RefusesToStart_WhenTheHelloReportsThePacketTools()
    {
        var tools = new FakeFeed { Tools = true };

        PublicStartException e = await Assert.ThrowsAsync<PublicStartException>(() => PublicRig.StartAsync(connector: tools));

        Assert.Contains("tools:true", e.Message, StringComparison.Ordinal);
    }

    // ---- headers and cookies ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OverTls_TheSessionCookie_IsHostPrefixedSecureHttpOnlySameSiteStrict_AndHstsIsSent()
    {
        await using PublicRig rig = await PublicRig.StartAsync(tls: true);

        Reply page = await rig.GetAsync("/api/public/status");

        string setCookie = Assert.Single(page.SetCookies);
        Assert.StartsWith("__Host-atlas=", setCookie, StringComparison.Ordinal);
        Assert.Contains("; secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("max-age=31536000", page.Header("Strict-Transport-Security"));
        Assert.Contains(rig.Logs.Lines, l => l.Message.Contains("tls=certificate", StringComparison.Ordinal) && l.Message.Contains("https://127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InTheInsecureDevelopmentMode_TheCookieIsAtlasDev_NotSecure_AndThereIsNoHsts()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        Reply page = await rig.GetAsync("/api/public/status");

        string setCookie = Assert.Single(page.SetCookies);
        Assert.StartsWith("atlas-dev=", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Null(page.Header("Strict-Transport-Security"));
    }

    [Fact]
    public async Task EveryResponse_CarriesTheSecurityHeaders_AndApiAnswersAreNotCached()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        foreach (string path in new[] { "/", "/api/public/status", "/api/public/nope", "/app/js/public/boot.js", "/missing" })
        {
            Reply reply = await rig.GetAsync(path);
            string csp = reply.Header("Content-Security-Policy") ?? string.Empty;
            Assert.Contains("worker-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
            Assert.Equal("nosniff", reply.Header("X-Content-Type-Options"));
            Assert.Equal("no-referrer", reply.Header("Referrer-Policy"));
            Assert.Equal("same-origin", reply.Header("Cross-Origin-Opener-Policy"));
            Assert.Equal("same-origin", reply.Header("Cross-Origin-Resource-Policy"));
            Assert.Equal("camera=(), microphone=(), geolocation=()", reply.Header("Permissions-Policy"));
            Assert.Null(reply.Header("Server"));
        }

        Assert.Equal("no-store", (await rig.GetAsync("/api/public/status")).Header("Cache-Control"));
        Assert.Equal("no-store", (await rig.GetAsync("/api/public/nope")).Header("Cache-Control"));
    }

    [Fact]
    public async Task TheRoot_ServesThePublicPage_AndCreatesTheGuestSession_ButNeverTheLocalPage()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        Reply root = await rig.GetAsync("/");

        Assert.Equal(200, root.Status);
        Assert.Contains("fake public ui", root.Text, StringComparison.Ordinal);
        Assert.NotNull(rig.CookieOf(root));
        Assert.Equal(1, rig.Runtime.Sessions.Count);

        foreach (string hidden in new[] { "/app/index.html", "/app/js/main.js", "/app/../index.html", "/app/js/../main.js", "/app/", "/app/js%2Fmain.js", "/app/js\\main.js" })
        {
            Assert.Equal(404, (await rig.GetAsync(hidden)).Status);
        }

        Assert.Equal(200, (await rig.GetAsync("/app/js/public/boot.js")).Status);
        Assert.Equal(405, (await rig.SendAsync(HttpMethod.Post, "/")).Status);
        Assert.Equal(404, (await rig.GetAsync("/api/status")).Status); // the Local route does not exist here
    }

    [Fact]
    public async Task AHostThatIsNotOnTheAllowlist_Gets421()
    {
        await using PublicRig rig = await PublicRig.StartAsync();

        Reply wrong = await rig.GetAsync("/api/public/status", tweak: r => r.Headers.Host = "evil.example");
        Reply wrongPort = await rig.GetAsync("/api/public/status", tweak: r => r.Headers.Host = $"127.0.0.1:{rig.Port + 1}");
        Reply right = await rig.GetAsync("/api/public/status");

        Assert.Equal(421, wrong.Status);
        Assert.Equal("bad-host", wrong.Code);
        Assert.Equal(421, wrongPort.Status);
        Assert.Equal(200, right.Status);
        Assert.Equal(1, rig.Runtime.Sessions.Count); // only the right request made a session
    }

    // ---- errors -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Errors_AreTheGenericBody_WithoutMessageOrExceptionText()
    {
        await using PublicRig rig = await PublicRig.StartAsync(RaiseLoginLimits);
        rig.World.LoginThrows = new InvalidOperationException("secret-internal-detail at D:\\private\\source.cs:42");
        Visitor visitor = await rig.VisitAsync();

        Reply notFound = await rig.GetAsync("/api/public/nope");
        Reply failed = await rig.PostAsync("/api/public/login", visitor, new { user = "someone", password = "pw" });

        Assert.Equal(404, notFound.Status);
        Assert.Equal(500, failed.Status);
        Assert.Equal("failed", failed.Code);
        foreach (Reply reply in new[] { notFound, failed })
        {
            JsonObject body = reply.Json!.AsObject();
            Assert.Equal(["ok", "error"], body.Select(p => p.Key).ToArray());
            Assert.False(body["ok"]!.GetValue<bool>());
            Assert.Equal(["code", "ref"], body["error"]!.AsObject().Select(p => p.Key).ToArray());
            Assert.DoesNotContain("secret-internal-detail", reply.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("source.cs", reply.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperation", reply.Text, StringComparison.Ordinal);
        }

        // The reference ties the answer to the log line, which is where the detail lives.
        string reference = failed.Json!["error"]!["ref"]!.GetValue<string>();
        Assert.Contains(rig.Logs.Lines, l => l.Level == LogLevel.Warning && l.Message.Contains($"(ref {reference})", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABodyThatIsNotJson_OrIsTooLarge_IsRefusedWithoutDetail()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        Visitor visitor = await rig.VisitAsync();

        Reply notJson = await rig.SendAsync(HttpMethod.Post, "/api/public/command", visitor, rawBody: "{not json");
        Reply huge = await rig.SendAsync(HttpMethod.Post, "/api/public/command", visitor, rawBody: "{\"name\":\"guest.help\",\"pad\":\"" + new string('x', 17_000) + "\"}");
        Reply loginHuge = await rig.SendAsync(HttpMethod.Post, "/api/public/login", visitor, rawBody: "{\"user\":\"a\",\"password\":\"" + new string('x', 3000) + "\"}");

        Assert.Equal(400, notJson.Status);
        Assert.Equal("bad-request", notJson.Code);
        Assert.Equal(413, huge.Status);
        Assert.Equal(413, loginHuge.Status);
    }

    // ---- CSRF and Origin -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APost_NeedsASession_TheCsrfToken_AndAnAllowedOrigin()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        Visitor visitor = await rig.VisitAsync();
        var body = new { name = "guest.help" };

        Reply noCookie = await rig.PostAsync("/api/public/command", null, body);
        Reply noToken = await rig.PostAsync("/api/public/command", visitor, body, csrf: false);
        Reply wrongToken = await rig.PostAsync("/api/public/command", visitor, body, csrf: false, tweak: r => r.Headers.TryAddWithoutValidation("X-Atlas-CSRF", visitor.Csrf + "x"));
        Reply otherSessionsToken = await rig.PostAsync("/api/public/command", visitor with { Csrf = (await rig.VisitAsync()).Csrf }, body);
        Reply foreignOrigin = await rig.PostAsync("/api/public/command", visitor, body, tweak: r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));
        Reply ownOrigin = await rig.PostAsync("/api/public/command", visitor, body, tweak: r => r.Headers.TryAddWithoutValidation("Origin", rig.Origin));
        Reply plain = await rig.PostAsync("/api/public/command", visitor, body);

        Assert.Equal((401, "no-session"), (noCookie.Status, noCookie.Code));
        Assert.Equal((403, "bad-csrf"), (noToken.Status, noToken.Code));
        Assert.Equal((403, "bad-csrf"), (wrongToken.Status, wrongToken.Code));
        Assert.Equal((403, "bad-csrf"), (otherSessionsToken.Status, otherSessionsToken.Code));
        Assert.Equal((403, "bad-origin"), (foreignOrigin.Status, foreignOrigin.Code));
        Assert.Equal(200, ownOrigin.Status);
        Assert.Equal(200, plain.Status);
    }

    [Fact]
    public async Task AGetOfAnApiRoute_ThatIsAPostRoute_Gets405_AndATextBodyGets415()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        Visitor visitor = await rig.VisitAsync();

        Reply get = await rig.GetAsync("/api/public/command", visitor);
        Reply text = await rig.SendAsync(HttpMethod.Post, "/api/public/command", visitor, tweak: r => r.Content = new StringContent("name=guest.help"));

        Assert.Equal(405, get.Status);
        Assert.Equal("POST", get.Header("Allow"));
        Assert.Equal(415, text.Status);
    }

    // ---- login and session rotation -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_RotatesTheCookieAndTheCsrfToken_TheOldCookieIsRefused_AndLogoutRotatesAgain()
    {
        await using PublicRig rig = await PublicRig.StartAsync(RaiseLoginLimits);
        rig.World.Accounts["gm"] = ("pw", 1);
        Visitor guest = await rig.VisitAsync();

        Visitor account = await rig.LoginAsync(guest, "gm", "pw");

        Assert.NotEqual(guest.Cookie, account.Cookie);
        Assert.NotEqual(guest.Csrf, account.Csrf);
        Assert.Equal(401, (await rig.GetAsync("/api/public/me", guest)).Status); // the old cookie is dead
        Assert.Equal(401, (await rig.PostAsync("/api/public/command", guest, new { name = "guest.help" })).Status);
        Reply me = await rig.GetAsync("/api/public/me", account);
        Assert.Equal("account", me.Json!["tier"]!.GetValue<string>());
        Assert.Equal("GM", me.Json["account"]!.GetValue<string>());
        Assert.Equal(1, me.Json["security"]!.GetValue<int>());
        JsonNode unanswered = await rig.CommandAsync(account, "me.characters");
        Assert.Equal("unavailable", unanswered["error"]!["code"]!.GetValue<string>()); // the fake world has no listing to give
        rig.World.CharactersAnswer = Args("""{"ok":true,"characters":[{"g":"00000000000000AB","n":"Mine","map":0,"x":1.5,"y":2.5,"z":3.5,"o":0.5,"level":12,"accountId":77}]}""");
        JsonNode listed = await rig.CommandAsync(account, "me.characters");
        JsonNode mine = listed["data"]!["characters"]![0]!;
        Assert.Equal("Mine", mine["n"]!.GetValue<string>());
        Assert.Null(mine["accountId"]); // only the allowlisted fields pass
        Visitor anotherGuest = await rig.VisitAsync();
        Assert.Equal("not-available", (await rig.CommandAsync(anotherGuest, "me.characters"))["error"]!["code"]!.GetValue<string>()); // a guest may not list characters

        Reply logout = await rig.PostAsync("/api/public/logout", account, null);
        Assert.Equal(200, logout.Status);
        string newCookie = rig.CookieOf(logout)!;
        Assert.NotEqual(account.Cookie, newCookie);
        Assert.NotEqual(account.Csrf, logout.Json!["csrf"]!.GetValue<string>());
        Assert.Equal("guest", logout.Json["tier"]!.GetValue<string>());
        Assert.Equal(401, (await rig.GetAsync("/api/public/me", account)).Status);
        Assert.Single(rig.World.EndedTickets);
    }

    [Fact]
    public async Task EveryFailedLogin_IsTheSameAnswer_WhateverTheCause()
    {
        await using PublicRig rig = await PublicRig.StartAsync(RaiseLoginLimits);
        rig.World.Accounts["gm"] = ("pw", 1);
        rig.World.Accounts["lowly"] = ("pw", 0);
        Visitor visitor = await rig.VisitAsync();

        Reply wrongPassword = await rig.PostAsync("/api/public/login", visitor, new { user = "gm", password = "nope" });
        Reply unknown = await rig.PostAsync("/api/public/login", visitor, new { user = "nobody", password = "pw" });
        Reply belowFloor = await rig.PostAsync("/api/public/login", visitor, new { user = "lowly", password = "pw" });
        Reply wrongCode = await rig.PostAsync("/api/public/login", visitor, new { user = "gm", password = "nope", code = "123456" });

        foreach (Reply reply in new[] { wrongPassword, unknown, belowFloor, wrongCode })
        {
            Assert.Equal(401, reply.Status);
            Assert.Equal("login-failed", reply.Code);
        }

        string Shape(Reply r) => r.Text[..r.Text.IndexOf("\"ref\"", StringComparison.Ordinal)];
        Assert.Single(new[] { wrongPassword, unknown, belowFloor, wrongCode }.Select(Shape).Distinct());
        Assert.Equal(1, rig.Runtime.Sessions.Count); // a failed login leaves the guest session as it was
        Assert.Equal(4, rig.Runtime.Stats.LoginFailuresRecent());
    }

    [Fact]
    public async Task ALoginFailureLine_EscapesTheVisitorsText_SoItCannotForgeLogLines()
    {
        await using PublicRig rig = await PublicRig.StartAsync(RaiseLoginLimits);
        Visitor visitor = await rig.VisitAsync();

        Reply reply = await rig.PostAsync("/api/public/login", visitor, new { user = "evil\r\nlogin ok account=admin", password = "pw" });

        Assert.Equal(401, reply.Status);
        Assert.Contains(rig.Logs.Lines, l => l.Message.StartsWith("login failed", StringComparison.Ordinal) && l.Message.Contains("evil\\r\\nlogin ok account=admin", StringComparison.Ordinal));
        Assert.All(rig.Logs.Lines, l => Assert.False(l.Message.Contains('\n') || l.Message.Contains('\r'), l.Message));
    }

    [Fact]
    public async Task Login_IsLimitedPerAddress_WithRetryAfter()
    {
        await using PublicRig rig = await PublicRig.StartAsync(o => { o.Login.PerIpPerMinute = 2; o.Login.PerAccountPer15Min = 100; });
        Visitor visitor = await rig.VisitAsync();

        Reply first = await rig.PostAsync("/api/public/login", visitor, new { user = "a", password = "x" });
        Reply second = await rig.PostAsync("/api/public/login", visitor, new { user = "b", password = "x" });
        Reply third = await rig.PostAsync("/api/public/login", visitor, new { user = "c", password = "x" });

        Assert.Equal(401, first.Status);
        Assert.Equal(401, second.Status);
        Assert.Equal(429, third.Status);
        Assert.Equal("login-failed", third.Code); // a refusal for rate looks like every other refusal
        Assert.NotNull(third.Header("Retry-After"));
        Assert.Equal(2, rig.World.Logins.Count); // the limited attempt never reached the world
    }

    [Fact]
    public async Task Login_IsRefusedWhenTheModeDoesNotAllowIt_AndNothingReachesTheWorld()
    {
        await using PublicRig rig = await PublicRig.StartAsync(RaiseLoginLimits);
        rig.World.Accounts["gm"] = ("pw", 1);
        Visitor visitor = await rig.VisitAsync();
        rig.Runtime.Config.LoginAllowed = false; // what PublicConfigCheck computes for AllowInsecureDev on a bind that is not loopback

        Reply reply = await rig.PostAsync("/api/public/login", visitor, new { user = "gm", password = "pw" });

        Assert.Equal(403, reply.Status);
        Assert.Equal("login-disabled", reply.Code);
        Assert.Empty(rig.World.Logins);
        Assert.False((await rig.GetAsync("/api/public/status")).Json!["login"]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ALoginThatTheWorldCannotServe_IsAnErrorNotAFailure()
    {
        await using PublicRig rig = await PublicRig.StartAsync(RaiseLoginLimits);
        rig.World.OtherwiseKind = WorldLoginKind.Unavailable;
        Visitor visitor = await rig.VisitAsync();

        Reply unavailable = await rig.PostAsync("/api/public/login", visitor, new { user = "gm", password = "pw" });
        rig.World.OtherwiseKind = WorldLoginKind.Off;
        Reply off = await rig.PostAsync("/api/public/login", visitor, new { user = "gm", password = "pw" });

        Assert.Equal((503, "feed-unavailable"), (unavailable.Status, unavailable.Code));
        Assert.Equal((403, "login-disabled"), (off.Status, off.Code));
    }

    [Fact]
    public async Task Sessions_AreCreatedOnlyByAPageLoad_AndCappedPerAddress()
    {
        await using PublicRig rig = await PublicRig.StartAsync(o => o.Guest.MaxSessionsPerIp = 3);

        Assert.Equal(401, (await rig.GetAsync("/api/public/me")).Status); // /me never creates one
        Assert.Equal(0, rig.Runtime.Sessions.Count);
        for (int i = 0; i < 5; i++)
        {
            await rig.GetAsync("/api/public/status");
        }

        Assert.Equal(3, rig.Runtime.Sessions.Count); // the address's idlest guest session is evicted past the cap
    }

    // ---- guest answers ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheGuestCommands_AnswerWithNoPath_NoLocalCommandName_AndOnlyTheAllowlist()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        Visitor visitor = await rig.VisitAsync();
        var answers = new List<string>
        {
            (await rig.GetAsync("/api/public/status")).Text,
            (await rig.CommandAsync(visitor, "guest.status")).ToJsonString(),
            (await rig.CommandAsync(visitor, "guest.maps")).ToJsonString(),
            (await rig.CommandAsync(visitor, "guest.layers")).ToJsonString(),
            (await rig.CommandAsync(visitor, "bookmarks.list")).ToJsonString(),
        };
        JsonNode help = await rig.CommandAsync(visitor, "guest.help");
        answers.Add(help.ToJsonString());

        string all = string.Join('\n', answers);
        foreach (string hostText in new[] { Path.GetTempPath(), AppContext.BaseDirectory, Environment.CurrentDirectory, Environment.UserName, Environment.MachineName }.Where(s => s.Length >= 4))
        {
            Assert.DoesNotContain(hostText, all, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("Map.dbc", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", all, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", all, StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/", all, StringComparison.Ordinal);

        string[] names = [.. help["data"]!.AsArray().Select(n => n!["name"]!.GetValue<string>())];
        Assert.Equal(PublicCommandPolicy.AllowedFor(PublicTier.Guest, 0, rig.Runtime.Options).ToArray(), names);
        Assert.DoesNotContain(names, n => n is "status" or "help" or "maps.list" or "layers.list" or "server.console" || n.StartsWith("live.tap", StringComparison.Ordinal));

        JsonNode maps = (await rig.CommandAsync(visitor, "guest.maps"))["data"]!;
        Assert.Equal(["Eastern Kingdoms", "Kalimdor"], maps.AsArray().Select(m => m!["name"]!.GetValue<string>()).ToArray()); // the operator's names, nothing from client files
    }

    [Fact]
    public async Task APublicMapOutsideTheAllowlist_IsRefused_AndLiveCallsStayInsideTheGuestProjection()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        Visitor visitor = await rig.VisitAsync();

        JsonNode instance = await rig.CommandAsync(visitor, "live.entities", Args("""{"map":389}"""));
        JsonNode badKind = await rig.CommandAsync(visitor, "live.entities", Args("""{"kinds":["player"]}"""));
        JsonNode badName = await rig.CommandAsync(visitor, "live.find", Args("""{"name":"x; .server shutdown"}"""));
        JsonNode fine = await rig.CommandAsync(visitor, "live.entities", Args("""{"map":0}"""));

        Assert.Equal("bad-args", instance["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad-args", badKind["error"]!["code"]!.GetValue<string>());
        Assert.Equal("bad-args", badName["error"]!["code"]!.GetValue<string>());
        Assert.True(fine["ok"]!.GetValue<bool>());
        Assert.Equal(0, fine["data"]!["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task ACookielessClient_CannotGetPastThePerAddressWorldCallBucket_ByCollectingSessions()
    {
        var time = new ManualTime(); // no refill while the test runs: the bucket is exactly its burst
        await using PublicRig rig = await PublicRig.StartAsync(time: time);
        var outcomes = new List<string>();

        for (int i = 0; i < 9; i++)
        {
            Visitor fresh = await rig.VisitAsync(); // every iteration is a client with no cookie, so a new session with its own session bucket
            JsonNode result = await rig.CommandAsync(fresh, "live.find", Args("""{"name":"Boar"}"""));
            outcomes.Add(result["ok"]!.GetValue<bool>() ? "ok" : result["error"]!["code"]!.GetValue<string>());
        }

        Assert.Equal(5, outcomes.Count(o => o == "ok"));
        Assert.Equal(4, outcomes.Count(o => o == "rate-limited"));
    }

    // ---- WebSocket handshake ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSocket_NeedsASession_AndAnOriginThatMatches_AndOnlyThenGreetsAnAuthenticatedClient()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        Visitor visitor = await rig.VisitAsync();

        WebSocketException noSession = await Assert.ThrowsAsync<WebSocketException>(() => rig.ConnectAsync(null));
        WebSocketException foreign = await Assert.ThrowsAsync<WebSocketException>(() => rig.ConnectAsync(visitor, "http://evil.example"));
        WebSocketException noOrigin = await Assert.ThrowsAsync<WebSocketException>(() => rig.ConnectAsync(visitor, null));
        Assert.Contains("401", noSession.Message, StringComparison.Ordinal);
        Assert.Contains("403", foreign.Message, StringComparison.Ordinal);
        Assert.Contains("403", noOrigin.Message, StringComparison.Ordinal);

        using ClientWebSocket socket = await rig.ConnectAsync(visitor);
        await socket.SendJsonAsync(new JsonObject { ["op"] = "auth", ["csrf"] = visitor.Csrf });
        JsonNode hello = (await socket.ReceiveUntilAsync("hello"))!;
        Assert.Equal("guest", hello["tier"]!.GetValue<string>());
        Assert.Equal(2, hello["maps"]!.AsArray().Count);
        Assert.Equal(1, rig.Server.Hub.Count);
    }

    [Fact]
    public async Task ASocket_WithAWrongCsrfToken_IsClosedUnauthorized()
    {
        await using PublicRig rig = await PublicRig.StartAsync();
        Visitor visitor = await rig.VisitAsync();

        using ClientWebSocket socket = await rig.ConnectAsync(visitor);
        await socket.SendJsonAsync(new JsonObject { ["op"] = "auth", ["csrf"] = "wrong" });
        string? next = await socket.ReceiveTextAsync();

        Assert.Null(next);
        Assert.Equal((WebSocketCloseStatus)4401, socket.CloseStatus);
    }

    [Fact]
    public async Task OverTls_TheSocketWorks_AndTheHandshakeRefusesProtocolsBelowTls12()
    {
        await using PublicRig rig = await PublicRig.StartAsync(tls: true);
        Visitor visitor = await rig.VisitAsync();

        using (ClientWebSocket socket = await rig.ConnectAsync(visitor))
        {
            await socket.SendJsonAsync(new JsonObject { ["op"] = "auth", ["csrf"] = visitor.Csrf });
            Assert.NotNull(await socket.ReceiveUntilAsync("hello"));
        }

        // Positive control: a TLS 1.2/1.3 client gets through, so the refusal below is the server's choice and not a broken probe.
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, rig.Port);
            using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
            Assert.True(ssl.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13, ssl.SslProtocol.ToString());
        }

#pragma warning disable SYSLIB0039 // TLS 1.0 and 1.1 are obsolete: that is exactly what this offers
        using var old = new TcpClient();
        await old.ConnectAsync(IPAddress.Loopback, rig.Port);
        using var oldSsl = new SslStream(old.GetStream(), false, (_, _, _, _) => true);
        await Assert.ThrowsAnyAsync<Exception>(() => oldSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1", EnabledSslProtocols = SslProtocols.Tls11 }));
#pragma warning restore SYSLIB0039
    }
}

using System.Net;
using System.Text.Json.Nodes;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The GitHub REST client of bot QA against a fake <see cref="HttpMessageHandler"/>: the headers, the marker search, the back-off rules, the
/// refusal of a pagination link to another host, and above all that a POST that may have been applied is never sent twice.
/// </summary>
public sealed class PlayerbotQaGitHubClientTests
{
    private const string Id = "0123456789abcdef";

    private static PlayerbotQaGitHubClient NewClient(HttpMessageHandler http, List<TimeSpan>? delays = null, TimeSpan? timeout = null, Func<DateTimeOffset>? now = null,
        PlayerbotQaGitHubOptions? options = null, string token = QaData.FakeToken)
        => new(http, QaData.Repo, token, options ?? new PlayerbotQaGitHubOptions { Enabled = true, Repository = QaData.Repo }, (span, _) =>
        {
            delays?.Add(span);
            return Task.CompletedTask;
        }, timeout, now);

    private static string BodyWithMarker(string id = Id) => PlayerbotQaIssueComposer.MarkerOf(id) + "\nwhatever";

    private static HttpResponseMessage WithHeader(HttpResponseMessage response, string name, string value)
    {
        response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private static string Issue(int number, string body, bool open = true)
        => new JsonObject { ["number"] = number, ["state"] = open ? "open" : "closed", ["html_url"] = $"https://github.com/{QaData.Repo}/issues/{number}", ["body"] = body }.ToJsonString();

    private static Task<HttpResponseMessage> Reply(HttpResponseMessage response) => Task.FromResult(response);

    [Fact]
    public async Task EveryRequest_CarriesTheTokenAndTheGitHubHeaders()
    {
        var github = new FakeGitHub();
        using PlayerbotQaGitHubClient client = NewClient(github.Http);

        Assert.Null(await client.FindByMarkerAsync(Id, CancellationToken.None));
        await client.CreateIssueAsync("title", BodyWithMarker(), ["bot-qa"], CancellationToken.None);

        Assert.NotEmpty(github.Http.Requests);
        foreach (SeenRequest request in github.Http.Requests)
        {
            Assert.Equal("Bearer " + QaData.FakeToken, request.Headers["Authorization"]);
            Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
            Assert.Equal("2022-11-28", request.Headers["X-GitHub-Api-Version"]);
            Assert.Equal("ArcaneCore-bot-qa", request.Headers["User-Agent"]);
            Assert.DoesNotContain(QaData.FakeToken, request.Uri, StringComparison.Ordinal);
            Assert.DoesNotContain(QaData.FakeToken, request.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FindByMarker_OpenIssueWithTheExactMarker_IsFound()
    {
        var github = new FakeGitHub();
        int other = github.Add(PlayerbotQaIssueComposer.MarkerOf("fedcba9876543210"));
        int mine = github.Add(BodyWithMarker());
        using PlayerbotQaGitHubClient client = NewClient(github.Http);

        QaIssueRef? found = await client.FindByMarkerAsync(Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(mine, found.Number);
        Assert.NotEqual(other, found.Number);
        Assert.True(found.Open);
    }

    [Fact]
    public async Task FindByMarker_ClosedIssue_IsFoundAsClosed()
    {
        var github = new FakeGitHub();
        int closed = github.Add(BodyWithMarker(), open: false);
        using PlayerbotQaGitHubClient client = NewClient(github.Http);

        QaIssueRef? found = await client.FindByMarkerAsync(Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(closed, found.Number);
        Assert.False(found.Open);
        Assert.Contains("state=all", github.Http.Requests[0].PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindByMarker_IgnoresPullRequests_AndAPartialMarker()
    {
        var github = new FakeGitHub();
        github.Add(BodyWithMarker(), pullRequest: true);
        github.Add("<!-- arcanecore-bot-qa:0123456789abcde -->"); // shorter id: not the same marker
        using PlayerbotQaGitHubClient client = NewClient(github.Http);

        Assert.Null(await client.FindByMarkerAsync(Id, CancellationToken.None));
    }

    [Fact]
    public async Task FindByMarker_FallsBackToSearch_ForAnIssueWhoseLabelWasRemoved()
    {
        var http = new FakeHttp((request, _) =>
        {
            if (request.PathAndQuery.StartsWith("/search/issues", StringComparison.Ordinal))
            {
                return Reply(FakeHttp.Json(HttpStatusCode.OK, new JsonObject { ["items"] = new JsonArray(JsonNode.Parse(Issue(77, BodyWithMarker(), open: false))) }.ToJsonString()));
            }

            return Reply(FakeHttp.Json(HttpStatusCode.OK, "[]"));
        });
        using PlayerbotQaGitHubClient client = NewClient(http);

        QaIssueRef? found = await client.FindByMarkerAsync(Id, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(77, found.Number);
        Assert.False(found.Open);
    }

    [Fact]
    public async Task Pagination_ToAnotherHost_IsRefused_AndNeverRequested()
    {
        var http = new FakeHttp((_, _) => Reply(WithHeader(FakeHttp.Json(HttpStatusCode.OK, "[]"), "Link", "<https://evil.example/repos/x/issues?page=2>; rel=\"next\"")));
        using PlayerbotQaGitHubClient client = NewClient(http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.FindByMarkerAsync(Id, CancellationToken.None));

        Assert.False(ex.Transient);
        Assert.Contains("another host", ex.Message, StringComparison.Ordinal);
        Assert.Single(http.Requests); // the link was not followed, and the token went nowhere else
    }

    [Fact]
    public async Task Pagination_OverPlainHttp_IsRefused()
    {
        var http = new FakeHttp((_, _) => Reply(WithHeader(FakeHttp.Json(HttpStatusCode.OK, "[]"), "Link", "<http://api.github.com/repos/x/issues?page=2>; rel=\"next\"")));
        using PlayerbotQaGitHubClient client = NewClient(http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.FindByMarkerAsync(Id, CancellationToken.None));

        Assert.False(ex.Transient);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Pagination_OnTheSameHost_IsFollowed_UpToMaxListPages()
    {
        string wrongBody = PlayerbotQaIssueComposer.MarkerOf("fedcba9876543210");
        var http = new FakeHttp((request, call) =>
        {
            if (request.PathAndQuery.StartsWith("/search/issues", StringComparison.Ordinal)) return Reply(FakeHttp.Json(HttpStatusCode.OK, "{\"items\":[]}"));
            string json = call < 3 ? $"[{Issue(call, wrongBody)}]" : $"[{Issue(300, BodyWithMarker())}]";
            HttpResponseMessage response = FakeHttp.Json(HttpStatusCode.OK, json);
            return Reply(call < 3 ? WithHeader(response, "Link", $"<https://api.github.com/repositories/1/issues?page={call + 1}>; rel=\"next\"") : response);
        });
        using PlayerbotQaGitHubClient client = NewClient(http);

        QaIssueRef? found = await client.FindByMarkerAsync(Id, CancellationToken.None);

        Assert.Equal(300, found?.Number);
        Assert.Equal(3, http.Requests.Length);
        Assert.Equal("/repositories/1/issues?page=2", http.Requests[1].PathAndQuery);
    }

    [Fact]
    public async Task Pagination_StopsAtMaxListPages()
    {
        var http = new FakeHttp((request, call) =>
        {
            if (request.PathAndQuery.StartsWith("/search/issues", StringComparison.Ordinal)) return Reply(FakeHttp.Json(HttpStatusCode.OK, "{\"items\":[]}"));
            return Reply(WithHeader(FakeHttp.Json(HttpStatusCode.OK, "[]"), "Link", $"<https://api.github.com/repositories/1/issues?page={call + 1}>; rel=\"next\""));
        });
        using PlayerbotQaGitHubClient client = NewClient(http, options: new PlayerbotQaGitHubOptions { Enabled = true, Repository = QaData.Repo, MaxListPages = 2 });

        Assert.Null(await client.FindByMarkerAsync(Id, CancellationToken.None));

        Assert.Equal(2, http.Requests.Count(r => r.PathAndQuery.StartsWith("/repositories/1/issues", StringComparison.Ordinal) || r.PathAndQuery.StartsWith($"/repos/{QaData.Repo}/issues", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Create_ThatTimesOutAfterTheServerAcceptedIt_IsNotResent_AndLeavesOneIssue()
    {
        var github = new FakeGitHub { TimeoutsAfterApply = 1 };
        var delays = new List<TimeSpan>();
        using PlayerbotQaGitHubClient client = NewClient(github.Http, delays);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None));

        Assert.True(ex.Transient);
        Assert.Equal(1, github.Creates); // exactly one POST left the client
        Assert.Empty(delays); // no back-off: nothing was retried

        // The next pass finds what the server did apply, and nothing is created twice.
        QaIssueRef? found = await client.FindByMarkerAsync(Id, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(1, github.Creates);
    }

    [Fact]
    public async Task Create_That502sAfterTheServerAcceptedIt_IsNotResent()
    {
        var github = new FakeGitHub { BadGatewaysAfterApply = 1 };
        using PlayerbotQaGitHubClient client = NewClient(github.Http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None));

        Assert.True(ex.Transient);
        Assert.Equal(1, github.Creates);
    }

    [Fact]
    public async Task Create_ThatHitsAConnectionError_IsNotResent()
    {
        var http = new FakeHttp((_, _) => throw new HttpRequestException("connection reset"));
        using PlayerbotQaGitHubClient client = NewClient(http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None));

        Assert.True(ex.Transient);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Comment_ThatTimesOutAfterTheServerAcceptedIt_IsNotResent()
    {
        var github = new FakeGitHub { TimeoutsAfterApply = 1 };
        int number = github.Add(BodyWithMarker());
        using PlayerbotQaGitHubClient client = NewClient(github.Http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CommentAsync(number, "more", CancellationToken.None));

        Assert.True(ex.Transient);
        Assert.Equal(1, github.Comments);
    }

    [Fact]
    public async Task Create_WhoseRealRequestTimeoutFires_IsNotResent()
    {
        var http = new HangingHandler();
        using PlayerbotQaGitHubClient client = NewClient(http, timeout: TimeSpan.FromMilliseconds(100));

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None));

        Assert.True(ex.Transient);
        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, http.Calls);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Read_ThatTimesOut_IsRetriedWithBackOff_ThenFailsTransient()
    {
        var delays = new List<TimeSpan>();
        var http = new FakeHttp((_, _) => throw new TaskCanceledException("timeout"));
        using PlayerbotQaGitHubClient client = NewClient(http, delays);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.GetIssueAsync(5, CancellationToken.None));

        Assert.True(ex.Transient);
        Assert.Equal(PlayerbotQaGitHubClient.MaxAttempts, http.Requests.Length);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Read_That500s_ThenSucceeds_IsRetried()
    {
        var delays = new List<TimeSpan>();
        var http = new FakeHttp((_, call) => Reply(call < 3 ? FakeHttp.Json(HttpStatusCode.InternalServerError, "{}") : FakeHttp.Json(HttpStatusCode.OK, Issue(5, BodyWithMarker()))));
        using PlayerbotQaGitHubClient client = NewClient(http, delays);

        var issue = await client.GetIssueAsync(5, CancellationToken.None);

        Assert.Equal(5, issue?.Issue.Number);
        Assert.Equal(3, http.Requests.Length);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Patch_That500s_IsRetried_BecauseItIsIdempotent()
    {
        var http = new FakeHttp((_, call) => Reply(call < 2 ? FakeHttp.Json(HttpStatusCode.BadGateway, "{}") : FakeHttp.Json(HttpStatusCode.OK, "{}")));
        using PlayerbotQaGitHubClient client = NewClient(http);

        await client.ReopenAsync(5, CancellationToken.None);

        Assert.Equal(2, http.Requests.Length);
        Assert.All(http.Requests, r => Assert.Equal(HttpMethod.Patch, r.Method));
    }

    [Fact]
    public async Task Post_AnsweredWith429RetryAfter_WaitsAsGitHubSays_ThenIsResent()
    {
        var delays = new List<TimeSpan>();
        var http = new FakeHttp((_, call) => Reply(call == 1
            ? WithHeader(FakeHttp.Json(HttpStatusCode.TooManyRequests, "{}"), "Retry-After", "7")
            : FakeHttp.Json(HttpStatusCode.Created, Issue(9, BodyWithMarker()))));
        using PlayerbotQaGitHubClient client = NewClient(http, delays);

        QaIssueRef issue = await client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None);

        Assert.Equal(9, issue.Number);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
        Assert.Equal(2, http.Requests.Length); // a refused request was never applied, so resending it cannot duplicate
    }

    [Fact]
    public async Task Forbidden_WithRetryAfter_IsASecondaryRateLimit_AndWaits()
    {
        var delays = new List<TimeSpan>();
        var http = new FakeHttp((_, call) => Reply(call == 1
            ? WithHeader(FakeHttp.Json(HttpStatusCode.Forbidden, "{\"message\":\"secondary rate limit\"}"), "Retry-After", "30")
            : FakeHttp.Json(HttpStatusCode.OK, "[]")));
        using PlayerbotQaGitHubClient client = NewClient(http, delays);

        await client.GetIssueAsync(1, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(30)], delays);
    }

    [Fact]
    public async Task RetryAfter_IsCappedAtFifteenMinutes()
    {
        var delays = new List<TimeSpan>();
        var http = new FakeHttp((_, call) => Reply(call == 1
            ? WithHeader(FakeHttp.Json(HttpStatusCode.TooManyRequests, "{}"), "Retry-After", "999999")
            : FakeHttp.Json(HttpStatusCode.OK, "[]")));
        using PlayerbotQaGitHubClient client = NewClient(http, delays);

        await client.GetIssueAsync(1, CancellationToken.None);

        Assert.Equal([PlayerbotQaGitHubClient.MaxWait], delays);
    }

    [Fact]
    public async Task Forbidden_WithExhaustedRateLimit_WaitsUntilTheReset()
    {
        var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        var delays = new List<TimeSpan>();
        var http = new FakeHttp((_, call) =>
        {
            if (call > 1) return Reply(FakeHttp.Json(HttpStatusCode.OK, "[]"));
            HttpResponseMessage response = FakeHttp.Json(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}");
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", now.AddSeconds(45).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Reply(response);
        });
        using PlayerbotQaGitHubClient client = NewClient(http, delays, now: () => now);

        await client.GetIssueAsync(1, CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(45)], delays);
    }

    [Fact]
    public async Task Forbidden_ForPermissions_FailsAtOnce_NotTransient()
    {
        var delays = new List<TimeSpan>();
        var http = new FakeHttp((_, _) => Reply(FakeHttp.Json(HttpStatusCode.Forbidden, "{\"message\":\"Resource not accessible by personal access token\"}")));
        using PlayerbotQaGitHubClient client = NewClient(http, delays);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None));

        Assert.False(ex.Transient);
        Assert.Single(http.Requests);
        Assert.Empty(delays);
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed_AndIsAPermanentFailure()
    {
        var http = new FakeHttp((_, _) => Reply(WithHeader(FakeHttp.Json(HttpStatusCode.Found, "{}"), "Location", "https://evil.example/steal")));
        using PlayerbotQaGitHubClient client = NewClient(http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.GetIssueAsync(1, CancellationToken.None));

        Assert.False(ex.Transient);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task ErrorBodyThatEchoesTheToken_NeverReachesTheExceptionMessage()
    {
        string secret = "plain-secret-without-a-known-shape-123456";
        var http = new FakeHttp((_, _) => Reply(FakeHttp.Json(HttpStatusCode.Unauthorized, "{\"message\":\"bad credentials: " + secret + " and Bearer " + secret + "\"}")));
        using PlayerbotQaGitHubClient client = NewClient(http, token: secret);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None));

        Assert.DoesNotContain(secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, ex.ToString(), StringComparison.Ordinal);
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionFromTheTransport_NeverCarriesTheToken()
    {
        var http = new FakeHttp((request, _) => throw new HttpRequestException("failed for " + request.Headers["Authorization"]));
        using PlayerbotQaGitHubClient client = NewClient(http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.CreateIssueAsync("t", BodyWithMarker(), ["bot-qa"], CancellationToken.None));

        Assert.DoesNotContain(QaData.FakeToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyOverTheSizeLimit_IsRefused()
    {
        var http = new FakeHttp((_, _) => Reply(FakeHttp.Json(HttpStatusCode.OK, new string(' ', 17 * 1024 * 1024))));
        using PlayerbotQaGitHubClient client = NewClient(http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.GetIssueAsync(1, CancellationToken.None));

        Assert.False(ex.Transient);
        Assert.Contains("16 MiB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyThatIsNotJson_IsAPermanentFailure()
    {
        var http = new FakeHttp((_, _) => Reply(FakeHttp.Json(HttpStatusCode.OK, "<html>captive portal</html>")));
        using PlayerbotQaGitHubClient client = NewClient(http);

        var ex = await Assert.ThrowsAsync<PlayerbotQaPublishException>(() => client.FindByMarkerAsync(Id, CancellationToken.None));

        Assert.False(ex.Transient);
    }

    [Fact]
    public async Task GetIssue_NotFound_IsNull()
    {
        var github = new FakeGitHub();
        using PlayerbotQaGitHubClient client = NewClient(github.Http);

        Assert.Null(await client.GetIssueAsync(4040, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_IsNotWrappedOrRetried()
    {
        var http = new FakeHttp((_, _) => throw new TaskCanceledException("canceled"));
        using PlayerbotQaGitHubClient client = NewClient(http);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetIssueAsync(1, cts.Token));

        Assert.Single(http.Requests);
    }

    [Fact]
    public void Constructor_RefusesABadRepository_AndAnEmptyToken()
    {
        var options = new PlayerbotQaGitHubOptions();
        Assert.Throws<ArgumentException>(() => new PlayerbotQaGitHubClient(null, "not a repo", "t", options));
        Assert.Throws<ArgumentException>(() => new PlayerbotQaGitHubClient(null, "../etc/passwd", "t", options));
        Assert.Throws<ArgumentException>(() => new PlayerbotQaGitHubClient(null, QaData.Repo, string.Empty, options));
    }
}

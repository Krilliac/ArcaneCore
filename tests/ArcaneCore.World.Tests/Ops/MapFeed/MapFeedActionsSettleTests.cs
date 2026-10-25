using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// What the action channel records once a command may already have run (review d1-review-3, BLOCKING 1 and IMPORTANT 1, 2): an action that
/// ran is always in the log, the in-memory audit and the durable file, whatever ended the wait (a timeout, the connection, the daemon); the
/// request's service scope outlives the command; a refusal flood on one connection is bounded.
/// </summary>
public sealed class MapFeedActionsSettleTests
{
    private static FeedWorldSetup LocalWorld(string directory, Action<MapFeedActionsOptions>? options = null, TimeSpan? queryTimeout = null)
        => new()
        {
            Terrain = true,
            QueryTimeout = queryTimeout ?? TimeSpan.FromSeconds(10),
            Actions = o =>
            {
                o.Enabled = true;
                o.LocalSecurity = AccountSecurity.GameMaster;
                o.AuditDirectory = directory;
                options?.Invoke(o);
            },
        };

    /// <summary>How many refusals the records account for: one per <c>action</c> record, and the repeat count of each <c>refusals</c> record.</summary>
    private static int Accounted(JsonElement[] records)
        => records.Sum(r => r.GetProperty("event").GetString() == "refusals" ? r.GetProperty("targets").GetInt32() : 1);

    private static bool EndsWith(FeedWorld world, string suffix)
        => world.ActionLog.Any(e => e.Message.EndsWith(suffix, StringComparison.Ordinal));

    [WindowsFact]
    public async Task ADisconnectDuringTheSettleWait_RecordsTheActionAsCancelled_AndReleasesTheScopeOnlyWhenItSettles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(directory));
            ActionSpies spies = ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync();

            await client.SendAsync(ActionWire.Action(1, ActionWire.Local(), command: ".zhold"));
            await WorldTestHost.WaitForAsync(() => spies.Count("zhold") == 1, "the held command ran");

            // The connection ends while the command is still open (the daemon cancels the connection's lifetime).
            await world.StopFeedAsync();
            await WorldTestHost.WaitForAsync(() => EndsWith(world, "-> cancelled"), "the cancelled action is logged");

            Assert.Single(world.ActionLog);
            JsonElement record = Assert.Single(WebAuditFile.Read(directory));
            Assert.Equal("cancelled", record.GetProperty("result").GetString());
            Assert.Equal(JsonValueKind.Null, record.GetProperty("code").ValueKind); // the command itself did not fail
            Assert.Contains(
                world.Host.WorldServices.GetRequiredService<GmAuditFeature>().Tail(50),
                e => e.Command.StartsWith("zhold", StringComparison.Ordinal));
            Assert.False(spies.ScopeProbeDisposed, "the scope must outlive a command that is still open");

            spies.ReleaseHold();

            await WorldTestHost.WaitForAsync(() => spies.ScopeProbeDisposed, "the scope is released once the command settled");
            Assert.True(spies.HoldResolvedScope, "the command used its session services after the connection ended");
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ACommandThatDoesNotSettleInTime_AnswersSettledFalse_AndRecordsUnsettled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(directory, o => o.SettleSeconds = 1));
            ActionSpies spies = ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync();

            JsonElement result = await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zhold");

            Assert.True(result.Ok(), result.ToString());
            Assert.False(result.GetProperty("settled").GetBoolean());
            Assert.EndsWith("-> unsettled", Assert.Single(world.ActionLog).Message, StringComparison.Ordinal);
            JsonElement record = Assert.Single(WebAuditFile.Read(directory));
            Assert.Equal("unsettled", record.GetProperty("result").GetString());
            Assert.Equal(JsonValueKind.Null, record.GetProperty("code").ValueKind);
            Assert.False(spies.ScopeProbeDisposed);

            spies.ReleaseHold();

            await WorldTestHost.WaitForAsync(() => spies.ScopeProbeDisposed, "the scope is released once the command settled");
            Assert.True(spies.HoldResolvedScope);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task AQueryTimeoutThatFiresAfterTheCommandStarted_IsRecordedAsTheRun_NotAsATimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(directory, queryTimeout: TimeSpan.FromMilliseconds(200)));
            ActionSpies spies = ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync();

            // The command takes 600 ms on the world thread; the 200 ms timeout fires while it runs.
            JsonElement result = await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zslow");

            Assert.True(result.Ok(), result.ToString());
            Assert.Contains("slow ran", result.Lines());
            Assert.Equal(1, spies.Count("zslow"));
            Assert.EndsWith("-> ok", Assert.Single(world.ActionLog).Message, StringComparison.Ordinal);
            Assert.Equal("ok", Assert.Single(WebAuditFile.Read(directory)).GetProperty("result").GetString());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task AQueryTimeoutBeforeTheCommandStarted_StillAnswersTimeout_AndNothingRan()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(LocalWorld(directory, queryTimeout: TimeSpan.FromMilliseconds(200)));
            ActionSpies spies = ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync();

            // The world thread stays busy until the refusal has been received (a fixed sleep raced a slow first request on a loaded machine:
            // the query timeout then started after the thread was free and the command ran). The 60 s cap only bounds a broken run.
            using var busy = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            world.Host.World.Post(() =>
            {
                busy.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            });
            Assert.True(busy.Wait(TimeSpan.FromSeconds(10)));

            JsonElement result;
            try
            {
                result = await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zspy");
            }
            finally
            {
                release.Set();
            }

            result.AssertRefused("timeout");
            Assert.EndsWith("-> timeout", Assert.Single(world.ActionLog).Message, StringComparison.Ordinal);
            await Task.Delay(800); // the skipped command must not run late
            Assert.Equal(0, spies.Count("zspy"));
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ARefusalFlood_OnAWebConnection_IsBounded()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new FeedTestClock();
            await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup
            {
                Terrain = true,
                Time = time,
                Tickets = new FakeFeedTickets(),
                Actions = o =>
                {
                    o.Enabled = true;
                    o.WebLogin = true;
                    o.AuditDirectory = directory;
                    o.WebPerSecond = 2;
                    o.WebBurst = 5;
                },
            });
            FeedClient client = await world.ConnectAsync("web");
            var answers = new Dictionary<string, int>(StringComparer.Ordinal);

            async Task SendAsync(int from, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    JsonElement refused = await ActionWire.ActAsync(client, from + i, new JsonObject { ["tier"] = "web" }, command: ".zplayer");
                    Assert.False(refused.Ok());
                    string code = refused.CodeOrNull()!;
                    answers[code] = answers.GetValueOrDefault(code) + 1;
                }
            }

            await SendAsync(1, 200);

            // The connection's bucket holds twice the web burst: 10 requests get the no-ticket refusal, the rest are rate-limited.
            Assert.Equal(10, answers["no-ticket"]);
            Assert.Equal(190, answers["rate-limited"]);

            // The clock is frozen: nothing refills. A second later exactly two more requests pass the bucket.
            time.Advance(TimeSpan.FromSeconds(1));
            await SendAsync(1000, 5);
            Assert.Equal(12, answers["no-ticket"]);
            Assert.Equal(193, answers["rate-limited"]);

            await client.DisposeAsync();
            int total = answers.Values.Sum();
            Assert.Equal(205, total);
            await WorldTestHost.WaitForAsync(() => Accounted(WebAuditFile.Read(directory)) == total, "the repeated refusals are flushed when the connection ends");

            JsonElement[] records = WebAuditFile.Read(directory);
            Assert.True(records.Length <= 15, $"205 refusals wrote {records.Length} durable records");
            foreach (string code in new[] { "no-ticket", "rate-limited" })
            {
                int first = records.Count(r => r.GetProperty("event").GetString() == "action" && r.GetProperty("result").GetString() == code);
                int repeats = records.Where(r => r.GetProperty("event").GetString() == "refusals" && r.GetProperty("code").GetString() == code)
                    .Sum(r => r.GetProperty("targets").GetInt32());
                Assert.Equal(answers[code], first + repeats);
            }
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }
}

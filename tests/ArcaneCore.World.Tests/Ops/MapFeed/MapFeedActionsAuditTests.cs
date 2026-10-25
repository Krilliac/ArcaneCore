using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The durable audit of the action channel (design 1.2 step 6, review item I15): <c>atlas-web-audit.jsonl</c>, one JSON object per line
/// <c>{time, event, actor, accountId, ticketRef, clientIp, line, targets, result, code}</c>, flushed before the answer is sent, and an
/// action that cannot be recorded is refused (<c>audit-unavailable</c>) instead of run. The in-memory <c>GmAuditFeature</c> tail is not durable
/// and log sinks can be filtered, which is why this file exists.
/// </summary>
public sealed class MapFeedActionsAuditTests
{
    private const string Ticket = "T-GM-secret-ticket-value";

    private static readonly string[] Fields = ["time", "event", "actor", "accountId", "ticketRef", "clientIp", "line", "targets", "result", "code"];

    private static FeedWorldSetup WebWorld(string directory, FakeFeedTickets tickets, FeedTestClock time, Action<MapFeedActionsOptions>? options = null)
        => new()
        {
            Terrain = true,
            Time = time,
            Tickets = tickets,
            Actions = o =>
            {
                o.Enabled = true;
                o.WebLogin = true;
                o.AuditDirectory = directory;
                options?.Invoke(o);
            },
        };

    private static FakeFeedTickets Tickets() => new FakeFeedTickets().Set(Ticket, 77, "WEBGM", AccountSecurity.GameMaster);

    [WindowsFact]
    public async Task AWebAction_AppendsOneRecord_WithTheDocumentedFields_BeforeTheAnswer_WithSecretsRedactedAndNoTicket()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new FeedTestClock();
            await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(directory, Tickets(), time));
            ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync("web");
            JsonObject actor = ActionWire.Web(Ticket);
            actor["ip"] = "203.0.113.9"; // RFC 5737 TEST-NET-3: a documentation address

            JsonElement result = await ActionWire.ActAsync(client, 1, actor, command: ".zspy password hunter2");

            Assert.True(result.Ok(), result.ToString());
            JsonElement record = Assert.Single(WebAuditFile.Read(directory)); // already on disk: flushed before the answer left
            Assert.Equal(Fields.Order(), record.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal("action", record.GetProperty("event").GetString());
            Assert.Equal("atlas-web:WEBGM", record.GetProperty("actor").GetString());
            Assert.Equal(77, record.GetProperty("accountId").GetInt32());
            Assert.Equal(ActionWire.TicketRef(Ticket), record.GetProperty("ticketRef").GetString(), ignoreCase: true);
            Assert.Equal("203.0.113.9", record.GetProperty("clientIp").GetString());
            Assert.Equal("zspy password ***", record.GetProperty("line").GetString());
            Assert.Equal(0, record.GetProperty("targets").GetInt32());
            Assert.Equal("ok", record.GetProperty("result").GetString());
            Assert.Equal(time.GetUtcNow(), DateTimeOffset.Parse(record.GetProperty("time").GetString()!, CultureInfo.InvariantCulture));

            string file = File.ReadAllText(Path.Combine(directory, WebAuditFile.FileName));
            Assert.DoesNotContain("hunter2", file, StringComparison.Ordinal);
            Assert.DoesNotContain(Ticket, file, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", string.Join('\n', world.ActionLog.Select(e => e.Message)), StringComparison.Ordinal);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task ARefusedWebAction_IsRecordedToo_WithItsCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(directory, Tickets(), new FeedTestClock()));
            ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync("web");

            (await ActionWire.ActAsync(client, 1, ActionWire.Web(Ticket), command: ".zadmin")).AssertRefused("security");

            JsonElement record = Assert.Single(WebAuditFile.Read(directory));
            Assert.Equal("action", record.GetProperty("event").GetString());
            Assert.Equal("security", record.GetProperty("result").GetString());
            Assert.Equal("security", record.GetProperty("code").GetString());
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ALocalAction_IsAppendedToTheDurableFile_OnlyWhenAuditLocalDurableIsOn(bool durable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup
            {
                Actions = o =>
                {
                    o.Enabled = true;
                    o.AuditDirectory = directory;
                    o.AuditLocalDurable = durable;
                },
            });
            ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync();

            Assert.True((await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zplayer")).Ok());

            JsonElement[] records = WebAuditFile.Read(directory);
            if (durable)
            {
                JsonElement record = Assert.Single(records);
                Assert.Equal(ActionWire.ExpectedLocalActor(), record.GetProperty("actor").GetString());
                Assert.Equal("action", record.GetProperty("event").GetString());
            }
            else
            {
                Assert.Empty(records);
            }
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task TheWriter_AcceptsLoginAndRevokeRecords_WithTheDocumentedFields()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        try
        {
            var time = new FeedTestClock();
            await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(directory, Tickets(), time));
            MapFeedWebAudit audit = world.Host.WorldServices.GetRequiredService<MapFeedWebAudit>();

            await audit.AppendAsync(new WebAuditRecord("login", Actor: "atlas-web:NOSUCH", AccountId: null, Ticket: null, ClientIp: "203.0.113.10", Line: null, Targets: 0, Result: "failed", Code: "login-failed"));
            await audit.AppendAsync(new WebAuditRecord("revoke", Actor: "atlas-web:WEBGM", AccountId: 77, Ticket: Ticket, ClientIp: null, Line: null, Targets: 0, Result: "ok", Code: null));

            JsonElement[] records = WebAuditFile.Read(directory);
            Assert.Equal(2, records.Length);
            Assert.All(records, r => Assert.Equal(Fields.Order(), r.EnumerateObject().Select(p => p.Name).Order()));
            Assert.Equal("login", records[0].GetProperty("event").GetString());
            Assert.Equal("failed", records[0].GetProperty("result").GetString());
            Assert.Equal("login-failed", records[0].GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, records[0].GetProperty("accountId").ValueKind);
            Assert.Equal("203.0.113.10", records[0].GetProperty("clientIp").GetString());
            Assert.Equal("revoke", records[1].GetProperty("event").GetString());
            Assert.Equal(77, records[1].GetProperty("accountId").GetInt32());
            Assert.Equal(ActionWire.TicketRef(Ticket), records[1].GetProperty("ticketRef").GetString(), ignoreCase: true);
            Assert.DoesNotContain(Ticket, File.ReadAllText(Path.Combine(directory, WebAuditFile.FileName)), StringComparison.Ordinal);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task AnUnwritableAuditFile_RefusesAWebAction_AuditUnavailable_AndRunsNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, WebAuditFile.FileName)); // a directory where the file must go: opening it for append fails
        try
        {
            await using FeedWorld world = await FeedWorld.StartAsync(WebWorld(directory, Tickets(), new FeedTestClock()));
            ActionSpies spies = ActionSpies.Install(world.Host);
            FeedClient client = await world.ConnectAsync("web");

            JsonElement result = await ActionWire.ActAsync(client, 1, ActionWire.Web(Ticket), command: ".zplayer");

            result.AssertRefused("audit-unavailable");
            Assert.Equal(0, spies.Total);
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }

    [WindowsFact]
    public async Task AnUnwritableAuditFile_RefusesALocalActionToo_WhileLocalAuditIsDurable_AndNotWhenItIsOff()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = WebAuditFile.NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, WebAuditFile.FileName));
        try
        {
            await using (FeedWorld durable = await FeedWorld.StartAsync(new FeedWorldSetup { Actions = o => { o.Enabled = true; o.AuditDirectory = directory; } }))
            {
                ActionSpies spies = ActionSpies.Install(durable.Host);
                FeedClient client = await durable.ConnectAsync();
                (await ActionWire.ActAsync(client, 1, ActionWire.Local(), command: ".zplayer")).AssertRefused("audit-unavailable");
                Assert.Equal(0, spies.Total);
            }

            await using FeedWorld volatileOnly = await FeedWorld.StartAsync(new FeedWorldSetup
            {
                Actions = o =>
                {
                    o.Enabled = true;
                    o.AuditDirectory = directory;
                    o.AuditLocalDurable = false;
                },
            });
            ActionSpies running = ActionSpies.Install(volatileOnly.Host);
            FeedClient other = await volatileOnly.ConnectAsync();
            Assert.True((await ActionWire.ActAsync(other, 1, ActionWire.Local(), command: ".zplayer")).Ok());
            Assert.Equal(1, running.Count("zplayer"));
        }
        finally
        {
            WebAuditFile.TryDelete(directory);
        }
    }
}

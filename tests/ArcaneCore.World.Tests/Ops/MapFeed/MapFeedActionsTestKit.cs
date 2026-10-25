using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Atlas;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.MapFeed;

/// <summary>
/// The wire contract of the action channel (docs/areas/atlas-actions.md; design <c>_land/atlas-devui-design.md</c> 1.1, 1.2, 1.6), as the tests
/// speak it. Every line is a feed line over the real named pipe; nothing here reaches into the channel.
/// <list type="bullet">
/// <item><c>{"op":"action","id":n,"actor":{"tier":"local"}|{"tier":"web","ticket":"..."},"command":"..."|"template":"id","args":{..},"targets":["HEX16",..],"point":{"map","instance","x","y","z","o"},"confirm":"CODE"}</c></item>
/// <item>answer <c>{"op":"result","id":n,"kind":"action","ok":bool,"code"?:"..","message"?:"..","lines":[..],"settled":bool,"failure"?:"..","confirm"?:{"code","expiresIn","summary"}}</c>;
/// a refusal has <c>ok:false</c> and a stable code (see <c>CodeOrNull</c>), an answer, never a strike.</item>
/// <item><c>{"op":"caps","id":n}</c> answered <c>{"op":"result","kind":"caps","ok":true,"actions":bool,"actionsWhyOff"?:"..","login":bool,"liveServer":bool,"operator":"atlas-local:&lt;token user&gt;","role":"local|web"}</c>.</item>
/// <item><c>{"op":"role","id":n,"role":"web"}</c> answered <c>{"ok":true,"role":"web"}</c> (first extension op only; later: <c>role-locked</c>).</item>
/// </list>
/// </summary>
internal static class ActionWire
{
    public const string ActorLocal = """{"tier":"local"}""";

    public static JsonObject Local(string? ignoredName = null)
    {
        var actor = new JsonObject { ["tier"] = "local" };
        if (ignoredName is not null)
        {
            actor["name"] = ignoredName;
        }

        return actor;
    }

    public static JsonObject Web(string ticket) => new() { ["tier"] = "web", ["ticket"] = ticket };

    public static JsonObject Point(uint map, float x, float y, float z, float o = 0f, uint instance = 0) => new()
    {
        ["map"] = map, ["instance"] = instance, ["x"] = x, ["y"] = y, ["z"] = z, ["o"] = o,
    };

    /// <summary>An <c>action</c> line (strings are JSON-escaped by the writer, so a newline in <paramref name="command"/> reaches the world as one).</summary>
    public static string Action(
        long id, JsonObject actor, string? command = null, string? template = null, JsonObject? args = null,
        IEnumerable<string>? targets = null, JsonObject? point = null, string? confirm = null)
    {
        var line = new JsonObject { ["op"] = "action", ["id"] = id, ["actor"] = actor.DeepClone() };
        if (command is not null)
        {
            line["command"] = command;
        }

        if (template is not null)
        {
            line["template"] = template;
        }

        if (args is not null)
        {
            line["args"] = args.DeepClone();
        }

        if (targets is not null)
        {
            line["targets"] = new JsonArray([.. targets.Select(t => (JsonNode?)JsonValue.Create(t))]);
        }

        if (point is not null)
        {
            line["point"] = point.DeepClone();
        }

        if (confirm is not null)
        {
            line["confirm"] = confirm;
        }

        return line.ToJsonString();
    }

    /// <summary>Send <paramref name="line"/> and read until the <c>result</c> of <paramref name="id"/> (other lines, such as a late <c>actionLine</c>, go to <paramref name="others"/>).</summary>
    public static async Task<JsonElement> RequestAsync(FeedClient client, string line, long id, List<JsonElement>? others = null)
    {
        await client.SendAsync(line);
        return await ReadResultAsync(client, id, others);
    }

    public static async Task<JsonElement> ReadResultAsync(FeedClient client, long id, List<JsonElement>? others = null)
    {
        while (true)
        {
            JsonElement next = await client.ReadAsync();
            if (next.Op() == "result" && next.TryGetProperty("id", out JsonElement got) && got.GetInt64() == id)
            {
                return next;
            }

            // An uncorrelated protocol error (an unknown op, bad JSON) is the daemon refusing the line itself: no result for this id will follow.
            Assert.True(next.Op() != "error", $"waiting for the result of request {id}, the daemon answered with a protocol error: {next}");
            others?.Add(next);
        }
    }

    public static async Task<JsonElement> ActAsync(
        FeedClient client, long id, JsonObject actor, string? command = null, string? template = null, JsonObject? args = null,
        IEnumerable<string>? targets = null, JsonObject? point = null, string? confirm = null)
        => await RequestAsync(client, Action(id, actor, command, template, args, targets, point, confirm), id);

    public static bool Ok(this JsonElement result) => result.GetProperty("ok").GetBoolean();

    /// <summary>The refusal code, or null when the answer has none.</summary>
    public static string? CodeOrNull(this JsonElement result) => result.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;

    public static string[] Lines(this JsonElement result) => result.TryGetProperty("lines", out JsonElement lines) ? lines.Strings() : [];

    public static void AssertRefused(this JsonElement result, string code)
    {
        Assert.False(result.Ok(), result.ToString());
        Assert.Equal(code, result.CodeOrNull());
    }

    /// <summary>The lowercase or uppercase hex of the first four bytes of SHA-256 of the ticket text (the audit's <c>ticketRef</c>, design 1.2 step 6).</summary>
    public static string TicketRef(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)))[..8];

    /// <summary>The local actor's invoker name, read independently of the world: this process's token user and pid (the pipe client is this process).</summary>
    public static string ExpectedLocalActor()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the local actor identity is a Windows token user");
        }

        using System.Security.Principal.WindowsIdentity me = System.Security.Principal.WindowsIdentity.GetCurrent();
        return string.Create(CultureInfo.InvariantCulture, $"atlas-local:{me.Name}#{Environment.ProcessId}");
    }
}

/// <summary>A hand-wound <see cref="TimeProvider"/> for the channel's caches, rate limits and confirm expiry (the channel reads only <c>GetUtcNow</c> and <c>GetTimestamp</c>).</summary>
internal sealed class FeedTestClock : TimeProvider
{
    private long _utcTicks = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

    public override long GetTimestamp() => Interlocked.Read(ref _utcTicks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _utcTicks, by.Ticks);
}

/// <summary>The ticket service lane auth implements (<see cref="IFeedActorTickets"/>), faked: tests state what each ticket resolves to.</summary>
internal sealed class FakeFeedTickets : IFeedActorTickets
{
    private readonly ConcurrentDictionary<string, FeedTicketLookup> _tickets = new(StringComparer.Ordinal);

    public int Resolves;

    public FakeFeedTickets Set(string ticket, int accountId, string account, AccountSecurity security, FeedTicketStatus status = FeedTicketStatus.Valid)
    {
        _tickets[ticket] = new FeedTicketLookup(status, accountId, account, security);
        return this;
    }

    public ValueTask<FeedTicketLookup> ResolveAsync(string ticket, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Resolves);
        return ValueTask.FromResult(_tickets.TryGetValue(ticket, out FeedTicketLookup? found)
            ? found
            : new FeedTicketLookup(FeedTicketStatus.Unknown, 0, string.Empty, AccountSecurity.Player));
    }
}

/// <summary>
/// Spy commands added to the live command table (<see cref="CommandTableSource.TryAdd"/>): they count every run, so "nothing ran" is a
/// number and not an inference. <c>zplayer</c> level 0, <c>zmod</c> Moderator, <c>zspy</c> GameMaster, <c>zadmin</c> Administrator,
/// <c>zwho</c> (Player) replies who it ran as, <c>zconsole</c> (GameMaster) is registered as Console-only by the tests that need it,
/// <c>zoffline &lt;accountId&gt;</c> (GameMaster) goes through the shared <c>GmTargets.ActOnOffline</c> gate, <c>zdefer</c> (GameMaster) reads
/// its <see cref="GmActionContext"/> inside a <c>World.Post</c> deferral.
/// </summary>
internal sealed class ActionSpies
{
    internal sealed record Call(string Name, string Args, string Invoker, AccountSecurity Security, bool IsRemote);

    private readonly ConcurrentQueue<Call> _calls = new();
    private int _offlineRan;

    public Call[] Calls => [.. _calls];

    public int Count(string name) => _calls.Count(c => c.Name == name);

    public int Total => _calls.Count;

    /// <summary>How many times the action passed to <c>ActOnOffline</c> ran (it must not, for a target the invoker may not act on).</summary>
    public int OfflineRan => Volatile.Read(ref _offlineRan);

    /// <summary>The click point the deferred part of <c>zdefer</c> saw (null: no action context was attached there).</summary>
    private object? _deferredPoint;

    public GmClickPoint? DeferredPoint => (GmClickPoint?)Volatile.Read(ref _deferredPoint);

    public volatile bool DeferredSawContext;

    private ScopeProbeState _probeState = new();
    private CommandContext? _holdContext;
    private IDisposable? _hold;

    /// <summary>Whether the per-request service scope the held command runs in has been disposed.</summary>
    public bool ScopeProbeDisposed => _probeState.Disposed;

    /// <summary>Whether <see cref="ReleaseHold"/> could still resolve a scoped service from the held command's session services (false: the scope was already disposed under the command).</summary>
    public volatile bool HoldResolvedScope;

    /// <summary>
    /// Finish the held <c>zhold</c> command: first use the command's session services the way a deferred handler would (resolve a scoped
    /// service), then release the hold so the command settles.
    /// </summary>
    public void ReleaseHold()
    {
        try
        {
            _ = _holdContext!.Session.Services.GetRequiredService<ScopeProbe>();
            HoldResolvedScope = true;
        }
        catch (ObjectDisposedException)
        {
            HoldResolvedScope = false;
        }

        _hold!.Dispose();
    }

    private bool Record(CommandContext context, string name, string args, string? reply = null)
    {
        _calls.Enqueue(new Call(name, args, context.InvokerName, context.Security, context.IsRemote));
        context.Reply(reply ?? $"{name} ran");
        return true;
    }

    public static ActionSpies Install(WorldTestHost host)
    {
        var spies = new ActionSpies { _probeState = host.WorldServices.GetService<ScopeProbeState>() ?? new ScopeProbeState() };
        ChatCommand[] roots =
        [
            new("zplayer", AccountSecurity.Player, "Syntax: .zplayer", (c, a) => spies.Record(c, "zplayer", a)),
            new("zmod", AccountSecurity.Moderator, "Syntax: .zmod", (c, a) => spies.Record(c, "zmod", a)),
            new("zspy", AccountSecurity.GameMaster, "Syntax: .zspy", (c, a) => spies.Record(c, "zspy", a)),
            new("zadmin", AccountSecurity.Administrator, "Syntax: .zadmin", (c, a) => spies.Record(c, "zadmin", a)),
            new("zconsole", AccountSecurity.GameMaster, "Syntax: .zconsole", (c, a) => spies.Record(c, "zconsole", a)),
            new("zwho", AccountSecurity.Player, "Syntax: .zwho", (c, a) => spies.Record(c, "zwho", a, $"who:{c.InvokerName}:{c.Security}")),
            new("zoffline", AccountSecurity.GameMaster, "Syntax: .zoffline #accountId", spies.Offline),
            new("zdefer", AccountSecurity.GameMaster, "Syntax: .zdefer", spies.Defer),
            new("zhold", AccountSecurity.GameMaster, "Syntax: .zhold", spies.Hold),
            new("zslow", AccountSecurity.GameMaster, "Syntax: .zslow", (c, a) =>
            {
                Thread.Sleep(600); // on the world thread: a command already running when the query timeout fires
                return spies.Record(c, "zslow", a, "slow ran");
            }),
            new("zctx", AccountSecurity.Player, "Syntax: .zctx", (c, a) => spies.Record(c, "zctx", a, "ctx:" + (GmActionContext.Of(c)?.ActorName ?? "none"))),
        ];
        CommandAddResult added = host.WorldServices.GetRequiredService<CommandTableSource>().TryAdd(roots);
        Assert.True(added.Applied, added.Error);
        return spies;
    }

    private bool Offline(CommandContext context, string args)
    {
        _calls.Enqueue(new Call("zoffline", args, context.InvokerName, context.Security, context.IsRemote));
        int accountId = int.Parse(args.Trim(), CultureInfo.InvariantCulture);

        // The real shared gate (GmTargets.ActOnOffline: ban, reset, send, guild commands all go through it). Its refusal reply arrives from
        // another thread, so the command stays open for a moment; the channel's late-line handling carries the reply into the answer.
        IDisposable hold = context.Defer();
        GmTargets.ActOnOffline(context, accountId, () => Interlocked.Increment(ref _offlineRan));
        _ = Task.Delay(750).ContinueWith(_ => hold.Dispose(), TaskScheduler.Default);
        return true;
    }

    private bool Hold(CommandContext context, string args)
    {
        _calls.Enqueue(new Call("zhold", args, context.InvokerName, context.Security, context.IsRemote));
        _holdContext = context;
        _ = context.Session.Services.GetRequiredService<ScopeProbe>(); // created in the request scope: its Dispose is the scope's
        _hold = context.Defer();
        return true;
    }

    private bool Defer(CommandContext context, string args)
    {
        _calls.Enqueue(new Call("zdefer", args, context.InvokerName, context.Security, context.IsRemote));
        IDisposable hold = context.Defer();
        context.World.Post(() =>
        {
            GmActionContext? attached = GmActionContext.Of(context);
            DeferredSawContext = attached is not null;
            Volatile.Write(ref _deferredPoint, attached?.Point is { } p ? p : null);
            hold.Dispose();
        });
        return true;
    }
}

/// <summary>Records that the per-request DI scope was disposed (registered as a singleton next to <see cref="ScopeProbe"/>).</summary>
internal sealed class ScopeProbeState
{
    public volatile bool Disposed;
}

/// <summary>A scoped service whose disposal marks the end of the request scope it was resolved in.</summary>
internal sealed class ScopeProbe(ScopeProbeState state) : IDisposable
{
    public void Dispose() => state.Disposed = true;
}

/// <summary>Reads the audit files the channel writes.</summary>
internal static class WebAuditFile
{
    public const string FileName = "atlas-web-audit.jsonl";

    public static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "arcanecore-web-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>The records in the file so far (each line must be one JSON object).</summary>
    public static JsonElement[] Read(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var records = new List<JsonElement>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            records.Add(document.RootElement.Clone());
        }

        return [.. records];
    }

    public static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory in the temp folder.
        }
    }
}

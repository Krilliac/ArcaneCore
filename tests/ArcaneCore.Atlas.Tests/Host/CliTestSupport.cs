using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Host;
using ArcaneCore.Atlas.Host.Cli;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Host;

/// <summary>Commands the fixtures register next to the real ones.</summary>
internal static class TestCommands
{
    /// <summary><c>test.echo</c>: returns its text, the call's origin and whether the host is headless.</summary>
    public static CommandDefinition Echo { get; } = new(
        "test.echo",
        "Echoes its text and the origin of the call.",
        [new("text", ArgType.String, "Text to echo.")],
        (invocation, _) => Task.FromResult<JsonNode?>(new JsonObject
        {
            ["echo"] = invocation.OptionalString("text"),
            ["origin"] = invocation.Origin.ToString(),
            ["headless"] = invocation.Headless,
        }));
}

/// <summary>A command that runs until the test lets it go or the call is cancelled.</summary>
internal sealed class GateCommand
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancelled;

    public GateCommand(string name = "test.wait")
    {
        Definition = new CommandDefinition(
            name,
            "Waits until released.",
            [],
            async (_, cancellationToken) =>
            {
                _started.TrySetResult();
                try
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelled);
                    throw;
                }

                return new JsonObject { ["released"] = true };
            });
    }

    public CommandDefinition Definition { get; }

    /// <summary>How many calls were cancelled while waiting.</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    public Task Started => _started.Task;

    public void Release() => _release.TrySetResult();
}

/// <summary>A writer that keeps what it is given as lines and lets a test wait for them. Safe for concurrent writers.</summary>
internal sealed class LineSink : TextWriter
{
    private readonly object _gate = new();
    private readonly StringBuilder _current = new();
    private readonly List<string> _lines = [];

    public override Encoding Encoding => new UTF8Encoding(false);

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string NewLine
    {
        get => "\n";
        set => _ = value;
    }

    /// <summary>The complete lines written so far.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>Everything written so far, with an unfinished last line.</summary>
    public string Text
    {
        get
        {
            lock (_gate)
            {
                return string.Concat(_lines.Select(l => l + "\n")) + _current;
            }
        }
    }

    public IReadOnlyList<JsonNode> Json => [.. Lines.Select(l => JsonNode.Parse(l)!)];

    public override void Write(char value)
    {
        lock (_gate)
        {
            Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (char c in value)
            {
                Append(c);
            }
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                Append(buffer[index + i]);
            }
        }
    }

    public async Task<IReadOnlyList<string>> WaitForLinesAsync(int count, int timeoutMs = 20_000)
    {
        await Wait.UntilAsync(() => Lines.Count >= count, $"{count} output line(s); got {Lines.Count}: {Text}", timeoutMs);
        return Lines;
    }

    private void Append(char c)
    {
        if (c == '\n')
        {
            _lines.Add(_current.ToString());
            _current.Clear();
        }
        else if (c != '\r')
        {
            _current.Append(c);
        }
    }
}

/// <summary>A reader fed line by line from the test, so a server can be kept waiting for input (or told it ended).</summary>
internal sealed class InputPipe : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public void Send(string line) => _lines.Writer.TryWrite(line);

    public void Send(JsonNode message) => Send(message.ToJsonString());

    /// <summary>Ends the input, as a closed standard input does.</summary>
    public void End() => _lines.Writer.TryComplete();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (await _lines.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_lines.Reader.TryRead(out string? line))
            {
                return line;
            }
        }

        return null;
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async Task<string> ReadToEndAsync(CancellationToken cancellationToken)
    {
        var all = new StringBuilder();
        while (await ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            all.Append(line).Append('\n');
        }

        return all.ToString();
    }
}

internal sealed class RecordingBrowser(string? name = "Fake Browser") : IBrowserLauncher
{
    public Uri? Opened { get; private set; }

    public int Opens { get; private set; }

    public string? TryOpenApp(Uri url)
    {
        Opened = url;
        Opens++;
        return name;
    }
}

/// <summary>What a finished verb left behind.</summary>
internal sealed record CliResult(int Exit, IReadOnlyList<string> Lines, string Error)
{
    public JsonNode Json(int line = 0) => JsonNode.Parse(Lines[line])!;

    /// <summary>The whole standard output as one JSON document (for the indented, multi-line form).</summary>
    public JsonNode Document() => JsonNode.Parse(string.Join('\n', Lines))!;
}

/// <summary>A verb that has been started; use <see cref="CompleteAsync"/> for verbs that end by themselves.</summary>
internal sealed class CliRun : IAsyncDisposable
{
    public CliRun(Task<int> exit, LineSink output, LineSink error, CancellationTokenSource cancel)
    {
        Exit = exit;
        Out = output;
        Error = error;
        Cancel = cancel;
    }

    public Task<int> Exit { get; }

    public LineSink Out { get; }

    public LineSink Error { get; }

    public CancellationTokenSource Cancel { get; }

    public async Task<CliResult> CompleteAsync(int timeoutMs = 60_000)
    {
        int exit = await Exit.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        return new CliResult(exit, Out.Lines, Error.Text);
    }

    /// <summary>Asks the verb to stop (as Ctrl+C does) and returns its result.</summary>
    public async Task<CliResult> StopAsync()
    {
        await Cancel.CancelAsync();
        return await CompleteAsync(30_000);
    }

    public async ValueTask DisposeAsync()
    {
        await Cancel.CancelAsync();
        try
        {
            await Exit.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            // Nothing more to do for a verb that ignores cancellation; the test has already failed.
        }

        Cancel.Dispose();
    }
}

/// <summary>
/// A command-line environment for <see cref="AtlasCli"/>: its own state folder (or a given one), the fixture module
/// list, an environment-variable table instead of the process's, and a browser that only records what it was asked to open.
/// </summary>
internal sealed class CliRig : IDisposable
{
    private readonly TempState? _temp;

    public CliRig(AtlasState? state = null, Action<AtlasContext>? register = null)
    {
        if (state is null)
        {
            _temp = new TempState();
            state = _temp.State;
        }

        State = state;
        Terrain = new FakeLayerSource(LayerIds.Terrain);
        Renderer = new FakeRenderer();
        Modules =
        [
            new HostModule(State),
            new TestModule(context =>
            {
                context.Layers.Add(Terrain);
                context.Services.Add<IHeadlessRenderer>(Renderer);
                context.Commands.Register(TestCommands.Echo);
                register?.Invoke(context);
            }),
        ];
    }

    public AtlasState State { get; }

    public FakeLayerSource Terrain { get; }

    public FakeRenderer Renderer { get; }

    public IReadOnlyList<IAtlasModule> Modules { get; }

    public RecordingBrowser Browser { get; set; } = new();

    public Dictionary<string, string> Env { get; } = new(StringComparer.Ordinal);

    public TextReader Stdin { get; set; } = TextReader.Null;

    public Func<DiscoveryRecord, bool>? IsAlive { get; set; }

    public IWebAssets Assets { get; set; } = new FakeAssets();

    public CliEnvironment Environment(LineSink output, LineSink error) => new()
    {
        In = Stdin,
        Out = output,
        Error = error,
        State = State,
        GetEnvironmentVariable = name => Env.GetValueOrDefault(name),
        Modules = Modules,
        Loggers = NullLoggerFactory.Instance,
        Browser = Browser,
        Assets = Assets,
        IsAlive = IsAlive,
    };

    public CliRun Start(params string[] args)
    {
        var output = new LineSink();
        var error = new LineSink();
        var cancel = new CancellationTokenSource();
        Task<int> exit = Task.Run(() => AtlasCli.RunAsync(args, Environment(output, error), cancel.Token));
        return new CliRun(exit, output, error, cancel);
    }

    /// <summary>Runs a verb that ends by itself.</summary>
    public async Task<CliResult> RunAsync(params string[] args)
    {
        await using CliRun run = Start(args);
        return await run.CompleteAsync();
    }

    public void Dispose() => _temp?.Dispose();
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.Control;

/// <summary>
/// The world daemon's local control pipe (docs/areas/server-console.md, "Control pipe"): the console commands, sent over a Windows
/// named pipe by a local tool (<c>tools/ops/live/redeploy.ps1</c>) instead of typed at the console window. Off by default
/// (<see cref="WorldControlPipeOptions.Enabled"/>).
/// <para>
/// Security: one pipe instance for the life of the process, created with <c>FirstPipeInstance</c> (a name another process created
/// first is refused, never shared), the descriptor of <see cref="ControlPipeSecurity.Create"/> (the daemon's account and SYSTEM only,
/// NETWORK denied) and the runtime's remote-client rejection. Each line runs through <see cref="WorldConsoleExecutor"/> exactly like a
/// console line, so it has the console's authority (Administrator, no character), its GM audit entry (<c>Console</c>) and its
/// redaction; the line is also logged here (<see cref="LogCategory"/>) with the client's process id.
/// </para>
/// <para>
/// Protocol (UTF-8, one line per message): the client sends a command line; the daemon answers with the command's reply lines (each
/// prefixed <see cref="WorldConsoleExecutor.Prefix"/>) and then <see cref="DoneMarker"/> once the world thread has run it. A command that
/// finishes later (<c>.reload</c>, <c>.hotmodule</c>) sends its result lines afterwards, while the connection stays open. One client
/// at a time; a client silent for <see cref="WorldControlPipeOptions.IdleTimeoutSeconds"/> or sending a line longer than
/// <see cref="WorldControlPipeOptions.MaxLineLength"/> is disconnected.
/// </para>
/// <para>
/// Threads: the accept loop and each session run on the thread pool; commands run on the world thread. Replies are queued and written
/// by a per-session pump, so a client that stops reading can never block the world thread (a full queue drops lines and says so).
/// </para>
/// </summary>
public sealed partial class WorldControlPipe : IHostedService
{
    /// <summary>Logger category of the "Control pipe command:" lines.</summary>
    public const string LogCategory = "ArcaneCore.Control";

    /// <summary>The line the daemon sends after the world thread ran a command.</summary>
    public const string DoneMarker = "#done";

    /// <summary>How long a session waits for the world thread to run a command.</summary>
    public static readonly TimeSpan WorldWait = TimeSpan.FromSeconds(30);

    /// <summary>How long a finished session's DI scope is kept for replies of commands that finish later.</summary>
    internal static readonly TimeSpan ScopeGrace = TimeSpan.FromMinutes(2);

    private const int QueuedReplyLines = 4096;

    private readonly WorldControlPipeOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly OpcodeTable _opcodes;
    private readonly WorldRuntime _world;
    private readonly SessionRegistry _registry;
    private readonly WorldSessionOptions _sessionOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<object, AsyncServiceScope> _retiredScopes = new();
    private NamedPipeServerStream? _server;
    private Task? _loop;
    private int _sessions;

    public WorldControlPipe(
        IOptions<WorldControlPipeOptions> options,
        IServiceScopeFactory scopes,
        OpcodeTable opcodes,
        WorldRuntime world,
        SessionRegistry registry,
        IOptions<WorldSessionOptions> sessionOptions,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _scopes = scopes;
        _opcodes = opcodes;
        _world = world;
        _registry = registry;
        _sessionOptions = sessionOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger(LogCategory);
    }

    /// <summary>The pipe name being served (without <c>\\.\pipe\</c>), or null when the pipe is off.</summary>
    public string? PipeName { get; private set; }

    /// <summary>Whether the accept loop is running.</summary>
    public bool IsListening => _loop is { IsCompleted: false };

    /// <summary>Connections served so far (tests).</summary>
    public int SessionsServed => Volatile.Read(ref _sessions);

    /// <summary>The server stream (tests read its security descriptor); null when the pipe is off.</summary>
    internal NamedPipeServerStream? Server => _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("control pipe off: World:Control:Pipe:Enabled is false");
            return Task.CompletedTask;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogWarning("control pipe off: World:Control:Pipe is Windows-only (a named pipe with an access list)");
            return Task.CompletedTask;
        }

        string? name = ControlPipeSecurity.ResolveName(_options.Name, Environment.ProcessId, out string? problem);
        if (name is null)
        {
            _logger.LogError("control pipe off: World:Control:Pipe:Name: {Problem}", problem);
            return Task.CompletedTask;
        }

        if (_options.IdleTimeoutSeconds <= 0 || _options.MaxLineLength < 16)
        {
            _logger.LogError("control pipe off: World:Control:Pipe:IdleTimeoutSeconds must be above 0 and MaxLineLength at least 16");
            return Task.CompletedTask;
        }

        Listen(name);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private void Listen(string name)
    {
        string user;
        NamedPipeServerStream server;
        try
        {
            System.Security.Principal.SecurityIdentifier sid = ControlPipeSecurity.CurrentUser();
            user = sid.Value;
            server = NamedPipeServerStreamAcl.Create(
                name,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                inBufferSize: 0,
                outBufferSize: 0,
                ControlPipeSecurity.Create(sid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // FirstPipeInstance: a name some other process created first is never joined (it could read every command).
            _logger.LogError(ex, "control pipe off: could not create \\\\.\\pipe\\{Name} (does another process already hold the name?)", name);
            return;
        }

        _server = server;
        PipeName = name;
        _loop = Task.Run(() => ServeAsync(server, _stopping.Token));
        _logger.LogWarning(
            "control pipe on: \\\\.\\pipe\\{Name} runs console commands (Administrator) for {User} and SYSTEM on this machine (World:Control:Pipe)", name, user);
    }

    [SupportedOSPlatform("windows")]
    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                Interlocked.Increment(ref _sessions);
                await ServeSessionAsync(server, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "control pipe session ended with an error");
            }
            finally
            {
                try
                {
                    server.Disconnect();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    // Not connected (the wait was cancelled) or already broken by the client: nothing to release.
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task ServeSessionAsync(NamedPipeServerStream server, CancellationToken token)
    {
        string client = DescribeClient(server);
        _logger.LogInformation("control pipe: client connected ({Client})", client);
        var replies = new ReplyQueue(QueuedReplyLines);
        Task pump = replies.PumpAsync(server, token);
        AsyncServiceScope scope = _scopes.CreateAsyncScope();
        try
        {
            WorldConsoleExecutor executor = WorldConsoleExecutor.Create(
                scope.ServiceProvider, _opcodes, _world, _registry, _sessionOptions, _loggerFactory.CreateLogger<WorldSession>(), replies);
            var reader = new BoundedLineReader(server, _options.MaxLineLength);
            while (true)
            {
                (string? line, bool tooLong) read;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(_options.IdleTimeoutSeconds));
                    try
                    {
                        read = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _logger.LogInformation("control pipe: closing an idle connection ({Client})", client);
                        executor.WriteLine($"no command for {_options.IdleTimeoutSeconds} s; closing the connection");
                        break;
                    }
                }

                if (read.tooLong)
                {
                    _logger.LogWarning("control pipe: a line longer than {Max} bytes closed the connection ({Client}); nothing was run", _options.MaxLineLength, client);
                    executor.WriteLine($"line longer than {_options.MaxLineLength} bytes; nothing was run, closing the connection");
                    break;
                }

                if (read.line is null)
                {
                    break;
                }

                await SubmitAsync(executor, read.line, client, token).ConfigureAwait(false);
                replies.WriteRaw(DoneMarker);
            }
        }
        finally
        {
            replies.Complete();
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                // A client that stopped reading: Disconnect (the caller's finally) breaks the pending write.
            }

            RetireScope(scope);
            _logger.LogInformation("control pipe: client disconnected ({Client}){Dropped}", client,
                replies.Dropped == 0 ? string.Empty : $"; {replies.Dropped} reply lines dropped (the client did not read them)");
        }
    }

    /// <summary>Log the line (redacted) and run it on the world thread like a console line; never throws for a command failure.</summary>
    private async Task SubmitAsync(WorldConsoleExecutor executor, string line, string client, CancellationToken token)
    {
        _logger.LogInformation("Control pipe command: {Line} ({Client})", ConsoleCommandRedactor.Redact(line), client);
        try
        {
            await _world.InvokeAsync(() =>
            {
                executor.Execute(line);
                return 0;
            }, token).WaitAsync(WorldWait, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            executor.WriteLine("the world did not run the command within " + (int)WorldWait.TotalSeconds + " s; it stays queued");
        }
        catch (OperationCanceledException)
        {
            executor.WriteLine("the world is stopping; command not run");
            throw;
        }
    }

    /// <summary>
    /// Keep a finished session's scope alive for <see cref="ScopeGrace"/>: a command that replies later (a reload) still resolves
    /// services through its session. Disposed by a timer, or at stop.
    /// </summary>
    private void RetireScope(AsyncServiceScope scope)
    {
        object key = new();
        _retiredScopes[key] = scope;
        _ = Task.Delay(ScopeGrace, _stopping.Token).ContinueWith(
            _ =>
            {
                if (_retiredScopes.TryRemove(key, out AsyncServiceScope retired))
                {
                    _ = retired.DisposeAsync().AsTask();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    [SupportedOSPlatform("windows")]
    private static string DescribeClient(NamedPipeServerStream server)
    {
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint pid))
        {
            return "client pid unknown";
        }

        string name;
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = "exited";
        }

        return $"client pid {pid} {name}";
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                _logger.LogWarning("control pipe: the accept loop did not end within 5 s");
            }
        }

        _server?.Dispose();
        foreach (object key in _retiredScopes.Keys)
        {
            if (_retiredScopes.TryRemove(key, out AsyncServiceScope scope))
            {
                await scope.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The reply sink of one session: lines are queued (any thread, never blocking) and written to the pipe by <see cref="PumpAsync"/>.
    /// After <see cref="Complete"/> or a broken pipe every further line is dropped, so a reply that arrives after the client left
    /// never reaches the next client of the same pipe instance.
    /// </summary>
    private sealed class ReplyQueue(int capacity) : TextWriter
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
        private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        private readonly StringBuilder _partial = new();
        private int _dropped;

        public override Encoding Encoding => Utf8;

        public int Dropped => Volatile.Read(ref _dropped);

        public override void Write(char value)
        {
            lock (_partial)
            {
                if (value == '\n')
                {
                    Enqueue(_partial.ToString().TrimEnd('\r'));
                    _partial.Clear();
                }
                else
                {
                    _partial.Append(value);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_partial)
            {
                string line = _partial.Length == 0 ? value ?? string.Empty : _partial.Append(value).ToString();
                _partial.Clear();
                Enqueue(line);
            }
        }

        /// <summary>A protocol line (no prefix).</summary>
        public void WriteRaw(string line) => Enqueue(line);

        public void Complete() => _lines.Writer.TryComplete();

        private void Enqueue(string line)
        {
            // Lines may hold '\n' only through Write(char); a reply line itself is one protocol line.
            if (!_lines.Writer.TryWrite(line.Replace('\n', ' ')))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        public async Task PumpAsync(Stream stream, CancellationToken token)
        {
            try
            {
                await foreach (string line in _lines.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    byte[] bytes = Utf8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // The client left or the daemon stops: drop whatever is still queued.
            }
            finally
            {
                _lines.Writer.TryComplete();
                while (_lines.Reader.TryRead(out _))
                {
                    Interlocked.Increment(ref _dropped);
                }
            }
        }
    }

    /// <summary>Reads '\n'-terminated UTF-8 lines (a trailing '\r' dropped) of at most a given number of bytes.</summary>
    private sealed class BoundedLineReader(Stream stream, int maxBytes)
    {
        private readonly byte[] _buffer = new byte[4096];
        private readonly MemoryStream _line = new();
        private int _start;
        private int _end;

        /// <summary>The next line; (null, false) at the end of the input; (null, true) when the line exceeds the limit.</summary>
        public async Task<(string? Line, bool TooLong)> ReadLineAsync(CancellationToken token)
        {
            while (true)
            {
                if (_start == _end)
                {
                    int read = await stream.ReadAsync(_buffer, token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return _line.Length > 0 ? (Take(), false) : (null, false);
                    }

                    _start = 0;
                    _end = read;
                }

                int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
                int stop = newline < 0 ? _end : newline;
                _line.Write(_buffer, _start, stop - _start);
                _start = newline < 0 ? _end : newline + 1;
                if (_line.Length > maxBytes)
                {
                    return (null, true);
                }

                if (newline >= 0)
                {
                    return (Take(), false);
                }
            }
        }

        private string Take()
        {
            string text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length).TrimEnd('\r');
            _line.SetLength(0);
            return text;
        }
    }
}

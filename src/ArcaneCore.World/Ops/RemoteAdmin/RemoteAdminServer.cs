using ArcaneCore.Kernel.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using ArcaneCore.Cryptography;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Accounts;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.RemoteAdmin;

/// <summary>
/// The world daemon's remote administration listener (RA, docs/ops/remote-admin.md): GM commands over a TCP line protocol, the
/// mangosd RA (cmangos RASocket.cpp) with an automation mode for scripts and AI agents. On by default, loopback only
/// (<see cref="RemoteAdminOptions"/>).
/// <para>
/// Login: <c>Username:</c> / <c>Password:</c> prompts (interactive, like mangosd), or one line <c>@login &lt;user&gt; &lt;password&gt;</c> or
/// <c>@token &lt;token&gt;</c> (automation). The account must be active and at least <see cref="RemoteAdminOptions.MinSecurity"/>; the password is
/// checked against the stored SRP6 verifier (the password itself is never stored or logged). The token (<see cref="RemoteAdminOptions.AgentLoginFile"/>)
/// logs in as the console (Administrator). Answers: <c>+Logged in as NAME (Level).</c> or <c>-Wrong username or password.</c>
/// </para>
/// <para>
/// Commands: one line each, without the leading '.', run on the world thread exactly like a console line but with the account's security
/// (every command is checked against it, and GM-audited as <c>RA:NAME</c>). In automation mode (an <c>@</c> login, or <c>@auto on</c>) each
/// command's reply lines are followed by exactly one <c>#done ok</c> or <c>#done error &lt;reason&gt;</c> (reasons: unknown-command,
/// unknown-subcommand, security, syntax, needs-player, exception, timeout, world-timeout, empty, or the command's own, e.g. a <c>.botctl</c>
/// code); <c>@id TAG command</c> echoes TAG after <c>#done</c>. A command that holds itself open (<c>.botctl wait</c>/<c>run</c>) is done
/// when its work is, or reported <c>#done error timeout</c> after <see cref="RemoteAdminOptions.CommandTimeoutSeconds"/> while it goes on.
/// Lines starting <c>#event</c> are pushed asynchronously (<c>.botctl stream</c>). <c>@ping</c> answers <c>#pong</c>; <c>quit</c> closes.
/// Commands of one connection run one at a time, except that one still running after the command timeout (<c>#done error timeout</c> in
/// automation mode) goes on while the next lines run.
/// </para>
/// </summary>
public sealed class RemoteAdminServer : IHostedService
{
    public const string LogCategory = "ArcaneCore.RemoteAdmin";

    /// <summary>The first word of the line that ends each command in automation mode.</summary>
    public const string DoneMarker = "#done";

    /// <summary>The interactive prompt (mangosd prints "mangos>").</summary>
    public const string Prompt = "AC>";

    private static readonly TimeSpan WorldWait = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ScopeGrace = TimeSpan.FromMinutes(2);

    private readonly RemoteAdminOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly OpcodeTable _opcodes;
    private readonly WorldRuntime _world;
    private readonly SessionRegistry _registry;
    private readonly WorldSessionOptions _sessionOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<int, Task> _connections = new();
    private TcpListener? _listener;
    private Task? _loop;
    private string? _tokenPath;
    private byte[]? _token;
    private int _nextConnection;

    public RemoteAdminServer(
        IOptions<RemoteAdminOptions> options,
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

    /// <summary>The bound port while listening (the configured one, or the one the system chose for port 0), else 0.</summary>
    public int Port { get; private set; }

    /// <summary>Whether the accept loop runs.</summary>
    public bool IsListening => _loop is { IsCompleted: false };

    /// <summary>The token of <c>@token</c> login (tests), or null when token login is off.</summary>
    internal string? Token => _token is null ? null : Encoding.ASCII.GetString(_token);

    /// <summary>Where the token was written, or null.</summary>
    public string? TokenPath => _tokenPath;

    /// <summary>Connections open now.</summary>
    public int OpenConnections => _connections.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("remote admin off: World:RemoteAdmin:Enabled is false");
            return Task.CompletedTask;
        }

        if (!IPAddress.TryParse(_options.BindAddress, out IPAddress? address))
        {
            _logger.LogError("remote admin off: World:RemoteAdmin:BindAddress '{Address}' is not an IP address", _options.BindAddress);
            return Task.CompletedTask;
        }

        if (_options.Port is < 0 or > 65535 || _options.MaxConnections < 1 || _options.MaxLineLength < 64)
        {
            _logger.LogError("remote admin off: World:RemoteAdmin needs Port 0..65535, MaxConnections >= 1 and MaxLineLength >= 64");
            return Task.CompletedTask;
        }

        var listener = new TcpListener(address, _options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "remote admin off: could not listen on {Address}:{Port}", address, _options.Port);
            return Task.CompletedTask;
        }

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        WriteToken();
        _loop = Task.Run(() => AcceptAsync(listener, _stopping.Token));
        if (!IPAddress.IsLoopback(address))
        {
            _logger.LogWarning("remote admin listens on {Address}:{Port}, which is not a loopback address: the RA protocol is clear text (passwords included)", address, Port);
        }
        else
        {
            _logger.LogInformation("remote admin on: {Address}:{Port} (accounts of {Level} and up{Token})", address, Port, _options.MinSecurity,
                _tokenPath is null ? string.Empty : "; @token login, token in " + _tokenPath);
        }

        return Task.CompletedTask;
    }

    private void WriteToken()
    {
        if (string.IsNullOrWhiteSpace(_options.AgentLoginFile)) return;
        string path = Path.IsPathRooted(_options.AgentLoginFile) ? _options.AgentLoginFile : Path.Combine(AppContext.BaseDirectory, _options.AgentLoginFile);
        byte[] token = Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        try
        {
            File.WriteAllText(path, Encoding.ASCII.GetString(token) + "\n");
            _token = token;
            _tokenPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "remote admin: could not write the token file {Path}; @token login is off", path);
        }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (token.IsCancellationRequested) break;
                _logger.LogWarning(ex, "remote admin: accept failed");
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            int id = Interlocked.Increment(ref _nextConnection);
            if (_connections.Count >= _options.MaxConnections)
            {
                _logger.LogWarning("remote admin: refused {Endpoint}: {Max} connections are open", client.Client.RemoteEndPoint, _options.MaxConnections);
                _ = RefuseAsync(client);
                continue;
            }

            Task run = Task.Run(() => ServeAsync(id, client, token));
            _connections[id] = run;
            _ = run.ContinueWith(_ => _connections.TryRemove(id, out Task? _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task RefuseAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes("-Too many connections; try again later.\r\n");
                await client.GetStream().WriteAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // The client left first.
            }
        }
    }

    private async Task ServeAsync(int id, TcpClient client, CancellationToken stopping)
    {
        string endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        client.NoDelay = true;
        NetworkStream stream = client.GetStream();
        var output = new LineQueue(8192);
        // The pump outlives the connection's lifetime token: the last lines ("+Bye.", a refusal) still go out before the socket closes.
        Task pump = output.PumpAsync(stream, stopping);
        AsyncServiceScope? scope = null;
        try
        {
            var reader = new LineReader(stream, _options.MaxLineLength);
            Login? login = await LoginAsync(reader, output, endpoint, lifetime.Token).ConfigureAwait(false);
            if (login is null) return;
            scope = _scopes.CreateAsyncScope();
            WorldConsoleExecutor executor = WorldConsoleExecutor.CreateRemote(scope.Value.ServiceProvider, _opcodes, _world, _registry,
                _sessionOptions, _loggerFactory.CreateLogger<WorldSession>(), output, login.Security, "RA:" + login.Name, lifetime.Token);
            await CommandsAsync(executor, reader, output, login, endpoint, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The daemon stops, or the connection timed out.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "remote admin connection {Id} ({Endpoint}) broke", id, endpoint);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("ops.remote-admin", ex);
            _logger.LogWarning(ex, "remote admin connection {Id} ({Endpoint}) ended with an error", id, endpoint);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            output.Complete();
            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                // A client that stopped reading.
            }

            client.Dispose();
            if (scope is { } retired)
            {
                // A command that replies later still resolves services through its session for a while.
                _ = Task.Delay(ScopeGrace, CancellationToken.None).ContinueWith(_ => retired.DisposeAsync().AsTask(), TaskScheduler.Default);
            }

            _logger.LogInformation("remote admin connection {Id} ({Endpoint}) closed{Dropped}", id, endpoint,
                output.Dropped == 0 ? string.Empty : $"; {output.Dropped} reply lines dropped (the client did not read them)");
        }
    }

    private sealed record Login(string Name, AccountSecurity Security, bool Automation);

    private async Task<Login?> LoginAsync(LineReader reader, LineQueue output, string endpoint, CancellationToken token)
    {
        output.WriteLine("ArcaneCore remote administration. Log in with your account (or one line: @login <user> <password> | @token <token>).");
        int failures = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.LoginTimeoutSeconds)));
        try
        {
            while (true)
            {
                output.WriteLine("Username:");
                string? first = await ReadAsync(reader, output, timeout.Token).ConfigureAwait(false);
                if (first is null) return null;
                first = first.Trim();
                if (first.Length == 0) continue;
                Login? login;
                string who;
                if (first.StartsWith("@token", StringComparison.OrdinalIgnoreCase))
                {
                    who = "@token";
                    login = CheckToken(first.Length > 6 ? first[6..].Trim() : string.Empty);
                }
                else if (first.StartsWith("@login", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = first.Length > 6 ? first[6..].Trim() : string.Empty;
                    int space = rest.IndexOf(' ');
                    who = space < 0 ? rest : rest[..space];
                    login = space < 0 ? null : await CheckAccountAsync(who, rest[(space + 1)..], automation: true, token).ConfigureAwait(false);
                }
                else
                {
                    who = first;
                    output.WriteLine("Password:");
                    string? password = await ReadAsync(reader, output, timeout.Token).ConfigureAwait(false);
                    if (password is null) return null;
                    login = await CheckAccountAsync(first, password, automation: false, token).ConfigureAwait(false);
                }

                if (login is not null)
                {
                    _logger.LogInformation("remote admin: {Endpoint} logged in as {Name} ({Level})", endpoint, login.Name, login.Security);
                    output.WriteLine($"+Logged in as {login.Name} ({login.Security}).");
                    return login;
                }

                failures++;
                _logger.LogWarning("remote admin: failed login {Count} from {Endpoint} as '{Name}'", failures, endpoint, Truncate(who, 32));
                await Task.Delay(Math.Max(0, _options.FailedLoginDelayMs), token).ConfigureAwait(false);
                if (failures >= Math.Max(1, _options.MaxFailedLogins))
                {
                    output.WriteLine("-Too many failed logins; closing.");
                    return null;
                }

                output.WriteLine("-Wrong username or password.");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            output.WriteLine("-Login timed out; closing.");
            return null;
        }
    }

    private Login? CheckToken(string presented)
    {
        if (_token is not { } token || presented.Length == 0) return null;
        byte[] bytes = Encoding.ASCII.GetBytes(presented);
        return bytes.Length == token.Length && CryptographicOperations.FixedTimeEquals(bytes, token)
            ? new Login(CommandContext.ConsoleName, AccountSecurity.Administrator, Automation: true)
            : null;
    }

    private async Task<Login?> CheckAccountAsync(string name, string password, bool automation, CancellationToken token)
    {
        if (name.Length is 0 or > 32 || password.Length is 0 or > 128) return null;
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        IAccountStore? accounts = scope.ServiceProvider.GetService<IAccountStore>();
        if (accounts is null) return null;
        // The shared check (equal-time verifier, status column and ban rows, as the realm and the gateway judge them): a banned account is refused.
        AccountCheckResult check = await AccountPasswordCheck.CheckAsync(accounts, scope.ServiceProvider.GetService<IBanStore>(), name, password,
            scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System, token).ConfigureAwait(false);
        if (check.Account is not { } account || account.Security < _options.MinSecurity) return null;
        return new Login(account.Username, account.Security, automation);
    }

    private async Task CommandsAsync(WorldConsoleExecutor executor, LineReader reader, LineQueue output, Login login, string endpoint,
        CancellationToken token)
    {
        bool automation = login.Automation;
        while (!token.IsCancellationRequested)
        {
            if (!automation) output.WriteRaw(Prompt + " ");
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                if (_options.IdleTimeoutSeconds > 0) idle.CancelAfter(TimeSpan.FromSeconds(_options.IdleTimeoutSeconds));
                try
                {
                    line = await ReadAsync(reader, output, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    output.WriteLine($"-No command for {_options.IdleTimeoutSeconds} s; closing.");
                    return;
                }
            }

            if (line is null) return;
            string text = line.Trim();
            if (text.Length == 0) continue;
            string? tag = null;
            if (text.StartsWith("@id ", StringComparison.OrdinalIgnoreCase))
            {
                string rest = text[4..].TrimStart();
                int space = rest.IndexOf(' ');
                tag = space < 0 ? rest : rest[..space];
                text = space < 0 ? string.Empty : rest[(space + 1)..].Trim();
            }

            string word = text.Split(' ', 2)[0].TrimStart('.').ToLowerInvariant();
            switch (word)
            {
                case "quit" or "exit" or "logout" or "@quit" when !text.Contains(' '):
                    output.WriteLine("+Bye.");
                    return;
                case "@ping":
                    output.WriteLine(Done(tag, "#pong"));
                    continue;
                case "@auto":
                    automation = !text.EndsWith(" off", StringComparison.OrdinalIgnoreCase);
                    output.WriteLine(Done(tag, DoneMarker + " ok"));
                    continue;
                case "":
                    if (automation) output.WriteLine(Done(tag, DoneMarker + " error empty"));
                    continue;
            }

            _logger.LogDebug("RA command from {Name} ({Endpoint}): {Line}", login.Name, endpoint, ConsoleCommandRedactor.Redact(text));
            string? failure = await RunAsync(executor, text, token).ConfigureAwait(false);
            if (automation) output.WriteLine(Done(tag, failure is null ? DoneMarker + " ok" : DoneMarker + " error " + failure));
        }
    }

    private static string Done(string? tag, string marker)
    {
        if (tag is null) return marker;
        int space = marker.IndexOf(' ');
        return space < 0 ? marker + " " + tag : marker[..space] + " " + tag + marker[space..];
    }

    /// <summary>Run one line on the world thread and wait until it settled; the failure reason, or null when it succeeded.</summary>
    private async Task<string?> RunAsync(WorldConsoleExecutor executor, string text, CancellationToken token)
    {
        CommandContext? context;
        try
        {
            context = await _world.InvokeAsync(() => executor.Run(text), token).WaitAsync(WorldWait, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            executor.WriteLine($"the world did not run the command within {(int)WorldWait.TotalSeconds} s; it stays queued");
            return "world-timeout";
        }

        if (context is null) return "empty";
        try
        {
            await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(Math.Max(1, _options.CommandTimeoutSeconds)), token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            executor.WriteLine($"the command still runs after {_options.CommandTimeoutSeconds} s; its later lines follow");
            return "timeout";
        }

        return context.Failure;
    }

    private static async Task<string?> ReadAsync(LineReader reader, LineQueue output, CancellationToken token)
    {
        (string? line, bool tooLong) = await reader.ReadLineAsync(token).ConfigureAwait(false);
        if (tooLong)
        {
            output.WriteLine("-Line too long; closing.");
            return null;
        }

        return line;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        Task[] running = [.. _connections.Values];
        if (_loop is { } loop) running = [.. running, loop];
        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning("remote admin: connections did not close within 5 s");
        }

        if (_tokenPath is { } path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Overwritten with a fresh token at the next start anyway.
            }
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The reply sink of one connection: lines queued from any thread (never blocking the world thread) and written by
    /// <see cref="PumpAsync"/>; a full queue drops lines and counts them.
    /// </summary>
    internal sealed class LineQueue(int capacity) : TextWriter
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
                    Enqueue(_partial.ToString().TrimEnd('\r') + "\r\n");
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
                Enqueue(line.Replace('\n', ' ').Replace('\r', ' ') + "\r\n");
            }
        }

        /// <summary>Text without a line end (the interactive prompt).</summary>
        public void WriteRaw(string text) => Enqueue(text);

        public void Complete() => _lines.Writer.TryComplete();

        private void Enqueue(string text)
        {
            if (!_lines.Writer.TryWrite(text)) Interlocked.Increment(ref _dropped);
        }

        public async Task PumpAsync(Stream stream, CancellationToken token)
        {
            try
            {
                await foreach (string text in _lines.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    await stream.WriteAsync(Utf8.GetBytes(text), token).ConfigureAwait(false);
                    if (_lines.Reader.Count == 0) await stream.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or SocketException)
            {
                // The client left or the daemon stops.
            }
            finally
            {
                _lines.Writer.TryComplete();
                while (_lines.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
            }
        }
    }

    /// <summary>Reads '\n'-terminated UTF-8 lines (a trailing '\r' and telnet IAC sequences dropped) of at most a given number of bytes.</summary>
    internal sealed class LineReader(Stream stream, int maxBytes)
    {
        private readonly byte[] _buffer = new byte[4096];
        private readonly MemoryStream _line = new();
        private int _start;
        private int _end;

        public async Task<(string? Line, bool TooLong)> ReadLineAsync(CancellationToken token)
        {
            while (true)
            {
                if (_start == _end)
                {
                    int read = await stream.ReadAsync(_buffer, token).ConfigureAwait(false);
                    if (read == 0) return _line.Length > 0 ? (ContentLength() > maxBytes ? (null, true) : (Take(), false)) : (null, false);
                    _start = 0;
                    _end = read;
                }

                while (_start < _end)
                {
                    byte b = _buffer[_start++];
                    if (b == (byte)'\n') return ContentLength() > maxBytes ? (null, true) : (Take(), false);
                    if (b == 0xFF)
                    {
                        // Telnet IAC: skip the command byte and its option (good enough for the negotiation a telnet client sends).
                        _start = Math.Min(_end, _start + 2);
                        continue;
                    }

                    if (b == 0) continue;
                    _line.WriteByte(b);
                    // One pending byte beyond the limit is allowed: it may be the carriage return of a CRLF line of exactly maxBytes.
                    if (_line.Length > maxBytes + 1L) return (null, true);
                }
            }
        }

        /// <summary>The pending line's length without the single '\r' that precedes the '\n'.</summary>
        private long ContentLength() => _line.Length > 0 && _line.GetBuffer()[_line.Length - 1] == (byte)'\r' ? _line.Length - 1 : _line.Length;

        private string Take()
        {
            string text = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)ContentLength());
            _line.SetLength(0);
            return text;
        }
    }
}
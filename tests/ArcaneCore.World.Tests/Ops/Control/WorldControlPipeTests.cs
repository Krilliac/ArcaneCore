using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Control;
using ArcaneCore.World.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Control;

/// <summary>
/// The world daemon's local control pipe (docs/areas/server-console.md, "Control pipe"): off by default; on, a named pipe only the
/// daemon's account and SYSTEM may open (NETWORK denied, nothing inherited), never shared with a pipe some other process created first,
/// that runs each line exactly like a console line (Administrator, GM audit as Console) and logs it, redacted, with the client process.
/// </summary>
public sealed class WorldControlPipeTests
{
    private const string Prefix = WorldConsoleExecutor.Prefix;

    internal static string UniqueName() => "arcanecore-test-" + Guid.NewGuid().ToString("N");

    internal static WorldControlPipe NewPipe(WorldTestHost host, WorldControlPipeOptions options, ILoggerFactory loggers)
        => new(
            Options.Create(options), host.WorldServices.GetRequiredService<IServiceScopeFactory>(), host.Opcodes, host.World, host.Registry,
            Options.Create(new WorldSessionOptions()), loggers);

    /// <summary>A connected client of the control pipe that reads the reply lines.</summary>
    private sealed class Client : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private Client(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }

        public static async Task<Client> ConnectAsync(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return new Client(pipe);
        }

        public Task SendRawAsync(string text) => _writer.WriteAsync(text);

        /// <summary>Send one command and return its reply lines up to (not including) the done marker.</summary>
        public async Task<List<string>> RunAsync(string command)
        {
            await _writer.WriteLineAsync(command);
            var lines = new List<string>();
            while (true)
            {
                string? line = await ReadLineAsync();
                Assert.NotNull(line);
                if (line == WorldControlPipe.DoneMarker)
                {
                    return lines;
                }

                lines.Add(line);
            }
        }

        public async Task<string?> ReadLineAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await _reader.ReadLineAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _writer.DisposeAsync();
            }
            catch (IOException)
            {
                // The daemon already closed the connection (over-long line, idle): flushing the writer finds the pipe broken.
            }

            _reader.Dispose();
            await _pipe.DisposeAsync();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOnlyUserAndSystem(PipeSecurity security, SecurityIdentifier user)
    {
        Assert.True(security.AreAccessRulesProtected, "the descriptor must not inherit entries from its container");
        Assert.Equal(user, security.GetOwner(typeof(SecurityIdentifier)));
        PipeAccessRule[] rules = [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>()];
        Assert.Equal(3, rules.Length);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Deny && r.IdentityReference.Equals(ControlPipeSecurity.Network)
            && r.PipeAccessRights == PipeAccessRights.FullControl);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(user)
            && r.PipeAccessRights == PipeAccessRights.FullControl);
        Assert.Contains(rules, r => r.AccessControlType == AccessControlType.Allow && r.IdentityReference.Equals(ControlPipeSecurity.LocalSystem)
            && r.PipeAccessRights == (PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize));
        // Nobody else: not Everyone, not Administrators, not Authenticated Users, not anonymous.
        string sddl = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        foreach (string other in new[] { "WD", "BA", "AU", "AN", "BU", "IU" })
        {
            Assert.DoesNotContain(";;;" + other + ")", sddl, StringComparison.Ordinal);
        }
    }

    [WindowsFact]
    public void SecurityDescriptor_AllowsOnlyTheDaemonAccountAndSystem_AndDeniesTheNetwork()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The pipe is Windows-only; StartAsync refuses elsewhere (not created).
        }

        SecurityIdentifier user = ControlPipeSecurity.CurrentUser();
        AssertOnlyUserAndSystem(ControlPipeSecurity.Create(user), user);
    }

    [Theory]
    [InlineData("arcanecore-world-{pid}", "arcanecore-world-4242")]
    [InlineData("ops.{PID}_x", "ops.4242_x")]
    [InlineData(" fixed-name ", "fixed-name")]
    [InlineData("", null)]
    [InlineData("..\\other", null)]
    [InlineData("a/b", null)]
    [InlineData("name with space", null)]
    public void ResolveName_ReplacesThePid_AndRefusesAnythingButAPlainName(string template, string? expected)
    {
        string? name = ControlPipeSecurity.ResolveName(template, 4242, out string? problem);
        Assert.Equal(expected, name);
        Assert.Equal(expected is null, problem is not null);
    }

    [Fact]
    public async Task Disabled_CreatesNoPipe()
    {
        await using var host = WorldTestHost.Start();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider()));
        string name = UniqueName();
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = false, Name = name }, loggers);

        await pipe.StartAsync(CancellationToken.None);
        try
        {
            Assert.False(pipe.IsListening);
            Assert.Null(pipe.PipeName);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
            Assert.Throws<TimeoutException>(() => client.Connect(200));
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [WindowsFact]
    public async Task Enabled_ThePipeItCreatesCarriesTheRestrictedDescriptor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider()));
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = UniqueName() }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(pipe.IsListening);
            Assert.NotNull(pipe.Server);
            AssertOnlyUserAndSystem(pipe.Server!.GetAccessControl(), ControlPipeSecurity.CurrentUser());
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [WindowsFact]
    public async Task Enabled_ThePipeItCreates_RejectsRemoteClients()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider()));
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = UniqueName() }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(pipe.IsListening);
            // Read back from the pipe file system, not from what was asked for.
            Assert.True(LocalPipeServer.RejectsRemoteClients(pipe.Server!.SafePipeHandle), "the control pipe must carry PIPE_REJECT_REMOTE_CLIENTS");
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [WindowsFact]
    public void ARuntimeCreatedPipe_DoesNotRejectRemoteClients_SoTheReadBackDistinguishes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The runtime's constructors cannot pass PIPE_REJECT_REMOTE_CLIENTS; the read-back must say so (it is not always true).
        using NamedPipeServerStream plain = NamedPipeServerStreamAcl.Create(
            UniqueName(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0,
            ControlPipeSecurity.Create(ControlPipeSecurity.CurrentUser()));
        Assert.False(LocalPipeServer.RejectsRemoteClients(plain.SafePipeHandle));
    }

    [WindowsFact]
    public void LocalPipeServer_RefusesANameThatAlreadyExists_EvenOneThatAllowsMoreInstances()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        PipeSecurity security = ControlPipeSecurity.Create(ControlPipeSecurity.CurrentUser());

        // A squatter of the current account that allows any number of instances, with a descriptor that lets this account in:
        // only FILE_FLAG_FIRST_PIPE_INSTANCE stands between the daemon and becoming the squatter's second instance.
        string squatted = UniqueName();
        using (NamedPipeServerStream squatter = NamedPipeServerStreamAcl.Create(
            squatted, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security))
        {
            Assert.IsType<UnauthorizedAccessException>(CreateFails(squatted, security));
        }

        // Its own name, already held by a single-instance pipe: refused too (the instance limit answers first).
        string held = UniqueName();
        using NamedPipeServerStream first = LocalPipeServer.Create(held, security);
        Assert.IsAssignableFrom<Exception>(CreateFails(held, security));

        [SupportedOSPlatform("windows")]
        static Exception? CreateFails(string name, PipeSecurity security)
        {
            try
            {
                using NamedPipeServerStream joined = LocalPipeServer.Create(name, security);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex;
            }
        }
    }

    [WindowsFact]
    public async Task ACommandsDoneMarker_PrecedesAnyResultThatCommandPostsToALaterTick()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider()));
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = UniqueName() }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            await using Client client = await Client.ConnectAsync(pipe.PipeName!);
            // ".reload config" answers "Re-loading config..." now and posts its result to a later world tick; the result line must
            // come after the done marker, so a client that stops reading at the marker still sees the result as a later line.
            List<string> now = await client.RunAsync(".reload config");
            Assert.Contains(now, l => l.Contains("Re-loading config", StringComparison.Ordinal));
            Assert.DoesNotContain(now, l => l.StartsWith(Prefix + "config", StringComparison.Ordinal));
            string? later = await client.ReadLineAsync();
            Assert.NotNull(later);
            Assert.StartsWith(Prefix + "config", later, StringComparison.Ordinal);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [WindowsFact]
    public async Task ANameAnotherProcessCreatedFirst_IsRefused_NotJoined()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        string name = UniqueName();
        // A squatter that allows any number of instances: without FILE_FLAG_FIRST_PIPE_INSTANCE the daemon would become its second instance.
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = name }, loggers);

        await pipe.StartAsync(CancellationToken.None);
        try
        {
            Assert.False(pipe.IsListening);
            Assert.Null(pipe.PipeName);
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("control pipe off: could not create", StringComparison.Ordinal));
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [WindowsFact]
    public async Task ACommand_RunsAsTheConsole_IsLoggedWithTheClient_AndGmAuditedAsConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = UniqueName() }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            await using (Client client = await Client.ConnectAsync(pipe.PipeName!))
            {
                List<string> status = await client.RunAsync(".reload status");
                Assert.Contains(Prefix + "config: not reloaded since start", status);
                Assert.All(status, l => Assert.StartsWith(Prefix, l, StringComparison.Ordinal));

                // A password typed in a command never reaches the log line.
                await client.RunAsync("account set-password nosuchaccount hunter2secret hunter2secret");
            }

            CapturedCategoryLog[] lines = [.. logs.Entries.Where(e => e.Category == WorldControlPipe.LogCategory && e.Message.StartsWith("Control pipe command:", StringComparison.Ordinal))];
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("Control pipe command: .reload status (client pid " + Environment.ProcessId + " ", lines[0].Message, StringComparison.Ordinal);
            Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("hunter2secret", StringComparison.Ordinal));

            GmAuditEntry audited = Assert.Single(host.WorldServices.GetRequiredService<GmAuditFeature>().Tail(50), e => e.Command.StartsWith("reload status", StringComparison.Ordinal));
            Assert.Equal("Console", audited.Player);
            Assert.Equal(0, audited.AccountId);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [WindowsFact]
    public async Task ALineOverTheLimit_RunsNothing_ClosesTheConnection_AndTheNextClientIsServed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = UniqueName(), MaxLineLength = 64 }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            await using (Client first = await Client.ConnectAsync(pipe.PipeName!))
            {
                await first.SendRawAsync("reload status " + new string('x', 200) + "\n");
                Assert.Equal(Prefix + "line longer than 64 bytes; nothing was run, closing the connection", await first.ReadLineAsync());
                Assert.Null(await first.ReadLineAsync());
            }

            Assert.DoesNotContain(logs.Entries, e => e.Message.StartsWith("Control pipe command:", StringComparison.Ordinal));

            await using Client second = await Client.ConnectAsync(pipe.PipeName!);
            Assert.Contains(Prefix + "config: not reloaded since start", await second.RunAsync("reload status"));
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [WindowsFact]
    public async Task AnIdleClient_IsDisconnected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider()));
        WorldControlPipe pipe = NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = UniqueName(), IdleTimeoutSeconds = 1 }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            await using Client client = await Client.ConnectAsync(pipe.PipeName!);
            Assert.Equal(Prefix + "no command for 1 s; closing the connection", await client.ReadLineAsync());
            Assert.Null(await client.ReadLineAsync());
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ProductionRegistration_AddsTheControlPipe_OffByDefault()
    {
        var services = new ServiceCollection();
        services.AddWorldControlPipe(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<IOptions<WorldControlPipeOptions>>().Value.Enabled);
        Assert.Contains(services, d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(WorldControlPipe));
    }

    /// <summary>A fact that runs only on Windows (the pipe is Windows-only; elsewhere the daemon does not create it).</summary>
    internal sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "the control pipe is Windows-only";
            }
        }
    }
}

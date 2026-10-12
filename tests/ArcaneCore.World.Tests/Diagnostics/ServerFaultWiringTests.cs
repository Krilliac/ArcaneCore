using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Diagnostics;

/// <summary>The session catch site records into the fault ledger; the <c>.server faults</c> command and the info line read it.</summary>
public sealed class ServerFaultWiringTests
{
    private sealed class SessionProbeException : Exception;

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new SessionProbeException();
    }

    [Fact]
    public async Task ASessionError_IsCountedAtTheSessionSite()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IAccountDataStore>(new InMemoryAccountDataStore());
        services.AddSingleton<IWorldDataStore>(new InMemoryWorldDataStore());
        services.AddSingleton<ICharacterStore>(new InMemoryCharacterStore());
        services.AddSingleton<CharacterSaveQueue>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            provider.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var server = new WorldServer(new ThrowingScopeFactory(),
            Options.Create(new WorldOptions { BindAddress = IPAddress.Loopback.ToString(), Port = port }),
            Options.Create(new WorldSessionOptions()), new OpcodeTable(), world, new SessionRegistry(),
            NullLoggerFactory.Instance, NullLogger<WorldServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await WorldTestHost.WaitForAsync(
                () => FaultLedger.Shared.Snapshot().Any(s => s.Site == "session" && s.ExceptionType == typeof(SessionProbeException).FullName),
                "the session error to reach the fault ledger");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static Exception Thrown(Exception ex)
    {
        try { throw ex; } catch (Exception caught) { return caught; }
    }

    private static (List<string> Lines, FaultLedger Ledger) Rig()
    {
        var ledger = new FaultLedger();
        ledger.Record("worldtick", Thrown(new InvalidOperationException("first problem")));
        ledger.Record("worldtick", Thrown(new InvalidOperationException("first problem")));
        ledger.Record("session", Thrown(new ArgumentException("second problem")));
        return ([], ledger);
    }

    [Fact]
    public void Faults_ListsTheMostFrequentFirst_AndShowPrintsOne()
    {
        (List<string> lines, FaultLedger ledger) = Rig();
        Assert.True(ServerFaultCommands.Run("", AccountSecurity.GameMaster, ledger, DateTimeOffset.UtcNow, lines.Add));
        Assert.StartsWith("faults: 3 (2 distinct, last ", lines[0], StringComparison.Ordinal);
        Assert.Contains("x2 worldtick System.InvalidOperationException", lines[1], StringComparison.Ordinal);
        Assert.Contains("x1 session System.ArgumentException", lines[2], StringComparison.Ordinal);

        string id = ledger.Snapshot()[0].Id;
        lines.Clear();
        Assert.True(ServerFaultCommands.Run("show " + id[..6], AccountSecurity.GameMaster, ledger, DateTimeOffset.UtcNow, lines.Add));
        Assert.Contains(lines, l => l.Contains(id, StringComparison.Ordinal) && l.Contains("worldtick", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Message: first problem", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  at ", StringComparison.Ordinal));

        Assert.False(ServerFaultCommands.Run("show zz", AccountSecurity.GameMaster, ledger, DateTimeOffset.UtcNow, lines.Add));
        Assert.False(ServerFaultCommands.Run("bogus", AccountSecurity.GameMaster, ledger, DateTimeOffset.UtcNow, lines.Add));
    }

    [Fact]
    public void Reset_NeedsAdministrator()
    {
        (List<string> lines, FaultLedger ledger) = Rig();
        Assert.True(ServerFaultCommands.Run("reset", AccountSecurity.GameMaster, ledger, DateTimeOffset.UtcNow, lines.Add));
        Assert.Contains("administrator", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, ledger.Total);

        lines.Clear();
        Assert.True(ServerFaultCommands.Run("reset", AccountSecurity.Administrator, ledger, DateTimeOffset.UtcNow, lines.Add));
        Assert.Equal(0, ledger.Total);
        Assert.Contains("cleared (2 distinct, 3 total)", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void InfoLine_ReportsTotalDistinctAndAge()
    {
        (_, FaultLedger ledger) = Rig();
        Assert.Matches(@"^faults: 3 \(2 distinct, last \d+s ago\)$", ServerFaultCommands.InfoLine(ledger, DateTimeOffset.UtcNow));
        Assert.Equal("faults: 0 (0 distinct, last never)", ServerFaultCommands.InfoLine(new FaultLedger(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void TheCommandIsInTheServerGroupOfTheTable()
    {
        CommandTable table = ChatCommands.CreateTable();
        CommandLookup lookup = table.Lookup("server faults", AccountSecurity.Administrator);
        Assert.Equal(CommandLookupResult.Ok, lookup.Result);
    }
}

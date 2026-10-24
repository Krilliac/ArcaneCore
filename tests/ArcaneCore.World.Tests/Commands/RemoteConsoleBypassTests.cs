using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Commands;
using ArcaneCore.World.Tests.Bans;
using ArcaneCore.World.Tests.Playerbots.Party;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Commands;

/// <summary>
/// An executor over a test host's world built exactly as the server builds it: <see cref="WorldConsoleExecutor.CreateRemote"/> for remote
/// administration (RemoteAdminServer), <see cref="WorldConsoleExecutor.Create"/> for the true console. Lines come back without the console
/// prefix. Also builds bare <see cref="CommandContext.ForRemote"/> contexts for handlers called directly.
/// </summary>
internal sealed class BypassRig : IAsyncDisposable
{
    private readonly WorldTestHost _host;
    private readonly AsyncServiceScope _scope;
    private readonly Sink _output = new();

    public BypassRig(WorldTestHost host, AccountSecurity? remoteSecurity)
    {
        _host = host;
        _scope = host.WorldServices.CreateAsyncScope();
        Executor = remoteSecurity is { } security
            ? WorldConsoleExecutor.CreateRemote(
                _scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, _output,
                security, "RA:bypass", CancellationToken.None)
            : WorldConsoleExecutor.Create(
                _scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, _output);
    }

    public WorldConsoleExecutor Executor { get; }

    public string[] Lines => [.. _output.Snapshot().Select(l => l.StartsWith(WorldConsoleExecutor.Prefix, StringComparison.Ordinal) ? l[WorldConsoleExecutor.Prefix.Length..] : l)];

    public async Task<CommandContext?> RunAsync(string line)
    {
        _output.Clear();
        return await _host.World.InvokeAsync(() => Executor.Run(line));
    }

    /// <summary>Run <paramref name="line"/>, then wait (up to 20 s) until a reply line matches (a late reply comes from a worker thread); returns all lines.</summary>
    public async Task<string[]> RunAndWaitAsync(string line, Func<string, bool> match, string what)
    {
        await RunAsync(line);
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!Lines.Any(match))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"timed out waiting for: {what}; got: {string.Join(" | ", Lines)}");
            await Task.Delay(20);
        }

        return Lines;
    }

    /// <summary>A remote invoker (<see cref="CommandContext.ForRemote"/>) over its own console session, replying into this rig's lines.</summary>
    public CommandContext RemoteContext(AccountSecurity security)
        => CommandContext.ForRemote(
            WorldSession.CreateConsole(_scope.ServiceProvider, _host.Opcodes, _host.World, _host.Registry, new WorldSessionOptions(), NullLogger.Instance),
            _host.WorldServices.GetRequiredService<CommandTableSource>().Current, _output.WriteLine, security, "RA:direct", CancellationToken.None);

    /// <summary>The true console (<see cref="CommandContext.ForConsole"/>) over its own console session.</summary>
    public CommandContext ConsoleContext()
        => CommandContext.ForConsole(
            WorldSession.CreateConsole(_scope.ServiceProvider, _host.Opcodes, _host.World, _host.Registry, new WorldSessionOptions(), NullLogger.Instance),
            _host.WorldServices.GetRequiredService<CommandTableSource>().Current, _output.WriteLine);

    public void ClearLines() => _output.Clear();

    public ValueTask DisposeAsync() => _scope.DisposeAsync();

    private sealed class Sink : TextWriter
    {
        private readonly object _gate = new();
        private readonly List<string> _lines = [];

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_gate) _lines.Add(value ?? string.Empty);
        }

        public string[] Snapshot()
        {
            lock (_gate) return [.. _lines];
        }

        public void Clear()
        {
            lock (_gate) _lines.Clear();
        }
    }
}

/// <summary>
/// A remote administration invoker (<see cref="CommandContext.ForRemote"/>: RA, and the Atlas actors built the same way) reports
/// <see cref="CommandContext.IsConsole"/> but is not the server console, which alone outranks every account (vmangos SEC_CONSOLE). These
/// tests run through the RA executor (<see cref="WorldConsoleExecutor.CreateRemote"/>) and pin each security check that used to be
/// skipped for a remote invoker, with the true console (<see cref="WorldConsoleExecutor.Create"/>) as the control that still passes.
/// </summary>
public sealed class RemoteConsoleBypassTests
{
    private static async Task OfflineCharacterAsync(WorldTestHost host, string account, string name, AccountSecurity security)
    {
        byte[] key = await host.AddAccountAsync(account, security);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
    }

    private static Task<bool> InGuildAsync(WorldTestHost host, string name) => host.OnWorldAsync(() =>
    {
        ArcaneCore.Game.Social.SocialContext social = host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context;
        return social.Characters.FindByName(name) is { } info && social.Guilds.GetGuildOf(info.Id) is not null;
    });

    private static async Task<int> IdAsync(WorldTestHost host, string account) => (await host.Accounts.FindByUsernameAsync(account))!.Id;

    // --- GmTargets.ActOnOffline ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RemoteGm_GuildCreateOnOfflineAdministratorsCharacter_IsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await OfflineCharacterAsync(host, "BYPOFFBOSS", "Bypoffboss", AccountSecurity.Administrator);
        await using var rig = new BypassRig(host, AccountSecurity.GameMaster);

        string[] lines = await rig.RunAndWaitAsync(".guild create Bypoffboss \"Coup\"", _ => true, "the reply");

        Assert.Equal([GmStrings.SecurityTooLow], lines);
        Assert.False(await InGuildAsync(host, "Bypoffboss"));
    }

    [Fact]
    public async Task Console_GuildCreateOnOfflineAdministratorsCharacter_Succeeds()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await OfflineCharacterAsync(host, "BYPOFFBOSS", "Bypoffboss", AccountSecurity.Administrator);
        await using var rig = new BypassRig(host, remoteSecurity: null);

        string[] lines = await rig.RunAndWaitAsync(".guild create Bypoffboss \"Coup\"", _ => true, "the reply");

        Assert.Equal(["Guild Coup created."], lines);
        Assert.True(await InGuildAsync(host, "Bypoffboss"));
    }

    // --- BanCommands: ProtectHigherSecurity ---------------------------------------------------------------------------

    [Fact]
    public async Task RemoteGm_BanAccountOfAdministrator_IsRefused_WhileProtectHigherSecurity()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.AddAccountAsync("BYPBOSS", AccountSecurity.Administrator);
        await using var rig = new BypassRig(host, AccountSecurity.GameMaster);

        string[] lines = await rig.RunAndWaitAsync(".ban account bypboss 1d x", _ => true, "the reply");

        Assert.Equal([BanCommandText.TargetSecurityTooHigh], lines);
        Assert.Null(await host.Bans.GetActiveAccountBanAsync(await IdAsync(host, "BYPBOSS")));
    }

    [Fact]
    public async Task Console_BanAccountOfAdministrator_Succeeds()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await host.AddAccountAsync("BYPBOSS", AccountSecurity.Administrator);
        await using var rig = new BypassRig(host, remoteSecurity: null);

        string[] lines = await rig.RunAndWaitAsync(".ban account bypboss 1d x", _ => true, "the reply");

        Assert.Equal(["BYPBOSS is banned for 1d. Reason: x."], lines);
        Assert.NotNull(await host.Bans.GetActiveAccountBanAsync(await IdAsync(host, "BYPBOSS")));
    }

    [Fact]
    public async Task RemoteAdmin_BanAllIp_SparesAnAccountOfEqualSecurity()
    {
        var addresses = new InMemoryAccountAddressStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IAccountAddressStore>(addresses));
        await host.AddAccountAsync("BYPPEER", AccountSecurity.Administrator);
        await host.AddAccountAsync("BYPPLAYER", AccountSecurity.Player);
        int peer = await IdAsync(host, "BYPPEER");
        int player = await IdAsync(host, "BYPPLAYER");
        addresses.Seed(peer, "10.9.9.1");
        addresses.Seed(player, "10.9.9.2");
        await using var rig = new BypassRig(host, AccountSecurity.Administrator);

        string[] lines = await rig.RunAndWaitAsync(".ban allip 10.9.9. x", l => l.Contains(" on this IP)", StringComparison.Ordinal), "the summary");

        Assert.Contains(lines, l => l.StartsWith("Account 'BYPPLAYER' permanently banned", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("BYPPEER", StringComparison.Ordinal));
        Assert.NotNull(await host.Bans.GetActiveAccountBanAsync(player));
        Assert.Null(await host.Bans.GetActiveAccountBanAsync(peer));
    }

    // --- Console-only commands ----------------------------------------------------------------------------------------

    private static void AddLeaf(WorldTestHost host, string name, Func<CommandContext, string, bool> handler)
    {
        CommandAddResult added = host.WorldServices.GetRequiredService<CommandTableSource>()
            .TryAdd([new ChatCommand(name, AccountSecurity.Administrator, "Syntax: ." + name, new CommandHandler(handler))]);
        Assert.True(added.Applied, added.Error);
    }

    [Fact]
    public async Task RemoteAdmin_ConsoleOnlyLeaf_GetsConsoleOnly()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        int ran = 0;
        AddLeaf(host, "zbypassconsole", (c, a) =>
        {
            Interlocked.Increment(ref ran);
            c.Reply("ran");
            return true;
        });
        ConsoleOnlyCommands.Register("zbypassconsole");
        await using var remote = new BypassRig(host, AccountSecurity.Administrator);
        await using var console = new BypassRig(host, remoteSecurity: null);

        CommandContext? refused = await remote.RunAsync(".zbypassconsole");
        CommandContext? served = await console.RunAsync(".zbypassconsole");

        Assert.Equal("console-only", refused!.Failure);
        Assert.Equal([CommandContextConsole.ConsoleOnlyText], remote.Lines);
        Assert.Equal(1, Volatile.Read(ref ran)); // only the console's run reached the handler
        Assert.Null(served!.Failure);
        Assert.Equal(["ran"], console.Lines);
    }

    [Fact]
    public async Task RemoteAdmin_RequireTrueConsoleInHandler_GetsConsoleOnly()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        AddLeaf(host, "zbypassguard", (c, a) =>
        {
            if (!c.RequireTrueConsole()) return true;
            c.Reply("ran");
            return true;
        });
        await using var remote = new BypassRig(host, AccountSecurity.Administrator);
        await using var console = new BypassRig(host, remoteSecurity: null);

        CommandContext? refused = await remote.RunAsync(".zbypassguard");
        CommandContext? served = await console.RunAsync(".zbypassguard");

        Assert.Equal("console-only", refused!.Failure);
        Assert.Equal([CommandContextConsole.ConsoleOnlyText], remote.Lines);
        Assert.Null(served!.Failure);
        Assert.Equal(["ran"], console.Lines);
    }

    // --- Playerbots ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Playerbots_FleetSecurityBlocks_RemoteBelowTarget_Blocks()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient admin = await world.AddGmAsync("BYPFLEETADM", "Bypfleetadm");
        await using var rig = new BypassRig(world.Host, remoteSecurity: null);

        (bool remoteModerator, bool remoteAdmin, bool console) = await world.Host.OnWorldAsync(() =>
        {
            Player target = world.Host.World.FindOnlinePlayer("Bypfleetadm")!;
            return (
                PlayerbotFleetCommands.SecurityBlocks(rig.RemoteContext(AccountSecurity.Moderator), target),
                PlayerbotFleetCommands.SecurityBlocks(rig.RemoteContext(AccountSecurity.Administrator), target),
                PlayerbotFleetCommands.SecurityBlocks(rig.ConsoleContext(), target));
        });

        Assert.True(remoteModerator); // a remote invoker below the target's rank is blocked, as a player would be
        Assert.False(remoteAdmin); // equal rank passes
        Assert.False(console); // the true console outranks everyone
    }

    [Fact]
    public async Task Playerbots_CommandsHelp_RemoteModerator_GetsPlayerHelp()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using var rig = new BypassRig(host, remoteSecurity: null);
        const string PlayerLine = "Dot commands: .bot <commands|gear|summon|party <fill|add|remove|list>>.";

        string[] remote = await host.OnWorldAsync(() =>
        {
            rig.ClearLines();
            Assert.True(PlayerbotExtraCommands.CommandsHelp(rig.RemoteContext(AccountSecurity.Moderator), string.Empty));
            return rig.Lines;
        });
        string[] console = await host.OnWorldAsync(() =>
        {
            rig.ClearLines();
            Assert.True(PlayerbotExtraCommands.CommandsHelp(rig.ConsoleContext(), string.Empty));
            return rig.Lines;
        });

        Assert.Contains(PlayerLine, remote);
        Assert.DoesNotContain(console, l => l == PlayerLine); // the true console gets the GM listing
        Assert.Contains(console, l => l.StartsWith("Dot commands: .bot <commands|gear|summon|party|raid|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Playerbots_Gear_RemoteModerator_IsRefused()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Bypgear", 40);
        await using var rig = new BypassRig(world.Host, remoteSecurity: null);

        string[] remote = await world.Host.OnWorldAsync(() =>
        {
            rig.ClearLines();
            Assert.True(PlayerbotExtraCommands.Gear(rig.RemoteContext(AccountSecurity.Moderator), "Bypgear"));
            return rig.Lines;
        });
        string[] console = await world.Host.OnWorldAsync(() =>
        {
            rig.ClearLines();
            Assert.True(PlayerbotExtraCommands.Gear(rig.ConsoleContext(), "Bypgear"));
            return rig.Lines;
        });

        Assert.Equal([GmStrings.SecurityTooLow], remote);
        Assert.StartsWith("Bypgear", console[0], StringComparison.Ordinal); // the true console is served
    }

    [Fact]
    public async Task Playerbots_SummonSingle_RemoteModerator_IsRefused_AndTheBotStays()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Bypsum", 40);
        (float X, float Y, float Z) before = await world.PositionAsync("Bypsum");
        await using var rig = new BypassRig(world.Host, remoteSecurity: null);

        string[] remote = await world.Host.OnWorldAsync(() =>
        {
            rig.ClearLines();
            Assert.True(PlayerbotExtraCommands.SummonSingleOrFleet(rig.RemoteContext(AccountSecurity.Moderator), "Bypsum"));
            return rig.Lines;
        });

        Assert.Equal([GmStrings.SecurityTooLow], remote);
        Assert.Equal(before, await world.PositionAsync("Bypsum"));
    }
}

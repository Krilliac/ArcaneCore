using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>Opcode groups and command groups standing in for "a type that only exists after an edit".</summary>
public sealed class BaseOpcodeGroup : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgPing, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
}

public sealed class AddedOpcodeGroup : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgQueryTime, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
}

public sealed class AnotherAddedOpcodeGroup : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgTutorialFlag, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
}

/// <summary>Claims the same opcode as <see cref="AddedOpcodeGroup"/>.</summary>
public sealed class DuplicateOpcodeGroup : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgQueryTime, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
}

public sealed class BaseCommandGroup : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
        [new ChatCommand("hotbase", AccountSecurity.Player, "base", (_, _) => true)];
}

public sealed class AddedCommandGroup : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
        [new ChatCommand("hotadded", AccountSecurity.Player, "added", (context, _) =>
        {
            context.Reply("hotadded: ran");
            return true;
        })];
}

/// <summary>A root that is a proper prefix of an existing one ("hotbase"), which a refresh must refuse.</summary>
public sealed class ShadowingCommandGroup : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
        [new ChatCommand("hotb", AccountSecurity.Player, "shadow", (_, _) => true)];
}

public sealed class FakeUpdaterA;

public sealed class FakeUpdaterB;

/// <summary>A catalog the test edits: what a rescan finds "after the edit".</summary>
internal sealed class TestCatalog : IHotCodeCatalog
{
    public List<Type> OpcodeGroups { get; } = [typeof(BaseOpcodeGroup)];

    public List<Type> CommandGroups { get; } = [typeof(BaseCommandGroup)];

    public List<Type> LiveUpdaters { get; } = [typeof(FakeUpdaterA)];

    public List<Type> ScannedUpdaters { get; } = [typeof(FakeUpdaterA)];

    public List<int> CommitThreads { get; } = [];

    public Exception? ScanFailure { get; set; }

    public Exception? CommitFailure { get; set; }

    public IReadOnlyList<Type> OpcodeGroupTypes() => [.. OpcodeGroups];

    public IReadOnlyList<Type> CommandGroupTypes() => [.. CommandGroups];

    public IReadOnlyList<Type> CurrentMapUpdaterTypes() => [.. LiveUpdaters];

    public MapUpdaterScan ScanMapUpdaters()
    {
        if (ScanFailure is not null)
        {
            throw ScanFailure;
        }

        Type[] scanned = [.. ScannedUpdaters];
        return new MapUpdaterScan(scanned, () =>
        {
            CommitThreads.Add(Environment.CurrentManagedThreadId);
            if (CommitFailure is not null)
            {
                throw CommitFailure;
            }

            Type[] added = [.. scanned.Except(LiveUpdaters)];
            LiveUpdaters.Clear();
            LiveUpdaters.AddRange(scanned);
            return added;
        });
    }
}

/// <summary>Runs posted commands on one dedicated thread, in order (a stand-in for the world thread).</summary>
internal sealed class ThreadedWorld : IHotCodeWorld, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;

    public ThreadedWorld()
    {
        _thread = new Thread(() =>
        {
            // No iterator and no stale local: the last command (a closure over what it swapped) must not
            // stay reachable while this thread waits, or a test that proves a module is collected measures this loop.
            Action? command;
            while (_queue.TryTake(out command, Timeout.Infinite))
            {
                command();
                command = null;
            }
        })
        { IsBackground = true, Name = "test-world" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public void Post(Action command) => _queue.Add(command);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join();
    }
}

/// <summary>Keeps posted commands until the test releases them (a world thread that is busy or stopped).</summary>
internal sealed class HeldWorld : IHotCodeWorld
{
    private readonly List<Action> _held = [];

    public void Post(Action command)
    {
        lock (_held)
        {
            _held.Add(command);
        }
    }

    public void RunHeld()
    {
        Action[] commands;
        lock (_held)
        {
            commands = [.. _held];
            _held.Clear();
        }

        foreach (Action command in commands)
        {
            command();
        }
    }
}

/// <summary>
/// <see cref="HotCodeRefresh"/>: after a code edit the registries the server built at startup learn
/// about new opcode handlers, chat command roots and default map updaters; a bad candidate changes
/// nothing; the same generation is never applied twice; frozen means frozen.
/// </summary>
public sealed class RefreshCoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly ThreadedWorld _world = new();
    private readonly TestCatalog _catalog = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly HotCodeState _state = new();
    private readonly HotCodeAudit _audit;
    private readonly HotCodeRefresh _refresh;

    public RefreshCoreTests()
    {
        new BaseOpcodeGroup().Register(_opcodes);
        _commands = new CommandTableSource(new CommandTable(new BaseCommandGroup().Commands));
        _audit = new HotCodeAudit(Path.Combine(_dir, "audit.log"));
        _refresh = new HotCodeRefresh(_state, _world, _opcodes, _commands, _catalog, _audit, NullLogger.Instance, TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        _world.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task AGroupAddedAtRuntime_IsUnknownBeforeRefresh_AndReachableAfter()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        _catalog.CommandGroups.Add(typeof(AddedCommandGroup));
        OpcodeTable sameTable = _opcodes;
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.Null(_commands.Current.Resolve("hotadded", AccountSecurity.Player));
        _opcodes.TryGet(WorldOpcode.CmsgPing, out OpcodeHandler baseHandler);

        RefreshResult result = await _refresh.RunAsync(1, "test");

        Assert.Equal(RefreshStatus.Applied, result.Status);
        Assert.Equal(1, result.NewOpcodes);
        Assert.Equal(1, result.NewCommandRoots);
        Assert.True(sameTable.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.Equal("hotadded", _commands.Current.Resolve("hotadded", AccountSecurity.Player)?.Name);
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgPing, out OpcodeHandler after));
        Assert.Same(baseHandler, after); // an existing handler is kept, not replaced
        Assert.Equal(1, _state.Snapshot().Applied);
        Assert.False(_state.Snapshot().Degraded);
    }

    [Fact]
    public async Task ACommitRunsOnTheWorldThread_NotOnTheCaller()
    {
        _catalog.ScannedUpdaters.Add(typeof(FakeUpdaterB));
        RefreshResult result = await _refresh.RunAsync(1, "test");
        Assert.Equal(RefreshStatus.Applied, result.Status);
        Assert.Equal(1, result.NewMapUpdaters);
        Assert.Equal([_world.ThreadId], _catalog.CommitThreads);
        Assert.NotEqual(Environment.CurrentManagedThreadId, _world.ThreadId);
    }

    [Fact]
    public async Task ADuplicateOpcode_KeepsTheOldTables_SetsDegraded_AndCommitsNothingElse()
    {
        _catalog.OpcodeGroups.AddRange([typeof(AddedOpcodeGroup), typeof(DuplicateOpcodeGroup)]);
        _catalog.CommandGroups.Add(typeof(AddedCommandGroup)); // valid, but must not be applied alone
        _catalog.ScannedUpdaters.Add(typeof(FakeUpdaterB));
        CommandTable commandsBefore = _commands.Current;

        RefreshResult result = await _refresh.RunAsync(1, "test");

        Assert.Equal(RefreshStatus.Rejected, result.Status);
        Assert.Contains("registered twice", result.Detail);
        Assert.Equal(1, _opcodes.Count);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.Same(commandsBefore, _commands.Current);
        Assert.Empty(_catalog.CommitThreads);
        HotCodeStatus status = _state.Snapshot();
        Assert.True(status.Degraded);
        Assert.Contains("registered twice", status.DegradedReason);
        Assert.Equal(1, status.Rejected);
        Assert.Equal(0, status.AppliedGeneration);
    }

    [Fact]
    public async Task AShadowingCommandRoot_RejectsTheWholeRefresh_IncludingValidOpcodes()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        _catalog.CommandGroups.Add(typeof(ShadowingCommandGroup));

        RefreshResult result = await _refresh.RunAsync(1, "test");

        Assert.Equal(RefreshStatus.Rejected, result.Status);
        Assert.Contains("prefix", result.Detail);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.Single(_commands.Current.Roots);
    }

    [Fact]
    public async Task AnInvalidUpdaterScan_Rejects_AndALaterGoodRefreshClearsTheDegradedFlag()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        _catalog.ScanFailure = new InvalidOperationException("Foo is marked [DefaultMapUpdater] but is not a concrete IMapUpdater");

        RefreshResult bad = await _refresh.RunAsync(1, "test");
        Assert.Equal(RefreshStatus.Rejected, bad.Status);
        Assert.True(_state.Snapshot().Degraded);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));

        _catalog.ScanFailure = null;
        RefreshResult good = await _refresh.RunAsync(2, "test");
        Assert.Equal(RefreshStatus.Applied, good.Status);
        Assert.False(_state.Snapshot().Degraded);
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
    }

    [Fact]
    public async Task TheSameGenerationTwice_IsANoOp()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        Assert.Equal(RefreshStatus.Applied, (await _refresh.RunAsync(5, "test")).Status);

        _catalog.OpcodeGroups.Add(typeof(AnotherAddedOpcodeGroup)); // would be picked up by a new generation
        RefreshResult again = await _refresh.RunAsync(5, "test");
        RefreshResult older = await _refresh.RunAsync(3, "test");

        Assert.Equal(RefreshStatus.AlreadyApplied, again.Status);
        Assert.Equal(RefreshStatus.AlreadyApplied, older.Status);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgTutorialFlag, out _));
        Assert.Equal(RefreshStatus.Applied, (await _refresh.RunAsync(6, "test")).Status);
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgTutorialFlag, out _));
    }

    [Fact]
    public async Task ARefreshThatFindsNothingNew_ChangesNothing_ButRecordsTheGeneration()
    {
        CommandTable before = _commands.Current;
        RefreshResult result = await _refresh.RunAsync(1, "test");
        Assert.Equal(RefreshStatus.NothingNew, result.Status);
        Assert.Same(before, _commands.Current);
        Assert.Equal(1, _state.Snapshot().AppliedGeneration);
        Assert.Equal(0, _state.Snapshot().Applied);
    }

    [Fact]
    public async Task Frozen_CommitsNothing_UntilThawed()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        _state.SetFrozen(true);

        RefreshResult frozen = await _refresh.RunAsync(1, "test");
        Assert.Equal(RefreshStatus.Frozen, frozen.Status);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));

        _state.SetFrozen(false);
        Assert.Equal(RefreshStatus.Applied, (await _refresh.RunAsync(1, "test")).Status);
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
    }

    [Fact]
    public async Task ACommitTheWorldNeverRuns_TimesOut_AndCannotApplyLater()
    {
        var held = new HeldWorld();
        var refresh = new HotCodeRefresh(_state, held, _opcodes, _commands, _catalog, _audit, NullLogger.Instance, TimeSpan.FromMilliseconds(100));
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));

        RefreshResult result = await refresh.RunAsync(1, "test");
        Assert.Equal(RefreshStatus.Rejected, result.Status);
        Assert.Contains("did not run the commit", result.Detail);

        held.RunHeld(); // the world thread finally gets to it: the abandoned commit must not apply
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.True(_state.Snapshot().Degraded);
    }

    [Fact]
    public async Task EveryOutcome_IsAudited()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        await _refresh.RunAsync(1, "first");
        await _refresh.RunAsync(1, "again");
        _state.SetFrozen(true);
        await _refresh.RunAsync(2, "frozen");

        string[] kinds = File.ReadAllLines(Path.Combine(_dir, "audit.log")).Select(l => l.Split('\t')[1]).ToArray();
        Assert.Equal(["refresh-applied", "refresh-alreadyapplied", "refresh-frozen"], kinds);
    }

    [Fact]
    public async Task ReadersNeverMissAnExistingOpcode_WhileTheTableIsSwapped()
    {
        var table = new OpcodeTable();
        new BaseOpcodeGroup().Register(table);
        using var stop = new CancellationTokenSource();
        long misses = 0;

        Task[] readers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!table.TryGet(WorldOpcode.CmsgPing, out _))
                {
                    Interlocked.Increment(ref misses);
                }
            }
        })).ToArray();

        var fresh = new OpcodeTable();
        new AddedOpcodeGroup().Register(fresh);
        for (int i = 0; i < 3000; i++)
        {
            OpcodeTable merged = table.WithNewHandlersFrom(fresh, out IReadOnlyList<WorldOpcode> added);
            Assert.True(added.Count is 0 or 1);
            table.Replace(merged);
        }

        await stop.CancelAsync();
        await Task.WhenAll(readers);
        Assert.Equal(0, Interlocked.Read(ref misses));
        Assert.True(table.TryGet(WorldOpcode.CmsgQueryTime, out _));
    }

    [Fact]
    public void ASwappedInTable_RefusesFurtherRegistration()
    {
        var table = new OpcodeTable();
        table.Replace(table.Copy());
        Assert.Throws<InvalidOperationException>(() => new BaseOpcodeGroup().Register(table));
    }

    [Fact]
    public async Task TheMetadataHandler_ForwardsToTheActiveRefresh_AndStopsWhenDeactivated()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        HotCodeMetadataHandler.UpdateApplication([typeof(AddedOpcodeGroup)]); // nothing active yet
        await Task.Delay(50);
        Assert.Equal(0, _state.Snapshot().MetadataUpdates);

        HotCodeMetadataHandler.Activate(_refresh);
        try
        {
            HotCodeMetadataHandler.UpdateApplication([typeof(AddedOpcodeGroup)]);
            await WaitForAsync(() => _opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _), "the refresh after a metadata update");
            HotCodeStatus status = _state.Snapshot();
            Assert.Equal(1, status.MetadataUpdates);
            Assert.True(status.DivergedFromBuild);
            Assert.Equal(1, status.Generation);
        }
        finally
        {
            HotCodeMetadataHandler.Deactivate(_refresh);
        }

        HotCodeMetadataHandler.UpdateApplication(null);
        await Task.Delay(50);
        Assert.Equal(1, _state.Snapshot().MetadataUpdates);
    }

    [Fact]
    public void TheAssembly_DeclaresTheHandlerThatTheRuntimeLooksFor()
    {
        MetadataUpdateHandlerAttribute? attribute = typeof(HotCodeRefresh).Assembly
            .GetCustomAttributes<MetadataUpdateHandlerAttribute>()
            .FirstOrDefault(a => a.HandlerType.Name == "HotCodeMetadataHandler");
        Assert.NotNull(attribute);
        MethodInfo? update = attribute.HandlerType.GetMethod(
            "UpdateApplication", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Type[])]);
        Assert.NotNull(update);
        Assert.Equal(typeof(void), update.ReturnType);
    }

    [Fact]
    public void WithHotCodeOff_NoHotCodeObjectIsRegistered()
    {
        IConfiguration off = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var servicesOff = new ServiceCollection().AddWorldDaemon(off);
        Assert.DoesNotContain(servicesOff, d => d.ServiceType == typeof(HotCodeRefresh) || d.ServiceType == typeof(HotCodeState));

        IConfiguration on = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["World:HotCode:Enabled"] = "true" }).Build();
        var servicesOn = new ServiceCollection().AddWorldDaemon(on);
        Assert.Contains(servicesOn, d => d.ServiceType == typeof(HotCodeRefresh));
        Assert.Contains(servicesOn, d => d.ServiceType == typeof(HotCodeState));
    }

    [Fact]
    public async Task OnTheRealWorld_ACommandAndAnOpcodeAddedAtRuntime_WorkWithoutARestart()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("HOTREF", "Hotref");
        await player.CollectAsync();

        WorldOpcode unused = Enum.GetValues<WorldOpcode>().First(o => o.ToString().StartsWith("Cmsg", StringComparison.Ordinal) && !host.Opcodes.TryGet(o, out _));
        RealWorldProbe.Opcode = unused;
        RealWorldProbe.Hits = 0;

        var catalog = new TestCatalog();
        catalog.OpcodeGroups.Clear();
        catalog.OpcodeGroups.AddRange(WorldServiceCollectionExtensions.HandlerGroups.Select(g => g.GetType()));
        catalog.OpcodeGroups.Add(typeof(RealWorldOpcodeGroup));
        catalog.CommandGroups.Clear();
        catalog.CommandGroups.AddRange(AssemblyDiscovery.FindTypes<ICommandGroup>());
        catalog.CommandGroups.Add(typeof(AddedCommandGroup));
        catalog.LiveUpdaters.Clear();
        catalog.ScannedUpdaters.Clear();

        var refresh = new HotCodeRefresh(
            new HotCodeState(), new WorldRuntimeHotCodeWorld(host.World), host.Opcodes,
            host.WorldServices.GetRequiredService<CommandTableSource>(), catalog, new HotCodeAudit(null), NullLogger.Instance);

        await player.SendChatAsync(ChatType.Say, Language.Common, ".hotadded");
        Assert.Equal("There is no such command.", (await player.ReadChatAsync()).Text);
        await player.SendAsync(unused, []);
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref RealWorldProbe.Hits));

        RefreshResult result = await refresh.RunAsync(1, "test");
        Assert.Equal(RefreshStatus.Applied, result.Status);
        Assert.Equal(1, result.NewOpcodes);
        Assert.Equal(1, result.NewCommandRoots);

        await player.SendChatAsync(ChatType.Say, Language.Common, ".hotadded");
        Assert.Equal("hotadded: ran", (await player.ReadChatAsync()).Text);
        await player.SendAsync(unused, []);
        await WaitForAsync(() => Volatile.Read(ref RealWorldProbe.Hits) == 1, "the new opcode handler to run");
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}

internal static class RealWorldProbe
{
    public static WorldOpcode Opcode;
    public static int Hits;
}

public sealed class RealWorldOpcodeGroup : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnWorld(RealWorldProbe.Opcode, (_, _, _) => Interlocked.Increment(ref RealWorldProbe.Hits));
}

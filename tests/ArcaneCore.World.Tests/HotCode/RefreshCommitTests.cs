using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>
/// The commit half of <see cref="HotCodeRefresh"/>: it runs later than the scan, on the world
/// thread, so it must not undo what happened in between and must undo itself completely on failure.
/// </summary>
public sealed class RefreshCommitTests
{
    private readonly TestCatalog _catalog = new();
    private readonly OpcodeTable _opcodes = new();
    private readonly CommandTableSource _commands;
    private readonly HeldWorld _world = new();
    private readonly HotCodeRefresh _refresh;

    public RefreshCommitTests()
    {
        new BaseOpcodeGroup().Register(_opcodes);
        _commands = new CommandTableSource(new CommandTable(new BaseCommandGroup().Commands));
        _refresh = new HotCodeRefresh(
            new HotCodeState(), _world, _opcodes, _commands, _catalog, new HotCodeAudit(null), NullLogger.Instance, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ARefreshCommit_DoesNotDropAnOpcodeAddedToTheLiveTableAfterItsScan()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));

        // The scan has run and the commit is queued (a posted command is held until the test runs it).
        Task<RefreshResult> refresh = _refresh.RunAsync(1, "test");

        // Meanwhile something else (a module load) puts a handler into the live table.
        var other = new OpcodeTable();
        other.OnSession(WorldOpcode.CmsgTutorialFlag, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
        _opcodes.Replace(_opcodes.WithNewHandlersFrom(other, out _));

        _world.RunHeld();
        RefreshResult result = await refresh;

        Assert.Equal(RefreshStatus.Applied, result.Status);
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _), "the refreshed handler is in force");
        Assert.True(_opcodes.TryGet(WorldOpcode.CmsgTutorialFlag, out _), "the handler added meanwhile must survive the commit");
    }

    [Fact]
    public async Task AFailedUpdaterCommit_LeavesNoCommandRootAndNoOpcodeBehind()
    {
        _catalog.OpcodeGroups.Add(typeof(AddedOpcodeGroup));
        _catalog.CommandGroups.Add(typeof(AddedCommandGroup));
        _catalog.ScannedUpdaters.Add(typeof(FakeUpdaterB));
        _catalog.CommitFailure = new InvalidOperationException("updater commit failed");

        Task<RefreshResult> refresh = _refresh.RunAsync(1, "test");
        _world.RunHeld();
        RefreshResult result = await refresh;

        Assert.Equal(RefreshStatus.Rejected, result.Status);
        Assert.Contains("rolled back", result.Detail);
        Assert.False(_opcodes.TryGet(WorldOpcode.CmsgQueryTime, out _));
        Assert.Null(_commands.Current.Resolve("hotadded", ArcaneCore.Kernel.Accounts.AccountSecurity.Player));
        Assert.Single(_commands.Current.Roots);
    }
}

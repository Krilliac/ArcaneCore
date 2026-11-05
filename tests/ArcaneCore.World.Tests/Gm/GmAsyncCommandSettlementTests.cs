using System.Collections.Concurrent;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Rename;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tests.Npc;
using ArcaneCore.World.Tests.Talents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>
/// A GM command that finishes its work off the world thread holds itself open (<see cref="CommandContext.Defer"/>): RA and the control
/// pipe report it done only once <see cref="CommandContext.WhenSettled"/> completes, so the reply (or the secret) must exist by then and a
/// failure must be visible as <see cref="CommandContext.Failure"/> (RemoteAdminServer.RunAsync, WorldControlPipe).
/// </summary>
public sealed class GmAsyncCommandSettlementTests
{
    /// <summary>
    /// Runs the command as the console and reports whether it settled before a reply existed. That is judged where the order is decided, not by when the handler
    /// returned: each reply line notes whether the command had already settled when it was written (RA would then have sent <c>#done ok</c> first), and at handler
    /// return a settled command counts only if no reply had been written yet. A worker that replies and releases its hold before the world thread gets back from the
    /// handler (a fast store on a loaded box) is the correct order, and must not count as early.
    /// </summary>
    private static async Task<(CommandContext Context, bool SettledBeforeItsReply, ConcurrentQueue<string> Replies)> RunAsync(
        WorldTestHost host, WorldSession session, string path, string args)
    {
        var replies = new ConcurrentQueue<string>();
        CommandContext? running = null;
        int settledBeforeAReply = 0;
        void Output(string line)
        {
            if (Volatile.Read(ref running) is { WhenSettled.IsCompleted: true }) Interlocked.Exchange(ref settledBeforeAReply, 1);
            replies.Enqueue(line);
        }

        (CommandContext context, bool settledWithoutReply) = await host.OnWorldAsync(() =>
        {
            CommandTable table = ChatCommands.CreateTable();
            CommandContext ctx = CommandContext.ForConsole(session, table, Output);
            Volatile.Write(ref running, ctx);
            ChatCommand command = table.Resolve(path, AccountSecurity.Administrator)!;
            ctx.Bind(command, path); // what CommandTable.Execute does
            Assert.True(command.Handler!(ctx, args));
            ctx.Complete();
            // Settled first, replies second: a hold is released only after its reply was enqueued, so a correct command that settled already shows its reply here.
            bool settled = ctx.WhenSettled.IsCompleted;
            return (ctx, settled && replies.IsEmpty);
        });
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));
        return (context, settledWithoutReply || Volatile.Read(ref settledBeforeAReply) != 0, replies);
    }

    [Fact]
    public async Task AnAccountFactorCommand_StaysOpenUntilItsReplyWasSent()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ALSADMIN", "Alsadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "account totp set", "NOSUCHACCT");

        Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
        Assert.Equal("Account does not exist.", Assert.Single(replies));
        Assert.Equal("no-such-account", context.Failure); // an unknown account is a failure for RA (#done error), not a success
    }

    [Fact]
    public async Task APlayerDumpLoad_StaysOpenUntilItsReplyWasPosted_AndReportsTheFailure()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("PDSADMIN", "Pdsadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "pdump load", "dump.txt nosuchacct Clone");

        Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
        Assert.Equal("Account NOSUCHACCT does not exist.", Assert.Single(replies));
        Assert.Equal("no-such-account", context.Failure);
    }

    [Theory]
    [InlineData("dump.txt NoSuchChar", "player-not-found")]
    [InlineData("sub/dump.txt Pdwadmin", "file-open-failed")]
    public async Task APlayerDumpWrite_ThatCannotStart_ReportsTheFailure(string args, string failure)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("PDWADMIN", "Pdwadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "pdump write", args);

        Assert.False(early);
        Assert.Single(replies);
        Assert.Equal(failure, context.Failure); // RA must report #done error, not #done ok
    }

    [Fact]
    public async Task APlayerDumpWrite_WhoseFileCannotBeOpened_ReportsTheFailureFromTheWorker()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-pdump-settle-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Path.Combine(dir, "dump.txt")); // the dump path is an existing directory, so the write cannot open it
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        try
        {
            await using (var db = new CharacterDbContext(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options))
            {
                await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
                db.Characters.Add(new CharacterRecord { AccountId = 77, Name = "Dumpme", Level = 30, Race = 1, Class = 1 });
                await db.SaveChangesAsync();
            }

            await using var host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddDbContext<CharacterDbContext>(o => o.UseSqlite(connection));
                services.AddSingleton(new PlayerDumpOptions { Directory = dir });
            });
            await using WorldTestClient admin = await host.EnterWorldAsync("PDOADMIN", "Pdoadmin", AccountSecurity.Administrator);
            await admin.CollectAsync();
            WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());
            int id;
            await using (var db = new CharacterDbContext(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite(connection).Options))
            {
                id = await db.Characters.Where(c => c.Name == "Dumpme").Select(c => c.Id).SingleAsync();
            }

            host.WorldServices.GetRequiredService<CharacterDirectory>().Add(new CharacterIdentity(id, 77, "Dumpme", 1, 0, 1, 30));

            (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "pdump write", "dump.txt Dumpme");

            Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
            Assert.Equal("Failed to open file: dump.txt", Assert.Single(replies));
            Assert.Equal("file-open-failed", context.Failure);
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AnArcaneBanCheck_OfAnUnknownAccount_StaysOpenUntilItsReplyWasSent_AndReportsTheFailure()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ALSARC", "Alsarc", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "arcane bancheck", "account NOSUCHACCT");

        Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
        Assert.Equal("Account NOSUCHACCT does not exist.", Assert.Single(replies));
        Assert.Equal("no-such-account", context.Failure);
    }

    [Fact]
    public async Task ActOnOffline_OfAnUnknownAccount_StaysOpenUntilItsRefusalWasPosted_ReportsTheFailure_AndNeverActs()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ALSOFF", "Alsoff", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());
        var replies = new ConcurrentQueue<string>();
        int acted = 0;

        CommandContext context = await host.OnWorldAsync(() =>
        {
            // A remote game master is not the console, so the owner's security is looked up off the world thread (account 987654 does not exist).
            CommandContext ctx = CommandContext.ForRemote(session, ChatCommands.CreateTable(), replies.Enqueue, AccountSecurity.GameMaster, "Gm", CancellationToken.None);
            GmTargets.ActOnOffline(ctx, 987654, () => Interlocked.Exchange(ref acted, 1));
            ctx.Complete();
            return ctx;
        });
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(GmStrings.PlayerNotFound, Assert.Single(replies));
        Assert.Equal("player-not-found", context.Failure);
        Assert.Equal(0, Volatile.Read(ref acted));
    }

    [Fact]
    public async Task APetRename_OfAnUnknownPet_StaysOpenUntilItsReplyWasPosted_AndReportsTheFailure()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("ALSPET", "Alspet", AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "pet rename", "4000000000 Rex");

        Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
        Assert.Single(replies); // "Pet #n not found", or "pet store unavailable" when the host has no pet store
        Assert.True(context.Failure is "no-such-pet" or "failed", $"unexpected failure '{context.Failure}'");
    }

    /// <summary>Runs the command as the console and returns without waiting for it to settle, so a test can hold the asynchronous work open.</summary>
    private static Task<(CommandContext Context, ConcurrentQueue<string> Replies)> BeginAsync(WorldTestHost host, WorldSession session, string path, string args)
    {
        var replies = new ConcurrentQueue<string>();
        return host.OnWorldAsync(() =>
        {
            CommandTable table = ChatCommands.CreateTable();
            CommandContext ctx = CommandContext.ForConsole(session, table, replies.Enqueue);
            ChatCommand command = table.Resolve(path, AccountSecurity.Administrator)!;
            ctx.Bind(command, path);
            Assert.True(command.Handler!(ctx, args));
            ctx.Complete();
            return (ctx, replies);
        });
    }

    private static async Task<(WorldTestClient Client, WorldSession Session)> EnterAdminAsync(WorldTestHost host, string username, string character)
    {
        WorldTestClient admin = await host.EnterWorldAsync(username, character, AccountSecurity.Administrator);
        await admin.CollectAsync();
        WorldSession session = await host.OnWorldAsync(() => host.World.OnlinePlayers.Select(p => p.Session).OfType<WorldSession>().First());
        return (admin, session);
    }

    /// <summary>An <see cref="ICharacterRenameStore"/> whose flag write waits for the test, then returns <see cref="Result"/> or throws.</summary>
    private sealed class ControlledRenameStore : ICharacterRenameStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Result { get; set; } = true;

        public bool Throw { get; set; }

        public async Task<bool> SetFlagAsync(int characterId, uint flag, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Gate.Task.ConfigureAwait(false);
            if (Throw)
            {
                throw new InvalidOperationException("the characters database is unavailable");
            }

            return Result;
        }

        public Task<IReadOnlyDictionary<int, uint>> GetFlagsAsync(int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<int, uint>>(new Dictionary<int, uint>());

        public Task<CharacterRenameResult> RenameAsync(int characterId, int accountId, string newName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>An <see cref="ITalentResetFlagStore"/> whose flag-all write waits for the test, then returns or throws.</summary>
    private sealed class ControlledTalentFlagStore : ITalentResetFlagStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Throw { get; set; }

        public async Task<int> FlagAllAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Gate.Task.ConfigureAwait(false);
            if (Throw)
            {
                throw new InvalidOperationException("the characters database is unavailable");
            }

            return 1;
        }

        public Task<bool> FlagAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsFlaggedAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task ClearAsync(int characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static WorldTestHost StartWithRenameStore(ControlledRenameStore store)
        => WorldTestHost.Start(configureServices: services => services.AddSingleton<ICharacterRenameStore>(store));

    private static WorldTestHost StartWithTalentStore(ControlledTalentFlagStore store)
    {
        TalentTestServices.Current.Value = new TalentWorldFixture();
        QuestInteractionTestServices.Current.Value = new QuestInteractionFixture();
        try
        {
            // Registered after the fixture's own in-memory flag store, so this one is the store the command resolves.
            return WorldTestHost.Start(configureServices: services => services.AddSingleton<ITalentResetFlagStore>(store));
        }
        finally
        {
            TalentTestServices.Current.Value = null;
            QuestInteractionTestServices.Current.Value = null;
        }
    }

    [Fact]
    public async Task ACharacterRename_OfAnOnlinePlayer_StaysOpenUntilItsStoreWorkRan()
    {
        var store = new ControlledRenameStore();
        await using WorldTestHost host = StartWithRenameStore(store);
        (WorldTestClient admin, WorldSession session) = await EnterAdminAsync(host, "ALSREN", "Alsren");
        await using WorldTestClient adminClient = admin;

        (CommandContext context, ConcurrentQueue<string> replies) = await BeginAsync(host, session, "character rename", "Alsren");
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(context.WhenSettled.IsCompleted, "the command settled while its store write was still running: #done ok would precede the outcome");
        store.Gate.SetResult();
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.StartsWith("Forced rename for player", Assert.Single(replies));
        Assert.Null(context.Failure);
    }

    [Fact]
    public async Task ACharacterRename_WhoseFlagWriteFindsNoCharacter_IsAFailure()
    {
        var store = new ControlledRenameStore { Result = false };
        await using WorldTestHost host = StartWithRenameStore(store);
        (WorldTestClient admin, WorldSession session) = await EnterAdminAsync(host, "ALSRENF", "Alsrenf");
        await using WorldTestClient adminClient = admin;

        (CommandContext context, ConcurrentQueue<string> replies) = await BeginAsync(host, session, "character rename", "Alsrenf");
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(context.WhenSettled.IsCompleted);
        store.Gate.SetResult();
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("player-not-found", context.Failure);
        Assert.Equal(GmStrings.PlayerNotFound, replies.Last());
    }

    [Fact]
    public async Task ACharacterRename_WhoseStoreThrows_IsAFailedCommand()
    {
        var store = new ControlledRenameStore { Throw = true };
        await using WorldTestHost host = StartWithRenameStore(store);
        (WorldTestClient admin, WorldSession session) = await EnterAdminAsync(host, "ALSRENT", "Alsrent");
        await using WorldTestClient adminClient = admin;

        (CommandContext context, ConcurrentQueue<string> replies) = await BeginAsync(host, session, "character rename", "Alsrent");
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(context.WhenSettled.IsCompleted);
        store.Gate.SetResult();
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("failed", context.Failure);
        Assert.Contains(replies, r => r.StartsWith("The character database is unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACharacterRename_OfAnOfflineCharacterWhoseAccountIsGone_StaysOpenUntilItsReplyWasPosted_AndReportsTheFailure()
    {
        var store = new ControlledRenameStore();
        await using WorldTestHost host = StartWithRenameStore(store);
        (WorldTestClient admin, WorldSession session) = await EnterAdminAsync(host, "ALSRENO", "Alsreno");
        await using WorldTestClient adminClient = admin;
        host.Directory.Add(new CharacterIdentity(987001, 987654, "Ghostly", 1, 0, 1)); // its account does not exist

        (CommandContext context, bool early, ConcurrentQueue<string> replies) = await RunAsync(host, session, "character rename", "Ghostly");

        Assert.False(early, "the command settled before its asynchronous work replied: #done ok would precede the reply");
        Assert.Equal(GmStrings.PlayerNotFound, Assert.Single(replies));
        Assert.Equal("player-not-found", context.Failure);
        Assert.False(store.Entered.Task.IsCompleted, "the flag was written for a character whose account does not exist");
    }

    [Fact]
    public async Task AResetAllTalents_StaysOpenUntilItsStoreWorkRan()
    {
        var store = new ControlledTalentFlagStore();
        await using WorldTestHost host = StartWithTalentStore(store);
        Assert.NotNull(host.WorldServices.GetRequiredService<TalentFeature>().Service); // the feature is live, so the command reaches the store
        (WorldTestClient admin, WorldSession session) = await EnterAdminAsync(host, "ALSRES", "Alsres");
        await using WorldTestClient adminClient = admin;

        (CommandContext context, ConcurrentQueue<string> replies) = await BeginAsync(host, session, "reset all", "talents");
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(context.WhenSettled.IsCompleted, "the command settled while its store write was still running: #done ok would precede the outcome");
        store.Gate.SetResult();
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(context.Failure);
        Assert.DoesNotContain(ResetCommands.StoreFailed, replies);
    }

    [Fact]
    public async Task AResetAllTalents_WhoseStoreThrows_IsAFailureWithItsReply()
    {
        var store = new ControlledTalentFlagStore { Throw = true };
        await using WorldTestHost host = StartWithTalentStore(store);
        Assert.NotNull(host.WorldServices.GetRequiredService<TalentFeature>().Service);
        (WorldTestClient admin, WorldSession session) = await EnterAdminAsync(host, "ALSRESF", "Alsresf");
        await using WorldTestClient adminClient = admin;

        (CommandContext context, ConcurrentQueue<string> replies) = await BeginAsync(host, session, "reset all", "talents");
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(context.WhenSettled.IsCompleted);
        store.Gate.SetResult();
        await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("failed", context.Failure);
        Assert.Contains(ResetCommands.StoreFailed, replies);
    }
}

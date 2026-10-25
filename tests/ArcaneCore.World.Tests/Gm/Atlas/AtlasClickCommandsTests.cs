using System.Globalization;
using System.Text.Json;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Atlas;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using ArcaneCore.World.Tests.Docs;
using ArcaneCore.World.Ops.ServerConsole;
using ArcaneCore.World.Tests.Ops.MapFeed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static ArcaneCore.World.Tests.Ops.Control.WorldControlPipeTests;

namespace ArcaneCore.World.Tests.Gm.Atlas;

/// <summary>
/// The click-context seam and the <c>.atlas</c> leaves (design 1.2 step 4, 1.4), without the pipe: a remote invoker
/// (<see cref="WorldConsoleExecutor.CreateRemote"/>, exactly how the channel builds its per-request executor) runs a line with a
/// <see cref="GmActionContext"/> attached through <c>Run(line, prepare)</c>. The headline: a command that reads the selected unit works
/// from a selection that is only a guid.
/// </summary>
public sealed class AtlasClickCommandsTests
{
    private static async Task<FeedWorld> StartAsync(int extraCreatures = 0, params string[] players)
    {
        FeedWorld world = await FeedWorld.StartAsync(new FeedWorldSetup { Content = true, Terrain = true, ExtraCreatures = extraCreatures });
        foreach (string player in players)
        {
            await world.AddPlayerAsync(player);
        }

        return world;
    }

    private sealed record Ran(CommandContext? Context, string[] Lines)
    {
        public string? Failure => Context?.Failure;
    }

    /// <summary>Run <paramref name="line"/> as a remote invoker of <paramref name="security"/> on the world thread and wait for it to settle.</summary>
    private static async Task<Ran> RunAsync(FeedWorld world, string line, AccountSecurity security, string invoker = "atlas-local:TEST#1", Action<CommandContext>? prepare = null)
    {
        WorldTestHost host = world.Host;
        var output = new StringWriter(CultureInfo.InvariantCulture);
        TextWriter sink = TextWriter.Synchronized(output);
        await using AsyncServiceScope scope = host.WorldServices.CreateAsyncScope();
        WorldConsoleExecutor executor = WorldConsoleExecutor.CreateRemote(
            scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), NullLogger.Instance, sink, security, invoker, CancellationToken.None);

        CommandContext? context = await host.OnWorldAsync(() => prepare is null ? executor.Run(line) : executor.Run(line, prepare));
        if (context is not null)
        {
            await context.WhenSettled.WaitAsync(TimeSpan.FromSeconds(10));
        }

        return new Ran(context, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static Action<CommandContext> Select(ulong guid, string actor = "atlas-local:TEST#1", int? accountId = null, GmClickPoint? point = null)
        => context => GmActionContext.Attach(context, new GmActionContext { ActorName = actor, ActorAccountId = accountId, Selection = [guid], Primary = guid, Point = point });

    private static Creature WolfOf(FeedWorld world, ulong guid)
        => world.Host.World.FindMap(0, 0)!.FindUpdater<CreatureMapSystem>()!.Creatures.Single(c => c.Guid.Value == guid);

    private static Task<T> OnWorld<T>(FeedWorld world, Func<T> read) => world.Host.OnWorldAsync(read);

    private static Task<int> CreatureCount(FeedWorld world) => OnWorld(world, () => world.Host.World.FindMap(0, 0)!.FindUpdater<CreatureMapSystem>()!.Creatures.Count);

    // ---- the reachability of the new root (I14) ------------------------------------------------------------------------

    [Fact]
    public void TheAtlasRoot_ShadowsNothing_AndEveryOtherRootStillResolvesToItself()
    {
        CommandTable table = ChatCommands.CreateTable();
        string[] roots = [.. table.Roots.Select(r => r.Name)];
        Assert.Contains("atlas", roots, StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots.Where(r => !r.Equals("atlas", StringComparison.OrdinalIgnoreCase)))
        {
            // No prefix relation with the new root in either direction (.account .additem .ai .alter .angle .announce .anticheat .arcane .aura .auras ...).
            Assert.False("atlas".StartsWith(root, StringComparison.OrdinalIgnoreCase), $"'{root}' is a prefix of 'atlas': '.{root}' could resolve to it");
            Assert.False(root.StartsWith("atlas", StringComparison.OrdinalIgnoreCase), $"'{root}' extends 'atlas'");

            CommandLookup found = table.Lookup(root, AccountSecurity.Administrator);
            Assert.True(found.Path.Split(' ')[0].Equals(root, StringComparison.OrdinalIgnoreCase), $"'.{root}' now resolves to '{found.Path}'");
        }

        CommandLookup atlas = table.Lookup("atlas", AccountSecurity.Administrator);
        Assert.Equal("atlas", atlas.Path);
    }

    [Fact]
    public void TheAtlasLeaves_AreTheDocumentedOnes_AtGameMaster_RetailLevelThree()
    {
        CommandTable table = ChatCommands.CreateTable();

        foreach ((string line, string path) in new[]
        {
            ("atlas place creature 299 0 1 2 3 0", "atlas place creature"),
            ("atlas place gobject 2843 0 1 2 3 0", "atlas place gobject"),
            ("atlas tele 0x1 0 1 2 3", "atlas tele"),
            ("atlas moveto 0x1 1 2 3", "atlas moveto"),
            ("atlas debug 0x1", "atlas debug"),
            ("atlas spawns 0 1 2 50", "atlas spawns"),
        })
        {
            CommandLookup moderator = table.Lookup(line, AccountSecurity.Moderator);
            Assert.Equal(CommandLookupResult.Ok, moderator.Result);
            Assert.Equal(path, moderator.Path);
            Assert.False(moderator.Available, $"{path} must not be available to a Moderator");
            Assert.True(table.Lookup(line, AccountSecurity.GameMaster).Available, $"{path} must be available to a GameMaster");
            Assert.Equal(AccountSecurity.GameMaster, moderator.Command!.Security);
            Assert.Equal((byte)3, moderator.Command.RetailLevel);
        }

        Assert.Equal(CommandLookupResult.UnknownSubcommand, table.Lookup("atlas nosuchleaf", AccountSecurity.Administrator).Result);
    }

    [Fact]
    public void TheGeneratedCommandReference_ListsTheAtlasLeaves()
    {
        IReadOnlyList<CommandNode> nodes = CommandReferenceRenderer.Nodes(reloadEnabled: false);

        foreach (string path in new[] { "atlas place creature", "atlas place gobject", "atlas tele", "atlas moveto", "atlas debug", "atlas spawns" })
        {
            Assert.Contains(nodes, n => n.Path == path && n.HasHandler && !string.IsNullOrWhiteSpace(n.Help));
        }
    }

    // ---- the action context -----------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task TheContext_IsReadOnlyByTheCommandContextItWasAttachedTo_NeverByTheNextRun()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync();
        ActionSpies.Install(world.Host);

        Ran first = await RunAsync(world, "zctx", AccountSecurity.Administrator, prepare: c => GmActionContext.Attach(c, new GmActionContext { ActorName = "ALPHA" }));
        Ran second = await RunAsync(world, "zctx", AccountSecurity.Administrator, prepare: c => GmActionContext.Attach(c, new GmActionContext { ActorName = "BRAVO" }));
        Ran none = await RunAsync(world, "zctx", AccountSecurity.Administrator);

        Assert.Equal(["ctx:ALPHA"], first.Lines);
        Assert.Equal(["ctx:BRAVO"], second.Lines);
        Assert.Equal(["ctx:none"], none.Lines);
    }

    [WindowsFact]
    public async Task RunWithPrepare_CallsPrepareOnceWithTheBuiltContext_BeforeTheCommandExecutes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync();
        ActionSpies spies = ActionSpies.Install(world.Host);
        int prepared = 0;
        int ranBeforePrepare = -1;

        Ran run = await RunAsync(world, "zplayer", AccountSecurity.Administrator, "atlas-local:PREP#9", c =>
        {
            prepared++;
            ranBeforePrepare = spies.Count("zplayer");
            Assert.True(c.IsRemote);
            Assert.Equal("atlas-local:PREP#9", c.InvokerName);
        });

        Assert.Equal(1, prepared);
        Assert.Equal(0, ranBeforePrepare);
        Assert.Equal(1, spies.Count("zplayer"));
        Assert.Null(run.Failure);
    }

    [WindowsFact]
    public async Task RunWithPrepare_OnAnEmptyLine_ReturnsNull_AndNeverCallsPrepare()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync();
        bool called = false;

        Ran run = await RunAsync(world, "   ", AccountSecurity.Administrator, prepare: _ => called = true);

        Assert.Null(run.Context);
        Assert.False(called);
    }

    // ---- selection through GmSelectedUnit (the headline RED test) ----------------------------------------------------------

    [WindowsFact]
    public async Task ARemoteInvoker_WithoutAnActionContext_StillFailsClosed_NeedsPlayer_AndTheCreatureLives()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");

        Ran run = await RunAsync(world, "die", AccountSecurity.Administrator);

        Assert.Equal("needs-player", run.Failure);
        Assert.True(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).IsAlive));
    }

    [WindowsFact]
    public async Task Die_WithOnlyAGuidSelection_KillsThatCreature_ThroughTheGmSelectedUnitHook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");
        Assert.True(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).IsAlive));

        Ran run = await RunAsync(world, "die", AccountSecurity.GameMaster, prepare: Select(FeedWorld.WolfGuid));

        Assert.Null(run.Failure);
        Assert.False(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).IsAlive), "the selected wolf is dead");
    }

    [WindowsFact]
    public async Task CreatureKill_WithOnlyAGuidSelection_KillsThatCreature()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The design (section 4) names ".creature kill" as the headline selection-only case and says it reaches the creature through the
        // GmSelectedUnit hook. On origin/main fd2ec70b6 it does not: CreatureCommands.Selected (CreatureCommands.cs:107-116) and System
        // (:102-105) read context.Player.Selection and context.Player.Map directly, so the lane also needs those two reads to honour the
        // action context (or to go through GmSelectedUnit). This test holds the lane to the design's acceptance.
        await using FeedWorld world = await StartAsync(players: "Feedalice");

        Ran run = await RunAsync(world, "creature kill", AccountSecurity.GameMaster, prepare: Select(FeedWorld.WolfGuid));

        Assert.NotEqual("needs-player", run.Failure);
        Assert.Null(run.Failure);
        Assert.Equal(CreatureDeathState.Corpse, await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).DeathState));
    }

    [WindowsFact]
    public async Task ASelectionThatIsAnOnlinePlayersGuid_ResolvesToThatPlayer_NotTheInvoker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");
        ulong alice = (await world.Host.PlayerAsync("Feedalice")).Guid.Value;

        Ran run = await RunAsync(world, "die", AccountSecurity.Administrator, prepare: Select(alice));

        Assert.Null(run.Failure);
        Assert.False(await world.Host.PlayerStateAsync("Feedalice", p => p.IsAlive));
        Assert.True(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).IsAlive), "only the selected unit died");
    }

    [WindowsFact]
    public async Task ASelectionThatIsGone_RepliesTheSelectFirstText_AndKillsNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");

        Ran run = await RunAsync(world, "die", AccountSecurity.GameMaster, prepare: Select(0xF130000123000000UL));

        Assert.Contains(GmStrings.SelectCharOrCreature, run.Lines);
        Assert.True(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).IsAlive));
    }

    [WindowsFact]
    public async Task AnActionContextWithNoSelection_IsStillNeedsPlayer_ForACommandThatNeedsOne()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");

        Ran run = await RunAsync(world, "die", AccountSecurity.GameMaster, prepare: c => GmActionContext.Attach(c, new GmActionContext { ActorName = "atlas-local:TEST#1" }));

        Assert.Equal("needs-player", run.Failure);
        Assert.True(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).IsAlive));
    }

    // ---- .atlas place --------------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task PlaceCreature_AddsOneTemporaryCreatureAtThePoint_FromExplicitNumbers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");
        float x = FeedWorld.StartX + 4f;
        float y = FeedWorld.StartY + 1.5f;
        int before = await CreatureCount(world);

        Ran run = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas place creature {FeedWorld.WolfEntry} 0 {x} {y} {FeedWorld.StartZ} 1.5"), AccountSecurity.GameMaster);

        Assert.Null(run.Failure);
        Creature[] all = await OnWorld<Creature[]>(world, () => [.. world.Host.World.FindMap(0, 0)!.FindUpdater<CreatureMapSystem>()!.Creatures]);
        Assert.Equal(before + 1, all.Length);
        Creature placed = Assert.Single(all, c => c.Guid.Value != FeedWorld.WolfGuid);
        Assert.Equal(FeedWorld.WolfEntry, placed.Template.Entry);
        Assert.InRange(placed.X, x - 0.01f, x + 0.01f);
        Assert.InRange(placed.Y, y - 0.01f, y + 0.01f);
    }

    [WindowsFact]
    public async Task PlaceGobject_AddsOneGameObjectAtThePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");
        int before = await OnWorld(world, () => world.Host.World.FindMap(0, 0)!.FindUpdater<GameObjectMapSystem>()!.GameObjects.Count);

        Ran run = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas place gobject {FeedWorld.ChestEntry} 0 {FeedWorld.StartX + 2f} {FeedWorld.StartY} {FeedWorld.StartZ} 0"), AccountSecurity.GameMaster);

        Assert.Null(run.Failure);
        Assert.Equal(before + 1, await OnWorld(world, () => world.Host.World.FindMap(0, 0)!.FindUpdater<GameObjectMapSystem>()!.GameObjects.Count));
    }

    [Theory]
    [InlineData("atlas place creature 299 1 -2000 -2000 50 0", "map-not-loaded")]
    [InlineData("atlas place creature 299 0 1 2", "syntax")]
    [InlineData("atlas place creature abc 0 1 2 3", "syntax")]
    [InlineData("atlas place creature 299 0 NaN 2 3", "syntax")]
    [InlineData("atlas place creature 299 0 1 2 3 0 extra", "syntax")]
    public async Task PlaceCreature_RefusesWhatItCannotPlace_WithAStableFailure_AndPlacesNothing(string line, string failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");
        int before = await CreatureCount(world);

        Ran run = await RunAsync(world, line, AccountSecurity.GameMaster);

        Assert.Equal(failure, run.Failure);
        Assert.Equal(before, await CreatureCount(world));
    }

    [WindowsFact]
    public async Task PlaceCreature_OfAnEntryThatDoesNotExist_FailsAndPlacesNothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");
        int before = await CreatureCount(world);

        Ran run = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas place creature 987654 0 {FeedWorld.StartX} {FeedWorld.StartY} {FeedWorld.StartZ}"), AccountSecurity.GameMaster);

        Assert.NotNull(run.Failure);
        Assert.NotEqual("unknown-command", run.Failure); // the root exists; it is the entry that is unknown
        Assert.NotEqual("unknown-subcommand", run.Failure);
        Assert.NotEqual("security", run.Failure);
        Assert.DoesNotContain(GmStrings.NoSuchCommand, run.Lines);
        Assert.Equal(before, await CreatureCount(world));
    }

    [WindowsFact]
    public async Task PlaceCreature_AsAModerator_IsASecurityFailure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");

        Ran run = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas place creature 299 0 {FeedWorld.StartX} {FeedWorld.StartY} {FeedWorld.StartZ}"), AccountSecurity.Moderator);

        Assert.Equal("security", run.Failure);
    }

    // ---- .atlas tele: "self" is an account id, never a name -----------------------------------------------------------------

    [Theory]
    [InlineData("Feedalice", "FEEDALICE", true, null)]       // the actor's account is the target character's account: self
    [InlineData("Feedbob", "FEEDALICE", false, "security")]  // another player's character needs Administrator
    [InlineData("Feedalice", null, false, "security")]       // the local actor has no account: no self
    [InlineData("Feedalice", "NOPE", false, "security")]     // an account that is not the target's
    public async Task Tele_OfAPlayer_ByAGameMaster_IsAllowedOnlyForTheActorsOwnAccount(string target, string? actorAccount, bool allowed, string? failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: ["Feedalice", "Feedbob"]);
        int? accountId = actorAccount is null ? null : (await world.Host.Accounts.FindByUsernameAsync(actorAccount))?.Id ?? 987654;
        ulong guid = (await world.Host.PlayerAsync(target)).Guid.Value;
        float x0 = await world.Host.PlayerStateAsync(target, p => p.X);

        // The actor's *name* is "Feedalice" in every row, the target's character name in two: the rule is the account id, so a name changes nothing.
        Ran run = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas tele 0x{guid:X16} 0 {FeedWorld.StartX + 40f} {FeedWorld.StartY} {FeedWorld.StartZ}"),
            AccountSecurity.GameMaster, "atlas-web:Feedalice", Select(guid, "atlas-web:Feedalice", accountId));

        if (allowed)
        {
            Assert.Null(run.Failure);
        }
        else
        {
            Assert.Equal(failure, run.Failure);
            Assert.Equal(x0, await world.Host.PlayerStateAsync(target, p => p.X));
        }
    }

    [WindowsFact]
    public async Task Tele_OfAnotherPlayer_IsAllowedForAnAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedbob");
        ulong bob = (await world.Host.PlayerAsync("Feedbob")).Guid.Value;

        Ran run = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas tele 0x{bob:X16} 0 {FeedWorld.StartX + 40f} {FeedWorld.StartY} {FeedWorld.StartZ}"),
            AccountSecurity.Administrator, "atlas-local:TEST#1", Select(bob));

        Assert.Null(run.Failure);
    }

    // ---- .atlas moveto / debug / spawns ----------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task MoveTo_StartsAWalkOfThatCreature_ToThePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");
        Assert.Null(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).Spline));

        Ran run = await RunAsync(world, $"atlas moveto 0x{FeedWorld.WolfGuid:X16} -8941 -131 83.5", AccountSecurity.GameMaster);

        Assert.Null(run.Failure);
        Assert.NotNull(await OnWorld(world, () => WolfOf(world, FeedWorld.WolfGuid).Spline));
    }

    [WindowsFact]
    public async Task Debug_AnswersOneJsonObject_ForOneUnit_AndNothingElse()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");

        Ran run = await RunAsync(world, $"atlas debug 0x{FeedWorld.WolfGuid:X16}", AccountSecurity.GameMaster);

        Assert.Null(run.Failure);
        using JsonDocument json = JsonDocument.Parse(Assert.Single(run.Lines));
        JsonElement root = json.RootElement;
        Assert.Equal(FeedWorld.Hex(FeedWorld.WolfGuid), root.GetProperty("guid").GetString());
        foreach (string key in new[] { "aggroRadius", "leash", "home", "spawn", "waypoints", "threat", "ai" })
        {
            Assert.True(root.TryGetProperty(key, out _), $"missing '{key}': {root}");
        }

        Assert.Equal(JsonValueKind.Array, root.GetProperty("threat").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("waypoints").ValueKind);
    }

    [WindowsFact]
    public async Task Spawns_ListsTheSpawnPointsInTheRadius_AsJson_AndIsEmptyFarAway()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(players: "Feedalice");

        Ran near = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas spawns 0 {FeedWorld.StartX} {FeedWorld.StartY} 50"), AccountSecurity.GameMaster);
        Ran far = await RunAsync(world, "atlas spawns 0 5000 5000 10", AccountSecurity.GameMaster);

        Assert.Null(near.Failure);
        using JsonDocument nearJson = JsonDocument.Parse(Assert.Single(near.Lines));
        JsonElement rows = nearJson.RootElement.GetProperty("rows");
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("kind").GetString() == "creature" && r.GetProperty("guid").GetUInt32() == FeedWorld.WolfSpawn);
        Assert.Contains(rows.EnumerateArray(), r => r.GetProperty("kind").GetString() == "gameobject" && r.GetProperty("guid").GetUInt32() == FeedWorld.ChestSpawn);
        Assert.False(nearJson.RootElement.GetProperty("truncated").GetBoolean());

        using JsonDocument farJson = JsonDocument.Parse(Assert.Single(far.Lines));
        Assert.Equal(0, farJson.RootElement.GetProperty("rows").GetArrayLength());
    }

    [WindowsFact]
    public async Task Spawns_StopsAtFiveHundredRows_AndSaysItWasTruncated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using FeedWorld world = await StartAsync(extraCreatures: 520, players: "Feedalice");

        Ran run = await RunAsync(world, string.Create(CultureInfo.InvariantCulture, $"atlas spawns 0 {FeedWorld.StartX} {FeedWorld.StartY} 500"), AccountSecurity.GameMaster);

        using JsonDocument json = JsonDocument.Parse(Assert.Single(run.Lines));
        Assert.Equal(500, json.RootElement.GetProperty("rows").GetArrayLength());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
    }
}

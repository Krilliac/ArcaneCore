using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.HotCode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.HotCode;

/// <summary>An opcode group for the commands test; the opcode is chosen per run because the real table already has most.</summary>
public sealed class HotCmdOpcodeGroup : IOpcodeHandlerGroup
{
    public static WorldOpcode Opcode;

    public void Register(OpcodeTable table)
        => table.OnSession(Opcode, SessionStates.Authenticated, (_, _) => Task.CompletedTask);
}

/// <summary>
/// <c>.hotcode</c>: Administrator only, present only when hot code is enabled, observes and pauses
/// but never loads code, and does not change how any existing command resolves.
/// </summary>
public sealed class HotCodeCommandTests
{
    private static HotCodeRefresh NewRefresh(WorldTestHost host, TestCatalog catalog, HotCodeState state, HotCodeAudit audit)
        => new(state, new WorldRuntimeHotCodeWorld(host.World), host.Opcodes,
            host.WorldServices.GetRequiredService<CommandTableSource>(), catalog, audit, NullLogger.Instance);

    private static async Task<string> SayAsync(WorldTestClient client, string text)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, text);
        return (await client.ReadChatAsync()).Text;
    }

    [Fact]
    public async Task WithHotCodeOff_NoOneHasTheCommand_NotEvenAnAdministrator()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("HCOFF", "Hcoff", AccountSecurity.Administrator);
        await admin.CollectAsync();
        Assert.Equal("There is no such command", await SayAsync(admin, ".hotcode status")); // Wave-2 integration: the GM lane's retail command texts (no trailing period; below-level commands answer CommandUnavailable).
        Assert.DoesNotContain("hotcode", await SayAsync(admin, ".commands"));
    }

    [Fact]
    public async Task OnlyAnAdministratorSeesTheCommand()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var state = new HotCodeState();
        var refresh = NewRefresh(host, new TestCatalog(), state, new HotCodeAudit(null));
        Assert.True(host.WorldServices.GetRequiredService<CommandTableSource>().TryAdd([HotCodeCommands.Create(refresh, new HotCodeAudit(null))]).Applied);

        await using WorldTestClient moderator = await host.EnterWorldAsync("HCMOD", "Hcmod", AccountSecurity.Moderator);
        await using WorldTestClient master = await host.EnterWorldAsync("HCGM", "Hcgm", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("HCADM", "Hcadm", AccountSecurity.Administrator);
        await moderator.CollectAsync();
        await master.CollectAsync();
        await admin.CollectAsync();

        Assert.Equal("This command is not available to you.", await SayAsync(moderator, ".hotcode status"));
        Assert.Equal("This command is not available to you.", await SayAsync(master, ".hotcode status"));
        Assert.Equal("This command is not available to you.", await SayAsync(master, ".hotc freeze"));
        Assert.False(state.Frozen);

        string status = await SayAsync(admin, ".hotcode status");
        Assert.StartsWith("Code hot reload: generation 0, applied 0, frozen: no, degraded: no", status);
    }

    [Fact]
    public async Task FreezeThawAndRefresh_AreAuditedAndWork_AndStatusShowsDivergence()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        string log = Path.Combine(Path.GetTempPath(), "arcane-hcmd-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var audit = new HotCodeAudit(log);
            var state = new HotCodeState();
            var catalog = new TestCatalog();
            catalog.OpcodeGroups.Clear(); // the host's real table is the baseline; the catalog only lists what the "edit" adds
            catalog.CommandGroups.Clear();
            var refresh = NewRefresh(host, catalog, state, audit);
            Assert.True(host.WorldServices.GetRequiredService<CommandTableSource>().TryAdd([HotCodeCommands.Create(refresh, audit)]).Applied);
            await using WorldTestClient admin = await host.EnterWorldAsync("HCADM2", "Hcadmtwo", AccountSecurity.Administrator);
            await admin.CollectAsync();

            // An edit adds an opcode group; frozen, a refresh must not pick it up.
            HotCmdOpcodeGroup.Opcode = Enum.GetValues<WorldOpcode>().First(o => o.ToString().StartsWith("Cmsg", StringComparison.Ordinal) && !host.Opcodes.TryGet(o, out _));
            catalog.OpcodeGroups.Add(typeof(HotCmdOpcodeGroup));
            Assert.Equal("Hot code refresh is frozen.", await SayAsync(admin, ".hotcode freeze"));
            Assert.True(state.Frozen);
            Assert.Equal("Refresh requested.", await SayAsync(admin, ".hotcode refresh"));
            Assert.StartsWith("Refresh Frozen:", (await admin.ReadChatAsync()).Text);
            Assert.False(host.Opcodes.TryGet(HotCmdOpcodeGroup.Opcode, out _));

            Assert.Equal("Hot code refresh is active.", await SayAsync(admin, ".hotcode thaw"));
            Assert.Equal("Refresh requested.", await SayAsync(admin, ".hotcode refresh"));
            string applied = (await admin.ReadChatAsync()).Text;
            Assert.StartsWith("Refresh Applied: 1 opcode handlers (", applied);
            Assert.True(host.Opcodes.TryGet(HotCmdOpcodeGroup.Opcode, out _));

            state.RecordMetadataUpdate(); // what the runtime callback does after an applied edit
            await admin.SendChatAsync(ChatType.Say, Language.Common, ".hotcode status");
            string status = string.Join(
                '\n', (await admin.ReadChatAsync()).Text, (await admin.ReadChatAsync()).Text, (await admin.ReadChatAsync()).Text);
            Assert.Contains("Process diverged from build: yes, 1 code edits applied in memory", status);
            Assert.Contains("Refreshes applied: 1, rejected: 0", status);

            string[] kinds = File.ReadAllLines(log).Select(l => l.Split('\t')[1]).ToArray();
            Assert.Equal(
                ["hotcode-command", "hotcode-command", "refresh-frozen", "hotcode-command", "hotcode-command", "refresh-applied"],
                kinds);
        }
        finally
        {
            File.Delete(log);
        }
    }

    [Fact]
    public async Task ADegradedRefresh_IsShownByStatus()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        var state = new HotCodeState();
        var catalog = new TestCatalog { ScanFailure = new InvalidOperationException("bad updater") };
        var refresh = NewRefresh(host, catalog, state, new HotCodeAudit(null));
        Assert.True(host.WorldServices.GetRequiredService<CommandTableSource>().TryAdd([HotCodeCommands.Create(refresh, new HotCodeAudit(null))]).Applied);
        await using WorldTestClient admin = await host.EnterWorldAsync("HCADM3", "Hcadmthree", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await SayAsync(admin, ".hotcode refresh");
        Assert.StartsWith("Refresh Rejected:", (await admin.ReadChatAsync()).Text);
        string status = await SayAsync(admin, ".hotcode status");
        Assert.Contains("degraded: yes, the last refresh was rejected: candidate rejected: bad updater", status);
    }

    [Fact]
    public void TheRoot_DoesNotChangeHowAnyExistingInputResolves()
    {
        using var world = new ThreadedWorld();
        CommandTable startup = ChatCommands.CreateTable();
        ChatCommand root = HotCodeCommands.Create(
            new HotCodeRefresh(new HotCodeState(), world, new OpcodeTable(), new CommandTableSource(startup), new TestCatalog(), new HotCodeAudit(null), NullLogger.Instance),
            new HotCodeAudit(null));

        Assert.Null(CommandTableSource.Validate(startup.Roots, [root]));

        AccountSecurity[] levels = Enum.GetValues<AccountSecurity>();
        string[] inputs = startup.Roots
            .SelectMany(c => Enumerable.Range(1, c.Name.Length).Select(length => c.Name[..length]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var after = new CommandTable([.. startup.Roots, root]);
        foreach (string input in inputs)
        {
            foreach (AccountSecurity level in levels)
            {
                Assert.Equal(startup.Resolve(input, level)?.Name, after.Resolve(input, level)?.Name);
            }
        }
    }
}

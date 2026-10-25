using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Tests.Gm.Audit;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>The command-facing confirm API (plan I7): <see cref="GmConfirm.Issue"/> and <see cref="GmConfirm.TryConsume"/> over a host's store.</summary>
public sealed class GmConfirmTests
{
    [Fact]
    public async Task IssueThenTryConsume_AcceptsOnce_IgnoringCaseAndBlanksOfTheText()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GMCONE", "Gmcone", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("GMCONE"))!.Id)!;
        List<bool> results = [];
        string code = string.Empty;

        await host.OnWorldAsync(() =>
        {
            CommandContext context = WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "purge all");
            code = GmConfirm.Issue(context, "purge all");
            results.Add(GmConfirm.TryConsume(context, "Purge   ALL", code.ToLowerInvariant()));
            results.Add(GmConfirm.TryConsume(context, "purge all", code));
        });

        Assert.Equal(ConfirmTokenStore.CodeLength, code.Length);
        Assert.Equal([true, false], results);
    }

    [Fact]
    public async Task TryConsume_RefusesADifferentCommandPath_AndDifferentText()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GMCTWO", "Gmctwo", AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("GMCTWO"))!.Id)!;
        List<bool> results = [];

        await host.OnWorldAsync(() =>
        {
            CommandContext purge = WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "purge all");
            string code = GmConfirm.Issue(purge, "purge all");
            CommandContext wipe = WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "wipe all");
            results.Add(GmConfirm.TryConsume(wipe, "purge all", code));
            results.Add(GmConfirm.TryConsume(purge, "purge bots", code));
            results.Add(GmConfirm.TryConsume(purge, "purge all", code));
        });

        Assert.Equal([false, false, true], results);
    }

    [Fact]
    public async Task ACharacterNamedLikeTheConsole_NeitherSharesNorReplacesTheConsolesCode()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("GMCTHR", CommandContext.ConsoleName, AccountSecurity.Administrator);
        var session = host.Registry.Find((await host.Accounts.FindByUsernameAsync("GMCTHR"))!.Id)!;
        List<bool> results = [];
        string[] keys = [];

        await host.OnWorldAsync(() =>
        {
            CommandContext console = WriteTestCommands.Bind(CommandContext.ForConsole(session, ChatCommands.CreateTable(), _ => { }), "purge all");
            CommandContext player = WriteTestCommands.Bind(new CommandContext(session, session.Player!, ChatCommands.CreateTable()), "purge all");
            keys = [GmConfirm.InvokerKey(console), GmConfirm.InvokerKey(player)];
            string consoleCode = GmConfirm.Issue(console, "purge all");
            string playerCode = GmConfirm.Issue(player, "purge all");
            results.Add(GmConfirm.TryConsume(player, "purge all", consoleCode));
            results.Add(GmConfirm.TryConsume(console, "purge all", consoleCode));
            results.Add(GmConfirm.TryConsume(player, "purge all", playerCode));
        });

        Assert.NotEqual(keys[0], keys[1]);
        Assert.Equal([false, true, true], results);
    }

    [Fact]
    public void TheApi_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => GmConfirm.InvokerKey(null!));
        Assert.Throws<ArgumentNullException>(() => GmConfirm.Issue(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => GmConfirm.TryConsume(null!, "x", "ABC234"));
    }
}

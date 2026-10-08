using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary><c>.playerbot scenario list|run</c>: Administrator only, refused unless World:Playerbots:Scenarios:Enabled.</summary>
public sealed class PlayerbotScenarioCommandTests
{
    [Fact]
    public void ScenarioCommands_AreAdministratorOnly()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.NotNull(table.Resolve("playerbot scenario run", AccountSecurity.Administrator));
        Assert.NotNull(table.Resolve("playerbot scenario list", AccountSecurity.Administrator));
        Assert.Null(table.Resolve("playerbot scenario run", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("playerbot scenario list", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("playerbot scenario run", AccountSecurity.Player));
    }

    [Fact]
    public void ScenarioOptions_AreOffByDefault_AndBounded()
    {
        var options = new PlayerbotOptions();
        Assert.False(options.Scenarios.Enabled);
        options.Validate();
        options.Scenarios.MaxDurationSeconds = 1;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task Run_IsRefused_WhenScenariosAreDisabled()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("SCNOFF", "Scnoff", AccountSecurity.Administrator);
        await admin.CollectAsync();
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot scenario run smoke");
        Assert.Equal("Playerbot scenarios are disabled (World:Playerbots:Scenarios:Enabled).", (await admin.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task ListAndRun_ReportTheScenarioThroughChat()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await using WorldTestClient admin = await world.EnterWorldAsync("SCNADMIN", "Scnadmin", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot scenario list");
        var listed = new List<string>();
        for (int i = 0; i < PlayerbotScenarioCatalog.Builtins.Count; i++) listed.Add((await admin.ReadChatAsync()).Text);
        Assert.Equal(PlayerbotScenarioCatalog.Builtins.Select(s => $"{s.Name}: {s.Description}"), listed);

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot scenario run nosuch");
        Assert.Equal("Unknown scenario 'nosuch'. Use .playerbot scenario list.", (await admin.ReadChatAsync()).Text);

        // The live command runs on the real clock; "smoke" needs no game time, so it completes on this manual-clock world.
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".playerbot scenario run smoke");
        Assert.Equal("Playerbot scenario smoke started.", (await admin.ReadChatAsync()).Text);
        // The runner has a 120 s wall-clock limit; its report can arrive well after a normal 10 s packet read on a loaded host.
        TimeSpan reportTimeout = TimeSpan.FromSeconds(150);
        string headline = (await admin.ReadChatAsync(reportTimeout)).Text;
        if (headline == "scenario smoke")
        {
            headline = (await admin.ReadChatAsync(reportTimeout)).Text; // the admin stands beside the bot and hears its /say too
        }

        Assert.StartsWith("SCENARIO smoke PASS steps=2", headline);
        Assert.Contains("login Scnalpha", (await admin.ReadChatAsync()).Text);
        Assert.Contains("say and hear it", (await admin.ReadChatAsync()).Text);
    }
}

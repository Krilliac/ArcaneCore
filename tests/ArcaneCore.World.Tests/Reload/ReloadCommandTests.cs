using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>Registers the reload feature's doubles for <see cref="WorldTestHost"/> (discovered). Live reload is off by default, so the host turns it on explicitly.</summary>
internal sealed class ReloadTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton(new ConfigurationManager());
        services.AddSingleton<IConfiguration>(sp => sp.GetRequiredService<ConfigurationManager>());
        services.AddSingleton(Options.Create(new HotReloadOptions { Commands = true }));
    }
}

/// <summary>The <c>.reload</c> chat commands end to end (vmangos Chat.cpp:794-935, root at :1212).</summary>
public sealed class ReloadCommandTests
{
    private static async Task<WorldTestClient> AdministratorAsync(WorldTestHost host, string name = "Reloader")
    {
        WorldTestClient client = await host.EnterWorldAsync(name.ToUpperInvariant(), name, AccountSecurity.Administrator);
        await client.CollectAsync();
        return client;
    }

    private static Task SayAsync(WorldTestClient client, string text) => client.SendChatAsync(ChatType.Say, Language.Common, text);

    [Fact]
    public async Task ReloadSpellTemplate_ReplacesTheStore_AndReportsBack()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);
        SpellStore before = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store;

        await SayAsync(admin, ".reload spell_template");

        Assert.Equal("Re-loading spell_template...", (await admin.ReadChatAsync()).Text);
        string done = (await admin.ReadChatAsync()).Text;
        Assert.StartsWith("spell_template reloaded:", done);
        Assert.NotSame(before, host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store);
    }

    [Fact]
    public async Task AbbreviatedNames_ResolveWhenUnique()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);

        await SayAsync(admin, ".reload spell_te");

        Assert.Equal("Re-loading spell_template...", (await admin.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task SharedSpellPrefix_ReportsAmbiguityAndDoesNotStartAReload()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);

        await SayAsync(admin, ".reload spell");

        string reply = (await admin.ReadChatAsync()).Text;
        Assert.StartsWith("Ambiguous reloadable 'spell':", reply);
        Assert.Contains("spell_template", reply);
        Assert.Contains("spell_enchant_charges", reply);
        Assert.Empty(host.WorldServices.GetRequiredService<ReloadFeature>().Coordinator.LastResults);
    }

    [Fact]
    public async Task ReloadConfig_AppliesLiveOptions_AndNamesTheRestartOnlyOnes()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);
        host.WorldServices.GetRequiredService<ConfigurationManager>().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Motd"] = "Reloaded@live",
            ["World:AutosaveIntervalMs"] = "0",
            ["World:TickIntervalMs"] = "999",
        });

        await SayAsync(admin, ".reload config");

        Assert.Equal("Re-loading config...", (await admin.ReadChatAsync()).Text);
        Assert.StartsWith("config reloaded:", (await admin.ReadChatAsync()).Text);
        Assert.Equal("World:TickIntervalMs option can't be changed at reload, using current value (5).", (await admin.ReadChatAsync()).Text);
        Assert.Equal("Reloaded@live", host.World.Options.Motd);
        Assert.Equal(5, host.World.Options.TickIntervalMs);
    }

    [Fact]
    public async Task ReloadWithoutAName_ListsTheNames()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);

        await SayAsync(admin, ".reload");

        string reply = (await admin.ReadChatAsync()).Text;
        Assert.StartsWith("Syntax: .reload", reply); // the GM lane prints the help text in place of "Incorrect syntax."
        Assert.Contains("spell_template", reply);
        Assert.Contains("config", reply);
    }

    [Fact]
    public async Task ReloadStatus_ListsEveryReloadable_AndWhatHappenedLast()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);
        await SayAsync(admin, ".reload spell_template");
        await admin.ReadChatAsync();
        await admin.ReadChatAsync();

        await SayAsync(admin, ".reload status");

        // One line per registered reloadable, in name order (other features register theirs too).
        List<string> lines = await ReadLinesUntilAsync(admin, l => l.StartsWith("spell_template:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("config: not reloaded since start", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("spell_template: Applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReloadAll_FollowsVmangos_NoConfig_NoItemOrCreatureTemplates()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);

        await SayAsync(admin, ".reload all");

        Assert.Equal("Re-loading all...", (await admin.ReadChatAsync()).Text);

        // vmangos reload all (ServerCommands.cpp:885-905) reaches areatrigger_teleport (:907-914), game_tele (:900) and spell_template (:969-971),
        // but neither item_template (all_item :996-1002), creature_template (all_npc :925-933) nor npc_text (in neither all_npc nor all_gossips).
        // ReloadAllMembershipTests pins the whole set.
        List<string> lines = await ReadLinesUntilAsync(admin, l => l.StartsWith("spell_template reloaded:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("areatrigger_teleport reloaded:", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("game_tele reloaded:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("item_template", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("creature_template", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("npc_text", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("config", StringComparison.Ordinal));
    }

    /// <summary>Chat lines up to and including the first that satisfies <paramref name="last"/> (at most 80: one line per reloadable and the list grows with every table a lane makes reloadable).</summary>
    private static async Task<List<string>> ReadLinesUntilAsync(WorldTestClient client, Func<string, bool> last)
    {
        var lines = new List<string>();
        for (int i = 0; i < 80; i++)
        {
            string line = (await client.ReadChatAsync()).Text;
            lines.Add(line);
            if (last(line))
            {
                return lines;
            }
        }

        throw new Xunit.Sdk.XunitException("the expected chat line never arrived: " + string.Join(" | ", lines));
    }

    [Fact]
    public async Task ReloadIsAdministratorOnly()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMONLY", "Gmonly", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await SayAsync(gm, ".reload spell_template");

        Assert.Equal("This command is not available to you.", (await gm.ReadChatAsync()).Text); // Wave-2 integration: the GM lane's retail command texts (no trailing period; below-level commands answer CommandUnavailable).
    }

    [Fact]
    public async Task WhenDisabled_TheReloadRootDoesNotExist_AndNothingIsReloaded()
    {
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton(Options.Create(new HotReloadOptions { Commands = false })));
        await using WorldTestClient admin = await AdministratorAsync(host);
        SpellStore before = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store;
        ReloadFeature feature = host.WorldServices.GetRequiredService<ReloadFeature>();

        await SayAsync(admin, ".reload spell_template");

        Assert.Equal("There is no such command", (await admin.ReadChatAsync()).Text);
        Assert.Same(before, host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store);
        Assert.False(feature.Enabled);
        Assert.Throws<InvalidOperationException>(() => feature.Coordinator);
    }

    [Fact]
    public void WithTheDefaults_TheReloadRootsDoNotExist()
    {
        // No HotReload section anywhere (an options object nobody touched): the shipped default.
        Assert.False(new HotReloadOptions().Commands);
        ServiceProvider services = new ServiceCollection().AddSingleton(Options.Create(new HotReloadOptions())).BuildServiceProvider();
        Assert.DoesNotContain(ChatCommands.CreateTable(services).Roots, c => c.Name.Equals("reload", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ChatCommands.CreateTable().Roots, c => c.Name.Equals("reload", StringComparison.OrdinalIgnoreCase));

        ServiceProvider enabled = new ServiceCollection().AddSingleton(Options.Create(new HotReloadOptions { Commands = true })).BuildServiceProvider();
        Assert.Contains(ChatCommands.CreateTable(enabled).Roots, c => c.Name.Equals("reload", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheShippedAppsettings_LeaveHotReloadOff()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        // The world daemon's file is copied next to the test assembly when it is referenced; otherwise read it from the source tree.
        if (!File.Exists(path))
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "src", "ArcaneCore.World", "appsettings.json")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            Assert.NotNull(dir);
            path = Path.Combine(dir!, "src", "ArcaneCore.World", "appsettings.json");
        }

        IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        Assert.False(configuration.GetSection(HotReloadOptions.SectionName).Get<HotReloadOptions>()!.Commands);
    }

    [Fact]
    public async Task UnknownName_ListsTheKnownOnes()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);

        await SayAsync(admin, ".reload nonsense");

        string reply = (await admin.ReadChatAsync()).Text;
        Assert.StartsWith("There is no reloadable 'nonsense'.", reply);
        Assert.Contains("spell_template", reply);
    }
}

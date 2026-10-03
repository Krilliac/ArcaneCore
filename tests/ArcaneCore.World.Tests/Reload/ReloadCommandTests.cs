using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Protocol;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>Registers the reload feature's doubles for <see cref="WorldTestHost"/> (discovered).</summary>
internal sealed class ReloadTestServices : IWorldTestServices
{
    public void Register(IServiceCollection services)
    {
        services.AddSingleton(new ConfigurationManager());
        services.AddSingleton<IConfiguration>(sp => sp.GetRequiredService<ConfigurationManager>());
        services.AddSingleton(Options.Create(new HotReloadOptions()));
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

        await SayAsync(admin, ".reload spell");

        Assert.Equal("Re-loading spell_template...", (await admin.ReadChatAsync()).Text);
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
        Assert.StartsWith("Incorrect syntax. .reload:", reply);
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

        var lines = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            lines.Add((await admin.ReadChatAsync()).Text);
        }

        Assert.Contains(lines, l => l.StartsWith("config: not reloaded since start", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("spell_template: Applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReloadAll_ReloadsTheContent_ButNotTheConfig()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);

        await SayAsync(admin, ".reload all");

        Assert.Equal("Re-loading all...", (await admin.ReadChatAsync()).Text);
        Assert.StartsWith("spell_template reloaded:", (await admin.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task ReloadIsAdministratorOnly()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("GMONLY", "Gmonly", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await SayAsync(gm, ".reload spell_template");

        Assert.Equal("There is no such command.", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task WhenDisabledByConfiguration_TheCommandSaysSo_AndReloadsNothing()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient admin = await AdministratorAsync(host);
        host.WorldServices.GetRequiredService<IOptions<HotReloadOptions>>().Value.Commands = false;
        SpellStore before = host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store;

        await SayAsync(admin, ".reload spell_template");

        Assert.Equal("Hot reload is disabled (HotReload:Commands).", (await admin.ReadChatAsync()).Text);
        Assert.Same(before, host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>().System.Store);
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

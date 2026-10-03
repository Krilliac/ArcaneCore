using System.Collections.Concurrent;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The extension seams parallel features plug into (docs/integration/seams.md):
/// discovered handler groups and command groups, world features, chat routing and the
/// login/logout events.
/// </summary>
public sealed class SeamTests
{
    [Fact]
    public void HandlerGroups_AreDiscovered_AndBuildOneTable()
    {
        Type[] m6Groups =
        [
            typeof(CharacterHandlers), typeof(AccountDataHandlers), typeof(QueryHandlers), typeof(MovementHandlers),
            typeof(PlayerHandlers), typeof(LogoutHandlers), typeof(ChatHandlers),
        ];
        IEnumerable<Type> discovered = WorldServiceCollectionExtensions.HandlerGroups.Select(g => g.GetType());
        Assert.All(m6Groups, t => Assert.Contains(t, discovered));
        Assert.True(WorldServiceCollectionExtensions.BuildOpcodeTable().TryGet(WorldOpcode.CmsgMessagechat, out _));
    }

    [Fact]
    public void BuiltinCommands_ComeFirst_InTheirOriginalOrder()
    {
        string[] builtins = ["help", "commands", "save", "saveall", "server", "gps", "announce", "notify", "gm", "kick", "modify"];
        CommandTable table = ChatCommands.CreateTable();
        Assert.Equal(builtins, table.Roots.Take(builtins.Length).Select(c => c.Name));
        Assert.Equal(table.Roots.Count, table.Roots.Select(c => c.Name.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void FeatureTypes_AreRegisteredAsSingletons()
    {
        var services = new ServiceCollection().AddWorldFeatures();
        Assert.All(WorldFeatures.FeatureTypes, t => Assert.Contains(services, d => d.ServiceType == t && d.Lifetime == ServiceLifetime.Singleton));
    }

    [Fact]
    public async Task ChatFeatures_SeeMessagesAfterCommands_AndMayConsumeThem()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient alice = await host.EnterWorldAsync("SEAMALICE", "Seamalice");
        await using WorldTestClient bob = await host.EnterWorldAsync("SEAMBOB", "Seambob");
        await alice.CollectAsync();
        await bob.CollectAsync();

        // A consumed message never reaches the core's /say.
        await alice.SendChatAsync(ChatType.Say, Language.Common, "probe:seam-consumed");
        await TestProbe.WaitForAsync("probe:seam-consumed");
        Assert.DoesNotContain(await bob.CollectAsync(), p => p.Opcode == WorldOpcode.SmsgMessagechat);

        // An unconsumed one is seen by the feature and delivered as usual.
        await alice.SendChatAsync(ChatType.Say, Language.Common, "seam-delivered");
        Assert.Equal("seam-delivered", (await bob.ReadChatAsync()).Text);
        Assert.Contains(TestProbe.Messages, m => m is { Type: ChatType.Say, Language: Language.Common, Text: "seam-delivered" });

        // Commands run before features see anything.
        await alice.SendChatAsync(ChatType.Say, Language.Common, ".server motd");
        await alice.ReadChatAsync();
        Assert.DoesNotContain(TestProbe.Messages, m => m.Text == ".server motd");
    }

    [Fact]
    public async Task PlayerLoggedIn_FollowsTheLoginSequence_AndLoggingOutFollowsDisconnects()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldTestClient carol = await host.EnterWorldAsync("SEAMCAROL", "Seamcarol");
        await TestProbe.WaitForEventAsync("in:Seamcarol");

        await carol.DisposeAsync();
        await TestProbe.WaitForEventAsync("out:Seamcarol");
    }

    /// <summary>Registers the probe in every test host; it only consumes "probe:" messages.</summary>
    private sealed class ProbeServices : IWorldTestServices
    {
        public void Register(IServiceCollection services)
        {
            services.AddSingleton<TestProbe>();
            services.AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<TestProbe>());
            services.AddSingleton<IChatMessageHandler>(sp => sp.GetRequiredService<TestProbe>());
        }
    }

    private sealed class TestProbe : IWorldFeature, IChatMessageHandler
    {
        public static ConcurrentQueue<ClientChatMessage> Messages { get; } = new();

        public static ConcurrentQueue<string> Events { get; } = new();

        public void Attach(WorldRuntime world)
        {
            world.PlayerLoggedIn += p => Events.Enqueue("in:" + p.Name);
            world.PlayerLoggingOut += p => Events.Enqueue("out:" + p.Name);
        }

        public bool TryHandle(WorldSession session, Player player, ClientChatMessage message)
        {
            Messages.Enqueue(message);
            return message.Text.StartsWith("probe:", StringComparison.Ordinal);
        }

        public static Task WaitForAsync(string text) => WorldTestHost.WaitForAsync(() => Messages.Any(m => m.Text == text), text);

        public static Task WaitForEventAsync(string name) => WorldTestHost.WaitForAsync(() => Events.Contains(name), name);
    }
}

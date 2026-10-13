using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.ServerMail;
using ArcaneCore.World.Economy;
using ArcaneCore.World.Items;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.ServerMail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.ServerMail;

/// <summary>
/// Server mail validates reward rows when the templates load, which is at attach, before any login. The item catalog is filled on
/// first use, so that validation used to see an empty catalog and skip every reward item for the life of the process.
/// </summary>
public sealed class ServerMailTemplateLoadTests
{
    private const uint Linen = 2589;

    [Fact]
    public async Task Reward_items_are_kept_when_the_catalog_has_not_been_opened_yet()
    {
        ItemTemplate linen = new() { Entry = Linen, Name = "Linen Cloth", Stackable = 20 };
        var content = new ServerMailContent(
            [new ServerMailTemplateRow(1, 0, 0, 0, "Welcome", "", true)],
            [
                new ServerMailItemRow(1, "Alliance", Linen, 5),
                new ServerMailItemRow(1, "Alliance", Linen, 21), // above stackable: still skipped
                new ServerMailItemRow(1, "Alliance", 1, 1), // no such template: still skipped
            ],
            []);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton<IItemTemplateSource>(new Catalog([linen]));
        services.AddSingleton<IServerMailStore>(new MailTables(content));
        services.AddSingleton<ItemsFeature>();
        services.AddSingleton(sp => new CharacterSaveQueue(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance));
        services.AddSingleton(sp => new EconomyFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<EconomyFeature>.Instance));
        services.AddSingleton<ServerMailFeature>();
        await using ServiceProvider provider = services.BuildServiceProvider();

        var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            provider.GetRequiredService<CharacterSaveQueue>(), NullLogger<WorldRuntime>.Instance);
        try
        {
            // Production attach order: economy wires the item feature (still empty), then server mail starts its load.
            provider.GetRequiredService<EconomyFeature>().Attach(world);
            ServerMailFeature feature = provider.GetRequiredService<ServerMailFeature>();
            feature.Attach(world);
            world.Start();

            for (int i = 0; i < 100 && feature.Templates.Count == 0; i++)
            {
                await world.InvokeAsync(() => true);
            }

            ServerMailTemplate welcome = Assert.Single(feature.Templates);
            Assert.Equal([new ServerMailItem(Linen, 5)], welcome.ItemsAlliance);
            Assert.Empty(welcome.ItemsHorde);
        }
        finally
        {
            await provider.GetRequiredService<EconomyFeature>().DisposeAsync();
            world.Dispose();
            await provider.GetRequiredService<CharacterSaveQueue>().StopAsync();
        }
    }

    private sealed class Catalog(IReadOnlyList<ItemTemplate> templates) : IItemTemplateSource
    {
        public Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(templates);

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartingItem>>([]);
    }

    private sealed class MailTables(ServerMailContent content) : IServerMailStore
    {
        public Task<ServerMailContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);

        public Task<IReadOnlyCollection<uint>> GetSentAsync(int characterId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyCollection<uint>>([]);
    }
}

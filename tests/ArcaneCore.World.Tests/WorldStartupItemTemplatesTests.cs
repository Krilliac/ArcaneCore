using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Items;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// Creatures built while the world features attach (script and world-state spawns, the first grid spawns) resolve their creature_equip_template
/// items through <see cref="ItemsFeature"/>; the item content must therefore be loaded by the world start, not by the first login.
/// </summary>
public sealed class WorldStartupItemTemplatesTests
{
    private const uint SwordItem = 1899;

    [Fact]
    public async Task ACreatureLookupMadeWhileTheFeaturesAttach_FindsItsItems_BeforeAnyLogin()
    {
        var probe = new AttachProbe();
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ICharacterStore>(new InMemoryCharacterStore())
            .AddSingleton<IItemTemplateSource>(new OneItemSource())
            .AddSingleton(sp => new ItemsFeature(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ItemsFeature>.Instance))
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<ItemsFeature>())
            .AddSingleton(probe)
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<AttachProbe>())
            .BuildServiceProvider();
        probe.Services = services;
        IServiceScopeFactory scopes = services.GetRequiredService<IServiceScopeFactory>();
        var queue = new CharacterSaveQueue(scopes, NullLogger<CharacterSaveQueue>.Instance);
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, queue, NullLogger<WorldRuntime>.Instance);
        var host = new WorldHost(world, queue, new CharacterDirectory(), scopes, NullLogger<WorldHost>.Instance) { ListenerExpected = false };

        await host.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(probe.Attached, "the probe feature never attached");
            Assert.NotNull(probe.Found);
            Assert.Equal(7420u, probe.Found!.DisplayId);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private sealed class OneItemSource : IItemTemplateSource
    {
        public Task<IReadOnlyList<ItemTemplate>> LoadTemplatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ItemTemplate>>([new ItemTemplate { Entry = SwordItem, Class = 2, SubClass = 7, DisplayId = 7420, InventoryType = 13 }]);

        public Task<IReadOnlyList<StartingItem>> LoadStartingItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StartingItem>>([]);
    }

    /// <summary>Does what a creature built during Attach does: the item lookup the creature systems are handed.</summary>
    private sealed class AttachProbe : IWorldFeature
    {
        public IServiceProvider Services { get; set; } = null!;
        public bool Attached { get; private set; }
        public ItemTemplate? Found { get; private set; }

        public void Attach(WorldRuntime world)
        {
            Attached = true;
            Found = CreatureAiServicesBinder.Build(Services, new CreatureOptions()).ItemTemplateOf?.Invoke(SwordItem);
        }
    }
}

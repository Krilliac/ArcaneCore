using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Social;

/// <summary>The <c>World:Guild</c> section reaches the running guild manager at startup.</summary>
public sealed class GuildConfigBindingTests
{
    private static async Task<(GuildOptions Feature, GuildOptions Manager)> AttachAsync(IConfiguration? configuration)
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NullSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        await using var feature = new SocialFeature(new CharacterDirectory(), provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance, configuration);

        feature.Attach(world);
        return (feature.GuildOptions, feature.Context.Guilds.Options);
    }

    [Fact]
    public async Task WithoutConfiguration_TheDefaultsAreTheRetailValues()
    {
        (GuildOptions feature, GuildOptions manager) = await AttachAsync(null);

        Assert.Same(feature, manager);
        Assert.Equal(9, manager.MinPetitionSigns);
        Assert.False(manager.AllowClientGuildCreate);
    }

    [Fact]
    public async Task TheGuildSection_IsBoundAndHandedToTheGuildManager()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Guild:AllowClientGuildCreate"] = "true",
            ["World:Guild:MinPetitionSigns"] = "4",
        }).Build();

        (_, GuildOptions manager) = await AttachAsync(configuration);

        Assert.True(manager.AllowClientGuildCreate);
        Assert.Equal(4, manager.MinPetitionSigns);
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

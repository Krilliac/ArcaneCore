using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Reload;

/// <summary>
/// The cross-faction social rules are a configuration surface (<c>World:Social</c>) mapped to the
/// vmangos AllowTwoSide.* keys (World.cpp:610-613, 618), all off by default, so the same values the
/// <c>.reload config</c> applies are the ones the daemon starts with.
/// </summary>
public sealed class SocialConfigTests
{
    private static async Task<SocialOptions> AttachedOptionsAsync(IConfiguration? configuration)
    {
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using var world = new WorldRuntime(
            new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 },
            new NullSaveQueue(),
            NullLogger<WorldRuntime>.Instance);
        await using var feature = new SocialFeature(new CharacterDirectory(), provider.GetRequiredService<IServiceScopeFactory>(), NullLoggerFactory.Instance, configuration);

        feature.Attach(world);
        return feature.Options;
    }

    [Fact]
    public async Task WithoutConfiguration_EveryTwoSideRule_IsOff()
    {
        SocialOptions options = await AttachedOptionsAsync(null);

        Assert.False(options.AllowTwoSideAddFriend);
        Assert.False(options.AllowTwoSideGroup);
        Assert.False(options.AllowTwoSideGuild);
        Assert.False(options.AllowTwoSideChannel);
    }

    [Fact]
    public async Task AnEmptyConfiguration_LeavesEveryTwoSideRuleOff()
    {
        SocialOptions options = await AttachedOptionsAsync(new ConfigurationBuilder().Build());

        Assert.False(options.AllowTwoSideGroup);
    }

    [Fact]
    public async Task TheSocialSection_IsBoundAtStartup()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["World:Social:AllowTwoSideGuild"] = "true",
            ["World:Social:AllowTwoSideAddFriend"] = "true",
        }).Build();

        SocialOptions options = await AttachedOptionsAsync(configuration);

        Assert.True(options.AllowTwoSideGuild);
        Assert.True(options.AllowTwoSideAddFriend);
        Assert.False(options.AllowTwoSideGroup);
        Assert.False(options.AllowTwoSideChannel);
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }
}

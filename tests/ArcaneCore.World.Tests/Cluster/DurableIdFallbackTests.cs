using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// S1 writer lock C4b: the group, guild, instance and economy (mail, auction, item text) id allocators fail closed in Worker mode when no durable
/// block allocator is primed, and keep their in-memory fallback in Realm mode.
/// </summary>
public sealed class DurableIdFallbackTests
{
    [Fact]
    public void UnprimedGroupGuildAndInstanceAllocators_InWorkerMode_Throw()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions(), new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var social = new SocialContext(world, new EmptyLookup(), new NoopSocialPersistence(), new SocialOptions());
        social.Groups.RequireDurableIds = true;
        social.Guilds.RequireDurableIds = true;
        Assert.Equal(IdRangeScopes.Group, Assert.Throws<IdRangeExhaustedException>(() => social.Groups.AllocateGroupId()).Scope);
        Assert.Equal(IdRangeScopes.Guild, Assert.Throws<IdRangeExhaustedException>(() => social.Guilds.AllocateGuildId()).Scope);

        var instances = new InstanceManager(world) { RequireDurableIds = true };
        Assert.Equal(IdRangeScopes.Instance, Assert.Throws<IdRangeExhaustedException>(() => instances.AllocateInstanceId()).Scope);
    }

    [Fact]
    public void UnprimedGroupGuildAndInstanceAllocators_InRealmMode_KeepCounting()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions(), new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        var social = new SocialContext(world, new EmptyLookup(), new NoopSocialPersistence(), new SocialOptions());
        Assert.False(social.Groups.RequireDurableIds);
        Assert.False(social.Guilds.RequireDurableIds);
        uint group = social.Groups.AllocateGroupId();
        Assert.True(social.Groups.AllocateGroupId() > group);
        int guild = social.Guilds.AllocateGuildId();
        Assert.True(social.Guilds.AllocateGuildId() > guild);

        var instances = new InstanceManager(world);
        Assert.False(instances.RequireDurableIds);
        uint instance = instances.AllocateInstanceId();
        Assert.True(instances.AllocateInstanceId() > instance);
    }

    [Fact]
    public void UnprimedEconomyAllocators_InWorkerMode_Throw()
    {
        EconomyFeature economy = NewEconomy();
        economy.RequireDurableIds = true;
        Assert.Equal(IdRangeScopes.Auction, Assert.Throws<IdRangeExhaustedException>(() => economy.NextAuctionId()).Scope);
        Assert.Equal(IdRangeScopes.Mail, Assert.Throws<IdRangeExhaustedException>(() => economy.ReserveMailId(5)).Scope);
        Assert.Equal(IdRangeScopes.ItemText, Assert.Throws<IdRangeExhaustedException>(() => economy.ReserveItemTextId(5)).Scope);
    }

    [Fact]
    public void UnprimedEconomyAllocators_InRealmMode_KeepCounting()
    {
        EconomyFeature economy = NewEconomy();
        Assert.False(economy.RequireDurableIds);
        uint auction = economy.NextAuctionId();
        Assert.True(economy.NextAuctionId() > auction);
        Assert.True(economy.ReserveMailId(5) > 5u);
        Assert.True(economy.ReserveItemTextId(5) > 5u);
    }

    private static EconomyFeature NewEconomy()
    {
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        return new EconomyFeature(services, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<EconomyFeature>.Instance);
    }

    private sealed class EmptyLookup : ICharacterLookup
    {
        public CharacterInfo? Find(uint characterId) => null;

        public CharacterInfo? FindByName(string name) => null;
    }

    private sealed class NoopSocialPersistence : ISocialPersistence
    {
        public void SetSocial(int characterId, int otherId, ArcaneCore.Kernel.Social.SocialFlags flags) { }

        public void SaveGuild(ArcaneCore.Kernel.Social.GuildData guild) { }

        public void DeleteGuild(int guildId) { }
    }
}

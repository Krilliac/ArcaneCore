using ArcaneCore.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// Group persistence end to end: two scripted bots form a group and change its loot rules through the real handlers, the
/// group reaches the SQLite character database through the real EF store (characters schema 37), and a second world
/// started over the same database restores it (vmangos ObjectMgr::LoadGroups).
/// </summary>
public sealed class GroupPersistenceScenarioTests
{
    [Fact]
    public async Task ABotGroup_IsStored_AndAWorldStartedOverTheSameDatabaseRestoresIt()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync();
        await world.RunPassingAsync(new DelegateScenario("group-persistence", async context =>
        {
            (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
            await ScenarioSteps.FormGroupAsync(context, a, b);
            await context.StepAsync("need before greed", async () =>
            {
                long mark = b.Mark();
                ScenarioContext.Expect(await a.SetLootMethodAsync(LootMethod.NeedBeforeGreed), "loot method refused");
                await b.WaitForPacketAsync(WorldOpcode.SmsgGroupList, ScenarioDecoders.GroupList, g => g.LootMethod == LootMethod.NeedBeforeGreed, mark);
            });
        }));

        // The runner logged the bots out; membership outlives a logout. Write what the world holds now and wait for it.
        SocialGroupPersistenceFeature persistence = world.Services.GetRequiredService<SocialGroupPersistenceFeature>();
        Assert.NotNull(persistence.Writes);
        await world.Host.OnWorldAsync(persistence.SyncNow);
        await persistence.Writes!.FlushAsync();

        IReadOnlyList<CharacterIdentity> identities = await world.WithScopeAsync(sp => sp.GetRequiredService<ICharacterStore>().GetAllIdentitiesAsync());
        int alpha = identities.Single(i => i.Name == PlayerbotScenarioCatalog.BotA).Id;
        int beta = identities.Single(i => i.Name == PlayerbotScenarioCatalog.BotB).Id;
        GroupRecord stored = Assert.Single(await world.WithScopeAsync(sp => sp.GetRequiredService<IGroupStore>().LoadGroupsAsync()));
        Assert.Equal((alpha, (byte)LootMethod.NeedBeforeGreed), (stored.LeaderId, stored.LootMethod));
        Assert.Equal([alpha, beta], stored.Members.Select(m => m.CharacterId));

        // A restarted daemon over the same database: the characters are known (as WorldHost loads its directory at start).
        string connection = await world.WithScopeAsync(sp => Task.FromResult(sp.GetRequiredService<CharacterDbContext>().Database.GetConnectionString()!));
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = connection,
        }).Build();
        var directory = new CharacterDirectory();
        directory.Load(identities);
        await using WorldTestHost restarted = WorldTestHost.Start(configureServices: services =>
        {
            services.AddCharacterDatabase(configuration);
            services.AddSingleton(directory);
        });

        Group back = await restarted.OnWorldAsync(() => restarted.WorldServices.GetRequiredService<SocialFeature>().Context.Groups.Groups.Single());
        Assert.Equal((stored.Id, ObjectGuid.Player((uint)alpha), LootMethod.NeedBeforeGreed), (back.Id, back.LeaderGuid, back.LootMethod));
        Assert.Equal([PlayerbotScenarioCatalog.BotA, PlayerbotScenarioCatalog.BotB], back.Members.Select(m => m.Name));
    }
}

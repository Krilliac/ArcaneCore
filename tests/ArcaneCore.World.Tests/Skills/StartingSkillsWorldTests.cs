using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Skills;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Skills;

public sealed class StartingSkillsWorldTests
{
    [Fact]
    public async Task NewCharacterLogin_AppliesCatalogValidatedSharedAndRaceClassStarterSkills()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("STARTSKILLS", "Startskills");
        Player player = await host.PlayerAsync("Startskills");

        (ushort defense, ushort unarmed, ushort swords) = await host.OnWorldAsync(() => (
            player.Skills!.GetValuePure(SkillIds.Defense), player.Skills.GetValuePure(SkillIds.Unarmed), player.Skills.GetValuePure(SkillIds.Swords)));

        Assert.Equal((1, 1, 1), (defense, unarmed, swords));
        Assert.Equal(((ushort)5, (ushort)5, (ushort)5), await host.OnWorldAsync(() => (
            player.Skills!.GetMaxPure(SkillIds.Defense), player.Skills.GetMaxPure(SkillIds.Unarmed), player.Skills.GetMaxPure(SkillIds.Swords))));
    }

    [Fact]
    public async Task Login_PersistedProgressWins_AndForgottenWeaponIsNotRecreated()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: Configure);
        byte[] key = await host.AddAccountAsync("STARTPERSIST");
        CharacterRecord character;
        await using (WorldTestClient create = await host.ConnectAsync())
        {
            await create.AuthenticateAsync("STARTPERSIST", key);
            await create.CreateCharacterAsync("Startpersist");
        }

        Account account = (await host.Accounts.FindByUsernameAsync("STARTPERSIST"))!;
        character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        var store = host.WorldServices.GetRequiredService<ICharacterSkillStore>();
        await store.ReplaceSnapshotAsync(character.Id, new CharacterSkillSnapshot(
            [new CharacterSkillRow((ushort)SkillIds.Swords, 42, 50)],
            [new ForgottenSkillRow((ushort)SkillIds.Axes, 33)]));

        await using WorldTestClient login = await host.ConnectAsync();
        await login.AuthenticateAsync("STARTPERSIST", key);
        await login.LoginAsync((ulong)character.Id);
        Player player = await host.PlayerAsync("Startpersist");

        (ushort swords, bool axes, ushort defense) = await host.OnWorldAsync(() => (
            player.Skills!.GetValuePure(SkillIds.Swords), player.Skills.Has(SkillIds.Axes), player.Skills.GetValuePure(SkillIds.Defense)));
        Assert.Equal((42, false, 1), (swords, axes, defense));
    }

    [Fact]
    public async Task MissingStartingSkillSource_PreservesExistingSkillLifecycle()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(Catalog());
            services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(EmptyContent()));
            services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
        });
        await using WorldTestClient client = await host.EnterWorldAsync("NOSTARTSKILLS", "Noskillstart");
        Player player = await host.PlayerAsync("Noskillstart");
        Assert.True(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SkillsFeature>().IsActive));
        Assert.False(await host.OnWorldAsync(() => player.Skills!.Has(SkillIds.Defense)));
    }

    private static void Configure(IServiceCollection services)
    {
        services.AddSingleton(Catalog());
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(EmptyContent()));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
        services.AddSingleton<IStartingSkillSource>(new InMemoryStartingSkillSource(
        [
            new StartingSkill(SkillIds.Defense, 0, "shared defense"),
            new StartingSkill(SkillIds.Unarmed, 0, "shared unarmed"),
            new StartingSkill(SkillIds.Swords, 0, "human warrior swords"),
            new StartingSkill(SkillIds.Axes, 0, "forgotten test weapon"),
        ]));
    }

    private static SkillCatalog Catalog()
    {
        SkillLineRecord[] lines =
        [
            new(SkillIds.Defense, SkillCategories.Attributes, "Defense", 0),
            new(SkillIds.Unarmed, SkillCategories.Weapon, "Unarmed", 0),
            new(SkillIds.Swords, SkillCategories.Weapon, "Swords", 0),
            new(SkillIds.Axes, SkillCategories.Weapon, "Axes", 0),
        ];
        SkillRaceClassInfoRecord[] masks =
        [
            new(SkillIds.Defense, 0, 0, 0, 0, 0),
            new(SkillIds.Unarmed, 0, 0, 0, 0, 0),
            new(SkillIds.Swords, 1, 1, 0, 0, 0),
            new(SkillIds.Axes, 1, 1, 0, 0, 0),
        ];
        return new SkillCatalog(lines, masks, [], []);
    }

    private static SpellContent EmptyContent() => new([], [], [], [new SpellRangeRow { Id = 1 }], [], [], []);

    private sealed class InMemoryStartingSkillSource(IReadOnlyList<StartingSkill> rows) : IStartingSkillSource
    {
        public Task<IReadOnlyList<StartingSkill>> GetAsync(byte race, byte playerClass, CancellationToken cancellationToken = default)
            => Task.FromResult(rows);
    }
}

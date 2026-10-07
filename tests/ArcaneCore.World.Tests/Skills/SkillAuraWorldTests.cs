using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Skills;

/// <summary>Skill bonuses through login, the live stat fields, and real session logout/relogin.</summary>
public sealed class SkillAuraWorldTests
{
    private const uint UnarmedSpell = 204;
    private const uint DefenseSpell = 81;
    private const uint PermanentBonus = 29701;
    private const uint TemporaryBonus = 29702;

    [Fact]
    public async Task LoginPassiveAndSkillChanges_UpdateEffectiveSkillAndCritFields()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        await using WorldTestClient client = await host.EnterWorldAsync("BONUS", "Bonus");
        Player player = await host.PlayerAsync("Bonus");
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(1, player.Skills!.GetValuePure(SkillIds.Unarmed));
            Assert.Equal(5, player.Skills.GetBonus(SkillIds.Unarmed, permanent: true));
            Assert.Equal(6, player.Skills.GetValue(SkillIds.Unarmed));
            // No imported agility rates: only (effective skill - level * 5) * 0.04.
            Assert.Equal(0.04f, player.GetFloat(UpdateFields.PlayerCritPercentage), 0.001f);
            player.Skills.Set(SkillIds.Unarmed, 5, 5);
            Assert.Equal(0.20f, player.GetFloat(UpdateFields.PlayerCritPercentage), 0.001f);
            host.WorldServices.GetRequiredService<SpellFeature>().System.RemoveAuras(player, PermanentBonus);
            Assert.Equal(0, player.Skills.GetBonus(SkillIds.Unarmed, permanent: true));
            Assert.Equal(0f, player.GetFloat(UpdateFields.PlayerCritPercentage), 0.001f);
        });
    }

    [Fact]
    public async Task Relog_RebuildsPassiveAndSavedTemporaryBonusesWithoutPersistingThemAsSkillValues()
    {
        await using var host = WorldTestHost.Start(configureServices: Configure);
        byte[] key = await host.AddAccountAsync("BONUSRELOG");
        await using (WorldTestClient creator = await host.ConnectAsync())
        {
            await creator.AuthenticateAsync("BONUSRELOG", key);
            await creator.CreateCharacterAsync("Bonusrelog");
        }

        Account account = (await host.Accounts.FindByUsernameAsync("BONUSRELOG"))!;
        CharacterRecord character = Assert.Single(await host.Characters.GetByAccountAsync(account.Id));
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
        var skillStore = host.WorldServices.GetRequiredService<ICharacterSkillStore>();
        await using (WorldTestClient first = await LoginAsync(host, key, character))
        {
            Player player = await host.PlayerAsync("Bonusrelog");
            await host.OnWorldAsync(() =>
            {
                player.Skills!.Set(SkillIds.Defense, 5, 5);
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, TemporaryBonus, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal(10, player.Skills.GetBonus(SkillIds.Defense));
                Assert.Equal(15, player.Skills.GetValue(SkillIds.Defense));
                Assert.Equal(5, player.Skills.GetValueBase(SkillIds.Defense));
                Assert.Equal(0.4f, player.GetFloat(UpdateFields.PlayerDodgePercentage), 0.001f);
            });
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session logs out");
        await host.WorldServices.GetRequiredService<ArcaneCore.World.Skills.SkillsFeature>().Saves.FlushAllAsync(TimeSpan.FromSeconds(10));
        await spells.State.FlushAsync();
        CharacterSkillSnapshot saved = await skillStore.LoadAsync(character.Id);
        Assert.Contains(new CharacterSkillRow((ushort)SkillIds.Defense, 5, 5), saved.Skills);
        Assert.Contains(new CharacterSkillRow((ushort)SkillIds.Unarmed, 1, 5), saved.Skills);
        ICharacterSpellStateStore auraStore = host.WorldServices.GetRequiredService<ICharacterSpellStateStore>();
        Assert.Contains((await auraStore.LoadAsync(character.Id)).Auras, a => a.Spell == TemporaryBonus && a.Amount0 == 10);

        await using WorldTestClient second = await LoginAsync(host, key, character);
        Player again = await host.PlayerAsync("Bonusrelog");
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(5, again.Skills!.GetBonus(SkillIds.Unarmed, permanent: true));
            Assert.Equal(6, again.Skills.GetValue(SkillIds.Unarmed));
            Assert.Equal(10, again.Skills.GetBonus(SkillIds.Defense));
            Assert.Equal(15, again.Skills.GetValue(SkillIds.Defense));
            Assert.Equal(0.4f, again.GetFloat(UpdateFields.PlayerDodgePercentage), 0.001f);
            Assert.Single(spells.System.GetAuras(again), a => a.Spell.Id == TemporaryBonus);
            spells.System.RemoveAuras(again, TemporaryBonus);
            Assert.Equal(5, again.Skills.GetValue(SkillIds.Defense));
            Assert.Equal(0f, again.GetFloat(UpdateFields.PlayerDodgePercentage), 0.001f);
        });
    }

    private static async Task<WorldTestClient> LoginAsync(WorldTestHost host, byte[] key, CharacterRecord character)
    {
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("BONUSRELOG", key);
        await client.LoginAsync((ulong)character.Id);
        return client;
    }

    private static void Configure(IServiceCollection services)
    {
        services.AddSingleton(new SkillCatalog(
            [new SkillLineRecord(SkillIds.Unarmed, SkillCategories.Weapon, "Unarmed", 0), new SkillLineRecord(SkillIds.Defense, SkillCategories.Class, "Defense", 0)],
            [new SkillRaceClassInfoRecord(SkillIds.Unarmed, 0, 0, 0, 0, 0), new SkillRaceClassInfoRecord(SkillIds.Defense, 0, 0, 0, 0, 0)],
            [],
            [new SkillLineAbilityRecord(1, SkillIds.Unarmed, UnarmedSpell, 0, 0, 0, 0, 2, 0, 0), new SkillLineAbilityRecord(2, SkillIds.Defense, DefenseSpell, 0, 0, 0, 0, 2, 0, 0)],
            SpellLearnSkillTable.Build([])));
        services.AddSingleton<ICharacterSkillStore, InMemoryCharacterSkillStore>();
        services.AddSingleton<ISpellContentStore>(new InMemorySkillSpellContentStore(new SpellContent(
            [
                Spell(UnarmedSpell, 25, passive: true),
                Spell(DefenseSpell, 22, passive: true),
                Spell(PermanentBonus, 6, passive: true, aura: 98, skill: SkillIds.Unarmed, amount: 5),
                Spell(TemporaryBonus, 6, passive: false, aura: 30, skill: SkillIds.Defense, amount: 10),
            ],
            [], [new SpellDurationRow { Id = 1, Duration = 120000, MaxDuration = 120000 }], [new SpellRangeRow { Id = 1 }], [],
            [new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = UnarmedSpell }, new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = DefenseSpell }, new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = PermanentBonus }],
            [])));
    }

    private static SpellTemplateRow Spell(uint id, uint effect, bool passive, uint aura = 0, uint skill = 0, int amount = 0) => new()
    {
        Id = id, SpellName = $"Synthetic skill spell {id}", RangeIndex = 1,
        Attributes = passive ? 0x40u : 0, DurationIndex = passive ? 0u : 1,
        Effect1 = effect, EffectImplicitTargetA1 = 1, EffectApplyAuraName1 = aura,
        EffectMiscValue1 = (int)skill, EffectBasePoints1 = amount - 1, EffectBaseDice1 = 1, EffectDieSides1 = 1,
    };
}

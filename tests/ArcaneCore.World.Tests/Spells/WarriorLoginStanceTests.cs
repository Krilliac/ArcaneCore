using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// A warrior that knows Battle Stance and carries no stance enters the world in Battle Stance (vmangos Player::LoadFromDB casts 2457
/// when the warrior has no shapeshift form), through the normal aura path; a warrior that already has a stance is left alone.
/// </summary>
public sealed class WarriorLoginStanceTests
{
    private const uint BattleStance = 2457;
    private const uint DefensiveStance = 71;
    private const uint BerserkerStance = 2458;

    private static WorldTestHost StartHost()
    {
        SpellContent baseContent = SpellTestServices.Content();
        SpellTemplateRow stance(uint id, string name, ShapeshiftForm form) => new()
        {
            Id = id, SpellName = name, RangeIndex = 1, DurationIndex = 21, Effect1 = 6, EffectBasePoints1 = 0,
            EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1,
            EffectApplyAuraName1 = (uint)AuraType.ModShapeshift, EffectMiscValue1 = (int)form,
            StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };

        SpellContent content = baseContent with
        {
            Spells = [.. baseContent.Spells, stance(BattleStance, "Battle Stance", ShapeshiftForm.BattleStance),
                stance(DefensiveStance, "Defensive Stance", ShapeshiftForm.DefensiveStance),
                stance(BerserkerStance, "Berserker Stance", ShapeshiftForm.BerserkerStance)],
            CreateSpells = [.. baseContent.CreateSpells,
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = BattleStance },
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = DefensiveStance },
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = BerserkerStance }],
        };

        return WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(new Store(content)));
    }

    private static uint[] ActiveStances(SpellFeature spells, Player player)
        => [.. spells.System.GetAuras(player).Where(h => !h.IsRemoved && h.HasAura(AuraType.ModShapeshift)).Select(h => h.Spell.Id).Order()];

    [Fact]
    public async Task NewWarrior_EntersTheWorldInBattleStance()
    {
        await using WorldTestHost host = StartHost();
        await using WorldTestClient client = await host.EnterWorldAsync("STANCENEW", "Stancenew");
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();

        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer("Stancenew") is { } p && ActiveStances(spells, p).Length > 0, "the login stance");

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Stancenew")!;
            Assert.Equal([BattleStance], ActiveStances(spells, player));
            Assert.Equal(ShapeshiftForm.BattleStance, ShapeshiftService.GetForm(player));
        });
    }

    [Fact]
    public async Task WarriorWithAStance_KeepsItAtLogin_AndGetsNoBattleStance()
    {
        await using WorldTestHost host = StartHost();
        byte[] key = await host.AddAccountAsync("STANCEKEEP");
        SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
        CharacterRecord character;
        await using (WorldTestClient first = await host.ConnectAsync())
        {
            await first.AuthenticateAsync("STANCEKEEP", key);
            await first.CreateCharacterAsync("Stancekeep");
            Account account = (await host.Accounts.FindByUsernameAsync("STANCEKEEP"))!;
            character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            await first.LoginAsync((ulong)character.Id);
            await host.WaitForWorldAsync(
                () => host.World.FindOnlinePlayer("Stancekeep") is { } p && ActiveStances(spells, p).Length > 0, "the login stance");
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Stancekeep")!;
                Assert.Equal(SpellCastResult.CastOk, spells.System.CastSpell(player, DefensiveStance, SpellCastTargets.ForSelf(), triggered: true));
                Assert.Equal([DefensiveStance], ActiveStances(spells, player));
            });
        }

        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "the first session leaves the world");
        await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the character save");
        await spells.State.FlushAsync();

        await using WorldTestClient second = await host.ConnectAsync();
        await second.AuthenticateAsync("STANCEKEEP", key);
        await second.LoginAsync((ulong)character.Id);
        await host.WaitForWorldAsync(
            () => host.World.FindOnlinePlayer("Stancekeep") is { } p && ActiveStances(spells, p).Length > 0, "the restored stance");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Stancekeep")!;
            Assert.Equal([DefensiveStance], ActiveStances(spells, player));
            Assert.Equal(ShapeshiftForm.DefensiveStance, ShapeshiftService.GetForm(player));
        });
    }

    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);

        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

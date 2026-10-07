using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Reputation;

public sealed class ReputationAuraWorldTests
{
    private const uint Faction = 72, General = 995156, Specific = 995190;
    private static FactionCatalog Factions() => new([new FactionRecord(Faction, 0,
        [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Synthetic reputation faction")]);
    private static SpellInfo Aura(uint id, AuraType type, int amount, int faction = 0) => new()
    {
        Id = id, Name = "Synthetic reputation bonus", RangeIndex = 1, Range = new SpellRange(0, 0),
        Duration = new SpellDuration(-1, 0, -1),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, AuraType = type, BasePoints = amount - 1,
            BaseDice = 1, DieSides = 1, MiscValue = faction, TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };
    private static void InstallSpells(SpellSystem spells)
        => spells.Store = new SpellStore([.. spells.Store.All,
            Aura(General, AuraType.ModReputationGain, 10),
            Aura(Specific, AuraType.ModFactionReputationGain, 20, (int)Faction)], [], []);

    [Fact]
    public async Task DiscoveredWorldFeaturesReadGeneralAndMatchingKillBonusFromActualAuras()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(Factions()));
        await using WorldTestClient client = await host.EnterWorldAsync("REPAURA", "Repaura");
        await host.OnWorldAsync(() =>
        {
            var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            var reputation = host.WorldServices.GetRequiredService<ReputationFeature>().Service;
            Player player = host.World.FindOnlinePlayer("Repaura")!;
            Assert.True(spells.HasAuraHandler(AuraType.ModReputationGain));
            Assert.True(spells.HasAuraHandler(AuraType.ModFactionReputationGain));
            InstallSpells(spells);
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, General, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, Specific, SpellCastTargets.ForSelf(), triggered: true));
            Assert.Equal(130, reputation.Gain(ReputationSource.Kill, player, 100, Faction, player.Level));
            Assert.Equal(110, reputation.Gain(ReputationSource.Quest, player, 100, Faction, player.Level));
            Assert.Equal(-100, reputation.Gain(ReputationSource.Kill, player, -100, Faction, player.Level));
            spells.RemoveAuras(player, General);
            Assert.Equal(100, reputation.Gain(ReputationSource.Quest, player, 100, Faction, player.Level));
        });
    }

    [Fact]
    public async Task QuestReputationBonusIsPersistedAndRelogRestoresTheRewardedStanding()
    {
        var store = new MemoryReputationStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton(Factions());
            services.AddSingleton<ICharacterReputationStore>(store);
        });
        byte[] key = await host.AddAccountAsync("REPAURASAVE");
        WorldTestClient first = await host.ConnectAsync();
        await first.AuthenticateAsync("REPAURASAVE", key);
        await first.CreateCharacterAsync("Repaurasave");
        await first.LoginAsync(1);
        await host.OnWorldAsync(() =>
        {
            var spells = host.WorldServices.GetRequiredService<SpellFeature>().System;
            var reputation = host.WorldServices.GetRequiredService<ReputationFeature>().Service;
            Player player = host.World.FindOnlinePlayer("Repaurasave")!;
            InstallSpells(spells);
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, General, SpellCastTargets.ForSelf(), triggered: true));
            reputation.RewardQuest(player, (int)player.Level, [new(Faction, 100)]);
        });
        await host.WorldServices.GetRequiredService<ReputationFeature>().FlushAsync();
        Assert.Equal(110, store.Row(1, Faction)!.Standing);
        await first.DisposeAsync();
        await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "reputation player to log out");
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("REPAURASAVE", key);
        await again.LoginAsync(1);
        Assert.Equal(110, await host.PlayerStateAsync("Repaurasave", player =>
            host.WorldServices.GetRequiredService<ReputationFeature>().Service.GetReputation(player, Faction)));
    }
}

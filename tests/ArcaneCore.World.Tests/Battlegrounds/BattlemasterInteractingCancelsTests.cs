using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// CMSG_BATTLEMASTER_HELLO ends Feign Death (AURA_INTERRUPT_INTERACTING_CANCELS) only when the list is going out: vmangos
/// HandleBattlemasterHelloOpcode (BattleGroundHandler.cpp:54-68) returns for a battlemaster without a battleground (:54-55) and for a
/// player below the bracket (:57-62, a notification) before it cancels (:66-67) and sends the list.
/// </summary>
public sealed class BattlemasterInteractingCancelsTests
{
    private const uint MappedEntry = 900730;
    private const uint UnmappedEntry = 900731;
    private const uint MappedSpawn = 900740;
    private const uint UnmappedSpawn = 900741;
    private const uint FeignSpell = 930790;
    private static readonly ObjectGuid MappedGuid = ObjectGuid.WithEntry(HighGuid.Unit, MappedEntry, MappedSpawn);
    private static readonly ObjectGuid UnmappedGuid = ObjectGuid.WithEntry(HighGuid.Unit, UnmappedEntry, UnmappedSpawn);

    private static WorldTestHost StartHost() => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<ICreatureDataStore>(new Creatures());
        services.AddSingleton<IBattlegroundContentStore>(new Battlegrounds());
        services.AddSingleton<IGraveyardDataStore>(new Graveyards());
        services.AddSingleton(new FactionTemplateCatalog([new(1, 1, 0, 1, 0, 0), new(900732, 0, 0, 8, 0, 0)]));
    });

    private static SpellSystem SpellsOf(WorldTestHost host) => host.WorldServices.GetRequiredService<SpellFeature>().System;

    private static async Task<WorldTestClient> FeigningPlayerAsync(WorldTestHost host, string account, string name, byte level)
    {
        WorldTestClient client = await host.EnterWorldAsync(account, name);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(MappedGuid)
            && host.World.FindOnlinePlayer(name)!.VisibleObjects.Contains(UnmappedGuid), "the battlemasters become visible");
        await client.CollectAsync(TimeSpan.FromMilliseconds(200));
        await host.OnWorldAsync(() =>
        {
            SpellSystem spells = SpellsOf(host);
            var feign = new SpellInfo
            {
                Id = FeignSpell, Name = "Synthetic Feign Death", RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Duration = new SpellDuration(-1, 0, -1),
                AuraInterruptFlags = (SpellAuraInterruptFlags)0x3C3C,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.FeignDeath, TargetA = SpellImplicitTarget.UnitCaster }, new(), new()],
            };
            spells.Store = new SpellStore([.. spells.Store.All.Where(s => s.Id != FeignSpell), feign], [], []);
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Level = level;
            Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, FeignSpell, SpellCastTargets.ForSelf(), triggered: true));
            Assert.True(spells.IsFeigningDeath(player), "setup: feigning before the hello");
        });
        await client.CollectAsync(TimeSpan.FromMilliseconds(200));
        return client;
    }

    private static Task<bool> FeigningAsync(WorldTestHost host, string name)
        => host.OnWorldAsync(() => SpellsOf(host).IsFeigningDeath(host.World.FindOnlinePlayer(name)!));

    [Fact]
    public async Task UnderLevelPlayer_KeepsFeignDeath_AndGetsTheLevelNotification()
    {
        await using WorldTestHost host = StartHost();
        await using WorldTestClient client = await FeigningPlayerAsync(host, "BMUNDER", "Bmunder", level: 40);

        await client.SendAsync(WorldOpcode.CmsgBattlemasterHello, BitConverter.GetBytes(MappedGuid.Value));

        await client.ReadUntilAsync(WorldOpcode.SmsgNotification);
        Assert.True(await FeigningAsync(host, "Bmunder"), "a refused battlemaster list must not end Feign Death (BattleGroundHandler.cpp:57-62 returns before :66)");
    }

    [Fact]
    public async Task BattlemasterWithoutABattleground_KeepsFeignDeath()
    {
        await using WorldTestHost host = StartHost();
        await using WorldTestClient client = await FeigningPlayerAsync(host, "BMNOBG", "Bmnobg", level: 60);

        await client.SendAsync(WorldOpcode.CmsgBattlemasterHello, BitConverter.GetBytes(UnmappedGuid.Value));
        await client.CollectAsync(TimeSpan.FromMilliseconds(300));

        Assert.True(await FeigningAsync(host, "Bmnobg"), "a battlemaster with no battleground returns before the cancel (BattleGroundHandler.cpp:54-55)");
    }

    [Fact]
    public async Task QualifiedPlayer_LosesFeignDeath_AndGetsTheList()
    {
        await using WorldTestHost host = StartHost();
        await using WorldTestClient client = await FeigningPlayerAsync(host, "BMOK", "Bmok", level: 60);

        await client.SendAsync(WorldOpcode.CmsgBattlemasterHello, BitConverter.GetBytes(MappedGuid.Value));

        await client.ReadUntilAsync(WorldOpcode.SmsgBattlefieldList);
        Assert.False(await FeigningAsync(host, "Bmok"), "the list goes out and Feign Death ends (BattleGroundHandler.cpp:66-68)");
    }

    private sealed class Creatures : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CreatureContent(
            [
                new CreatureTemplate { Entry = MappedEntry, Name = "AV battlemaster", Faction = 900732, DisplayIds = [49], NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster) },
                new CreatureTemplate { Entry = UnmappedEntry, Name = "Unmapped battlemaster", Faction = 900732, DisplayIds = [49], NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.BattleMaster) },
            ],
            [
                new CreatureSpawn { Guid = MappedSpawn, Entry = MappedEntry, MapId = 0, X = -8948.95f, Y = -132.49f, Z = 83.53f },
                new CreatureSpawn { Guid = UnmappedSpawn, Entry = UnmappedEntry, MapId = 0, X = -8947.95f, Y = -132.49f, Z = 83.53f },
            ],
            [], [], []));
    }

    private sealed class Graveyards : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [new WorldSafeLoc(611, 30, 100f, 0f, 50f, 0f, "AV Alliance start"), new WorldSafeLoc(610, 30, -100f, 0f, 50f, 0f, "AV Horde start")],
            []));
    }

    private sealed class Battlegrounds : IBattlegroundContentStore
    {
        public Task<BattlegroundContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BattlegroundContent(
            [new BattlegroundTemplateRecord(1, 1, 40, 51, 60, 611, 610, 100, 0)],
            [], [],
            [new BattlemasterRecord(MappedEntry, 1)]));
    }
}

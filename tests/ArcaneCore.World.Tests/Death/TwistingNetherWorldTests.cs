using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

public sealed class TwistingNetherWorldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulDeathProcResurrectsThroughEmptyRequestAndSavesLife(bool ghost)
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using WorldTestClient client = await host.EnterWorldAsync("NETHER", "Nether");
        ulong guid = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Nether")!;
            SpellFeature feature = Install(host);
            player.MaxHealth = 1000;
            Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, 23701, SpellCastTargets.ForSelf(), triggered: true));
            player.Map!.Combat.Kill(null, player);
            Assert.Equal(23700u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
            if (ghost) Assert.True(player.Map!.Combat.RepopPlayer(player));
            return player.Guid.Value;
        });
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgSelfRes, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.True(await host.PlayerStateAsync("Nether", p => p.IsAlive));
        Assert.Equal(0u, await host.PlayerStateAsync("Nether", p => p.GetUInt32(UpdateFields.PlayerSelfResSpell)));
        await WorldTestHost.WaitForAsync(() => host.Characters.Life((int)guid) is { Health: 250, IsGhost: false, Corpse: null }, "Nether revival snapshot");
    }

    [Fact]
    public async Task ReleasedGhostRelogDoesNotRestoreOrRerollThePendingOffer()
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using WorldTestClient client = await host.EnterWorldAsync("NETHRELOG", "Nethrelog");
        int rolls = 0;
        ulong guid = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Nethrelog")!;
            SpellFeature feature = Install(host);
            feature.System.Random = new ProcRandom(() => rolls++);
            Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, 23701, SpellCastTargets.ForSelf(), triggered: true));
            player.Map!.Combat.Kill(null, player);
            Assert.Equal(23700u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            return player.Guid.Value;
        });
        await client.CollectAsync();
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await WorldTestHost.WaitForAsync(() => host.Characters.Life((int)guid) is { IsGhost: true, Corpse: not null }, "stored ghost before relog");
        Assert.True(host.Characters.Life((int)guid)!.IsGhost);
        await client.LoginAsync(guid);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Nethrelog")!;
            Assert.False(player.IsAlive);
            Assert.NotNull(player.Combat.Corpse);
            Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
            Assert.Equal(1, rolls);
        });
        await client.SendAsync(WorldOpcode.CmsgSelfRes, []);
        await client.CollectAsync();
        Assert.False(await host.PlayerStateAsync("Nethrelog", p => p.IsAlive));
    }

    private static SpellFeature Install(WorldTestHost host)
    {
        SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
        feature.System.Random = new ProcRandom();
        feature.System.Store = new SpellStore([.. feature.System.Store.All,
            new SpellInfo
            {
                Id = 23701, Name = "Synthetic Twisting Nether", Attributes = SpellAttributes.Passive,
                RangeIndex = SpellConstants.RangeIndexSelfOnly, Duration = new SpellDuration(-1, 0, -1),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy,
                    TargetA = SpellImplicitTarget.UnitCaster }],
            },
            new SpellInfo
            {
                Id = 23700, Name = "Synthetic Nether Resurrection", Attributes = SpellAttributes.AllowCastWhileDead,
                AttributesEx2 = SpellAttributesEx2.AllowDeadTarget, RangeIndex = SpellConstants.RangeIndexSelfOnly,
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.SelfResurrect, BasePoints = 24, BaseDice = 1,
                    DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
        return feature;
    }

    private sealed class ProcRandom(Action? rolled = null) : Random(1)
    {
        public override int Next(int maxValue)
        {
            if (maxValue != 100) return base.Next(maxValue);
            rolled?.Invoke();
            return 0;
        }
    }
}

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

public sealed class SoulstoneRequestWorldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySelfResPacketUsesSoulstoneWithoutLearningInternalSpell_AndSavesRevival(bool ghost)
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using WorldTestClient client = await host.EnterWorldAsync("SOULREQ", "Soulreq");
        ulong guid = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Soulreq")!;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All,
                new SpellInfo
                {
                    Id = 20707, Name = "Synthetic Soulstone", SpellVisual = 99, SpellIconId = 92,
                    RangeIndex = SpellConstants.RangeIndexSelfOnly, Duration = new SpellDuration(30_000, 0, 30_000),
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, AuraType = AuraType.Dummy,
                        TargetA = SpellImplicitTarget.UnitCaster }],
                },
                new SpellInfo
                {
                    Id = 3026, Name = "Synthetic Soulstone Resurrection", Attributes = SpellAttributes.AllowCastWhileDead,
                    AttributesEx2 = SpellAttributesEx2.AllowDeadTarget, RangeIndex = SpellConstants.RangeIndexSelfOnly,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.SelfResurrect, BasePoints = 24, BaseDice = 1,
                        DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }],
                }], [], []);
            player.MaxHealth = 1000;
            Assert.Equal(SpellCastResult.CastOk, feature.System.CastSpell(player, 20707, SpellCastTargets.ForSelf(), triggered: true));
            player.Map!.Combat.Kill(null, player);
            Assert.Equal(3026u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
            Assert.Empty(feature.System.GetAuras(player));
            Assert.False(feature.Spellbook.HasSpell(player, 3026));
            if (ghost) Assert.True(player.Map!.Combat.RepopPlayer(player));
            return player.Guid.Value;
        });
        await client.CollectAsync();

        // A spell ID is not part of the vanilla request; extra bytes must not select a spell.
        await client.SendAsync(WorldOpcode.CmsgSelfRes, BitConverter.GetBytes(3026u));
        await client.CollectAsync();
        Assert.Equal(3026u, await host.PlayerStateAsync("Soulreq", p => p.GetUInt32(UpdateFields.PlayerSelfResSpell)));
        Assert.False(await host.PlayerStateAsync("Soulreq", p => p.IsAlive));

        await client.SendAsync(WorldOpcode.CmsgSelfRes, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);

        Assert.True(await host.PlayerStateAsync("Soulreq", p => p.IsAlive));
        Assert.Equal(0u, await host.PlayerStateAsync("Soulreq", p => p.GetUInt32(UpdateFields.PlayerSelfResSpell)));
        await WorldTestHost.WaitForAsync(() => host.Characters.Life((int)guid) is { Health: 250, IsGhost: false, Corpse: null }, "Soulstone revival snapshot");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Soulreq")!.Health = 123);
        await client.SendAsync(WorldOpcode.CmsgSelfRes, []);
        await client.CollectAsync();
        Assert.InRange(await host.PlayerStateAsync("Soulreq", p => p.Health), 123u, 249u);
        Assert.Equal(250u, host.Characters.Life((int)guid)!.Health);
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await client.LoginAsync(guid);
        Assert.True(await host.PlayerStateAsync("Soulreq", p => p.IsAlive));
        Assert.Equal(0u, await host.PlayerStateAsync("Soulreq", p => p.GetUInt32(UpdateFields.PlayerSelfResSpell)));
        Assert.Null(await host.PlayerStateAsync("Soulreq", p => p.Combat.Corpse));
    }
}

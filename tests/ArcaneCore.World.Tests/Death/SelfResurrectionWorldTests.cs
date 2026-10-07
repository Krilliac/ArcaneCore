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

public sealed class SelfResurrectionWorldTests
{
    private const uint SpellId = 991094;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownSelfResurrection_NormalClientCast_RestoresAndSavesLifeAcrossRelog(bool ghost)
    {
        await using WorldTestHost host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using WorldTestClient client = await host.EnterWorldAsync("SELFRES", "Selfres");
        ulong guid = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Selfres")!;
            SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
            {
                Id = SpellId, Name = "Test Self Resurrection", Attributes = SpellAttributes.AllowCastWhileDead,
                AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
                RangeIndex = SpellConstants.RangeIndexSelfOnly, Range = new SpellRange(0, 0),
                Effects = [new SpellEffectInfo { Effect = SpellEffectName.SelfResurrect, BasePoints = 24, BaseDice = 1,
                    DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }],
            }], [], []);
            feature.Spellbook.LearnSpell(player, SpellId);
            player.MaxHealth = 1000;
            player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 800);
            player.Map!.Combat.KillPlayer(player);
            if (ghost) Assert.True(player.Map!.Combat.RepopPlayer(player));
            return player.Guid.Value;
        });
        await client.CollectAsync();
        var writer = new PacketWriter();
        writer.WriteUInt32(SpellId);
        SpellCastTargets.ForSelf().Write(writer);

        await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);

        Assert.True(await host.PlayerStateAsync("Selfres", p => p.IsAlive));
        Assert.Null(await host.PlayerStateAsync("Selfres", p => p.Combat.Corpse));
        await WorldTestHost.WaitForAsync(() => host.Characters.Life((int)guid) is { Health: 250, IsGhost: false, Corpse: null }, "self resurrection save");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Selfres")!.Health = 123);
        await client.SendAsync(WorldOpcode.CmsgCastSpell, writer.ToArray());
        await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);
        Assert.InRange(await host.PlayerStateAsync("Selfres", p => p.Health), 123u, 249u);
        Assert.Equal(250u, host.Characters.Life((int)guid)!.Health);
        await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
        await client.LoginAsync(guid);
        Assert.True(await host.PlayerStateAsync("Selfres", p => p.IsAlive));
        Assert.False(await host.PlayerStateAsync("Selfres", p => p.Flags.HasFlag(PlayerFlags.Ghost)));
        Assert.Null(await host.PlayerStateAsync("Selfres", p => p.Combat.Corpse));
    }
}

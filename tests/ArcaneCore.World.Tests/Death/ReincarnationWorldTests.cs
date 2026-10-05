using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

public sealed class ReincarnationWorldTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySelfResRequestConsumesAndSavesAnkh_AndRestoresCooldownAcrossRelog(bool ghost)
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = 17030, Name = "Synthetic Ankh", Class = 5, Stackable = 20 });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, 17030, 2));
        WorldTestHost host;
        using (content.Use()) host = WorldTestHost.Start(configure: o => o.InstantLogoutSecurity = AccountSecurity.Player);
        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("REINC", "Reinc");
            ulong guid = await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Reinc")!;
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([.. feature.System.Store.All, Effect()], [], []);
                feature.Spellbook.LearnSpell(player, 20608);
                player.MaxHealth = 1000;
                Assert.Equal(2u, player.Inventory.GetItemCount(17030));
                player.Map!.Combat.Kill(null, player);
                Assert.Equal(21169u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
                if (ghost) Assert.True(player.Map!.Combat.RepopPlayer(player));
                return player.Guid.Value;
            });
            await client.CollectAsync();
            await client.SendAsync(WorldOpcode.CmsgSelfRes, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgCastResult);

            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Reinc")!;
                Assert.True(player.IsAlive);
                Assert.Equal(1u, player.Inventory.GetItemCount(17030));
                Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
                Assert.False(host.WorldServices.GetRequiredService<SpellFeature>().System.IsSpellReady(player, Effect()));
            });
            await WorldTestHost.WaitForAsync(() => host.Characters.Life((int)guid) is { Health: 250, IsGhost: false }, "Reincarnation life snapshot");
            await WorldTestHost.WaitForAsync(() => content.Items.Get((int)guid).Sum(r => r.Item.Entry == 17030 ? r.Item.Count : 0) == 1, "consumed Ankh snapshot");
            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await client.LoginAsync(guid);
            await host.OnWorldAsync(() =>
            {
                Player player = host.World.FindOnlinePlayer("Reinc")!;
                Assert.True(player.IsAlive);
                Assert.Equal(1u, player.Inventory.GetItemCount(17030));
                Assert.False(host.WorldServices.GetRequiredService<SpellFeature>().System.IsSpellReady(player, Effect()));
                player.Map!.Combat.Kill(null, player);
                Assert.Equal(0u, player.GetUInt32(UpdateFields.PlayerSelfResSpell));
            });
        }
    }

    private static SpellInfo Effect() => new()
    {
        Id = 21169, Name = "Synthetic Reincarnation", Attributes = SpellAttributes.AllowCastWhileDead,
        AttributesEx2 = SpellAttributesEx2.AllowDeadTarget, RangeIndex = SpellConstants.RangeIndexSelfOnly,
        Reagents = [new(17030, 1)], RecoveryTime = 3_600_000,
        Effects = [new SpellEffectInfo { Effect = SpellEffectName.SelfResurrect, BasePoints = 24, BaseDice = 1,
            DieSides = 1, TargetA = SpellImplicitTarget.UnitCaster }],
    };
}

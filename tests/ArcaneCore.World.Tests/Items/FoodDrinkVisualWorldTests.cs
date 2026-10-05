using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

public sealed class FoodDrinkVisualWorldTests
{
    [Fact]
    public async Task CmsgUseItem_FoodEmoteAndHeartbeatVisualUseExactPayload()
    {
        const uint item = 993010;
        const uint spell = 993011;
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = item, Class = (uint)ItemClass.Consumable, Stackable = 1,
            Spells = [new ItemSpell(spell, 0, 1, 0, 0, 0, 0)],
        });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, item, 1));
        WorldTestHost host;
        using (content.Use()) host = WorldTestHost.Start();
        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("FOODVIS", "Foodvis"))
        {
            await host.OnWorldAsync(() =>
            {
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                feature.System.Store = new SpellStore([.. feature.System.Store.All, new SpellInfo
                {
                    Id = spell, SpellFamilyName = 0, Duration = new SpellDuration(20_000, 0, 20_000),
                    Attributes = SpellAttributes.AllowWhileSitting,
                    AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 19,
                        AuraType = AuraType.ModRegen, TargetA = SpellImplicitTarget.UnitCaster }],
                }], [], []);
                Player player = host.World.FindOnlinePlayer("Foodvis")!;
                player.SetStandState(StandState.Sit);
            });
            await client.CollectAsync();
            await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0]);
            byte[] emote = await client.ReadUntilAsync(WorldOpcode.SmsgEmote);
            ulong guid = await host.PlayerStateAsync("Foodvis", p => p.Guid.Value);
            Assert.Equal(12, emote.Length);
            Assert.Equal(7u, BitConverter.ToUInt32(emote, 0));
            Assert.Equal(guid, BitConverter.ToUInt64(emote, 4));
            byte[] visual = await client.ReadUntilAsync(WorldOpcode.SmsgPlaySpellVisual);
            Assert.Equal(12, visual.Length);
            Assert.Equal(guid, BitConverter.ToUInt64(visual, 0));
            Assert.Equal(406u, BitConverter.ToUInt32(visual, 8));
            await client.SendAsync(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u));
            await host.WaitForWorldAsync(() => !host.WorldServices.GetRequiredService<SpellFeature>().System
                .HasAura(host.World.FindOnlinePlayer("Foodvis")!, spell), "standing removes visual holder");
        }
    }
}

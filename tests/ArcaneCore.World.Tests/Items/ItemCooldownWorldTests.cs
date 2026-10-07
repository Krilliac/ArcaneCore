using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

public sealed class ItemCooldownWorldTests
{
    [Fact]
    public async Task ItemUse_RelogInitialSpellsPreservesItemAndEffectiveCategory()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = 6948, Class = 0, Stackable = 1,
            Spells = [new ItemSpell(Heal, 0, 3, 0, 50, 77, 120_000)] });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, 6948, 1));
        WorldTestHost host;
        using (content.Use()) host = WorldTestHost.Start(configure: options => options.LogoutDelayMs = 20);
        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("ITEMCD", "Itemcd"))
        {
            Player player = await host.PlayerAsync("Itemcd");
            await host.OnWorldAsync(() => player.Health = Math.Max(1u, player.Health - 10));
            await client.CollectAsync();
            await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0]);
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<ArcaneCore.World.Spells.SpellFeature>()
                .System.GetActiveCooldowns(player).Any(c => c.SpellId == Heal && c.CooldownMs == 0 && c.CategoryCooldownMs > 0), "spell cooldown expiry");
            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 0, "logout");
            Account account = (await host.Accounts.FindByUsernameAsync("ITEMCD"))!;
            CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            await client.LoginAsync((ulong)character.Id);
            var reader = new PacketReader(client.LoginPacket(WorldOpcode.SmsgInitialSpells));
            reader.ReadByte();
            ushort known = reader.ReadUInt16();
            reader.Skip(known * 4);
            ushort cooldowns = reader.ReadUInt16();
            Assert.Equal(1, cooldowns);
            Assert.Equal((ushort)Heal, reader.ReadUInt16());
            Assert.Equal((ushort)6948, reader.ReadUInt16());
            Assert.Equal((ushort)77, reader.ReadUInt16());
            Assert.Equal(0u, reader.ReadUInt32());
            Assert.True(reader.ReadUInt32() > 0);
        }
    }
}

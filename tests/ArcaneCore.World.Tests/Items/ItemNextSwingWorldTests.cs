using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

public sealed class ItemNextSwingWorldTests
{
    private const uint Next = 49101;

    [Fact]
    public async Task UseItem_NextSwingCarriesCastItemGuidThroughRealCombatSwing()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate { Entry = 6948, Class = 0, Stackable = 1,
            Spells = [new ItemSpell(Next, 0, 3, 0, 5000, 77, 10000)] });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, 6948, 1));
        SpellContent baseContent = SpellTestServices.Content();
        var row = new SpellTemplateRow { Id = Next, SpellName = "Item Next", RangeIndex = 1, Effect1 = 3,
            EffectBasePoints1 = -1, EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1,
            Attributes = (uint)SpellAttributesCombat.OnNextSwing, StartRecoveryCategory = 133, StartRecoveryTime = 1500 };
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start(configure: options => options.LogoutDelayMs = 20,
                configureServices: services => services.AddSingleton<ISpellContentStore>(new Store(baseContent with { Spells = [.. baseContent.Spells, row] })));
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("NEXTITEM", "Nextitem"))
        await using (WorldTestClient target = await host.EnterWorldAsync("NEXTTARGET", "Nexttarget"))
        {
            await host.OnWorldAsync(() =>
            {
                var hooks = new HostileHooks();
                CombatHooks.Register(host.World, hooks);
                host.World.GetMap(0).Combat.Hooks = hooks;
            });
            ulong targetGuid = await host.PlayerStateAsync("Nexttarget", p => p.Guid.Value);
            await client.CollectAsync();
            await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0]);
            byte[] start = await client.ReadUntilAsync(WorldOpcode.SmsgSpellStart);
            ulong itemGuid = await host.PlayerStateAsync("Nextitem", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.Guid.Value);
            var startReader = new PacketReader(start);
            Assert.Equal(itemGuid, startReader.ReadPackedGuid());
            await client.SendAsync(WorldOpcode.CmsgAttackswing, BitConverter.GetBytes(targetGuid));
            var goReader = new PacketReader(await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo));
            Assert.Equal(itemGuid, goReader.ReadPackedGuid());
            Assert.Equal(2, await host.PlayerStateAsync("Nextitem", p => p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges)));
            await client.SendAsync(WorldOpcode.CmsgLogoutRequest, []);
            await client.ReadUntilAsync(WorldOpcode.SmsgLogoutComplete);
            await host.WaitForWorldAsync(() => host.World.OnlinePlayerCount == 1, "item player logout");
            Account account = (await host.Accounts.FindByUsernameAsync("NEXTITEM"))!;
            CharacterRecord character = (await host.Characters.GetByAccountAsync(account.Id)).Single();
            await client.LoginAsync((ulong)character.Id);
            PlayerState restored = await host.PlayerStateAsync("Nextitem", p => new PlayerState(
                p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.GetInt32(UpdateFields.ItemFieldSpellCharges),
                p.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.ItemStart)!.Guid.Value));
            Assert.Equal(2, restored.Charges);
            Assert.Equal(itemGuid, restored.Guid);
            var initial = new PacketReader(client.LoginPacket(WorldOpcode.SmsgInitialSpells));
            initial.ReadByte();
            initial.Skip(initial.ReadUInt16() * 4);
            Assert.Equal(1, initial.ReadUInt16());
            Assert.Equal((ushort)Next, initial.ReadUInt16());
            Assert.Equal((ushort)6948, initial.ReadUInt16());
            Assert.Equal((ushort)77, initial.ReadUInt16());
            Assert.True(initial.ReadUInt32() > 0);
            Assert.True(initial.ReadUInt32() > 0);
        }
    }

    private readonly record struct PlayerState(int Charges, ulong Guid);

    private sealed class HostileHooks : CombatHooks
    {
        public override bool IsFriendly(ArcaneCore.Game.Entities.Unit a, ArcaneCore.Game.Entities.Unit b) => false;
        public override bool CanAttack(ArcaneCore.Game.Entities.Unit a, ArcaneCore.Game.Entities.Unit b)
            => !ReferenceEquals(a, b) && b.IsInWorld && ReferenceEquals(a.Map, b.Map);
    }

    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

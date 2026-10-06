using ArcaneCore.Game.Items;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.Items;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Character;

public sealed class RepairCommandTests
{
    [Fact]
    public void RepairItems_RequiresGameMasterRetailLevel()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("repairitems", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("repairitems", AccountSecurity.GameMaster));
    }

    [Fact]
    public async Task RepairItems_RepairsDamagedInventoryAndLeavesOtherItemStateAlone()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("REPAIRGM", "Repairgm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".additem 25 1");
        await gm.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);

        (uint beforeHealth, uint beforeMoney, uint maxDurability) = await host.PlayerStateAsync("Repairgm", player =>
        {
            Item item = Assert.Single(player.Inventory.AllItems, value => value.Entry == 25);
            uint max = item.MaxDurability;
            item.Durability = Math.Max(1u, max / 2);
            return (player.Health, player.Money, max);
        });
        Assert.True(maxDurability > 0);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".repairitems");
        Assert.Contains("Repaired", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
        (uint health, uint money, uint durability) = await host.PlayerStateAsync("Repairgm", player =>
        {
            Item item = Assert.Single(player.Inventory.AllItems, value => value.Entry == 25);
            return (player.Health, player.Money, item.Durability);
        });
        Assert.Equal(beforeHealth, health);
        Assert.Equal(beforeMoney, money);
        Assert.Equal(maxDurability, durability);
    }

    [Fact]
    public async Task RepairItems_IsUnavailableToPlayers()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient player = await host.EnterWorldAsync("REPAIRPL", "Repairpl");
        await player.CollectAsync();
        await player.SendChatAsync(ChatType.Say, Language.Common, ".repairitems");
        Assert.Equal("This command is not available to you.", (await player.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task RepairItems_RepairsOnlyTheRequestedItemGuid_AndBadArgumentsStayReadOnly()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("REPAIRGUID", "Repairguid", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".additem 25 1");
        await gm.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
        uint guid = await host.PlayerStateAsync("Repairguid", player =>
        {
            Item item = Assert.Single(player.Inventory.AllItems, value => value.Entry == 25);
            item.Durability = Math.Max(1u, item.MaxDurability / 2);
            return checked((uint)item.Guid.Low);
        });

        await gm.SendChatAsync(ChatType.Say, Language.Common, $".repairitems {guid}");
        Assert.Contains("Repaired", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
        Assert.Equal(0u, await host.PlayerStateAsync("Repairguid", player =>
            Assert.Single(player.Inventory.AllItems, value => value.Entry == 25).MaxDurability
            - Assert.Single(player.Inventory.AllItems, value => value.Entry == 25).Durability));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".repairitems invalid");
        Assert.StartsWith("Syntax:", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepairItems_RefusesHigherSecurityTargetAndPendingSettlement()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("REPAIRLOW", "Repairlow", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("REPAIRHIGH", "Repairhigh", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".additem 25 1");
        await gm.ReadUntilAsync(WorldOpcode.SmsgItemPushResult);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Repairlow")!.Selection = host.World.FindOnlinePlayer("Repairhigh")!.Guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".repairitems");
        Assert.Contains("security", (await gm.ReadChatAsync()).Text, StringComparison.OrdinalIgnoreCase);

        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Repairlow")!;
            Item item = Assert.Single(player.Inventory.AllItems, value => value.Entry == 25);
            item.Durability = Math.Max(1u, item.MaxDurability / 2);
            Assert.True(player.BeginQuestSettlement(Guid.NewGuid()));
            player.Selection = default;
            host.World.FindOnlinePlayer("Repairhigh")!.Selection = player.Guid;
        });
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".repairitems");
        Assert.Contains("settling", (await admin.ReadChatAsync()).Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(await host.PlayerStateAsync("Repairlow", player =>
            Assert.Single(player.Inventory.AllItems, value => value.Entry == 25).Durability
            < Assert.Single(player.Inventory.AllItems, value => value.Entry == 25).MaxDurability));
    }

    private static WorldTestHost Start()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 25, Class = 2, SubClass = 7, Name = "Worn Shortsword", DisplayId = 1542,
            Quality = 1, InventoryType = 21, Delay = 1900, MaxDurability = 20,
            Damages = [new ItemDamage(1, 3, 0)],
        });
        using (content.Use())
        {
            return WorldTestHost.Start();
        }
    }
}

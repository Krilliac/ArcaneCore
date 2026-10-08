using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Scenarios.ScenarioTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Scenarios;

/// <summary>
/// A gift across two sessions: A wraps a sword (CMSG_WRAP_ITEM, vmangos ItemHandler.cpp:1049-1139) and mails it, the letter's escrow keeps the
/// wrapped state in the characters database (item_instance.gift_entry/gift_flags), B takes it and opens it (CMSG_OPEN_ITEM, vmangos
/// SpellHandler.cpp:200-227) and holds the sword again, with its durability.
/// </summary>
public sealed class GiftWrapScenarioTests
{
    public const uint Paper = 990_200;
    public const uint GiftBox = 990_201;
    public const uint Sword = 990_202;

    [Fact]
    public async Task AWrappedGift_IsMailed_AndTheReceiverOpensIt()
    {
        await using ScenarioTestWorld world = await ScenarioTestWorld.StartAsync(services =>
        {
            var items = new InMemoryItemTemplateSource();
            items.Templates.AddRange(
            [
                new ItemTemplate { Entry = LinenCloth, Name = "Linen Cloth", Class = 7, SubClass = 0, Quality = 1, Stackable = 20, SellPrice = 13 },
                new ItemTemplate { Entry = Paper, Name = "Wrapping Paper", Class = 0, Stackable = 20, Flags = (uint)ItemTemplateFlags.Wrapper, WrappedGift = GiftBox },
                new ItemTemplate { Entry = GiftBox, Name = "Wrapped Gift", Class = 0, Stackable = 1, Flags = (uint)ItemTemplateFlags.Wrapper },
                new ItemTemplate { Entry = Sword, Name = "Gift Sword", Class = 2, SubClass = 7, InventoryType = 13, Delay = 2000, MaxDurability = 50, Damages = [new ItemDamage(1, 3, 0)] },
            ]);
            services.AddSingleton<IItemTemplateSource>(items);
        });
        await world.RunPassingAsync(new GiftWrapScenario(world.Time));
    }
}

internal sealed class GiftWrapScenario(ScenarioTimeProvider time) : IPlayerbotScenario
{
    public string Name => "gift-wrap";

    public string Description => "A wraps a sword and mails it to B, B opens the gift";

    public async Task RunAsync(ScenarioContext context)
    {
        (ScenarioBot a, ScenarioBot b) = await PlayerbotScenarioCatalog.PairAsync(context);
        ObjectGuid sword = await context.StepAsync("A wraps a sword", async () =>
        {
            await context.GiveMoneyAsync(a, 100);
            ObjectGuid paper = await context.GiveItemAsync(a, GiftWrapScenarioTests.Paper, 2);
            ObjectGuid item = await context.GiveItemAsync(a, GiftWrapScenarioTests.Sword);
            await a.ReadAsync(p => p.Inventory.GetItemByGuid(item)!.Durability = 33);
            (byte paperBag, byte paperSlot, byte itemBag, byte itemSlot) = await a.ReadAsync(p =>
            {
                Item paperItem = p.Inventory.GetItemByGuid(paper)!;
                Item swordItem = p.Inventory.GetItemByGuid(item)!;
                return (paperItem.BagSlot, paperItem.Slot, swordItem.BagSlot, swordItem.Slot);
            });
            ScenarioContext.Expect(await a.SendAsync(WorldOpcode.CmsgWrapItem, ScenarioItemWire.Wrap(paperBag, paperSlot, itemBag, itemSlot)), "wrap refused");
            await context.WaitUntilAsync("the sword is wrapped", () => a.RequirePlayer().Inventory.GetItemByGuid(item) is { Entry: GiftWrapScenarioTests.GiftBox });
            ScenarioContext.ExpectEqual(GiftWrapScenarioTests.Sword, await a.ReadAsync(p => p.Inventory.GetItemByGuid(item)!.GiftEntry), "gift contents");
            ScenarioContext.ExpectEqual(a.Guid.Value, await a.ReadAsync(p => p.Inventory.GetItemByGuid(item)!.GetUInt64(UpdateFields.ItemFieldGiftcreator)), "gift creator");
            await context.ExpectItemCountAsync(a, GiftWrapScenarioTests.Paper, 1);
            return item;
        });

        uint mailId = await context.StepAsync("A mails the gift to B and the escrow keeps it wrapped", async () =>
        {
            long mark = a.Mark();
            ScenarioContext.Expect(await a.SendMailAsync(Mailbox, b.Name, "a gift", "for you", sword), "send mail refused");
            MailResultView result = await a.WaitForPacketAsync(WorldOpcode.SmsgSendMailResult, ScenarioDecoders.MailResult, since: mark);
            ScenarioContext.ExpectEqual(MailResult.Ok, result.Result, "send mail result");
            await using AsyncServiceScope scope = context.Services.CreateAsyncScope();
            IReadOnlyList<MailRecord> mails = await scope.ServiceProvider.GetRequiredService<IEconomyStore>().GetMailsAsync((int)b.Guid.Low);
            MailRecord letter = mails.Count == 1 ? mails[0] : throw new ScenarioAssertionException($"{mails.Count} letters stored for B");
            ScenarioContext.ExpectEqual(GiftWrapScenarioTests.GiftBox, letter.ItemEntry, "the letter holds the gift");
            IReadOnlyDictionary<uint, ItemInstanceData> escrow = await scope.ServiceProvider.GetRequiredService<IEconomyStore>().GetEscrowItemsAsync([sword.Low]);
            ItemInstanceData stored = escrow.TryGetValue(sword.Low, out ItemInstanceData? row) ? row : throw new ScenarioAssertionException("no escrow row for the gift");
            ScenarioContext.ExpectEqual((GiftWrapScenarioTests.GiftBox, GiftWrapScenarioTests.Sword, (uint)ItemDynFlags.Wrapped, 33u),
                (stored.Entry, stored.GiftEntry, stored.Flags, stored.Durability), "the persisted escrow row");
            return letter.Id;
        });

        await context.StepAsync("B takes the gift from the letter", async () =>
        {
            time.Advance(TimeSpan.FromSeconds(3601)); // letters with items arrive after an hour
            long listed = b.Mark();
            ScenarioContext.Expect(await b.GetMailListAsync(Mailbox), "mail list refused");
            await b.WaitForPacketAsync(WorldOpcode.SmsgMailListResult, ScenarioDecoders.MailListCount, since: listed);
            long taken = b.Mark();
            ScenarioContext.Expect(await b.TakeMailItemAsync(Mailbox, mailId), "take item refused");
            MailResultView result = await b.WaitForPacketAsync(WorldOpcode.SmsgSendMailResult, ScenarioDecoders.MailResult, r => r.Action == MailAction.ItemTaken, taken);
            ScenarioContext.ExpectEqual(MailResult.Ok, result.Result, "take item result");
            await context.ExpectItemCountAsync(b, GiftWrapScenarioTests.GiftBox, 1);
            ScenarioContext.ExpectEqual(GiftWrapScenarioTests.Sword, await b.ReadAsync(p => p.Inventory.GetItemByGuid(sword)!.GiftEntry), "gift contents after the mail");
        });

        await context.StepAsync("B opens the gift and holds the sword", async () =>
        {
            (byte bag, byte slot) = await b.ReadAsync(p => (p.Inventory.GetItemByGuid(sword)!.BagSlot, p.Inventory.GetItemByGuid(sword)!.Slot));
            ScenarioContext.Expect(await b.SendAsync(WorldOpcode.CmsgOpenItem, ScenarioItemWire.Open(bag, slot)), "open refused");
            await context.WaitUntilAsync("the gift is opened", () => b.RequirePlayer().Inventory.GetItemByGuid(sword) is { Entry: GiftWrapScenarioTests.Sword });
            await context.ExpectItemCountAsync(b, GiftWrapScenarioTests.GiftBox, 0);
            ScenarioContext.ExpectEqual((50u, 33u), await b.ReadAsync(p => (p.Inventory.GetItemByGuid(sword)!.MaxDurability, p.Inventory.GetItemByGuid(sword)!.Durability)), "sword durability");
            ScenarioContext.ExpectEqual(0ul, await b.ReadAsync(p => p.Inventory.GetItemByGuid(sword)!.GetUInt64(UpdateFields.ItemFieldGiftcreator)), "gift creator after opening");
        });
    }
}

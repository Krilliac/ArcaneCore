using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.MockClient.Scenarios;
using ArcaneCore.Protocol;
using ArcaneCore.World.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// Expiry, return and delete of letters against vmangos: D:\refs\vmangos\src\game\ObjectMgr.cpp:6995-7044
/// (ReturnOrDeleteOldMails: only letters with an item are returned, COD payments and returned letters are deleted),
/// D:\refs\vmangos\src\game\Handlers\MailHandler.cpp:469-491 (delete refuses only COD letters).
/// Only SQLite is exercised here; the settlement shapes are the existing DeleteMail/DeleteEscrowItem/InsertMail changes.
/// </summary>
public sealed class EconomyMailExpiryParityTests
{
    [Fact]
    public async Task Expired_money_only_letter_is_deleted_not_returned_when_the_vmangos_option_is_on()
    {
        await using Rig rig = await Rig.StartAsync();
        rig.Server.Services.GetRequiredService<EconomyFeature>().Options.ReturnExpiredMoneyOnlyMail = false;
        await SeedAsync(rig, Letter(rig, 6001, money: 10));
        await SweepAsync(rig, () => rig.MailsOfAsync(rig.ReceiverGuid), mails => mails.Count == 0);
        Assert.Empty(await rig.MailsOfAsync(rig.SenderGuid));
    }

    [Fact]
    public async Task Expired_money_only_letter_is_returned_by_default()
    {
        await using Rig rig = await Rig.StartAsync();
        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        await SeedAsync(rig, Letter(rig, 6001, money: 10));
        await SweepAsync(rig, () => rig.MailsOfAsync(rig.SenderGuid), mails => mails.Count == 1);
        MailRecord back = Assert.Single(await rig.MailsOfAsync(rig.SenderGuid));
        Assert.Equal((10u, now, now + (30 * 86400)), (back.Money, back.DeliverTime, back.ExpireTime));
        Assert.True((back.Checked & MailCheckMask.Returned) != 0);
        Assert.Empty(await rig.MailsOfAsync(rig.ReceiverGuid));
    }

    [Fact]
    public async Task Expired_cod_payment_and_system_letters_are_deleted_even_with_the_money_default()
    {
        await using Rig rig = await Rig.StartAsync();
        await SeedAsync(rig,
            Letter(rig, 6001, money: 10) with { Checked = MailCheckMask.CodPayment },
            Letter(rig, 6002, money: 10) with { MessageType = MailMessageType.Auction, SenderId = 2 });
        await SweepAsync(rig, () => rig.MailsOfAsync(rig.ReceiverGuid), mails => mails.Count == 0);
        Assert.Empty(await rig.MailsOfAsync(rig.SenderGuid));
    }

    [Fact]
    public async Task Expired_item_letter_returns_once_then_is_deleted_with_its_item()
    {
        await using Rig rig = await Rig.StartAsync();
        await SeedAsync(rig, Letter(rig, 6001, item: 7001), Letter(rig, 6002, item: 7002) with { Checked = MailCheckMask.Returned });
        await SweepAsync(rig, () => rig.MailsOfAsync(rig.ReceiverGuid), mails => mails.Count == 0);
        MailRecord back = Assert.Single(await rig.MailsOfAsync(rig.SenderGuid));
        Assert.Equal(7001u, back.ItemGuid);
        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(1, await db.Set<ItemInstanceRow>().CountAsync(i => i.Guid == 7001, rig.Token));
        Assert.Equal(0, await db.Set<ItemInstanceRow>().CountAsync(i => i.Guid == 7002, rig.Token)); // exactly-once: gone with its letter
    }

    [Fact]
    public async Task Delete_refuses_letters_with_attachments_by_default_but_removes_emptied_ones()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        await SeedAsync(rig,
            Letter(rig, 6001, money: 10, expired: false),
            Letter(rig, 6002, item: 7003, expired: false),
            Letter(rig, 6003, item: 7004, expired: false) with { Cod = 5 },
            Letter(rig, 6004, expired: false));
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), rig.Token);
        Assert.Equal(4, (await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgMailListResult, rig.Token))[0]);

        foreach (uint id in new uint[] { 6001, 6002, 6003, 6004 })
        {
            await rig.Receiver.SendAsync(WorldOpcode.CmsgMailDelete, ScenarioWire.GuidQuest(Mailbox.Value, id), rig.Token);
            var reader = new PacketReader(await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgSendMailResult, rig.Token));
            Assert.Equal(id, reader.ReadUInt32());
            Assert.Equal((uint)MailAction.Deleted, reader.ReadUInt32());
            Assert.Equal(id == 6004 ? MailResult.Ok : MailResult.InternalError, (MailResult)reader.ReadUInt32());
        }

        Assert.Equal(new uint[] { 6001, 6002, 6003 }, (await rig.MailsOfAsync(rig.ReceiverGuid)).Select(m => m.Id).Order().ToArray());
        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(1, await db.Set<ItemInstanceRow>().CountAsync(i => i.Guid == 7003, rig.Token));
        Assert.Equal(1, await db.Set<ItemInstanceRow>().CountAsync(i => i.Guid == 7004, rig.Token));
    }

    [Fact]
    public async Task Delete_removes_letters_with_attachments_but_refuses_cash_on_delivery_when_the_vmangos_option_is_on()
    {
        await using Rig rig = await Rig.StartAsync(loginReceiver: true);
        rig.Server.Services.GetRequiredService<EconomyFeature>().Options.AllowDeleteWithAttachments = true;
        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        await SeedAsync(rig,
            Letter(rig, 6001, money: 10, expired: false),
            Letter(rig, 6002, item: 7003, expired: false),
            Letter(rig, 6003, item: 7004, expired: false) with { Cod = 5 });
        await rig.Receiver!.SendAsync(WorldOpcode.CmsgGetMailList, ScenarioWire.Guid(Mailbox.Value), rig.Token);
        Assert.Equal(3, (await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgMailListResult, rig.Token))[0]);

        foreach (uint id in new uint[] { 6001, 6002, 6003 })
        {
            await rig.Receiver.SendAsync(WorldOpcode.CmsgMailDelete, ScenarioWire.GuidQuest(Mailbox.Value, id), rig.Token);
            var reader = new PacketReader(await rig.Receiver.ReadUntilAsync(WorldOpcode.SmsgSendMailResult, rig.Token));
            Assert.Equal(id, reader.ReadUInt32());
            Assert.Equal((uint)MailAction.Deleted, reader.ReadUInt32());
            Assert.Equal(id == 6003 ? MailResult.InternalError : MailResult.Ok, (MailResult)reader.ReadUInt32());
        }

        Assert.Equal(6003u, Assert.Single(await rig.MailsOfAsync(rig.ReceiverGuid)).Id);
        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(0, await db.Set<ItemInstanceRow>().CountAsync(i => i.Guid == 7003, rig.Token));
        Assert.Equal(1, await db.Set<ItemInstanceRow>().CountAsync(i => i.Guid == 7004, rig.Token));
        Assert.True(now > 0);
    }

    private static MailRecord Letter(Rig rig, uint id, uint money = 0, uint item = 0, bool expired = true)
    {
        long now = rig.Clock.GetUtcNow().ToUnixTimeSeconds();
        return new MailRecord
        {
            Id = id, MessageType = MailMessageType.Normal, SenderId = checked((uint)rig.SenderGuid), ReceiverId = checked((int)rig.ReceiverGuid),
            Subject = $"letter {id}", Money = money, ItemGuid = item, ItemEntry = item == 0 ? 0u : 117u,
            DeliverTime = now - 1000, ExpireTime = expired ? now - 10 : now + 86400,
        };
    }

    private static async Task SeedAsync(Rig rig, params MailRecord[] letters)
    {
        // Economy commits need a context without tracked state, so the escrow rows are seeded through their own scope.
        await using (AsyncServiceScope itemScope = rig.Server.Services.CreateAsyncScope())
        {
            CharacterDbContext items = itemScope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            foreach (MailRecord letter in letters.Where(l => l.HasItem))
            {
                var row = new ItemInstanceRow { Guid = letter.ItemGuid };
                row.CopyFrom(0, new ItemInstanceData { Guid = letter.ItemGuid, Entry = letter.ItemEntry, Count = 1, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] });
                items.Add(row);
            }

            await items.SaveChangesAsync(rig.Token);
        }

        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.Equal(EconomyCommitResult.Committed, await new EfEconomyStore(db).CommitAsync(
            new EconomyCommitRequest(Guid.NewGuid(), [], [.. letters.Select(l => (EconomyChange)new InsertMail(l, null))]), rig.Token));
    }

    /// <summary>Run the world-thread expiry sweep until <paramref name="done"/> holds for what <paramref name="read"/> returns.</summary>
    private static async Task SweepAsync(Rig rig, Func<Task<IReadOnlyList<MailRecord>>> read, Func<IReadOnlyList<MailRecord>, bool> done)
    {
        EconomyFeature economy = rig.Server.Services.GetRequiredService<EconomyFeature>();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await rig.Server.World.InvokeAsync(() =>
            {
                economy.RunExpirySweep();
                return true;
            }).WaitAsync(rig.Token);
            await economy.DrainAsync().WaitAsync(rig.Token);
            await Task.Delay(50, rig.Token);
            if (done(await read()))
            {
                return;
            }
        }

        Assert.Fail("the expiry sweep did not settle the seeded letters");
    }
}

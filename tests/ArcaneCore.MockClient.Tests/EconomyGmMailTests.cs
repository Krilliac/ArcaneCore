using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>EconomyFeature.SendGmMail, the store side of .send mail/money/items (vmangos MiscCommands.cpp HandleSend*Command).</summary>
public sealed class EconomyGmMailTests
{
    [Fact]
    public async Task AGmLetter_IsAMailNormalFromTheGm_WithGmStationery_ItsMoneyAndANewEscrowedItem()
    {
        await using Rig rig = await Rig.StartAsync();
        EconomyFeature economy = rig.Server.Services.GetRequiredService<EconomyFeature>();
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new GmMailRequest(checked((int)rig.ReceiverGuid), checked((uint)rig.SenderGuid), "Sorry", "For the outage", 2500,
            new ServerMailItem(SyntheticArcaneServer.FixedRewardItem, 1));

        await rig.Server.World.InvokeAsync(() =>
        {
            economy.SendGmMail(request, ok => sent.TrySetResult(ok));
            return true;
        }).WaitAsync(rig.Token);
        Assert.True(await sent.Task.WaitAsync(rig.Token));

        MailRecord mail = Assert.Single(await rig.ReceiverMailAsync());
        Assert.Equal(MailMessageType.Normal, mail.MessageType);
        Assert.Equal(MailStationery.Gm, mail.Stationery);
        Assert.Equal(checked((uint)rig.SenderGuid), mail.SenderId);
        Assert.Equal("Sorry", mail.Subject);
        Assert.Equal(2500u, mail.Money);
        Assert.Equal(SyntheticArcaneServer.FixedRewardItem, mail.ItemEntry);
        Assert.True((mail.Checked & MailCheckMask.HasBody) != 0);

        await using AsyncServiceScope scope = rig.Server.Services.CreateAsyncScope();
        var store = new EfEconomyStore(scope.ServiceProvider.GetRequiredService<CharacterDbContext>());
        Assert.Contains(mail.ItemGuid, (await store.GetEscrowItemsAsync([mail.ItemGuid], rig.Token)).Keys);
        Assert.Equal("For the outage", await store.GetItemTextAsync(mail.ItemTextId, rig.Token));
    }

    [Fact]
    public async Task AGmLetter_WithAnUnknownItem_SendsNothing()
    {
        await using Rig rig = await Rig.StartAsync();
        EconomyFeature economy = rig.Server.Services.GetRequiredService<EconomyFeature>();
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new GmMailRequest(checked((int)rig.ReceiverGuid), 0, "x", "", 0, new ServerMailItem(987654, 1));

        await rig.Server.World.InvokeAsync(() =>
        {
            economy.SendGmMail(request, ok => sent.TrySetResult(ok));
            return true;
        }).WaitAsync(rig.Token);

        Assert.False(await sent.Task.WaitAsync(rig.Token));
        Assert.Empty(await rig.ReceiverMailAsync());
    }
}
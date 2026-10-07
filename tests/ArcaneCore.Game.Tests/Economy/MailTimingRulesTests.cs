using ArcaneCore.Game.Economy;
using ArcaneCore.Kernel.Economy;
using Xunit;

namespace ArcaneCore.Game.Tests.Economy;

/// <summary>
/// Mail timing and limits against vmangos: D:\refs\vmangos\src\game\Handlers\MailHandler.cpp:155-166 (limits),
/// :404-406 (delivery delay), D:\refs\vmangos\src\game\Mail\Mail.cpp:312-326 (expiry from the delivery time),
/// D:\refs\vmangos\src\game\World.cpp:691 (MailDeliveryDelay = 1 hour).
/// </summary>
public sealed class MailTimingRulesTests
{
    private const long Now = 1_000_000;

    [Fact]
    public void CodPaymentLetter_CannotBeReturned_LikeOnExpiry()
    {
        // The seller's COD payment letter: the buyer already holds the item, so the gold never goes back
        // (vmangos ObjectMgr.cpp:6999 deletes it on expiry instead of returning it).
        var payment = new MailRecord
        {
            Id = 8, MessageType = MailMessageType.Normal, SenderId = 7, ReceiverId = 9, Money = 500,
            Checked = MailCheckMask.CodPayment, DeliverTime = Now, ExpireTime = Now + 10,
        };
        Assert.True(MailRules.CanReturn(payment with { Checked = MailCheckMask.None }));
        Assert.False(MailRules.CanReturn(payment));
        Assert.False(MailRules.CanReturn(payment with { Checked = MailCheckMask.CodPayment | MailCheckMask.Read }));
    }

    [Fact]
    public void Defaults_FollowVmangos()
    {
        var o = new EconomyOptions();
        Assert.Equal((3600u, 3u, 100_000_000u, 64, 500, false),
            (o.MailDeliveryDelaySeconds, o.MailReadExpiryDays, o.MailMaxCodCopper, o.MailSubjectMaxLength, o.MailBodyMaxLength, o.MailOversizeAnswersError));
    }

    [Theory]
    [InlineData(false, 0u, 0L, 30L)]    // text only: instant, 30 days
    [InlineData(true, 0u, 3600L, 30L)]  // an item or money: one hour delay, expiry counted from delivery
    [InlineData(true, 50u, 3600L, 3L)]  // cash on delivery: 3 days from delivery
    public void Timing_DelaysOnlyLettersWithContents_AndExpiresFromDelivery(bool contents, uint cod, long delay, long days)
    {
        (long deliver, long expire) = MailRules.SendTiming(Now, contents, cod, new EconomyOptions());
        Assert.Equal(Now + delay, deliver);
        Assert.Equal(deliver + (days * MailRules.SecondsPerDay), expire);
    }

    [Fact]
    public void Timing_HonoursTheConfiguredDelay()
        => Assert.Equal(Now, MailRules.SendTiming(Now, true, 0, new EconomyOptions { MailDeliveryDelaySeconds = 0 }).Deliver);

    [Fact]
    public void AuctionNote_WithoutItemOrMoney_ExpiresAfterOneHour()
    {
        var o = new EconomyOptions();
        Assert.Equal(Now + 3600, MailRules.AuctionLetter(1, 2, 3, "1:0:3", Now, o).ExpireTime);
        Assert.Equal(Now + (30 * MailRules.SecondsPerDay), MailRules.AuctionLetter(1, 2, 3, "1:0:3", Now, o, money: 5).ExpireTime);
        Assert.Equal(Now + (30 * MailRules.SecondsPerDay), MailRules.AuctionLetter(1, 2, 3, "1:0:3", Now, o, itemGuid: 9, itemEntry: 1).ExpireTime);
    }

    [Theory]
    [InlineData(20L, 3L)]  // more than 3 days left: clamped to 3 days
    [InlineData(2L, 2L)]   // already shorter: untouched
    public void ReadExpiry_ClampsToThreeDays(long daysLeft, long expectedDays)
    {
        long expire = Now + (daysLeft * MailRules.SecondsPerDay);
        Assert.Equal(Now + (expectedDays * MailRules.SecondsPerDay), MailRules.ExpireAfterRead(expire, Now, 3));
        Assert.Equal(expire, MailRules.ExpireAfterRead(expire, Now, 0)); // option 0 disables the clamp
    }

    [Fact]
    public void ExpiryReturnsOnlyUnreturnedPlayerLettersWithAnItem()
    {
        var o = new EconomyOptions();
        var item = new MailRecord { MessageType = MailMessageType.Normal, SenderId = 7, ItemGuid = 5, ItemEntry = 1 };
        Assert.True(MailRules.ReturnsOnExpiry(item, o));
        Assert.False(MailRules.ReturnsOnExpiry(item with { Checked = MailCheckMask.Returned }, o));
        Assert.False(MailRules.ReturnsOnExpiry(item with { Checked = MailCheckMask.CodPayment }, o));
        Assert.False(MailRules.ReturnsOnExpiry(item with { MessageType = MailMessageType.Auction }, o));
        var gold = new MailRecord { MessageType = MailMessageType.Normal, SenderId = 7, Money = 9 };
        // Retail default: a money-only letter is returned too; the vmangos delete is the opt-in deviation.
        Assert.True(o.ReturnExpiredMoneyOnlyMail);
        Assert.True(MailRules.ReturnsOnExpiry(gold, o));
        Assert.False(MailRules.ReturnsOnExpiry(gold, new EconomyOptions { ReturnExpiredMoneyOnlyMail = false }));
        Assert.False(MailRules.ReturnsOnExpiry(gold with { Checked = MailCheckMask.CodPayment }, o));
        Assert.False(o.AllowDeleteWithAttachments);
    }

    [Fact]
    public void MailList_IsCappedAt254Letters()
    {
        List<MailView> mails = [.. Enumerable.Range(1, 300).Select(i => new MailView(
            new MailRecord { Id = (uint)i, MessageType = MailMessageType.Auction, Subject = "s" }, null))];
        Assert.Equal(254, EconomyPackets.MailList(mails, Now, _ => null)[0]);
    }

    [Fact]
    public void ReturnedLetter_ExpiresFromItsDeliveryTime()
    {
        var mail = new MailRecord { Id = 1, MessageType = MailMessageType.Normal, SenderId = 7, ReceiverId = 9, ItemGuid = 5, ItemEntry = 1 };
        MailRecord returned = MailRules.Returned(mail, 2, Now, new EconomyOptions(), deliverDelaySeconds: 3600);
        Assert.Equal((Now + 3600, Now + 3600 + (30 * MailRules.SecondsPerDay)), (returned.DeliverTime, returned.ExpireTime));
    }
}

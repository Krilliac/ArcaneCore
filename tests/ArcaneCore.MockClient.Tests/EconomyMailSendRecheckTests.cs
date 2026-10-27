using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Economy;
using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Economy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.MockClient.Tests.EconomyMailSendParityTests;

namespace ArcaneCore.MockClient.Tests;

/// <summary>
/// CMSG_SEND_MAIL re-checks the mailbox after the asynchronous recipient box-size read. vmangos@0e3ff01 checks CheckMailBox (alive, mailbox in
/// reach; MailHandler.cpp:65-80) once, at the top of HandleSendMail (:131), and never again: its box-size callback (:93-108, then
/// HandleSendMailCallback :237) checks only the session, guid and IsInWorld, and runs synchronously for an online receiver (:222-226), so
/// only an offline receiver's COUNT(*) (:232) leaves a window. ArcaneCore re-checks because that read can race the sender dying or
/// walking away, so such a sender must not post the letter. A failed check before the read answers MAIL_ERR_INTERNAL_ERROR as vmangos does (:131-134).
/// </summary>
public sealed class EconomyMailSendRecheckTests
{
    [Theory]
    [InlineData("dead")]
    [InlineData("out-of-reach")]
    public async Task Send_is_refused_when_the_sender_dies_or_leaves_the_mailbox_during_the_box_size_read(string change)
    {
        var mailbox = new ToggleMailbox();
        var control = new PausedRead();
        await using Rig rig = await Rig.StartAsync(loginReceiver: false, services =>
        {
            services.AddSingleton<IMailboxAccess>(mailbox);
            services.AddScoped<IEconomyStore>(provider => new PausedReadStore(
                new EfEconomyStore(provider.GetRequiredService<CharacterDbContext>()), control));
        });
        control.Token = rig.Token;
        control.TargetId = checked((int)rig.ReceiverGuid);
        try
        {
            await rig.SendAsync("Mailrecv", "hello", "body", money: 0, cod: 0);
            await control.Captured.Task.WaitAsync(rig.Token);

            await rig.Server.World.InvokeAsync(() =>
            {
                Player sender = rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!;
                if (change == "dead")
                {
                    sender.Health = 0;
                }
                else
                {
                    mailbox.InReach = false;
                }

                return true;
            }).WaitAsync(rig.Token);

            control.Release.TrySetResult(true);
            (MailAction action, MailResult result) = await rig.ReadResultAsync();

            // The read must have resumed normally: a read abandoned by the settlement budget would also answer InternalError without ever reaching the re-check.
            Assert.True(control.Resumed, "the paused box-size read did not resume normally");
            Assert.Equal(MailAction.Send, action);
            Assert.Equal(MailResult.InternalError, result);
            Assert.Empty(await rig.ReceiverMailAsync());
        }
        finally
        {
            control.Release.TrySetResult(true);
        }
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("out-of-reach")]
    public async Task Send_answers_internal_error_when_the_mailbox_check_fails_before_the_box_size_read(string change)
    {
        // vmangos@0e3ff01 MailHandler.cpp:131-134: a failed CheckMailBox before the count query answers MAIL_ERR_INTERNAL_ERROR.
        // The receiver is unknown, so a send that got past the first check would answer RecipientNotFound: the post-read re-check cannot
        // stand in for the first check here, because the letter never reaches the box-size read.
        var mailbox = new ToggleMailbox();
        await using Rig rig = await Rig.StartAsync(loginReceiver: false, services => services.AddSingleton<IMailboxAccess>(mailbox));
        await rig.Server.World.InvokeAsync(() =>
        {
            if (change == "dead")
            {
                rig.Server.World.FindOnlinePlayer(new ObjectGuid(rig.SenderGuid))!.Health = 0;
            }
            else
            {
                mailbox.InReach = false;
            }

            return true;
        }).WaitAsync(rig.Token);

        await rig.SendAsync("Nobodyhere", "hello", "body", money: 0, cod: 0);
        (MailAction action, MailResult result) = await rig.ReadResultAsync();
        Assert.Equal(MailAction.Send, action);
        Assert.Equal(MailResult.InternalError, result);
        Assert.Empty(await rig.ReceiverMailAsync());
    }

    /// <summary>The retail check's two conditions (alive, mailbox in reach), with reach switchable by the test.</summary>
    private sealed class ToggleMailbox : IMailboxAccess
    {
        public volatile bool InReach = true;

        public bool CanUseMailbox(Player player, ObjectGuid mailbox)
            => player.IsInWorld && player.IsAlive && InReach && mailbox.High == HighGuid.GameObject;
    }

    private sealed class PausedRead
    {
        internal TaskCompletionSource<bool> Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile int TargetId = -1;
        internal int Paused;

        /// <summary>The test's own token: the paused read waits on it, not on the store's settlement budget, so the budget cannot end the pause early.</summary>
        internal CancellationToken Token;

        internal volatile bool Resumed;
    }

    /// <summary>Holds the first read of the receiver's box until the test releases it.</summary>
    private sealed class PausedReadStore(IEconomyStore inner, PausedRead control) : IEconomyStore
    {
        public async Task<IReadOnlyList<MailRecord>> GetMailsAsync(int id, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<MailRecord> result = await inner.GetMailsAsync(id, cancellationToken);
            if (id == control.TargetId && Interlocked.Exchange(ref control.Paused, 1) == 0)
            {
                control.Captured.TrySetResult(true);
                await control.Release.Task.WaitAsync(control.Token);
                control.Resumed = true;
            }

            return result;
        }

        public Task<EconomyCommitResult> CommitAsync(EconomyCommitRequest r, CancellationToken c = default) => inner.CommitAsync(r, c);
        public Task<bool> IsCommittedAsync(Guid id, CancellationToken c = default) => inner.IsCommittedAsync(id, c);
        public Task<IReadOnlyList<MailRecord>> GetExpiredMailsAsync(long n, int m, CancellationToken c = default) => inner.GetExpiredMailsAsync(n, m, c);
        public Task<IReadOnlyList<MailRecord>> GetMailsInvolvingAsync(int id, CancellationToken c = default) => inner.GetMailsInvolvingAsync(id, c);
        public Task<string?> GetItemTextAsync(uint id, CancellationToken c = default) => inner.GetItemTextAsync(id, c);
        public Task<IReadOnlyList<AuctionRecord>> GetAuctionsAsync(CancellationToken c = default) => inner.GetAuctionsAsync(c);
        public Task<IReadOnlyDictionary<uint, ItemInstanceData>> GetEscrowItemsAsync(IReadOnlyCollection<uint> ids, CancellationToken c = default) => inner.GetEscrowItemsAsync(ids, c);
        public Task<AuctionSnapshot> GetAuctionSnapshotAsync(AuctionSnapshotFilter f, CancellationToken c = default) => inner.GetAuctionSnapshotAsync(f, c);
        public Task<EconomyIdSeed> GetIdSeedAsync(CancellationToken c = default) => inner.GetIdSeedAsync(c);
    }
}

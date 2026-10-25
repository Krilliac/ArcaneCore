using System.Numerics;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// What the boss watch concludes from the facts it gathers (<see cref="PlayerbotWorldBossStatus.Resolve"/>): up, dead until a time, dead with no
/// known time, or unknown (a boss the bots never travel to), and which entry a dragon portal asks for
/// (<see cref="PlayerbotWorldBossStatus.PortalEntry"/>).
/// </summary>
public sealed class PlayerbotWorldBossStatusTests
{
    private const long Now = 1_800_000_000;
    private static readonly Vector3 Spot = new(10f, 20f, 30f);

    [Fact]
    public void ALivingBoss_IsUp_WithWhatTheWatchSawAtIt()
    {
        PlayerbotWorldBossStatus status = PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts
        {
            Living = true, Position = Spot, TappedOrFightingOthers = true, GridLoaded = true,
        }, Now);

        Assert.Equal(PlayerbotWorldBossState.Up, status.State);
        Assert.Equal(Spot, status.Position);
        Assert.True(status.TappedOrFightingOthers);
    }

    [Fact]
    public void ALivingBoss_IsUp_EvenWithAStalePendingRespawn()
    {
        // The ledger can still hold the previous death's entry for a moment; a creature standing there wins.
        Assert.Equal(PlayerbotWorldBossState.Up, PlayerbotWorldBossStatus.Resolve(
            new PlayerbotWorldBossFacts { Living = true, PendingRespawnUnix = Now + 500, GridLoaded = true }, Now).State);
    }

    [Fact]
    public void NoneLiving_WithARespawnPendingInTheFuture_IsDeadUntilThen_EvenWhereTheGridIsNotLoaded()
    {
        foreach (bool loaded in new[] { true, false })
        {
            PlayerbotWorldBossStatus status = PlayerbotWorldBossStatus.Resolve(
                new PlayerbotWorldBossFacts { PendingRespawnUnix = Now + 3600, GridLoaded = loaded }, Now);
            Assert.Equal(PlayerbotWorldBossState.Dead, status.State);
            Assert.Equal(Now + 3600, status.DeadUntilUnix);
        }
    }

    [Fact]
    public void ARespawnDueRightNow_IsNotPending_SoALoadedGridWithNoBossIsDeadWithNoKnownTime()
    {
        // Boundary: pending > now is a pending respawn; pending == now is not.
        PlayerbotWorldBossStatus due = PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts { PendingRespawnUnix = Now, GridLoaded = true }, Now);
        Assert.Equal(PlayerbotWorldBossState.Dead, due.State);
        Assert.Null(due.DeadUntilUnix);

        PlayerbotWorldBossStatus justBefore = PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts { PendingRespawnUnix = Now + 1, GridLoaded = true }, Now);
        Assert.Equal(PlayerbotWorldBossState.Dead, justBefore.State);
        Assert.Equal(Now + 1, justBefore.DeadUntilUnix);
    }

    [Fact]
    public void NoneLiving_OnALoadedGrid_WithNoLedgerEntry_IsDeadWithNoKnownTime()
    {
        PlayerbotWorldBossStatus status = PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts { GridLoaded = true }, Now);
        Assert.Equal(PlayerbotWorldBossState.Dead, status.State);
        Assert.Null(status.DeadUntilUnix);
    }

    [Fact]
    public void NoneLiving_WhereTheGridIsNotLoaded_AndNoLedgerEntry_IsUnknown_NeverUp()
    {
        PlayerbotWorldBossStatus status = PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts(), Now);
        Assert.Equal(PlayerbotWorldBossState.Unknown, status.State);
        Assert.Null(status.DeadUntilUnix);
        // An expired ledger entry says nothing about a grid nobody has loaded.
        Assert.Equal(PlayerbotWorldBossState.Unknown, PlayerbotWorldBossStatus.Resolve(new PlayerbotWorldBossFacts { PendingRespawnUnix = Now - 10 }, Now).State);
    }

    // --- dragons: the portal asks for an entry only while the event runs and its dragon is not killed -----------------

    [Fact]
    public void ADragonPortal_AsksForItsEntry_OnlyWhileTheEventIsActiveAndItsKilledBitIsClear()
    {
        const uint Ysondre = 14887;
        Assert.Equal(Ysondre, PlayerbotWorldBossStatus.PortalEntry(active: true, killedMask: 0b0000, portal: 0, entryAtPortal: Ysondre));
        Assert.Equal(0u, PlayerbotWorldBossStatus.PortalEntry(active: false, killedMask: 0b0000, portal: 0, entryAtPortal: Ysondre));
        Assert.Equal(0u, PlayerbotWorldBossStatus.PortalEntry(active: true, killedMask: 0b0001, portal: 0, entryAtPortal: Ysondre));
    }

    [Theory]
    [InlineData(0, 0b0010, true)]   // another portal's bit does not matter
    [InlineData(1, 0b0010, false)]
    [InlineData(0, 0b1110, true)]
    [InlineData(3, 0b0111, true)]
    [InlineData(3, 0b1000, false)]
    public void ADragonPortal_ReadsOnlyItsOwnKilledBit(int portal, int killedMask, bool wantsEntry)
    {
        uint entryAtPortal = 14887u + (uint)portal;
        Assert.Equal(wantsEntry ? entryAtPortal : 0u, PlayerbotWorldBossStatus.PortalEntry(active: true, killedMask, portal, entryAtPortal));
    }
}

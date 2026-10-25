using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Groups;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The per-boss campaign and its caps (<see cref="PlayerbotWorldBossCampaign"/>, p7b design 3.4): when a boss that is up may be opened for a muster
/// (player grace, concurrent raids, attempts per UTC day, the retry cooldown, the group slots), what counts as an attempt (only a raid that
/// reached Travelling), and how an open muster ends. Time is Unix seconds; the day is the UTC day.
/// </summary>
public sealed class PlayerbotWorldBossCampaignTests
{
    private const uint Boss = 6109;
    private const int MaxGroups = 16;

    /// <summary>00:00 UTC of a day, plus one hour: far enough from both ends of the day for the cooldown tests.</summary>
    private static readonly long Day = 20_000L * 86_400 + 3_600;

    private static readonly PlayerbotWorldBossLoad Idle = new(ActiveRaids: 0, OwnGroupCount: 0, QuestGroupSlotFree: true);

    private static PlayerbotWorldBossOptions Options(Action<PlayerbotWorldBossOptions>? edit = null)
    {
        var o = new PlayerbotWorldBossOptions();
        edit?.Invoke(o);
        return o;
    }

    private static PlayerbotWorldBossCampaign UpSince(long when)
    {
        var campaign = new PlayerbotWorldBossCampaign(Boss);
        campaign.NoteUp(when);
        return campaign;
    }

    // --- opening ------------------------------------------------------------------------------------------------------

    [Fact]
    public void ABossNeverSeenUp_IsNotOpened()
        => Assert.Equal(PlayerbotWorldBossOpening.NotUp, new PlayerbotWorldBossCampaign(Boss).MayOpen(Options(), MaxGroups, Idle, Day));

    [Fact]
    public void TheGraceWindow_RunsFromTheFirstSightingOfTheBoss_AndEndsExactlyAfterThePlayerGraceMinutes()
    {
        PlayerbotWorldBossCampaign campaign = UpSince(Day);
        PlayerbotWorldBossOptions o = Options(); // 15 minutes
        Assert.Equal(PlayerbotWorldBossOpening.Grace, campaign.MayOpen(o, MaxGroups, Idle, Day));
        Assert.Equal(PlayerbotWorldBossOpening.Grace, campaign.MayOpen(o, MaxGroups, Idle, Day + 15 * 60 - 1));
        Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(o, MaxGroups, Idle, Day + 15 * 60));
    }

    [Fact]
    public void AGraceOfZero_OpensAtTheFirstSighting()
        => Assert.Equal(PlayerbotWorldBossOpening.Ok, UpSince(Day).MayOpen(Options(o => o.PlayerGraceMinutes = 0), MaxGroups, Idle, Day));

    [Fact]
    public void SeeingTheBossUpAgain_DoesNotRestartTheGrace_ButItGoingAwayDoes()
    {
        PlayerbotWorldBossCampaign campaign = UpSince(Day);
        campaign.NoteUp(Day + 600); // the next watch sees it still up
        Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(Options(), MaxGroups, Idle, Day + 15 * 60));

        campaign.NoteNotUp();       // killed, or its grid unloaded: the next sighting is a new one
        Assert.Equal(PlayerbotWorldBossOpening.NotUp, campaign.MayOpen(Options(), MaxGroups, Idle, Day + 3600));
        campaign.NoteUp(Day + 7200);
        Assert.Equal(PlayerbotWorldBossOpening.Grace, campaign.MayOpen(Options(), MaxGroups, Idle, Day + 7200 + 60));
    }

    [Fact]
    public void WorldBossesOff_OpensNothing()
        => Assert.Equal(PlayerbotWorldBossOpening.Disabled, UpSince(Day).MayOpen(Options(o => o.Enabled = false), MaxGroups, Idle, Day + 86_400));

    [Theory]
    [InlineData(0, 1, PlayerbotWorldBossOpening.Ok)]
    [InlineData(1, 1, PlayerbotWorldBossOpening.Concurrent)]
    [InlineData(2, 1, PlayerbotWorldBossOpening.Concurrent)]
    [InlineData(1, 2, PlayerbotWorldBossOpening.Ok)]
    [InlineData(0, 0, PlayerbotWorldBossOpening.Concurrent)] // MaxConcurrentRaids 0 opens none
    public void TheConcurrentRaidCap_IsServerWide(int active, int max, PlayerbotWorldBossOpening expected)
        => Assert.Equal(expected, UpSince(Day).MayOpen(Options(o => o.MaxConcurrentRaids = max), MaxGroups, new PlayerbotWorldBossLoad(active, 0, true), Day + 3600));

    [Fact]
    public void TheGroupSlots_AreRespected_MaxGroupsAndTheQuestGroupLimit()
    {
        PlayerbotWorldBossCampaign campaign = UpSince(Day);
        long later = Day + 3600;
        Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(Options(), MaxGroups, new PlayerbotWorldBossLoad(0, MaxGroups - 1, true), later));
        Assert.Equal(PlayerbotWorldBossOpening.Groups, campaign.MayOpen(Options(), MaxGroups, new PlayerbotWorldBossLoad(0, MaxGroups, true), later));
        Assert.Equal(PlayerbotWorldBossOpening.QuestSlot, campaign.MayOpen(Options(), MaxGroups, new PlayerbotWorldBossLoad(0, 0, QuestGroupSlotFree: false), later));
    }

    [Fact]
    public void TheCampaignsOfTwoBosses_AreIndependent()
    {
        PlayerbotWorldBossCampaign azuregos = UpSince(Day), kazzak = new(12397);
        kazzak.NoteUp(Day);
        azuregos.Open(Day + 3600);
        azuregos.Formed();
        Assert.Equal(PlayerbotWorldBossOpening.Ok, kazzak.MayOpen(Options(o => o.MaxConcurrentRaids = 2), MaxGroups, new PlayerbotWorldBossLoad(1, 1, true), Day + 3600));
    }

    // --- phases ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnOpenOrFormedCampaign_IsNotOpenedAgain()
    {
        PlayerbotWorldBossCampaign campaign = UpSince(Day);
        long when = Day + 3600;
        campaign.Open(when);
        Assert.Equal(PlayerbotWorldBossPhase.Open, campaign.Phase);
        Assert.Equal(PlayerbotWorldBossOpening.Busy, campaign.MayOpen(Options(), MaxGroups, Idle, when + 1));
        campaign.Formed();
        Assert.Equal(PlayerbotWorldBossPhase.Formed, campaign.Phase);
        Assert.Equal(PlayerbotWorldBossOpening.Busy, campaign.MayOpen(Options(), MaxGroups, Idle, when + 2));
    }

    [Fact]
    public void AnOpenMuster_ExpiresExactlyAfterTheMusterWindow_AndThenWaitsOutTheRetryCooldown_WithoutUsingAnAttempt()
    {
        PlayerbotWorldBossOptions o = Options(); // 180 s window, 1800 s retry
        PlayerbotWorldBossCampaign campaign = UpSince(Day);
        long opened = Day + 3600;
        campaign.Open(opened);
        Assert.False(campaign.MusterExpired(o, opened + 179));
        Assert.True(campaign.MusterExpired(o, opened + 180));

        long failed = opened + 180;
        campaign.MusterFailed(o, failed);
        Assert.Equal(PlayerbotWorldBossPhase.Watch, campaign.Phase);
        Assert.Equal(0, campaign.AttemptsToday(failed));
        Assert.Equal(failed + 1800, campaign.CooldownUntilUnix);
        Assert.Equal(PlayerbotWorldBossOpening.Cooldown, campaign.MayOpen(o, MaxGroups, Idle, failed + 1799));
        Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(o, MaxGroups, Idle, failed + 1800));
    }

    [Fact]
    public void AMusterThatIsNotOpen_NeverExpires()
        => Assert.False(UpSince(Day).MusterExpired(Options(), Day + 10 * 86_400));

    // --- attempts and the retry cooldown -------------------------------------------------------------------------------

    private static PlayerbotWorldBossCampaign Raid(PlayerbotWorldBossOptions o, long start, bool reachedTravelling, bool killed = false, long duration = 600)
    {
        PlayerbotWorldBossCampaign campaign = UpSince(start - 7200);
        campaign.Open(start);
        campaign.Formed();
        campaign.Ended(o, start + duration, reachedTravelling, killed);
        return campaign;
    }

    [Fact]
    public void ARaidThatReachedTravelling_IsAnAttempt_AndSetsTheRetryCooldown()
    {
        PlayerbotWorldBossOptions o = Options();
        PlayerbotWorldBossCampaign campaign = Raid(o, Day, reachedTravelling: true);
        long ended = Day + 600;
        Assert.Equal(PlayerbotWorldBossPhase.Watch, campaign.Phase);
        Assert.Equal(1, campaign.AttemptsToday(ended));
        Assert.Equal(ended + 1800, campaign.CooldownUntilUnix);
        Assert.Equal(PlayerbotWorldBossOpening.Cooldown, campaign.MayOpen(o, MaxGroups, Idle, ended + 1799));
        Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(o, MaxGroups, Idle, ended + 1800));
    }

    [Fact]
    public void AFormingFailure_DoesNotUseAnAttempt_ButStillSetsTheCooldown()
    {
        PlayerbotWorldBossOptions o = Options(x => x.MaxAttemptsPerDay = 1);
        PlayerbotWorldBossCampaign campaign = Raid(o, Day, reachedTravelling: false);
        long ended = Day + 600;
        Assert.Equal(0, campaign.AttemptsToday(ended));
        Assert.Equal(ended + 1800, campaign.CooldownUntilUnix);
        Assert.Equal(PlayerbotWorldBossOpening.Cooldown, campaign.MayOpen(o, MaxGroups, Idle, ended + 1));
        // With the cooldown over the single attempt of the day is still unused.
        Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(o, MaxGroups, Idle, ended + 1800));
    }

    [Fact]
    public void ManyFormingFailures_NeverExhaustTheDay_ButEachWaitsTheCooldown()
    {
        PlayerbotWorldBossOptions o = Options(x => { x.MaxAttemptsPerDay = 1; x.RetrySeconds = 60; });
        PlayerbotWorldBossCampaign campaign = UpSince(Day - 7200);
        for (int i = 0; i < 5; i++)
        {
            long start = Day + i * 1000;
            Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(o, MaxGroups, Idle, start));
            campaign.Open(start);
            campaign.Formed();
            campaign.Ended(o, start + 100, reachedTravelling: false, killed: false);
        }

        Assert.Equal(0, campaign.AttemptsToday(Day + 6000));
    }

    [Fact]
    public void TheAttemptsPerDay_AreCountedPerUtcDay_AndRollOverAtMidnightUtc()
    {
        PlayerbotWorldBossOptions o = Options(x => { x.MaxAttemptsPerDay = 3; x.RetrySeconds = 60; });
        PlayerbotWorldBossCampaign campaign = UpSince(Day - 7200);
        for (int i = 0; i < 3; i++)
        {
            long start = Day + i * 1000;
            campaign.Open(start);
            campaign.Formed();
            campaign.Ended(o, start + 100, reachedTravelling: true, killed: false);
        }

        long lastSecondOfTheDay = (Day / 86_400 + 1) * 86_400 - 1, midnight = lastSecondOfTheDay + 1;
        Assert.Equal(3, campaign.AttemptsToday(lastSecondOfTheDay));
        Assert.Equal(PlayerbotWorldBossOpening.Attempts, campaign.MayOpen(o, MaxGroups, Idle, Day + 5000));
        Assert.Equal(PlayerbotWorldBossOpening.Attempts, campaign.MayOpen(o, MaxGroups, Idle, lastSecondOfTheDay));
        Assert.Equal(0, campaign.AttemptsToday(midnight));
        Assert.Equal(PlayerbotWorldBossOpening.Ok, campaign.MayOpen(o, MaxGroups, Idle, midnight));
    }

    [Fact]
    public void MaxAttemptsPerDayZero_OpensNone()
        => Assert.Equal(PlayerbotWorldBossOpening.Attempts, UpSince(Day).MayOpen(Options(o => o.MaxAttemptsPerDay = 0), MaxGroups, Idle, Day + 3600));

    [Fact]
    public void AKill_CountsAsTheAttemptItWas()
    {
        PlayerbotWorldBossCampaign campaign = Raid(Options(), Day, reachedTravelling: true, killed: true);
        Assert.Equal(1, campaign.AttemptsToday(Day + 600));
        Assert.Equal(PlayerbotWorldBossPhase.Watch, campaign.Phase);
    }
}

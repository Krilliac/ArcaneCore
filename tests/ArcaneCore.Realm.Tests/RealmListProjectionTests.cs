using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Protocol;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// <see cref="RealmListProjection"/>: the flags the logon daemon sends for a realm row, from the world's state, the age of its
/// heartbeat and the asking account (docs/ops/realm-liveness.md). Time is a fixed instant, so no test waits for the 60 s threshold.
/// </summary>
public sealed class RealmListProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly RealmLivenessOptions Defaults = new();

    private static RealmEntry Row(RealmRuntimeState state, double? beatAgeSeconds, RealmFlags flags = RealmFlags.NewPlayers, byte allowedSecurityLevel = 0)
        => new()
        {
            Id = 1,
            Name = "Realm",
            Address = "127.0.0.1:8085",
            Flags = flags,
            State = state,
            HeartbeatUtc = beatAgeSeconds is { } age ? Now.UtcDateTime.AddSeconds(-age) : null,
            AllowedSecurityLevel = allowedSecurityLevel,
        };

    private static RealmFlags Show(RealmEntry row, AccountSecurity account = AccountSecurity.Player, RealmLivenessOptions? options = null)
        => RealmListProjection.Evaluate(row, account, Now, options ?? Defaults).Flags;

    [Fact]
    public void StaleHeartbeat_SendsOffline()
    {
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(Row(RealmRuntimeState.Online, 61)));
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(Row(RealmRuntimeState.Online, null)));
    }

    [Fact]
    public void FreshHeartbeat_SendsStoredFlags()
    {
        Assert.Equal(RealmFlags.NewPlayers, Show(Row(RealmRuntimeState.Online, 0)));
        Assert.Equal(RealmFlags.NewPlayers, Show(Row(RealmRuntimeState.Online, 59)));
        Assert.Equal(RealmFlags.NewPlayers, Show(Row(RealmRuntimeState.Online, 60))); // exactly at the threshold is still alive
    }

    [Fact]
    public void UnknownState_IgnoresHeartbeat()
    {
        Assert.Equal(RealmFlags.NewPlayers, Show(Row(RealmRuntimeState.Unknown, 3600)));
        Assert.Equal(RealmFlags.NewPlayers, Show(Row(RealmRuntimeState.Unknown, null)));
        Assert.Equal(RealmFlags.Offline, Show(Row(RealmRuntimeState.Unknown, null, RealmFlags.Offline))); // the stored Offline flag still shows
    }

    [Fact]
    public void Starting_SendsOffline()
    {
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(Row(RealmRuntimeState.Starting, 1)));
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(Row(RealmRuntimeState.Stopped, 1)));
    }

    [Fact]
    public void Restarting_FreshBeat_SendsInvalid()
    {
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Invalid, Show(Row(RealmRuntimeState.Restarting, 5)));
        Assert.Equal(
            RealmFlags.NewPlayers | RealmFlags.Offline,
            Show(Row(RealmRuntimeState.Restarting, 5), options: new RealmLivenessOptions { RestartingAs = RestartingRealmDisplay.Offline }));
    }

    [Fact]
    public void Restarting_StaleBeat_SendsOffline()
    {
        RealmFlags shown = Show(Row(RealmRuntimeState.Restarting, 120));
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, shown);
        Assert.False(shown.HasFlag(RealmFlags.Invalid));
    }

    [Fact]
    public void ShuttingDown_SendsStoredFlags()
    {
        Assert.Equal(RealmFlags.NewPlayers, Show(Row(RealmRuntimeState.ShuttingDown, 5)));
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(Row(RealmRuntimeState.ShuttingDown, 500))); // a shutdown countdown does not excuse a missing heartbeat
    }

    [Fact]
    public void LockedRealm_BelowLevel_SendsOffline()
    {
        RealmEntry locked = Row(RealmRuntimeState.Online, 1, allowedSecurityLevel: (byte)AccountSecurity.GameMaster);
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(locked, AccountSecurity.Player));
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(locked, AccountSecurity.Moderator));

        // The lock applies to a row nobody manages as well (vmangos AuthSocket.cpp:1088-1090).
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(Row(RealmRuntimeState.Unknown, null, allowedSecurityLevel: 1), AccountSecurity.Player));
    }

    [Fact]
    public void LockedRealm_GmAccount_SendsStoredFlags()
    {
        RealmEntry locked = Row(RealmRuntimeState.Online, 1, allowedSecurityLevel: (byte)AccountSecurity.GameMaster);
        Assert.Equal(RealmFlags.NewPlayers, Show(locked, AccountSecurity.GameMaster));
        Assert.Equal(RealmFlags.NewPlayers, Show(locked, AccountSecurity.Administrator));
    }

    [Fact]
    public void FutureHeartbeat_WithinSkew_IsFresh()
    {
        RealmView within = RealmListProjection.Evaluate(Row(RealmRuntimeState.Online, -4), AccountSecurity.Player, Now, Defaults);
        Assert.Equal(RealmFlags.NewPlayers, within.Flags);
        Assert.False(within.ClockSkewSuspect);
        Assert.Equal(TimeSpan.Zero, within.HeartbeatAge);
    }

    [Fact]
    public void FutureHeartbeat_BeyondSkew_IsFreshAndWarns()
    {
        // Clock drift between hosts is not a dead world: the beat counts as fresh (age clamped to 0) and the caller is told to warn.
        RealmView beyond = RealmListProjection.Evaluate(Row(RealmRuntimeState.Online, -30), AccountSecurity.Player, Now, Defaults);
        Assert.Equal(RealmFlags.NewPlayers, beyond.Flags);
        Assert.True(beyond.ClockSkewSuspect);
        Assert.Equal(TimeSpan.Zero, beyond.HeartbeatAge);
    }

    [Fact]
    public void ZeroStaleSeconds_TurnsTheHeartbeatCheckOff_ButKeepsStateAndLock()
    {
        var off = new RealmLivenessOptions { HeartbeatStaleSeconds = 0 };
        Assert.Equal(RealmFlags.NewPlayers, Show(Row(RealmRuntimeState.Online, 9999), options: off));
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Show(Row(RealmRuntimeState.Stopped, 1), options: off));
    }

    [Fact]
    public void Apply_ReturnsCopies_AndLeavesTheStoredRowsAlone()
    {
        RealmEntry stored = Row(RealmRuntimeState.Online, 600);
        IReadOnlyList<RealmEntry> shown = RealmListProjection.Apply([stored], AccountSecurity.Player, Now, Defaults, out bool skew);

        Assert.False(skew);
        Assert.Equal(RealmFlags.NewPlayers, stored.Flags);
        Assert.Equal(RealmFlags.NewPlayers | RealmFlags.Offline, Assert.Single(shown).Flags);
        Assert.Equal(stored.Name, shown[0].Name);
        Assert.Equal(stored.Address, shown[0].Address);
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Combat;

public sealed class PlayerbotHostilityTests
{
    private static async Task Run(Func<WorldTestHost, Player, Player, Task> body, byte otherRace = PlayerbotPvpTestKit.Orc)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession self = await PlayerbotPvpTestKit.EnterAsync(host, "HOSTSELF", "Hostself", PlayerbotPvpTestKit.Human);
        WorldSession other = await PlayerbotPvpTestKit.EnterAsync(host, "HOSTOTHER", "Hostother", otherRace);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Assert.Same(self.Player!.Map, other.Player!.Map);
                other.Player.Relocate(self.Player.X + 3, self.Player.Y, self.Player.Z, 0, host.World.NowMs);
                return true;
            });
            await body(host, self.Player!, other.Player!);
        }
        finally
        {
            self.Kick();
            other.Kick();
            await self.ManagedClosed;
            await other.ManagedClosed;
        }
    }

    [Fact]
    public Task UnflaggedEnemyPlayerIsNotHostile() => Run((host, self, other) => host.OnWorldAsync(() =>
    {
        Assert.False(PlayerbotHostility.IsHostilePlayer(self, other));
    }));

    [Fact]
    public Task FlaggedEnemyPlayerIsHostile() => Run((host, self, other) => host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(other);
        Assert.True(PlayerbotHostility.IsHostilePlayer(self, other));
        Assert.False(PlayerbotHostility.IsHostilePlayer(self, self));
    }));

    [Fact]
    public Task SameTeamPlayerIsNeverHostile() => Run((host, self, other) => host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(other);
        Assert.Equal(self.Team, other.Team);
        Assert.False(PlayerbotHostility.IsHostilePlayer(self, other));
    }), PlayerbotPvpTestKit.Human);

    [Fact]
    public Task DuelOpponentIsHostileWithoutFlag() => Run((host, self, other) => host.OnWorldAsync(() =>
    {
        Assert.False(PlayerbotHostility.IsHostilePlayer(self, other));
        self.Duel = new DuelInfo(self, other) { StartTimeSeconds = 1 };
        other.Duel = new DuelInfo(self, self) { StartTimeSeconds = 1 };
        Assert.True(PlayerbotHostility.IsHostilePlayer(self, other));
    }));

    [Fact]
    public Task GameMasterInGmModeIsNeverHostile() => Run((host, self, other) => host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(other);
        other.Flags |= PlayerFlags.Gm;
        Assert.False(PlayerbotHostility.IsHostilePlayer(self, other));
    }));

    [Fact]
    public Task UnprovokedFlaggedPlayerNeedsOrderDefendOrSiege() => Run((host, self, other) => host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(other);
        Assert.True(PlayerbotHostility.IsHostilePlayer(self, other));
        Assert.False(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.None, null));
        Assert.False(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.Provoked, null));
        Assert.False(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.GroupProvoked, null));
        Assert.True(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.Ordered, null));
        Assert.True(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.Defend, null));
        Assert.True(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.Siege, null));
        // An order never overrides the hostility rule: an unflagged enemy stays off limits.
        other.UnitFlags &= ~UnitFlags.Pvp;
        Assert.False(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.Ordered, null));
    }));

    [Fact]
    public Task AttackerIsProvoked() => Run((host, self, other) => host.OnWorldAsync(() =>
    {
        PlayerbotPvpTestKit.Flag(other);
        PlayerbotPvpTestKit.Flag(self);
        Assert.Null(PlayerbotHostility.FirstProvokingPlayer(self));
        Assert.True(self.Map!.Combat.Attack(other, self));
        Assert.Same(other, PlayerbotHostility.FirstProvokingPlayer(self));
        Assert.True(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.Provoked, null));
        Assert.True(PlayerbotHostility.MayEngage(self, other, PlayerbotEngageReason.None, null));
    }));
}

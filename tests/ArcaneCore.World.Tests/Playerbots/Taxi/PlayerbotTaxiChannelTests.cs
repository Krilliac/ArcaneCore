using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Playerbots.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Taxi;

public sealed partial class PlayerbotTaxiTests
{
    /// <summary>
    /// A 1.12 client keeps its channels while it rides a taxi and updates the local ones only once it is off. The membership pass must do
    /// the same: during a flight it sends no join and no leave, after the landing it applies the change. If the freeze were missing, the
    /// Joins and Leaves counts below would move while <see cref="UnitFlags.TaxiFlight"/> is set.
    /// </summary>
    [Fact]
    public async Task ChannelMembership_TaxiFlight_KeepsChannels()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.OnPlayer((player, _) =>
        {
            var membership = new PlayerbotChannelMembership();
            var channels = ArcaneCore.World.Social.SocialHandlers.Social(rig.Session).Channels;
            var bots = new List<PlayerbotChannelBot> { new(Guid.NewGuid(), rig.Session, player) };
            var all = new PlayerbotSpeechOptions { Enabled = true, WorldChannels = PlayerbotWorldChannels.LookingForGroup };
            var none = new PlayerbotSpeechOptions { Enabled = true, WorldChannels = PlayerbotWorldChannels.None };

            // On the ground the bot joins LookingForGroup (the one channel with no zone).
            membership.Run(bots, channels, null, all, 1_000);
            Assert.Equal(1, membership.Joins);
            Assert.Equal(0, membership.Leaves);

            // In the air a changed wanted set is not applied: nothing is left, nothing is joined.
            player.UnitFlags |= UnitFlags.TaxiFlight;
            membership.Run(bots, channels, null, none, 2_000);
            membership.Run(bots, channels, null, none, 3_000);
            Assert.Equal(1, membership.Joins);
            Assert.Equal(0, membership.Leaves);

            // After the landing the same pass applies it.
            player.UnitFlags &= ~UnitFlags.TaxiFlight;
            membership.Run(bots, channels, null, none, 4_000);
            Assert.Equal(1, membership.Joins);
            Assert.Equal(1, membership.Leaves);
            return 0;
        });
    }
}

using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Tests.Social;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Social;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>
/// WorldDefense needs INTERNAL honor rank 15 to speak (vmangos Channel.cpp:636-648) and the channel message carries that rank
/// (:670). Internal rank 15 is visual rank 11, which is what mangos-classic's threshold (visual 11) also means: there is no
/// deviation between the two cores, only two numberings.
/// </summary>
public sealed class WorldDefenseRankTests
{
    private static List<ChatNotify> Notifies(SocialFixture f, Player player)
        => [.. f.Sent(player, WorldOpcode.SmsgChannelNotify).Select(p => (ChatNotify)p[0])];

    private static SocialFixture WithRanks(Func<Player, byte> rank)
    {
        var f = new SocialFixture();
        HonorHooks.Register(f.World, new HonorHooks(new HonorOptions(), HonorClock.System) { InternalRank = rank });
        return f;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(14, false)]
    [InlineData(15, true)]
    [InlineData(18, true)]
    public void Speaking_needs_internal_rank_15(int rank, bool allowed)
    {
        using SocialFixture f = WithRanks(_ => (byte)rank);
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Channels.Join(a, "WorldDefense", string.Empty);
        f.Context.Channels.Join(b, "WorldDefense", string.Empty);
        f.ClearAll();

        f.Context.Channels.Say(a, "WorldDefense", "inc", Language.Common);

        if (allowed)
        {
            byte[] message = Assert.Single(f.Sent(b, WorldOpcode.SmsgMessagechat));
            // type u8, language u32, channel cstring, rank u32 ...
            int rankOffset = 1 + 4 + "WorldDefense".Length + 1;
            Assert.Equal((uint)rank, BitConverter.ToUInt32(message, rankOffset));
            Assert.Empty(Notifies(f, a));
        }
        else
        {
            Assert.Equal([ChatNotify.Muted], Notifies(f, a));
            Assert.Empty(f.Sent(b, WorldOpcode.SmsgMessagechat));
        }
    }

    [Fact]
    public void Ordinary_channels_carry_the_speakers_rank_without_needing_one()
    {
        using SocialFixture f = WithRanks(_ => 9);
        Player a = f.AddPlayer(1);
        Player b = f.AddPlayer(2);
        f.Context.Channels.Join(a, "Club", string.Empty);
        f.Context.Channels.Join(b, "Club", string.Empty);
        f.ClearAll();

        f.Context.Channels.Say(a, "Club", "hi", Language.Common);

        byte[] message = Assert.Single(f.Sent(b, WorldOpcode.SmsgMessagechat));
        Assert.Equal(9u, BitConverter.ToUInt32(message, 1 + 4 + "Club".Length + 1));
    }

    [Fact]
    public void Without_the_honor_feature_everyone_is_unranked_as_before()
    {
        using var f = new SocialFixture();
        Player a = f.AddPlayer(1);
        f.Context.Channels.Join(a, "WorldDefense", string.Empty);
        f.ClearAll();
        f.Context.Channels.Say(a, "WorldDefense", "inc", Language.Common);
        Assert.Equal([ChatNotify.Muted], Notifies(f, a));
    }
}

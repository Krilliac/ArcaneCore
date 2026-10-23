using System.Text;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Chat;
using ArcaneCore.World.Playerbots.Commands;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>Mood (P2 section 4): events move it, the half-life decays it, it scales chances, picks the template tone and adds a prompt fact and a GM line.</summary>
public sealed class PlayerbotMoodTests
{
    private static readonly Guid Bot = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static PlayerbotMood Mood(out long[] clock)
    {
        long[] time = [0];
        clock = time;
        return new PlayerbotMood { Clock = () => time[0] };
    }

    [Fact]
    public void EventsNudge_AreClamped_AndDecayWithTheHalfLife()
    {
        PlayerbotMood mood = Mood(out long[] clock);
        Assert.Equal(0f, mood.Get(Bot));
        mood.Nudge(Bot, PlayerbotMoodEvent.LevelUp);
        Assert.Equal(0.3f, mood.Get(Bot), 3);
        for (int i = 0; i < 10; i++) mood.Nudge(Bot, PlayerbotMoodEvent.LevelUp);
        Assert.Equal(1f, mood.Get(Bot), 3);
        for (int i = 0; i < 10; i++) mood.Nudge(Bot, PlayerbotMood.DeltaOf(PlayerbotSocialEvent.Kicked));
        Assert.Equal(-1f, mood.Get(Bot), 3);

        clock[0] = 30 * 60_000L; // one half-life
        Assert.Equal(-0.5f, mood.Get(Bot), 3);
        clock[0] = 60 * 60_000L;
        Assert.Equal(-0.25f, mood.Get(Bot), 3);
        mood.HalfLifeMinutes = 60;
        Assert.Equal(-0.5f, mood.Get(Bot), 3); // the same age is half as old
    }

    [Fact]
    public void TheEvents_GoTheRightWay()
    {
        foreach (PlayerbotSocialEvent up in new[] { PlayerbotSocialEvent.HelpedBy, PlayerbotSocialEvent.GroupSucceeded })
            Assert.True(PlayerbotMood.DeltaOf(up) > 0, up.ToString());
        foreach (PlayerbotSocialEvent down in new[] { PlayerbotSocialEvent.GroupWiped, PlayerbotSocialEvent.Kicked, PlayerbotSocialEvent.RudeEmote, PlayerbotSocialEvent.ChatStrike })
            Assert.True(PlayerbotMood.DeltaOf(down) < 0, down.ToString());
        Assert.Equal(0f, PlayerbotMood.DeltaOf(PlayerbotSocialEvent.ChatExchange));
        // A PvP death is the mood lane's Death nudge alone: KilledBy must not move the mood a second time.
        Assert.Equal(0f, PlayerbotMood.DeltaOf(PlayerbotSocialEvent.KilledBy));
        Assert.True(PlayerbotMood.DeltaOf(PlayerbotMoodEvent.LevelUp) > 0 && PlayerbotMood.DeltaOf(PlayerbotMoodEvent.LootUpgrade) > 0 && PlayerbotMood.DeltaOf(PlayerbotMoodEvent.Death) < 0);
    }

    [Fact]
    public void TheChanceMultiplier_RunsFromHalfToOneAndAHalf_AndTheToneHasABand()
    {
        Assert.Equal(0.5f, PlayerbotMood.ChanceMultiplier(-1f));
        Assert.Equal(1f, PlayerbotMood.ChanceMultiplier(0f));
        Assert.Equal(1.5f, PlayerbotMood.ChanceMultiplier(1f));
        Assert.Equal(1.5f, PlayerbotMood.ChanceMultiplier(7f));
        Assert.Equal(PlayerbotTone.Cheerful, PlayerbotMood.ToneOf(0.4f));
        Assert.Equal(PlayerbotTone.Grumpy, PlayerbotMood.ToneOf(-0.4f));
        Assert.Equal(PlayerbotTone.Neutral, PlayerbotMood.ToneOf(0.1f));
    }

    [Fact]
    public void Disabled_MeansNoMood()
    {
        PlayerbotMood mood = Mood(out _);
        mood.Configure(new PlayerbotSocialMoodOptions { Enabled = false });
        mood.Nudge(Bot, 0.5f);
        Assert.Equal(0f, mood.Get(Bot));
        mood.Configure(new PlayerbotSocialMoodOptions { Enabled = true, HalfLifeMinutes = 10 });
        mood.Nudge(Bot, 0.5f);
        Assert.Equal(0.5f, mood.Get(Bot), 3);
        Assert.Equal(10, mood.HalfLifeMinutes);
    }

    private static BotChatAsk Ask(float mood)
    {
        var persona = new BotChatPersona("Moodbot", Race.Human, Class.Warrior, Gender.Male, 20, null, null, null, null, null, false, [], Mood: mood);
        return new BotChatAsk(Bot, ObjectGuid.Empty, persona, ObjectGuid.Empty, "Pat", ArcaneCore.Protocol.ChatType.Say, string.Empty, false, false);
    }

    [Fact]
    public void ATemplate_TakesTheToneOfTheMood_AndThePlainLinesOtherwise()
    {
        for (int i = 0; i < 30; i++)
        {
            Assert.Contains(PlayerbotBuiltinChat.Pick("social.thanks", Ask(0.8f), null, new Random(i)), PlayerbotSocialTemplates.Lines["social.thanks.mood.cheerful"].Select(l => l.Replace("{player}", "Pat")));
            Assert.Contains(PlayerbotBuiltinChat.Pick("social.thanks", Ask(-0.8f), null, new Random(i)), PlayerbotSocialTemplates.Lines["social.thanks.mood.grumpy"].Select(l => l.Replace("{player}", "Pat")));
            Assert.Contains(PlayerbotBuiltinChat.Pick("social.thanks", Ask(0f), null, new Random(i)), PlayerbotSocialTemplates.Lines["social.thanks"].Select(l => l.Replace("{player}", "Pat")));
        }
    }

    [Fact]
    public void TheModelPrompt_GetsAMoodFact_OnlyWhenTheMoodIsClear()
    {
        var good = new StringBuilder();
        var bad = new StringBuilder();
        var middling = new StringBuilder();
        PlayerbotChatPrompts.AppendMood(good, 0.6f);
        PlayerbotChatPrompts.AppendMood(bad, -0.6f);
        PlayerbotChatPrompts.AppendMood(middling, 0.1f);
        Assert.Contains("good mood", good.ToString());
        Assert.Contains("bad mood", bad.ToString());
        Assert.Equal(string.Empty, middling.ToString());
    }

    [Fact]
    public void TheGmLine_ShowsTheMood()
    {
        string line = PlayerbotExtraCommands.MoodLine(0.45f);
        Assert.Contains("Mood +0.45", line);
        Assert.Contains("cheerful", line);
        Assert.Contains("x1.23", line);
        Assert.Contains("grumpy", PlayerbotExtraCommands.MoodLine(-0.9f));
    }

    [Fact]
    public async Task ARecordedEvent_AndALevelUp_MoveTheMoodOfARunningBot()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid bot = await world.AddBotAsync("Moodrun");
        await using WorldTestClient other = await world.AddPlayerAsync("Moodother");
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Moodrun", p => p.Guid);
        ObjectGuid otherGuid = await world.Host.PlayerStateAsync("Moodother", p => p.Guid);
        PlayerbotMood mood = HelpTestKit.RecorderOf(world).Mood;

        await world.Host.OnWorldAsync(() => HelpTestKit.RecorderOf(world).Note(botGuid, otherGuid, PlayerbotSocialEvent.RudeEmote));
        Assert.True(mood.Get(bot) < -0.1f);

        // The lane sets its baseline on the first pass, then sees the level go up.
        await world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).MoodLane.Run(SocialTestKit.BotPass(world.Host, 1_000)));
        float before = mood.Get(bot);
        await world.Host.OnWorldAsync(() =>
        {
            world.Host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.GiveLevel(HelpTestKit.Online(world, "Moodrun"), 5);
            SocialTestKit.Feature(world.Host).MoodLane.Run(SocialTestKit.BotPass(world.Host, 2_000));
        });
        Assert.True(mood.Get(bot) > before + 0.2f);
    }

    [Fact]
    public async Task WearingABetterItem_RaisesTheMoodOfARunningBot_ByTheLootUpgradeDelta()
    {
        var items = new ArcaneCore.World.Tests.Items.ItemTestContent();
        items.Templates.Templates.Add(new ArcaneCore.Kernel.Items.ItemTemplate { Entry = 41101, Class = 2, SubClass = 7, Name = "Mood Test Sword", DisplayId = 1542, Quality = 2, InventoryType = 21, Stackable = 1, ItemLevel = 20, MaxDurability = 20 });
        SocialWorld world;
        using (items.Use()) world = await SocialWorld.StartAsync(frozen: true);
        await using (world)
        {
            Guid bot = await world.AddBotAsync("Moodloot");
            PlayerbotMood mood = HelpTestKit.RecorderOf(world).Mood;
            await world.Host.OnWorldAsync(() =>
            {
                HelpTestKit.Online(world, "Moodloot").Inventory.Load([]);
                SocialTestKit.Feature(world.Host).MoodLane.Run(SocialTestKit.BotPass(world.Host, 1_000)); // the baseline: nothing worn
            });
            float before = mood.Get(bot);
            await world.Host.OnWorldAsync(() =>
            {
                Player player = HelpTestKit.Online(world, "Moodloot");
                Assert.Equal(ArcaneCore.Game.Items.InventoryResult.Ok, player.Inventory.AddItem(41101, 1, out ArcaneCore.Game.Items.Item? sword));
                player.Inventory.AutoEquipItem(sword!.BagSlot, sword.Slot); // true only when the item went into a bag slot
                Assert.Contains(player.Inventory.Equipped, e => e.Item2.Template.Entry == 41101);
                SocialTestKit.Feature(world.Host).MoodLane.Run(SocialTestKit.BotPass(world.Host, 12_000)); // past the ten-second item check
            });
            Assert.Equal(before + PlayerbotMood.DeltaOf(PlayerbotMoodEvent.LootUpgrade), mood.Get(bot), 2);
        }
    }
}

using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The trouble scan (P2 section 2) on a running world with a frozen social feature: a hand-built pass at a world time the test picks runs the
/// help lane over a real player who is losing a fight, and the bot's own think does the rest. The decision is a hash of the bot, the victim and
/// the fight, so the tests pick the times where it says yes or no and check the lane obeys it.
/// </summary>
public sealed class PlayerbotTroubleWorldTests
{
    private sealed record Scene(Guid Bot, ObjectGuid BotGuid, ObjectGuid VictimGuid, Creature Mob);

    private static async Task<Scene> SetUpAsync(SocialWorld world, uint victimHealthPercent = 25, ArcaneCore.Kernel.WorldData.Creatures.CreatureTemplate? template = null)
    {
        Guid bot = await world.AddBotAsync("Troublebot");
        await HelpTestKit.StandAsync(world, "Troublebot", ("Troublevic", 6f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Troublebot", p => p.Guid);
        ObjectGuid victimGuid = await world.Host.PlayerStateAsync("Troublevic", p => p.Guid);
        Creature mob = null!;
        await world.Host.OnWorldAsync(() =>
        {
            Player victim = HelpTestKit.Online(world, "Troublevic");
            mob = HelpTestKit.SpawnMob(victim, 4f, template);
            HelpTestKit.Engage(mob, victim);
            victim.Health = Math.Max(1u, victim.MaxHealth * victimHealthPercent / 100);
        });
        return new Scene(bot, botGuid, victimGuid, mob);
    }

    private static Task ScanAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Help.Run(SocialTestKit.BotPass(world.Host, nowMs)));

    private static float HelpfulnessOf(Scene scene) => PlayerbotTraits.For(scene.BotGuid.Value).Helpfulness;

    [Fact]
    public async Task AnIdleHealthyBot_HelpsALowHealthPlayerUnderAttack_AndDamagesItsAttacker()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient victim = await world.AddPlayerAsync("Troublevic");
        Scene scene = await SetUpAsync(world);
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));
        uint before = await world.Host.OnWorldAsync(() => scene.Mob.Health);

        await ScanAsync(world, now);

        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(1, lane.Helped);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, scene.Bot);
        await HelpTestKit.UntilAsync(() => brain.HelpsDone == 1, "the bot's think to carry out the request");
        Assert.Equal(PlayerbotHelpKind.Assist, brain.LastHelpKind);
        Assert.Equal(scene.VictimGuid, brain.LastHelpTarget);
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => scene.Mob.Health).Result < before, "the bot to damage the attacker");

        // Recorded once, in the bot's memory (HelpedThem) and, the victim being a real player, nowhere else. The credit comes with the bot's next think after it hurt the attacker.
        await HelpTestKit.UntilAsync(() => HelpTestKit.RecorderOf(world).Memory.TryGet(scene.Bot, scene.VictimGuid.Value, out _), "the help to be credited");
        Assert.True(HelpTestKit.RecorderOf(world).Memory.TryGet(scene.Bot, scene.VictimGuid.Value, out PlayerbotRelation relation));
        Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerHelp, relation.Affinity);
        Assert.Equal(1, relation.Familiarity);
    }

    [Fact]
    public async Task TheDecision_IsTheSameForTheWholeFight_WhateverTheNumberOfPasses()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient victim = await world.AddPlayerAsync("Troublevic");
        Scene scene = await SetUpAsync(world);
        long no = HelpTestKit.NowWhere(t => !PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));

        // The first pass sees the trouble (the fight's episode starts there) and the roll says no; so do the next ones, a few seconds apart.
        for (int i = 0; i < 5; i++) await ScanAsync(world, no + i * 1000L);

        Assert.Equal(0, SocialTestKit.Feature(world.Host).Help.Helped);
        Assert.Equal(PlayerbotHelpKind.None, HelpTestKit.BrainOf(world, scene.Bot).PendingHelp);
        Assert.Equal(0, HelpTestKit.BrainOf(world, scene.Bot).HelpsDone);
    }

    [Fact]
    public async Task ARivalIsNeverHelped_EvenWithAYesRoll()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient victim = await world.AddPlayerAsync("Troublevic");
        Scene scene = await SetUpAsync(world);
        await world.Host.OnWorldAsync(() =>
        {
            for (int i = 0; i < 3; i++) HelpTestKit.RecorderOf(world).Note(scene.BotGuid, scene.VictimGuid, PlayerbotSocialEvent.KilledBy);
        });
        Assert.True(HelpTestKit.RecorderOf(world).Memory.TryGet(scene.Bot, scene.VictimGuid.Value, out PlayerbotRelation relation));
        Assert.True(HelpTestKit.RecorderOf(world).Memory.IsRival(relation));
        // The roll is taken with the rival's real affinity, so only the rival guard (not a low roll) can be what stops the help.
        int rivalAffinity = HelpTestKit.RecorderOf(world).Memory.EffectiveAffinity(relation);
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), rivalAffinity));

        await ScanAsync(world, now);

        Assert.Equal(0, SocialTestKit.Feature(world.Host).Help.Helped);
        Assert.Equal(PlayerbotHelpKind.None, HelpTestKit.BrainOf(world, scene.Bot).PendingHelp);
    }

    [Fact]
    public async Task NoOneIsHelped_WhoIsHealthy_OrWhenTheBotIsBusy_OrTheSwitchIsOff()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient victim = await world.AddPlayerAsync("Troublevic");
        Scene scene = await SetUpAsync(world, victimHealthPercent: 90); // in a fight, but not in trouble
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));
        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, scene.Bot);

        await ScanAsync(world, now);
        Assert.Equal(0, lane.Helped); // healthy

        await world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Troublevic").Health = HelpTestKit.Online(world, "Troublevic").MaxHealth / 4);
        await world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Troublebot").Health = HelpTestKit.Online(world, "Troublebot").MaxHealth / 2);
        await ScanAsync(world, now + 1000);
        Assert.Equal(0, lane.Helped); // the bot is hurt itself: it keeps to itself

        await world.Host.OnWorldAsync(() =>
        {
            Player self = HelpTestKit.Online(world, "Troublebot");
            self.Health = self.MaxHealth;
            HelpTestKit.Engage(HelpTestKit.SpawnMob(self, -4f), self); // in a fight of its own
        });
        await ScanAsync(world, now + 2000);
        Assert.Equal(0, lane.Helped);
        Assert.Equal(PlayerbotHelpKind.None, brain.PendingHelp);

        // Switched off, a bot that is free and a victim who is in trouble still give nothing.
        await world.Host.OnWorldAsync(() => world.Options.Social.Helping.Enabled = false);
        await ScanAsync(world, now + 3000);
        Assert.Equal(0, lane.Helped);
    }

    [Fact]
    public async Task APlayerAttacker_OrAPlayersPet_IsOnlyTakenByABotThatMayBeFlagged()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient victim = await world.AddPlayerAsync("Troublevic");
        await using WorldTestClient orc = await world.AddPlayerAsync("Troubleorc", SocialTestKit.Orc);
        Scene scene = await SetUpAsync(world);
        await world.Host.OnWorldAsync(() =>
        {
            Player self = HelpTestKit.Online(world, "Troublebot");
            Player vic = HelpTestKit.Online(world, "Troublevic");
            Player enemy = HelpTestKit.Online(world, "Troubleorc");
            // The only attacker is now a hostile player's pet: a fight that would flag the bot.
            scene.Mob.SetUInt64(UpdateFields.UnitFieldSummonedby, enemy.Guid.Value);
            // A pet that fights a player has flagged its owner; an unflagged owner's pet is not attackable at all.
            enemy.Flags |= PlayerFlags.PvpDesired; // persistent: the world's pvp timer keeps the flag
            enemy.UnitFlags |= UnitFlags.Pvp;
            Assert.True(PlayerbotHelpRules.WouldFlag(scene.Mob));
            Assert.False(PlayerbotHelpRules.MayFlag(self, warMode: false));
            Assert.Null(PlayerbotHelpRules.AttackerOf(self, vic, mayFlag: false, groupSize: 1));
            Assert.Same(scene.Mob, PlayerbotHelpRules.AttackerOf(self, vic, mayFlag: true, groupSize: 1));

            // Flagged already, or War Mode on: it may.
            self.UnitFlags |= UnitFlags.Pvp;
            Assert.True(PlayerbotHelpRules.MayFlag(self, warMode: false));
            self.UnitFlags &= ~UnitFlags.Pvp;
            Assert.True(PlayerbotHelpRules.MayFlag(self, warMode: true));
        });

        // The pet is attackable by the bot, so the War Mode gate alone decides the scan below.
        await world.Host.OnWorldAsync(() => Assert.True(HelpTestKit.Online(world, "Troublebot").Map!.Combat.Hooks.CanAttack(HelpTestKit.Online(world, "Troublebot"), scene.Mob)));

        // Through the lane: War Mode off, no help; the roll cannot matter.
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));
        await ScanAsync(world, now);
        Assert.Equal(0, SocialTestKit.Feature(world.Host).Help.Helped);
        Assert.Equal(PlayerbotHelpKind.None, HelpTestKit.BrainOf(world, scene.Bot).PendingHelp);

        // With War Mode on the same scene is helped.
        await world.Host.OnWorldAsync(() => world.Options.Social.WarMode.Enabled = true);
        long warNow = HelpTestKit.NowWhere(t => t > now + 60_000 && PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));
        await ScanAsync(world, warNow);
        Assert.Equal(1, SocialTestKit.Feature(world.Host).Help.Helped);
    }

    [Fact]
    public async Task APvpFlaggedCreature_IsAFlaggingFight_AndIsOnlyHelpedWithWarMode()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await using WorldTestClient victim = await world.AddPlayerAsync("Troublevic");
        Scene scene = await SetUpAsync(world);
        await world.Host.OnWorldAsync(() =>
        {
            Player self = HelpTestKit.Online(world, "Troublebot");
            Player vic = HelpTestKit.Online(world, "Troublevic");
            scene.Mob.UnitFlags |= UnitFlags.Pvp; // a guard or an enemy town NPC
            Assert.True(PlayerbotHelpRules.WouldFlag(scene.Mob));
            Assert.Null(PlayerbotHelpRules.AttackerOf(self, vic, mayFlag: false, groupSize: 1));
        });

        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));
        await ScanAsync(world, now);
        Assert.Equal(0, SocialTestKit.Feature(world.Host).Help.Helped);

        await world.Host.OnWorldAsync(() => world.Options.Social.WarMode.Enabled = true);
        long warNow = HelpTestKit.NowWhere(t => t > now + 60_000 && PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));
        await ScanAsync(world, warNow);
        Assert.Equal(1, SocialTestKit.Feature(world.Host).Help.Helped);
    }

    [Fact]
    public async Task AFightAboveTheBotsLevel_OrAnEliteOne_IsNotWinnable_SoNobodyIsHelped()
    {
        foreach (ArcaneCore.Kernel.WorldData.Creatures.CreatureTemplate template in new[] { HelpTestKit.Strong, HelpTestKit.Elite })
        {
            await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
            await using WorldTestClient victim = await world.AddPlayerAsync("Troublevic");
            Scene scene = await SetUpAsync(world, template: template);
            long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(scene.BotGuid.Value, scene.VictimGuid.Value, (uint)t, HelpfulnessOf(scene), 0));

            await ScanAsync(world, now);

            Assert.Equal(0, SocialTestKit.Feature(world.Host).Help.Helped);
            Assert.Equal(PlayerbotHelpKind.None, HelpTestKit.BrainOf(world, scene.Bot).PendingHelp);
        }
    }
}

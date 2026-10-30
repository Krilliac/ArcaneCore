using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// War Mode on the bots, on a running world (<c>World:WarMode:Enabled</c>, docs/areas/playbots-social.md): the hunt (a bot with a war Rival grudge against a
/// flagged enemy within the radius attacks it) and the help against player and bot attackers (a bot attacked by a flagged enemy draws a same-faction helper).
/// Each refusal runs in a world built exactly like the one that acts, with one thing changed, so a refusal is never an artefact of the set-up.
/// </summary>
public sealed class PlayerbotWarModeWorldTests
{
    public enum Variation { None, WarModeOff, TargetInExemptArea, BotInCombat, NoGrudge, TargetOutsideRadius }

    private static void FlagForGood(Player player)
    {
        player.Flags |= PlayerFlags.PvpDesired; // the world's PvP timer keeps a wanted flag
        PlayerbotPvpTestKit.Flag(player);
    }

    /// <summary>Builds the hunt scene with one variation and answers whether the bot took the foe on.</summary>
    private static async Task<bool> HuntsAsync(Variation variation, bool foeIsBot)
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        Guid bot = await world.AddBotAsync("Hubot");
        WorldTestClient? client = null;
        try
        {
            if (foeIsBot) await world.AddBotAsync("Hufoe", race: RivalryKit.Undead);
            else client = await world.AddPlayerAsync("Hufoe", RivalryKit.Undead);
            await HelpTestKit.StandAsync(world, "Hubot", ("Hufoe", variation == Variation.TargetOutsideRadius ? 40f : 5f));

            // The grudge is seeded with War Mode on (the war layer is fed only then) and the foe standing in an exempt area, so that nothing the bot's own thinks do
            // meanwhile can start the hunt; the variation is then set up and asked about in one world step.
            await world.Host.OnWorldAsync(() => PvpAreaState.SetWarModeExempt(RivalryKit.Online(world, "Hufoe"), true));
            world.Options.Social.WarMode.Enabled = true;
            world.Options.Social.WarMode.HuntRadiusYards = variation == Variation.TargetOutsideRadius ? 30 : 60;
            if (variation != Variation.NoGrudge) await RivalryKit.SeedAsync(world, "Hubot", "Hufoe", killerIsBot: foeIsBot, kills: 1);
            ulong foeGuid = (await RivalryKit.GuidAsync(world, "Hufoe")).Value;
            Assert.Equal(variation != Variation.NoGrudge, RivalryKit.RelationOf(world, bot, foeGuid) is { } seeded && RivalryKit.Memory(world).IsRival(seeded));

            PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
            return await world.Host.OnWorldAsync(() =>
            {
                Player self = RivalryKit.Online(world, "Hubot");
                Player foe = RivalryKit.Online(world, "Hufoe");
                FlagForGood(foe);
                PvpAreaState.SetWarModeExempt(foe, variation == Variation.TargetInExemptArea);
                if (variation == Variation.BotInCombat) HelpTestKit.Engage(HelpTestKit.SpawnMob(self, -4f), self);
                if (variation == Variation.WarModeOff) world.Options.Social.WarMode.Enabled = false;

                bool acted = brain.TryHunt(self, 500);
                return acted || brain.EngagedPlayer is not null;
            });
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnIdleBot_WithAWarGrudge_AttacksAFlaggedEnemyWithinTheRadius(bool foeIsBot)
        => Assert.True(await HuntsAsync(Variation.None, foeIsBot));

    [Theory]
    [InlineData(Variation.WarModeOff)]
    [InlineData(Variation.TargetInExemptArea)]
    [InlineData(Variation.BotInCombat)]
    [InlineData(Variation.NoGrudge)]
    [InlineData(Variation.TargetOutsideRadius)]
    public async Task TheHunt_IsRefused(Variation variation)
    {
        Assert.False(await HuntsAsync(variation, foeIsBot: true));
        Assert.False(await HuntsAsync(variation, foeIsBot: false));
    }

    [Fact]
    public async Task TheHuntedBot_FightsOnWithTheGrudgeReason_AndLetsGoWhenTheTargetEntersAnExemptArea()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        Guid bot = await world.AddBotAsync("Hubot");
        await world.AddBotAsync("Hufoe", race: RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Hubot", ("Hufoe", 5f));
        world.Options.Social.WarMode.Enabled = true;
        await RivalryKit.SeedAsync(world, "Hubot", "Hufoe", killerIsBot: true, kills: 1);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        await world.Host.OnWorldAsync(() => FlagForGood(RivalryKit.Online(world, "Hufoe")));

        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => brain.TryHunt(RivalryKit.Online(world, "Hubot"), 500) || brain.EngagedPlayer is not null).Result, "the bot to take its grudge on");
        Player foe = await world.Host.OnWorldAsync(() => RivalryKit.Online(world, "Hufoe"));
        Assert.Same(foe, await world.Host.OnWorldAsync(() => brain.EngagedPlayer));
        Assert.Equal(PlayerbotEngageReason.Grudge, await world.Host.OnWorldAsync(() => brain.EngagedReason));
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => ReferenceEquals(brain.InspectionPvpTarget, foe)).Result, "the bot to fight it");

        // The target walks into an exempt area: the next think lets go and never reaches in again.
        await world.Host.OnWorldAsync(() => PvpAreaState.SetWarModeExempt(foe, true));
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => brain.EngagedPlayer is null && brain.InspectionPvpTarget is null).Result, "the bot to give the chase up");
        Assert.False(await world.Host.OnWorldAsync(() => brain.TryHunt(RivalryKit.Online(world, "Hubot"), 500)));
    }

    [Fact]
    public async Task TheGrudgeReason_IsAllowedOnlyWhileWarModeIsOn_AndNeverIntoAnExemptArea()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        await world.AddBotAsync("Hubot");
        await world.AddBotAsync("Hufoe", race: RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Hubot", ("Hufoe", 5f));
        await world.Host.OnWorldAsync(() =>
        {
            Player self = RivalryKit.Online(world, "Hubot"), foe = RivalryKit.Online(world, "Hufoe");
            FlagForGood(foe);
            var war = new ArcaneCore.Game.WorldState.WarModeOptions();

            Assert.True(PlayerbotHostility.IsHostilePlayer(self, foe));
            Assert.False(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Grudge, null, war));      // War Mode off
            Assert.False(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Grudge, null, null));
            war.Enabled = true;
            Assert.True(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Grudge, null, war));
            Assert.True(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Help, null, null));         // help needs no War Mode

            PvpAreaState.SetWarModeExempt(foe, true);
            Assert.False(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Grudge, null, war));
            Assert.False(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Help, null, null));
            Assert.True(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Defend, null, null));       // a defend post keeps its own rule
            return true;
        });
    }

    // --- help against players and bots -----------------------------------------------------------------------------------------------------------

    public enum HelpCase { HelperFlaggedWarModeOff, HelperUnflaggedWarModeOn, HelperUnflaggedWarModeOff, AttackerInExemptArea, AttackerNotFlagged }

    private sealed record HelpOutcome(int Helped, bool Engaged, PlayerbotEngageReason Reason);

    private static async Task<HelpOutcome> HelpAsync(HelpCase scenario, bool attackerIsBot)
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid helper = await world.AddBotAsync("Hhelp");
        await world.AddBotAsync("Hvic");
        WorldTestClient? client = null;
        try
        {
            if (attackerIsBot) await world.AddBotAsync("Hfoe", race: RivalryKit.Undead);
            else client = await world.AddPlayerAsync("Hfoe", RivalryKit.Undead);
            await HelpTestKit.StandAsync(world, "Hhelp", ("Hvic", 6f), ("Hfoe", 8f));
            ObjectGuid helperGuid = await world.Host.PlayerStateAsync("Hhelp", p => p.Guid);
            ObjectGuid victimGuid = await world.Host.PlayerStateAsync("Hvic", p => p.Guid);

            await world.Host.OnWorldAsync(() =>
            {
                Player self = HelpTestKit.Online(world, "Hhelp"), vic = HelpTestKit.Online(world, "Hvic"), foe = HelpTestKit.Online(world, "Hfoe");
                FlagForGood(vic); // the victim is a fair target; the enemy attacks it
                if (scenario != HelpCase.AttackerNotFlagged) FlagForGood(foe);
                if (scenario == HelpCase.HelperFlaggedWarModeOff) FlagForGood(self);
                if (scenario == HelpCase.AttackerInExemptArea) PvpAreaState.SetWarModeExempt(foe, true);
                world.Options.Social.WarMode.Enabled = scenario == HelpCase.HelperUnflaggedWarModeOn;

                Assert.True(vic.Map!.Combat.Attack(foe, vic), "the enemy could not attack the victim");
                vic.Map!.Combat.DealDamage(foe, vic, 1);
                vic.Health = Math.Max(1u, vic.MaxHealth * 25 / 100);
            });

            long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(helperGuid.Value, victimGuid.Value, (uint)t, PlayerbotTraits.For(helperGuid.Value).Helpfulness, 0));
            await world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Help.Run(SocialTestKit.BotPass(world.Host, now)));

            PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
            PlayerbotBrain brain = HelpTestKit.BrainOf(world, helper);
            if (lane.Helped == 1)
            {
                await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => brain.EngagedPlayer is not null).Result, "the helper to take the attacker on");
            }

            return await world.Host.OnWorldAsync(() => new HelpOutcome(lane.Helped, brain.EngagedPlayer is { } engaged && ReferenceEquals(engaged, HelpTestKit.Online(world, "Hfoe")), brain.EngagedReason));
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(HelpCase.HelperFlaggedWarModeOff, true)]
    [InlineData(HelpCase.HelperFlaggedWarModeOff, false)]
    [InlineData(HelpCase.HelperUnflaggedWarModeOn, true)]
    [InlineData(HelpCase.HelperUnflaggedWarModeOn, false)]
    public async Task ABotAttackedByAFlaggedEnemy_DrawsASameFactionHelper_WhoTakesTheAttackerOn(HelpCase scenario, bool attackerIsBot)
    {
        HelpOutcome outcome = await HelpAsync(scenario, attackerIsBot);

        Assert.Equal(1, outcome.Helped);
        Assert.True(outcome.Engaged);
        Assert.Equal(PlayerbotEngageReason.Help, outcome.Reason);
    }

    [Theory]
    [InlineData(HelpCase.HelperUnflaggedWarModeOff)]
    [InlineData(HelpCase.AttackerInExemptArea)]
    [InlineData(HelpCase.AttackerNotFlagged)]
    public async Task NoHelperComes_WhenTheHelperWouldFlagItself_TheAttackerIsInAnExemptArea_OrTheAttackerIsNotFlagged(HelpCase scenario)
    {
        foreach (bool attackerIsBot in new[] { true, false })
        {
            HelpOutcome outcome = await HelpAsync(scenario, attackerIsBot);

            Assert.Equal(0, outcome.Helped);
            Assert.False(outcome.Engaged);
        }
    }

    [Fact]
    public async Task AttackerOf_ReturnsAnEnemyPlayer_OnlyWhenFlaggedAndMayFlagAndOutsideAnExemptArea()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await world.AddBotAsync("Hhelp");
        await world.AddBotAsync("Hvic");
        await world.AddBotAsync("Hfoe", race: RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Hhelp", ("Hvic", 6f), ("Hfoe", 8f));
        await world.Host.OnWorldAsync(() =>
        {
            Player self = HelpTestKit.Online(world, "Hhelp"), vic = HelpTestKit.Online(world, "Hvic"), foe = HelpTestKit.Online(world, "Hfoe");
            FlagForGood(vic);
            FlagForGood(foe);
            Assert.True(vic.Map!.Combat.Attack(foe, vic));

            Assert.Same(foe, PlayerbotHelpRules.AttackerOf(self, vic, mayFlag: true, groupSize: 1));
            Assert.Null(PlayerbotHelpRules.AttackerOf(self, vic, mayFlag: false, groupSize: 1));
            PvpAreaState.SetWarModeExempt(foe, true);
            Assert.Null(PlayerbotHelpRules.AttackerOf(self, vic, mayFlag: true, groupSize: 1));
            PvpAreaState.SetWarModeExempt(foe, false);
            foe.Level = (byte)(self.Level + PlayerbotHelpRules.MaxLevelsAbove + 1);
            Assert.Null(PlayerbotHelpRules.AttackerOf(self, vic, mayFlag: true, groupSize: 1)); // not winnable
            return true;
        });
    }
}

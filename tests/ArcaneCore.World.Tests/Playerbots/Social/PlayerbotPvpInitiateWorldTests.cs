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
/// <c>World:Playerbots:Pvp:Initiate</c> on a running world (docs/areas/playbots-combat.md): an idle bot starts on a flagged enemy player or bot nearby,
/// inside a level window, outside the exempt areas, when it is healthy and not outnumbered; with the rule off, or any one of those wrong, it does not.
/// Each refusal runs in a world built exactly like the one that acts, with one thing changed.
/// </summary>
public sealed class PlayerbotPvpInitiateWorldTests
{
    public enum Variation { None, Disabled, TargetUnflagged, TargetTooLow, TargetTooHigh, Outnumbered, LowHealth, BotInCombat, TargetInExemptArea, Matched, AtTheWindowEdge, TargetOutsideRadius }

    private static void FlagForGood(Player player)
    {
        player.Flags |= PlayerFlags.PvpDesired;
        PlayerbotPvpTestKit.Flag(player);
    }

    private static async Task<(bool Acted, PlayerbotEngageReason Reason)> InitiatesAsync(Variation variation, bool foeIsBot)
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        Guid bot = await world.AddBotAsync("Inbot");
        WorldTestClient? client = null;
        try
        {
            if (foeIsBot) await world.AddBotAsync("Infoe", race: RivalryKit.Undead);
            else client = await world.AddPlayerAsync("Infoe", RivalryKit.Undead);
            var stand = new List<(string, float)> { ("Infoe", variation == Variation.TargetOutsideRadius ? 40f : 6f) };
            if (variation is Variation.Outnumbered or Variation.Matched)
            {
                await world.AddBotAsync("Inextra", race: RivalryKit.Undead);
                stand.Add(("Inextra", 9f));
            }

            if (variation == Variation.Matched)
            {
                await world.AddBotAsync("Inally");
                stand.Add(("Inally", 3f));
            }

            await HelpTestKit.StandAsync(world, "Inbot", [.. stand]);
            // The rule stays off, and the foe in an exempt area, until the one world step that sets the variation up and asks.
            await world.Host.OnWorldAsync(() => PvpAreaState.SetWarModeExempt(RivalryKit.Online(world, "Infoe"), true));
            PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
            return await world.Host.OnWorldAsync(() =>
            {
                Player self = RivalryKit.Online(world, "Inbot");
                Player foe = RivalryKit.Online(world, "Infoe");
                self.Level = 20;
                foe.Level = variation switch
                {
                    Variation.TargetTooLow => 14,
                    Variation.TargetTooHigh => 24,
                    Variation.AtTheWindowEdge => 15,
                    _ => 20,
                };
                if (variation != Variation.TargetUnflagged) FlagForGood(foe);
                PvpAreaState.SetWarModeExempt(foe, variation == Variation.TargetInExemptArea);
                if (variation == Variation.LowHealth) self.Health = self.MaxHealth * 50 / 100;
                if (variation == Variation.BotInCombat) HelpTestKit.Engage(HelpTestKit.SpawnMob(self, -4f), self);
                PlayerbotPvpInitiateOptions rule = world.Options.Pvp.Initiate;
                rule.Enabled = variation != Variation.Disabled;
                rule.RadiusYards = 30;

                bool acted = brain.TryInitiate(self, 500) || brain.EngagedPlayer is not null;
                return (acted, brain.EngagedReason);
            });
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(Variation.None, true)]
    [InlineData(Variation.None, false)]
    [InlineData(Variation.Matched, true)]
    [InlineData(Variation.AtTheWindowEdge, false)]
    public async Task AnIdleBot_StartsOnAFlaggedEnemyBotOrPlayerNearby(Variation variation, bool foeIsBot)
    {
        (bool acted, PlayerbotEngageReason reason) = await InitiatesAsync(variation, foeIsBot);

        Assert.True(acted);
        Assert.Equal(PlayerbotEngageReason.Initiate, reason);
    }

    [Theory]
    [InlineData(Variation.Disabled)]
    [InlineData(Variation.TargetUnflagged)]
    [InlineData(Variation.TargetTooLow)]
    [InlineData(Variation.TargetTooHigh)]
    [InlineData(Variation.Outnumbered)]
    [InlineData(Variation.LowHealth)]
    [InlineData(Variation.BotInCombat)]
    [InlineData(Variation.TargetInExemptArea)]
    [InlineData(Variation.TargetOutsideRadius)]
    public async Task TheBot_DoesNotStart(Variation variation)
    {
        Assert.False((await InitiatesAsync(variation, foeIsBot: true)).Acted);
        Assert.False((await InitiatesAsync(variation, foeIsBot: false)).Acted);
    }

    [Fact]
    public async Task TheInitiateReason_IsAllowedOnlyWhileTheRuleIsOn_AndNeverIntoAnExemptArea()
    {
        await using SocialWorld world = await RivalryKit.StartAsync();
        await world.AddBotAsync("Inbot");
        await world.AddBotAsync("Infoe", race: RivalryKit.Undead);
        await HelpTestKit.StandAsync(world, "Inbot", ("Infoe", 5f));
        await world.Host.OnWorldAsync(() =>
        {
            Player self = RivalryKit.Online(world, "Inbot"), foe = RivalryKit.Online(world, "Infoe");
            FlagForGood(foe);
            var rule = new PlayerbotPvpInitiateOptions();

            Assert.False(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Initiate, null, null, rule));  // off by default
            Assert.False(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Initiate, null, null, null));
            rule.Enabled = true;
            Assert.True(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Initiate, null, null, rule));
            PvpAreaState.SetWarModeExempt(foe, true);
            Assert.False(PlayerbotHostility.MayEngage(self, foe, PlayerbotEngageReason.Initiate, null, null, rule));
            return true;
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASameFactionHelper_JoinsWhenTheTargetFightsTheInitiatorBack(bool foeIsBot)
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid initiator = await world.AddBotAsync("Inini");
        Guid helper = await world.AddBotAsync("Inhelp");
        WorldTestClient? client = null;
        try
        {
            if (foeIsBot) await world.AddBotAsync("Infoe", race: RivalryKit.Undead);
            else client = await world.AddPlayerAsync("Infoe", RivalryKit.Undead);
            await HelpTestKit.StandAsync(world, "Inini", ("Inhelp", 5f), ("Infoe", 8f));
            ObjectGuid helperGuid = await world.Host.PlayerStateAsync("Inhelp", p => p.Guid);
            ObjectGuid iniGuid = await world.Host.PlayerStateAsync("Inini", p => p.Guid);
            PlayerbotBrain iniBrain = HelpTestKit.BrainOf(world, initiator);

            bool started = await world.Host.OnWorldAsync(() =>
            {
                Player ini = HelpTestKit.Online(world, "Inini"), foe = HelpTestKit.Online(world, "Infoe"), help = HelpTestKit.Online(world, "Inhelp");
                FlagForGood(foe);
                FlagForGood(ini); // the initiator carries the flag a PvP fight gives; a flagged helper may join
                FlagForGood(help);
                world.Options.Pvp.Initiate.Enabled = true;
                bool began = iniBrain.TryInitiate(ini, 500);
                world.Options.Pvp.Initiate.Enabled = false; // from here only the help lane can bring the helper in, not its own initiative
                return began;
            });
            Assert.True(started, "the initiator did not start on the enemy");

            await world.Host.OnWorldAsync(() =>
            {
                Player ini = HelpTestKit.Online(world, "Inini"), foe = HelpTestKit.Online(world, "Infoe");
                Assert.True(ini.Map!.Combat.Attack(foe, ini), "the enemy could not attack back");
                ini.Map!.Combat.DealDamage(foe, ini, 1);
                ini.Health = Math.Max(1u, ini.MaxHealth * 25 / 100);
            });

            long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(helperGuid.Value, iniGuid.Value, (uint)t, PlayerbotTraits.For(helperGuid.Value).Helpfulness, 0));
            await world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Help.Run(SocialTestKit.BotPass(world.Host, now)));
            PlayerbotBrain helperBrain = HelpTestKit.BrainOf(world, helper);
            await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => helperBrain.EngagedPlayer is not null).Result, "the helper to take the enemy on");

            Assert.Equal(PlayerbotEngageReason.Help, await world.Host.OnWorldAsync(() => helperBrain.EngagedReason));
            Assert.Same(await world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Infoe")), await world.Host.OnWorldAsync(() => helperBrain.EngagedPlayer));
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
    }
}

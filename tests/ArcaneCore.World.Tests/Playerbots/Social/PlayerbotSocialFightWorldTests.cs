using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The fight-driven social triggers on a running world, through the real combat paths (<c>MapCombat.DealDamage</c> and its kill path, the
/// threat list of a creature that fights the bot, the bot's own think): helpers earn affinity once per fight, a bot that dies after asking
/// for help blames only the bystanders it asked, and a PvP kill (never a duel) is rivalry for the bot on either side.
/// </summary>
public sealed class PlayerbotSocialFightWorldTests
{
    private const uint MobEntry = 990_520;

    private static readonly CreatureTemplate Mob = new()
    {
        Entry = MobEntry, Name = "social test mob", CreatureType = 1, MinLevel = 1, MaxLevel = 1, MinLevelHealth = 200_000, MaxLevelHealth = 200_000,
    };

    private static PlayerbotSocialMemory MemoryOf(SocialWorld world) => PartyTestHost.Feature(world.Host).Social.Memory;

    private static PlayerbotSocialRecorder RecorderOf(SocialWorld world) => PartyTestHost.Feature(world.Host).Social;

    private static PlayerbotRelation? RelationOf(SocialWorld world, Guid bot, ObjectGuid other)
        => MemoryOf(world).TryGet(bot, other.Value, out PlayerbotRelation relation) ? relation : null;

    private static Task UntilAsync(Func<bool> condition, string what, int seconds = 20) => PlayerbotSocialTriggerWorldTests.UntilAsync(condition, what, seconds);

    /// <summary>World thread: <paramref name="mob"/> fights <paramref name="victim"/> (the swing is registered, so the mob is among its attackers) and lands a hit.</summary>
    private static void Engage(Creature mob, Player victim, uint damage = 1)
    {
        Assert.True(victim.Map!.Combat.Attack(mob, victim), "the mob could not attack the player");
        victim.Map!.Combat.DealDamage(mob, victim, damage);
    }

    private static Player Online(SocialWorld world, string name) => world.Host.World.FindOnlinePlayer(name)!;

    /// <summary>World thread: a creature that nobody updates (it neither moves nor attacks) beside <paramref name="near"/>.</summary>
    private static Creature SpawnMob(Player near, float dx = 3f)
    {
        CreatureMapSystem system = Systems.GetValue(near.Map!, map => new CreatureMapSystem(map, new CreatureContent([Mob], [], [], [], []), random: new Random(1)));
        return system.SpawnTemporary(Mob, near.X + dx, near.Y, near.Z, 0f);
    }

    // One creature system per map: each numbers its own spawns, so a second one would hand out the same guid again.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ArcaneCore.Game.Maps.Map, CreatureMapSystem> Systems = new();

    /// <summary>Put <paramref name="players"/> at the given offsets east of <paramref name="anchor"/> and wait until the anchor sees them all.</summary>
    private static async Task StandAsync(SocialWorld world, string anchor, params (string Name, float Dx)[] players)
    {
        await world.Host.OnWorldAsync(() =>
        {
            Player a = Online(world, anchor);
            foreach ((string name, float dx) in players) Online(world, name).Relocate(a.X + dx, a.Y, a.Z, 0f, world.Host.World.NowMs);
        });
        await world.Host.WaitForWorldAsync(() =>
        {
            Player a = Online(world, anchor);
            return players.All(p => a.VisibleObjects.Contains(Online(world, p.Name).Guid));
        }, "the bystanders to be in sight");
    }

    /// <summary>
    /// Wait until a PvP kill involving any of <paramref name="bots"/> or <paramref name="players"/> would be heard: each bot is one the recorder tracks, each player is online,
    /// and the world's kill feed listens on its map. These are the two conditions <see cref="PlayerbotSocialRecorder.NotePvpKill"/> needs.
    /// </summary>
    private static Task KillsHeardAsync(SocialWorld world, string[] bots, params string[] players)
        => world.Host.WaitForWorldAsync(() => PartyTestHost.Feature(world.Host).PvpKills is { } feed
            && bots.All(name => world.Host.World.FindOnlinePlayer(name) is { Map: { } map } p && RecorderOf(world).IsManaged(p.Guid) && feed.Hears(map))
            && players.All(name => world.Host.World.FindOnlinePlayer(name) is { Map: { } map } && feed.Hears(map)),
            "the kill feed to hear every map and the recorder to track every bot");

    private static void Reposition(SocialWorld world, string anchor, string name, float dx)
    {
        Player a = Online(world, anchor);
        Online(world, name).Relocate(a.X + dx, a.Y, a.Z, 0f, world.Host.World.NowMs);
    }

    // --- d) helped by someone who joins the bot's fight -----------------------------------------------------------------------

    [Fact]
    public async Task AUnitOnTheThreatListOfAMobFightingTheBot_IsHelpedBy_OncePerFight_WithNoDoubleCountFromTheAssistPath()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid victim = await world.AddBotAsync("Helpvictim");
        Guid helperBot = await world.AddBotAsync("Helpbotter");
        await using WorldTestClient first = await world.AddPlayerAsync("Helpfirst");
        await using WorldTestClient second = await world.AddPlayerAsync("Helpsecond");
        await using WorldTestClient orc = await world.AddPlayerAsync("Helporc", SocialTestKit.Orc);
        await StandAsync(world, "Helpvictim", ("Helpbotter", 2f), ("Helpfirst", 2.5f), ("Helpsecond", 3f), ("Helporc", 3.5f));
        ObjectGuid victimGuid = await world.Host.PlayerStateAsync("Helpvictim", p => p.Guid);
        ObjectGuid botHelperGuid = await world.Host.PlayerStateAsync("Helpbotter", p => p.Guid);
        ObjectGuid firstGuid = await world.Host.PlayerStateAsync("Helpfirst", p => p.Guid);
        ObjectGuid secondGuid = await world.Host.PlayerStateAsync("Helpsecond", p => p.Guid);
        ObjectGuid orcGuid = await world.Host.PlayerStateAsync("Helporc", p => p.Guid);
        short help = (short)new PlayerbotSocialMemoryOptions().AffinityPerHelp;

        Creature mob = null!;
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = Online(world, "Helpvictim");
            mob = SpawnMob(bot);
            Engage(mob, bot); // the mob fights the bot
            Assert.Contains(mob, bot.Combat.Attackers);
            // The bot's own assist path reports the first helper at once (what PlayerbotBrain.BeginAssist does), before the fight read sees it.
            RecorderOf(world).NoteHelp(Online(world, "Helpfirst"), bot);
            // Everybody joins in: a hit on the mob puts the hitter on its threat list.
            foreach (string name in new[] { "Helpfirst", "Helpsecond", "Helporc", "Helpbotter" })
                bot.Map!.Combat.DealDamage(Online(world, name), mob, 1);
        });

        // Each of them is helped-by once. The first came through the assist path and is not counted again by the threat read.
        await UntilAsync(() => new[] { secondGuid, orcGuid, botHelperGuid }.All(g => RelationOf(world, victim, g) is not null), "the fight read to record every helper");
        await Task.Delay(3500); // several more reads of the same fight
        Assert.Equal(help, RelationOf(world, victim, firstGuid)!.Affinity);
        Assert.Equal(help, RelationOf(world, victim, secondGuid)!.Affinity);
        Assert.Equal(help, RelationOf(world, victim, orcGuid)!.Affinity);
        Assert.Equal(1, RelationOf(world, victim, secondGuid)!.Familiarity);
        Assert.True(RelationOf(world, victim, botHelperGuid)!.OtherIsBot);
        Assert.False(RelationOf(world, victim, secondGuid)!.OtherIsBot);

        // Across factions the help counts, marked as the other side's.
        Assert.True(RelationOf(world, victim, orcGuid)!.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        Assert.False(RelationOf(world, victim, secondGuid)!.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));

        // Two managed bots on one mob help each other, and each remembers it once: the helper bot is credited toward the victim (HelpedBy) and
        // remembers helping it (HelpedThem); the victim bot fighting the mob too is the same help seen from the other side, not a second one.
        PlayerbotRelation byBot = RelationOf(world, victim, botHelperGuid)!;
        PlayerbotRelation helped = RelationOf(world, helperBot, victimGuid)!;
        Assert.True(byBot.OtherIsBot && helped.OtherIsBot);
        Assert.Equal(help, byBot.Affinity);
        Assert.Equal(help, helped.Affinity);
        Assert.Equal(1, byBot.Familiarity);
        Assert.Equal(1, helped.Familiarity);
        short botBefore = byBot.Affinity, helperBefore = helped.Affinity;

        // The assist path firing again for the same pair in the same fight changes nothing.
        await world.Host.OnWorldAsync(() =>
        {
            RecorderOf(world).NoteHelp(Online(world, "Helpsecond"), Online(world, "Helpvictim"));
            RecorderOf(world).NoteHelp(Online(world, "Helpbotter"), Online(world, "Helpvictim"));
        });
        Assert.Equal(help, RelationOf(world, victim, secondGuid)!.Affinity);
        Assert.Equal(botBefore, RelationOf(world, victim, botHelperGuid)!.Affinity);
        Assert.Equal(helperBefore, RelationOf(world, helperBot, victimGuid)!.Affinity);

        // The fight ends and a new one starts (another mob): the helper who joins it is credited again.
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = Online(world, "Helpvictim");
            bot.Map!.Combat.CombatStop(bot);
            Assert.False(bot.Combat.IsInCombat);
        });
        PlayerbotFightSocial tracker = PartyTestHost.Feature(world.Host).FightOf(victim)!;
        await world.Host.WaitForWorldAsync(() => !tracker.InFight, "the fight tracker to see the fight end");
        await world.Host.OnWorldAsync(() =>
        {
            Player bot = Online(world, "Helpvictim");
            Creature next = SpawnMob(bot, 4f);
            Engage(next, bot);
            bot.Map!.Combat.DealDamage(Online(world, "Helpsecond"), next, 1);
        });
        await UntilAsync(() => RelationOf(world, victim, secondGuid)!.Affinity == 2 * help, "the second fight to credit the helper again");
        await Task.Delay(2500);
        Assert.Equal(2 * help, RelationOf(world, victim, secondGuid)!.Affinity);
    }

    [Fact]
    public async Task TheFightObserver_AllocatesNothing_OutOfCombat_OrInsideACombatThinkThatDoesNotSample()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        await world.AddBotAsync("Allocbot");
        PlayerbotSocialRecorder recorder = RecorderOf(world);
        var options = new PlayerbotSocialMemoryOptions();
        var fight = new PlayerbotFightSocial(Guid.NewGuid(), options) { Recorder = recorder };

        await world.Host.OnWorldAsync(() =>
        {
            Player bot = Online(world, "Allocbot");
            WorldRuntime runtime = world.Host.World;
            for (int i = 0; i < 50; i++) fight.Observe(bot, runtime, 1000); // warm up: jitted, delegates cached
            Assert.False(bot.Combat.IsInCombat);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) fight.Observe(bot, runtime, 1000);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000, "per-call allocation"); // the closure it replaced cost 56 bytes a call

            // In a fight, between two samples (a think a second apart does read the threat lists; a think inside the second does not).
            Creature mob = SpawnMob(bot);
            Engage(mob, bot);
            Assert.True(bot.Combat.IsInCombat);
            fight.Observe(bot, runtime, 5000);
            Assert.True(fight.InFight);
            for (int i = 0; i < 50; i++) fight.Observe(bot, runtime, 5001);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) fight.Observe(bot, runtime, 5001);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000, "per-call allocation"); // the closure it replaced cost 56 bytes a call

            // A think that samples (a second on from the last): the attacker and threat sets and the mob's threat list are read without an enumerator allocation.
            uint at = 6000;
            for (int i = 0; i < 50; i++) fight.Observe(bot, runtime, at += PlayerbotFightSocial.SampleMs);
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) fight.Observe(bot, runtime, at += PlayerbotFightSocial.SampleMs);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_000, "per-sample allocation");
        });
    }

    [Fact]
    public async Task AnAssistBetweenTwoBots_IsOneHelp_ExactlyOnEachSide_AssistPathAndFightReadsBoth()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid victim = await world.AddBotAsync("Assistvictim");
        Guid helper = await world.AddBotAsync("Assisthelper");
        await StandAsync(world, "Assistvictim", ("Assisthelper", 2f));
        ObjectGuid victimGuid = await world.Host.PlayerStateAsync("Assistvictim", p => p.Guid);
        ObjectGuid helperGuid = await world.Host.PlayerStateAsync("Assisthelper", p => p.Guid);
        short help = (short)new PlayerbotSocialMemoryOptions().AffinityPerHelp;

        await world.Host.OnWorldAsync(() =>
        {
            Player v = Online(world, "Assistvictim"), h = Online(world, "Assisthelper");
            Creature mob = SpawnMob(v);
            Engage(mob, v); // the mob fights the victim bot
            RecorderOf(world).NoteHelp(h, v); // what the helper bot's BeginAssist reports
            v.Map!.Combat.DealDamage(h, mob, 1); // and the helper is on the mob's threat list, which the victim's read finds; the helper's own read finds the victim
        });

        await UntilAsync(() => RelationOf(world, victim, helperGuid) is not null && RelationOf(world, helper, victimGuid) is not null, "both bots to remember the assist");
        await Task.Delay(3500); // several reads of both fight trackers
        PlayerbotRelation byHelper = RelationOf(world, victim, helperGuid)!;
        PlayerbotRelation ofVictim = RelationOf(world, helper, victimGuid)!;
        Assert.Equal(help, byHelper.Affinity);
        Assert.Equal(help, ofVictim.Affinity);
        Assert.Equal(1, byHelper.Familiarity);
        Assert.Equal(1, ofVictim.Familiarity);
    }

    [Fact]
    public async Task AHelpCredit_EndsWithTheFightOfTheBotItBelongsTo_WhetherTheBotHelpedOrWasHelped()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid bot = await world.AddBotAsync("Endbot");
        await using WorldTestClient other = await world.AddPlayerAsync("Endplayer");
        await StandAsync(world, "Endbot", ("Endplayer", 2f));
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Endbot", p => p.Guid);
        ObjectGuid otherGuid = await world.Host.PlayerStateAsync("Endplayer", p => p.Guid);
        short help = (short)new PlayerbotSocialMemoryOptions().AffinityPerHelp;

        // The player helps the bot; then the bot's fight ends, and the bot helping the player (a victim with no fight tracker of its own) is credited again, once.
        await world.Host.OnWorldAsync(() =>
        {
            RecorderOf(world).NoteHelp(Online(world, "Endplayer"), Online(world, "Endbot"));
            RecorderOf(world).NoteHelp(Online(world, "Endplayer"), Online(world, "Endbot"));
        });
        Assert.Equal(help, RelationOf(world, bot, otherGuid)!.Affinity);
        await world.Host.OnWorldAsync(() =>
        {
            RecorderOf(world).EndFight(botGuid);
            RecorderOf(world).NoteHelp(Online(world, "Endbot"), Online(world, "Endplayer"));
            RecorderOf(world).NoteHelp(Online(world, "Endbot"), Online(world, "Endplayer"));
        });
        Assert.Equal(2 * help, RelationOf(world, bot, otherGuid)!.Affinity); // the credit ended with the bot's fight; the reverse report within it counted nothing
    }

    [Fact]
    public async Task AMobHitOnlyByTheBotsEnemy_OrAnUnrelatedPlayerOutOfTheFight_CreditsNobody()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid victim = await world.AddBotAsync("Helpnone");
        await using WorldTestClient joiner = await world.AddPlayerAsync("Helpjoiner");
        await using WorldTestClient idle = await world.AddPlayerAsync("Helpidle");
        await StandAsync(world, "Helpnone", ("Helpjoiner", 2f), ("Helpidle", 3f));
        ObjectGuid joinerGuid = await world.Host.PlayerStateAsync("Helpjoiner", p => p.Guid);
        ObjectGuid idleGuid = await world.Host.PlayerStateAsync("Helpidle", p => p.Guid);

        await world.Host.OnWorldAsync(() =>
        {
            Player bot = Online(world, "Helpnone");
            Creature mob = SpawnMob(bot);
            Engage(mob, bot);
            bot.Map!.Combat.DealDamage(Online(world, "Helpjoiner"), mob, 1); // the control: this one is credited
        });

        await UntilAsync(() => RelationOf(world, victim, joinerGuid) is not null, "the joiner to be credited");
        await Task.Delay(1500);
        Assert.Null(RelationOf(world, victim, idleGuid)); // stood next to the fight and did nothing
    }

    // --- e) a bot that dies calls for help or goes it alone -----------------------------------------------------------------

    private static Task<SocialWorld> StartDeathWorldAsync(double baseChance, double sociabilityWeight) => SocialWorld.StartAsync(options =>
    {
        options.Chat.Enabled = true;
        options.Social.Memory.BystanderMinSeconds = 1;
        options.Social.Memory.CallForHelpBaseChance = (float)baseChance;
        options.Social.Memory.CallForHelpSociabilityWeight = (float)sociabilityWeight;
    }, frozen: true);

    /// <summary>World thread: the mob hits the bot down to a third of its health and keeps fighting it, and the bot is then in its low-health window.</summary>
    private static Creature Wound(SocialWorld world, string botName)
    {
        Player bot = Online(world, botName);
        Creature mob = SpawnMob(bot);
        Engage(mob, bot, bot.Health - Math.Max(1u, bot.MaxHealth / 3));
        Assert.True(bot.IsAlive && bot.Combat.IsInCombat);
        return mob;
    }

    [Fact]
    public async Task ABotThatDiesAfterAskingForHelp_RecordsWatchedMeDie_OnlyTowardTheAskedBystandersThatStillQualify()
    {
        await using SocialWorld world = await StartDeathWorldAsync(baseChance: 1.0, sociabilityWeight: 0.0);
        Guid bot = await world.AddBotAsync("Deathasker");
        string[] names = ["Bystok", "Bystfoe", "Bystfar", "Bystbusy", "Bystweak", "Bystaway", "Bysthurt", "Bystfight", "Bystlastfar", "Bystlastlow", "Bystlastbusy"];
        var clients = new List<WorldTestClient>();
        try
        {
            foreach (string name in names) clients.Add(await world.AddPlayerAsync(name));
            var guids = new Dictionary<string, ObjectGuid>();
            foreach (string name in names) guids[name] = await world.Host.PlayerStateAsync(name, p => p.Guid);
            ObjectGuid G(string name) => guids[name];
            await StandAsync(world, "Deathasker", [.. names.Select((n, i) => (n, 4f + i * 0.5f))]);

            // Before the fight: one bystander is already disliked (a strike), so it is a candidate that is never asked.
            ObjectGuid foe = G("Bystfoe");
            await world.Host.OnWorldAsync(() =>
            {
                Player b = Online(world, "Deathasker");
                MemoryOf(world).Note(bot, (int)b.Guid.Low, foe.Value, false, PlayerbotSocialEvent.ChatStrike);
                // Not candidates when the window opens: out of range, already in combat, already hurt.
                Reposition(world, "Deathasker", "Bystfar", 40f);
                Online(world, "Bystweak").Health = Online(world, "Bystweak").MaxHealth / 4;
                Creature other = SpawnMob(Online(world, "Bystbusy"), 6f);
                Engage(other, Online(world, "Bystbusy"));
                Assert.True(Online(world, "Bystbusy").Combat.IsInCombat);
            });
            short disliked = RelationOf(world, bot, foe)!.Affinity;
            Assert.True(disliked < 0);
            await world.Host.WaitForWorldAsync(() => Online(world, "Deathasker").VisibleObjects.Contains(Online(world, "Bystfar").Guid), "the far bystander to be in sight");

            Creature mob = null!;
            await world.Host.OnWorldAsync(() => mob = Wound(world, "Deathasker"));
            // The window opens at the next think and the decision is made once: the bot calls out. The shout is the chat service's announcement of this
            // world (its only one: the social passes are frozen), so an answered announcement is the sign that the bot asked.
            await UntilAsync(() => PartyTestHost.Feature(world.Host).Chat!.Status().Answered > 0, "the bot to call for help");

            // Later in the window: one walks off, one is hurt, one is drawn into a fight. Time enough for the window's re-checks.
            await world.Host.OnWorldAsync(() =>
            {
                Reposition(world, "Deathasker", "Bystaway", 45f);
                Online(world, "Bysthurt").Health = Online(world, "Bysthurt").MaxHealth / 5;
                Creature other = SpawnMob(Online(world, "Bystfight"), 6f);
                Engage(other, Online(world, "Bystfight"));
                // The far one comes close again and the busy and weak ones recover: they were not there to be asked when the window opened.
                Reposition(world, "Deathasker", "Bystfar", 6f);
                Online(world, "Bystweak").Health = Online(world, "Bystweak").MaxHealth;
            });
            // The window's re-check (once a second while it is open) has ruled out the three that stopped qualifying, and the window is open long
            // enough for a death in it to be blamed on anyone (the same test the death itself runs).
            PlayerbotFightSocial tracker = PartyTestHost.Feature(world.Host).FightOf(bot)!;
            PlayerbotSocialMemoryOptions memoryOptions = PartyTestHost.PlayerbotOptionsOf(world.Host).Social.Memory;
            ulong away = G("Bystaway").Value, hurt = G("Bysthurt").Value, fighting = G("Bystfight").Value;
            await world.Host.WaitForWorldAsync(() => tracker.WindowOpen
                && tracker.Holds(away) == false && tracker.Holds(hurt) == false && tracker.Holds(fighting) == false
                && PlayerbotCallForHelp.WindowLongEnough(memoryOptions, unchecked(world.Host.World.NowMs - tracker.WindowStartMs)),
                "the window to re-check the bystanders and be long enough");

            // The bot dies. At the same instant three more fail the final check only.
            await world.Host.OnWorldAsync(() =>
            {
                Player b = Online(world, "Deathasker");
                Reposition(world, "Deathasker", "Bystlastfar", 50f);
                Online(world, "Bystlastlow").Health = Online(world, "Bystlastlow").MaxHealth / 4;
                Creature other = SpawnMob(Online(world, "Bystlastbusy"), 6f);
                Engage(other, Online(world, "Bystlastbusy"));
                b.Map!.Combat.DealDamage(mob, b, b.Health);
                Assert.False(b.IsAlive);
            });

            // The one who was asked, healthy, in range and out of the fight for the whole window is the one the bot remembers watching it die.
            await UntilAsync(() => RelationOf(world, bot, G("Bystok")) is not null, "the death to be blamed on the bystander");
            await Task.Delay(500);
            PlayerbotRelation watched = RelationOf(world, bot, G("Bystok"))!;
            Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerWatchedDie, watched.Affinity);
            Assert.False(watched.OtherIsBot);
            Assert.Equal(1, watched.Familiarity);
            foreach (string name in names.Where(n => n is not "Bystok" and not "Bystfoe"))
                Assert.True(RelationOf(world, bot, G(name)) is null, $"{name} was blamed for watching the bot die");
            Assert.Equal(disliked, RelationOf(world, bot, foe)!.Affinity); // a bot never begs someone it dislikes, so it blames them for nothing
        }
        finally
        {
            foreach (WorldTestClient client in clients) await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task ABotThatGoesItAlone_RecordsNoWatchedMeDie_EvenWithAQualifyingBystanderBesideIt()
    {
        // The same fight as above with the chance of asking at zero: the bot decides to fight on alone and says nothing.
        await using SocialWorld world = await StartDeathWorldAsync(baseChance: 0.0, sociabilityWeight: 0.0);
        Guid bot = await world.AddBotAsync("Deathalone");
        await using WorldTestClient bystander = await world.AddPlayerAsync("Alonewatcher");
        await StandAsync(world, "Deathalone", ("Alonewatcher", 4f));
        ObjectGuid watcher = await world.Host.PlayerStateAsync("Alonewatcher", p => p.Guid);

        Creature mob = null!;
        await world.Host.OnWorldAsync(() => mob = Wound(world, "Deathalone"));
        // The window opens and the bot decides, once, to fight on alone: the bystander is a candidate it did not ask.
        PlayerbotFightSocial tracker = PartyTestHost.Feature(world.Host).FightOf(bot)!;
        await world.Host.WaitForWorldAsync(() => tracker.WindowOpen, "the low-health window to open");
        await Task.Delay(2500); // longer than the window needs
        await world.Host.OnWorldAsync(() =>
        {
            Assert.True(tracker.WindowOpen && tracker.InFight);
            Assert.False(tracker.AskedForHelp);
            Assert.Contains(tracker.Candidates, c => c.Guid == watcher.Value && !c.Asked);
            Player b = Online(world, "Deathalone");
            b.Map!.Combat.DealDamage(mob, b, b.Health);
            Assert.False(b.IsAlive);
        });

        await world.Host.WaitForWorldAsync(() => !tracker.InFight, "the bot's next think to see the death"); // the fight ends with it
        Assert.Null(RelationOf(world, bot, watcher));
        Assert.Equal(0, world.Log.Count("Deathalone", ArcaneCore.Protocol.WorldOpcode.CmsgTextEmote));
        Assert.Equal(0, PartyTestHost.Feature(world.Host).Chat!.Status().Answered); // no shout either
        Assert.Empty(MemoryOf(world).Snapshot(bot));
    }

    [Fact]
    public async Task ABotThatDiesInsideAStartedDuel_RecordsNoWatchedMeDie_WhereTheSameDeathOutsideOneDoes()
    {
        // A started duel is dropped by the map's next update when no duel flag stands in the world, so this drives the fight observer by hand
        // (the world thread, a clock the test sets) on two sessions that nothing else ticks: the same wounded bot, the same asked bystander, the same
        // mob, the same death, and the duel the only difference. The combat, the kill and the memory are the real ones.
        await using SocialWorld world = await StartDeathWorldAsync(baseChance: 1.0, sociabilityWeight: 0.0);
        WorldSession duelSession = await PartyTestHost.EnterManagedAsync(world.Host, "DUELBOTA", "Duelbota");
        WorldSession plainSession = await PartyTestHost.EnterManagedAsync(world.Host, "DUELBOTB", "Duelbotb");
        await using WorldTestClient bystander = await world.AddPlayerAsync("Duelwatcher");
        try
        {
            ManagedPlayerbotFeature feature = PartyTestHost.Feature(world.Host);
            PlayerbotSocialRecorder recorder = feature.Social;
            var options = PartyTestHost.PlayerbotOptionsOf(world.Host).Social.Memory;
            Guid duelBot = Guid.NewGuid(), plainBot = Guid.NewGuid();
            await StandAsync(world, "Duelbota", ("Duelbotb", 2f), ("Duelwatcher", 4f));
            ObjectGuid watcher = await world.Host.PlayerStateAsync("Duelwatcher", p => p.Guid);
            var asked = new List<string>();
            PlayerbotFightSocial Observer(Guid id) => new(id, options) { Recorder = recorder, CallForHelp = (bot, _) => { asked.Add(bot.Name); return true; } };
            PlayerbotFightSocial duelFight = Observer(duelBot), plainFight = Observer(plainBot);

            await world.Host.OnWorldAsync(() =>
            {
                Player duelist = duelSession.Player!, plain = plainSession.Player!;
                recorder.Track(duelBot, (int)duelist.Guid.Low);
                recorder.Track(plainBot, (int)plain.Guid.Low);
                Creature duelMob = SpawnMob(duelist, 2f), plainMob = SpawnMob(plain, 2f);
                Engage(duelMob, duelist, duelist.Health - duelist.MaxHealth / 3);
                Engage(plainMob, plain, plain.Health - plain.MaxHealth / 3);
                WorldRuntime runtime = world.Host.World;

                // The window opens, the decision is made (asked), and a second later the window is re-checked.
                duelFight.Observe(duelist, runtime, 10_000);
                plainFight.Observe(plain, runtime, 10_000);
                Assert.True(duelFight.WindowOpen && plainFight.WindowOpen);
                Assert.True(duelFight.AskedForHelp && plainFight.AskedForHelp);
                Assert.Contains(duelFight.Candidates, c => c.Guid == watcher.Value && c.Asked); // the duel bot had a bystander to blame
                duelFight.Observe(duelist, runtime, 11_500);
                plainFight.Observe(plain, runtime, 11_500);

                // The duelist is in a started duel with the plain bot when the mob finishes both off.
                duelist.Duel = new DuelInfo(duelist, plain) { StartTimeSeconds = 1 };
                plain.Duel = null;
                duelist.Map!.Combat.DealDamage(duelMob, duelist, duelist.Health);
                plain.Map!.Combat.DealDamage(plainMob, plain, plain.Health);
                Assert.False(duelist.IsAlive);
                Assert.False(plain.IsAlive);
                Assert.NotNull(duelist.Duel);
                duelFight.Observe(duelist, runtime, 13_000);
                plainFight.Observe(plain, runtime, 13_000);
            });

            Assert.True(MemoryOf(world).TryGet(plainBot, watcher.Value, out PlayerbotRelation blamed), "the bot that died outside a duel blames the asked bystander");
            Assert.Equal((short)options.AffinityPerWatchedDie, blamed.Affinity);
            Assert.False(MemoryOf(world).TryGet(duelBot, watcher.Value, out _), "the bot that died inside a duel blames nobody");
            Assert.Empty(MemoryOf(world).Snapshot(duelBot));
        }
        finally
        {
            duelSession.Kick();
            plainSession.Kick();
            await duelSession.ManagedClosed;
            await plainSession.ManagedClosed;
        }
    }

    // --- f) PvP kills ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APvpKill_RecordsKilledBy_AndKilledThem_WithOtherFactionSetWhenTheTeamsDiffer()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid slayer = await world.AddBotAsync("Pvpslayer");
        Guid orcBot = await world.AddBotAsync("Pvporcbot", race: SocialTestKit.Orc);
        Guid humanBot = await world.AddBotAsync("Pvphumanbot");
        Guid sameBot = await world.AddBotAsync("Pvpsamebot");
        await using WorldTestClient orcPlayer = await world.AddPlayerAsync("Pvporcplayer", SocialTestKit.Orc);
        await using WorldTestClient humanPlayer = await world.AddPlayerAsync("Pvphumplayer");
        ObjectGuid slayerGuid = await world.Host.PlayerStateAsync("Pvpslayer", p => p.Guid);
        ObjectGuid orcBotGuid = await world.Host.PlayerStateAsync("Pvporcbot", p => p.Guid);
        ObjectGuid humanBotGuid = await world.Host.PlayerStateAsync("Pvphumanbot", p => p.Guid);
        ObjectGuid sameBotGuid = await world.Host.PlayerStateAsync("Pvpsamebot", p => p.Guid);
        ObjectGuid orcPlayerGuid = await world.Host.PlayerStateAsync("Pvporcplayer", p => p.Guid);
        ObjectGuid humanPlayerGuid = await world.Host.PlayerStateAsync("Pvphumplayer", p => p.Guid);
        await KillsHeardAsync(world, ["Pvpslayer", "Pvporcbot", "Pvphumanbot", "Pvpsamebot"], "Pvporcplayer", "Pvphumplayer");
        var options = new PlayerbotSocialMemoryOptions();

        await world.Host.OnWorldAsync(() =>
        {
            Player Get(string name) => Online(world, name);
            void Slay(Player killer, Player victim) => killer.Map!.Combat.DealDamage(killer, victim, victim.Health);
            Slay(Get("Pvpslayer"), Get("Pvporcbot"));      // bot kills bot, teams differ
            Slay(Get("Pvporcplayer"), Get("Pvphumanbot")); // a player kills a bot, teams differ
            Slay(Get("Pvphumplayer"), Get("Pvpsamebot")); // a player kills a bot, same team
            Slay(Get("Pvpslayer"), Get("Pvporcplayer"));   // a bot kills a player (the killer's side only)
            Assert.False(Get("Pvporcbot").IsAlive);
            Assert.False(Get("Pvphumanbot").IsAlive);
            Assert.False(Get("Pvpsamebot").IsAlive);
        });

        await UntilAsync(() => RelationOf(world, slayer, orcBotGuid) is not null && RelationOf(world, orcBot, slayerGuid) is not null
            && RelationOf(world, humanBot, orcPlayerGuid) is not null && RelationOf(world, sameBot, humanPlayerGuid) is not null
            && RelationOf(world, slayer, orcPlayerGuid) is not null, "every kill to be remembered");
        await Task.Delay(500);

        // Bot against bot, across the teams: both remember, both mark the other side.
        PlayerbotRelation killerSide = RelationOf(world, slayer, orcBotGuid)!;
        Assert.Equal((short)options.AffinityPerKilledThem, killerSide.Affinity);
        Assert.True(killerSide.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        Assert.True(killerSide.OtherIsBot);
        PlayerbotRelation victimSide = RelationOf(world, orcBot, slayerGuid)!;
        Assert.Equal((short)options.AffinityPerKilledBy, victimSide.Affinity);
        Assert.True(victimSide.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        Assert.True(victimSide.OtherIsBot);

        // A player's kill of a bot: only the victim bot has a memory.
        PlayerbotRelation byPlayer = RelationOf(world, humanBot, orcPlayerGuid)!;
        Assert.Equal((short)options.AffinityPerKilledBy, byPlayer.Affinity);
        Assert.True(byPlayer.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        Assert.False(byPlayer.OtherIsBot);

        // The same team: still rivalry, but not another faction's.
        PlayerbotRelation sameTeam = RelationOf(world, sameBot, humanPlayerGuid)!;
        Assert.Equal((short)options.AffinityPerKilledBy, sameTeam.Affinity);
        Assert.False(sameTeam.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));

        // A bot's kill of a player: the killer remembers the victim (the victim is a player and has no memory to write).
        PlayerbotRelation victimPlayer = RelationOf(world, slayer, orcPlayerGuid)!;
        Assert.Equal((short)options.AffinityPerKilledThem, victimPlayer.Affinity);
        Assert.True(victimPlayer.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
        Assert.False(victimPlayer.OtherIsBot);

        // Each event is recorded once: no other relation appeared.
        Assert.Equal(2, MemoryOf(world).Snapshot(slayer).Count);
        Assert.Single(MemoryOf(world).Snapshot(orcBot));
        Assert.Single(MemoryOf(world).Snapshot(humanBot));
        Assert.Single(MemoryOf(world).Snapshot(sameBot));
    }

    [Fact]
    public async Task APvpKillInAnotherWorldOfTheSameProcess_IsNotRecordedInThisWorldsMemory()
    {
        // Two worlds share one process and character ids restart at 1 in every world: a kill in world B between its characters 1 and 2 must
        // not be written into the memory of world A's bot, which also has character id 1 (each feature hears only its own world's maps).
        await using SocialWorld a = await SocialWorld.StartAsync(frozen: true);
        await using SocialWorld b = await SocialWorld.StartAsync(frozen: true);
        Guid bystander = await a.AddBotAsync("Leakbota");
        Guid killer = await b.AddBotAsync("Leakkiller");
        await b.AddBotAsync("Leakvictim", race: SocialTestKit.Orc);
        Assert.Equal(await a.Host.PlayerStateAsync("Leakbota", p => p.Guid.Low), await b.Host.PlayerStateAsync("Leakkiller", p => p.Guid.Low)); // the same character id in both
        ObjectGuid victimGuid = await b.Host.PlayerStateAsync("Leakvictim", p => p.Guid);
        await KillsHeardAsync(a, ["Leakbota"]);
        await KillsHeardAsync(b, ["Leakkiller", "Leakvictim"]);

        await b.Host.OnWorldAsync(() =>
        {
            Player slayer = Online(b, "Leakkiller"), victim = Online(b, "Leakvictim");
            slayer.Map!.Combat.DealDamage(slayer, victim, victim.Health);
            Assert.False(victim.IsAlive);
        });

        await UntilAsync(() => RelationOf(b, killer, victimGuid) is not null, "the kill to be recorded in its own world");
        await Task.Delay(500);
        Assert.Empty(MemoryOf(a).Snapshot(bystander));
    }

    [Fact]
    public async Task ADuel_RecordsNoPvpKill_NeitherAtTheClampedLethalHit_NorWhenThePlayerDiesInsideAStartedDuel()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Guid loser = await world.AddBotAsync("Duelloser");
        Guid winner = await world.AddBotAsync("Duelwinner");
        Guid plain = await world.AddBotAsync("Plainvictim");
        await using WorldTestClient rival = await world.AddPlayerAsync("Duelrival");
        ObjectGuid loserGuid = await world.Host.PlayerStateAsync("Duelloser", p => p.Guid);
        ObjectGuid winnerGuid = await world.Host.PlayerStateAsync("Duelwinner", p => p.Guid);
        ObjectGuid rivalGuid = await world.Host.PlayerStateAsync("Duelrival", p => p.Guid);
        await KillsHeardAsync(world, ["Duelloser", "Duelwinner", "Plainvictim"], "Duelrival");

        await world.Host.OnWorldAsync(() =>
        {
            Player l = Online(world, "Duelloser"), w = Online(world, "Duelwinner"), r = Online(world, "Duelrival");
            // The duel with a bot: a started duel clamps the lethal hit to 1 health, the duel ends and nobody dies.
            l.Duel = new DuelInfo(l, w) { StartTimeSeconds = 1 };
            w.Duel = new DuelInfo(l, l) { StartTimeSeconds = 1 };
            w.Map!.Combat.DealDamage(w, l, l.Health);
            Assert.True(l.IsAlive);

            // A kill reported while a started duel is still attached (a third party finishing the loser off): not rivalry either.
            r.Duel = new DuelInfo(r, w) { StartTimeSeconds = 1 };
            w.Duel = new DuelInfo(r, r) { StartTimeSeconds = 1 };
            w.Map!.Combat.Kill(r, w);
            Assert.False(w.IsAlive);
        });

        // The control: a plain kill of a third bot by the same player, right after, is remembered, so the two before it were heard and dropped.
        await world.Host.OnWorldAsync(() =>
        {
            Player r = Online(world, "Duelrival"), p = Online(world, "Plainvictim");
            r.Duel = null;
            r.Map!.Combat.DealDamage(r, p, p.Health);
            Assert.False(p.IsAlive);
        });
        await UntilAsync(() => RelationOf(world, plain, rivalGuid) is not null, "the plain kill to be remembered");
        Assert.Null(RelationOf(world, loser, winnerGuid));
        Assert.Null(RelationOf(world, winner, loserGuid));
        Assert.Null(RelationOf(world, winner, rivalGuid));
        Assert.Empty(MemoryOf(world).Snapshot(loser));
        Assert.Empty(MemoryOf(world).Snapshot(winner));
    }
}

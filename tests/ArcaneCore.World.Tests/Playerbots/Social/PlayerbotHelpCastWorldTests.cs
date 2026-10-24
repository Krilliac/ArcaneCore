using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// The spell side of helping a stranger (P2 section 2): a priest that comes upon a dead player raises them with its real rank-1 spell, once per
/// corpse, and one that passes a stranger buffs them once per cooldown. The pass is hand-built at a world time the test picks; the cast is the bot's own.
/// </summary>
public sealed class PlayerbotHelpCastWorldTests
{
    private const byte Priest = 5;
    private const uint Fortitude = 990_801;

    /// <summary>The real resurrection spell casts for 10 seconds on the live clock: the offer reaches the client after that, so the wait must outlast it with room for a loaded machine.</summary>
    private static readonly TimeSpan ResurrectCastWait = TimeSpan.FromSeconds(60);

    /// <summary>The in-memory world data (human and orc warriors only) that also lets a human priest be created.</summary>
    private sealed class PriestWorldData : IWorldDataStore
    {
        private readonly InMemoryWorldDataStore _inner = new();

        public Task<StartPosition?> GetStartPositionAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => _inner.GetStartPositionAsync(race, cls == Priest ? (byte)1 : cls, cancellationToken);

        public Task<RaceInfo?> GetRaceInfoAsync(byte race, byte gender, CancellationToken cancellationToken = default) => _inner.GetRaceInfoAsync(race, gender, cancellationToken);

        public Task<ClassInfo?> GetClassInfoAsync(byte cls, CancellationToken cancellationToken = default) => _inner.GetClassInfoAsync(cls, cancellationToken);

        public Task<bool> IsValidRaceClassAsync(byte race, byte cls, CancellationToken cancellationToken = default)
            => _inner.IsValidRaceClassAsync(race, cls == Priest ? (byte)1 : cls, cancellationToken);
    }

    private static Task<SocialWorld> StartWorldAsync()
        => SocialWorld.StartAsync(frozen: true, services: services => services.AddSingleton<IWorldDataStore>(new PriestWorldData()));

    private static SpellInfo FortitudeSpell() => new()
    {
        Id = Fortitude, Name = "Power Word: Fortitude", RangeIndex = 4, Range = new SpellRange(0, 30),
        Duration = new SpellDuration(1_800_000, 0, 1_800_000),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura, AuraType = AuraType.ModStat, BasePoints = 2, BaseDice = 1, DieSides = 1, MiscValue = -1,
            TargetA = SpellImplicitTarget.UnitFriend,
        }],
    };

    /// <summary>A level-20 priest standing beside a real player, knowing <paramref name="spells"/> (added to the spell store first).</summary>
    private static async Task<(Guid Bot, ObjectGuid BotGuid, ObjectGuid OtherGuid)> SetUpAsync(SocialWorld world, string other, SpellInfo[] spells)
    {
        Guid bot = await world.AddBotAsync("Helpprie", characterClass: Priest);
        await HelpTestKit.StandAsync(world, "Helpprie", (other, 4f));
        await world.Host.OnWorldAsync(() =>
        {
            SpellFeature feature = world.Host.WorldServices.GetRequiredService<SpellFeature>();
            feature.System.Store = new SpellStore([.. feature.System.Store.All.Where(s => spells.All(x => x.Id != s.Id)), .. spells], [], []);
            Player priest = HelpTestKit.Online(world, "Helpprie");
            // The bot's own think resolves its abilities now and then; one that ran before the learn below is the case to cover (without this it depends on timing).
            _ = HelpTestKit.BrainOf(world, bot).CombatSpells.Abilities(priest);
            world.Host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.GiveLevel(priest, 20);
            // Through the spell system, not the book: only it sends SMSG_LEARNED_SPELL, which is what makes the bot rebuild the abilities it already resolved.
            foreach (SpellInfo spell in spells) feature.System.LearnSpell(priest, spell.Id);
            priest.Health = priest.MaxHealth;
            SpellSystem.SetPower(priest, PowerType.Mana, priest.GetUInt32(UpdateFields.UnitFieldMaxpower1)); // a healer helps only with mana to spare
        });
        // The bot is free to help (not between goals, healthy, mana to spare) before any pass is taken: a scan at a moment it is not would see nobody to help.
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => SocialTestKit.BotPass(world.Host, 0).Bots.Any(b => b.Id == bot && PlayerbotHelpLane.IsFree(b))).Result, "the bot to be free");
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Helpprie", p => p.Guid);
        ObjectGuid otherGuid = await world.Host.PlayerStateAsync(other, p => p.Guid);
        return (bot, botGuid, otherGuid);
    }

    private static Task ScanAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Help.Run(SocialTestKit.BotPass(world.Host, nowMs)));

    /// <summary>
    /// Scans until the buff lane has made one more offer than it had. A scan that finds the bot not free (a goal change, a vital, a sight or range
    /// check that has not settled since <see cref="SetUpAsync"/> looked) offers nothing and records nothing, so it is safe to take again.
    /// </summary>
    private static async Task ScanUntilBuffedAsync(SocialWorld world, long nowMs)
    {
        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        int before = lane.Buffs;
        await HelpTestKit.UntilAsync(() =>
        {
            ScanAsync(world, nowMs).GetAwaiter().GetResult();
            return lane.Buffs > before;
        }, "the scan to offer the buff");
    }

    [Fact]
    public async Task APriest_ResurrectsADeadStranger_WithItsRealSpell_OncePerCorpse()
    {
        await using SocialWorld world = await StartWorldAsync();
        await using WorldTestClient dead = await world.AddPlayerAsync("Helpghost");
        (Guid bot, ObjectGuid botGuid, ObjectGuid deadGuid) = await SetUpAsync(world, "Helpghost", PlayerbotGroupResurrectionTests.RealResurrectionSpells());
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(botGuid.Value, deadGuid.Value, (uint)t, helpfulness, 0));
        await world.Host.OnWorldAsync(() =>
        {
            Player body = HelpTestKit.Online(world, "Helpghost");
            body.Map!.Combat.KillPlayer(body);
            Assert.False(body.IsAlive);
        });

        await ScanAsync(world, now);

        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(1, lane.Resurrections);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        await HelpTestKit.UntilAsync(() => brain.HelpsDone == 1, "the priest to start the resurrection");
        Assert.Equal(PlayerbotHelpKind.Resurrect, brain.LastHelpKind);
        Assert.Equal(deadGuid, brain.LastHelpTarget);

        // The dead player sees the offer and turns it down; only then is "not offered again" worth checking (an offer still open would hide a second one).
        await dead.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest, ResurrectCastWait); // the real spell casts for 10 s of real time
        await dead.SendAsync(WorldOpcode.CmsgResurrectResponse, DeclineResponse());
        await world.Host.WaitForWorldAsync(() => ResurrectionRequests.Get(world.Host.World.FindOnlinePlayer("Helpghost")!) is null, "the declined offer to be gone");
        for (int i = 1; i <= 3; i++) await ScanAsync(world, now + i * 6_000L);
        Assert.Equal(1, lane.Resurrections);
    }

    private static byte[] DeclineResponse()
    {
        var writer = new PacketWriter();
        writer.WriteUInt64(0);
        writer.WriteByte(0);
        return writer.ToArray();
    }

    [Fact]
    public async Task OldBones_DoNotShadowADeadStrangerFurtherAway()
    {
        await using SocialWorld world = await StartWorldAsync();
        await using WorldTestClient first = await world.AddPlayerAsync("Helpbonea");
        await using WorldTestClient second = await world.AddPlayerAsync("Helpboneb");
        (Guid bot, ObjectGuid botGuid, ObjectGuid firstGuid) = await SetUpAsync(world, "Helpbonea", PlayerbotGroupResurrectionTests.RealResurrectionSpells());
        ObjectGuid secondGuid = await world.Host.PlayerStateAsync("Helpboneb", p => p.Guid);
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(botGuid.Value, secondGuid.Value, (uint)t, helpfulness, 0));
        // A dies 2 yd from the priest and its body turns to bones (an insignia was taken); A then stands 100 yd away, still dead, while B lies dead 10 yd off.
        await world.Host.OnWorldAsync(() =>
        {
            Player a = HelpTestKit.Online(world, "Helpbonea");
            a.Map!.Combat.KillPlayer(a);
            Assert.True(a.Map!.Combat.RepopPlayer(a)); // released: a body lies where A fell
            Assert.NotNull(a.Map!.Combat.ConvertCorpseToBones(a.Combat.Corpse!));
        });
        await HelpTestKit.StandAsync(world, "Helpprie", ("Helpboneb", 10f));
        await world.Host.OnWorldAsync(() =>
        {
            Player priest = HelpTestKit.Online(world, "Helpprie");
            Player a = HelpTestKit.Online(world, "Helpbonea");
            a.Relocate(priest.X + 100f, priest.Y, priest.Z, 0f, world.Host.World.NowMs);
            Player b = HelpTestKit.Online(world, "Helpboneb");
            b.Map!.Combat.KillPlayer(b);
            Assert.False(b.IsAlive);
        });
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Helpprie").VisibleObjects.Any(g => g.High == HighGuid.Corpse)).Result, "the priest to see a body");

        await ScanAsync(world, now);

        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(1, lane.Resurrections);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        await HelpTestKit.UntilAsync(() => brain.HelpsDone == 1, "the priest to start the resurrection");
        Assert.Equal(secondGuid, brain.LastHelpTarget);
    }

    [Fact]
    public async Task ACorpseThatKeepsDeclining_GetsExactlyOneOfferPerDeath_EvenHalfAnHourLater()
    {
        await using SocialWorld world = await StartWorldAsync();
        await using WorldTestClient dead = await world.AddPlayerAsync("Helpstubborn");
        (Guid bot, ObjectGuid botGuid, ObjectGuid deadGuid) = await SetUpAsync(world, "Helpstubborn", PlayerbotGroupResurrectionTests.RealResurrectionSpells());
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(botGuid.Value, deadGuid.Value, (uint)t, helpfulness, 0));
        // The body may lie 60 minutes before it is too old to raise: the one-offer-per-death memory, not the corpse age, is what holds the second offer back.
        await world.Host.OnWorldAsync(() => world.Options.Social.Helping.ResurrectMaxCorpseMinutes = 60);
        await world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Helpstubborn").Map!.Combat.KillPlayer(HelpTestKit.Online(world, "Helpstubborn")));

        await ScanAsync(world, now);
        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(1, lane.Resurrections);
        await dead.ReadUntilAsync(WorldOpcode.SmsgResurrectRequest, ResurrectCastWait); // the real spell casts for 10 s of real time
        await dead.SendAsync(WorldOpcode.CmsgResurrectResponse, DeclineResponse());
        await world.Host.WaitForWorldAsync(() => ResurrectionRequests.Get(world.Host.World.FindOnlinePlayer("Helpstubborn")!) is null, "the declined offer to be gone");
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        await HelpTestKit.UntilAsync(() => brain.PendingHelp == PlayerbotHelpKind.None, "the bot to be free again");

        // Half an hour on the same body still lies there, and a pass whose roll would say yes for a fresh death: no second offer, the one was the death's.
        long later = HelpTestKit.NowWhere(t => t > now + 31 * 60_000L && PlayerbotHelpRules.Helps(botGuid.Value, deadGuid.Value, (uint)t, helpfulness, 0));
        await ScanAsync(world, later);
        Assert.Equal(1, lane.Resurrections);
    }

    [Fact]
    public async Task APriest_BuffsAStranger_AndNotAgainWithinTheCooldown()
    {
        await using SocialWorld world = await StartWorldAsync();
        await using WorldTestClient other = await world.AddPlayerAsync("Helpbuffed");
        (Guid bot, ObjectGuid botGuid, ObjectGuid otherGuid) = await SetUpAsync(world, "Helpbuffed", [FortitudeSpell()]);
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Offers(botGuid.Value, otherGuid.Value, t, helpfulness, PlayerbotHelpLane.BuffRate));

        await ScanUntilBuffedAsync(world, now);

        PlayerbotHelpLane lane = SocialTestKit.Feature(world.Host).Help;
        Assert.Equal(1, lane.Buffs);
        PlayerbotBrain brain = HelpTestKit.BrainOf(world, bot);
        await HelpTestKit.UntilAsync(() => brain.HelpsDone == 1, "the priest to cast the buff");
        Assert.Equal(PlayerbotHelpKind.Buff, brain.LastHelpKind);
        await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() =>
        {
            SpellFeature feature = world.Host.WorldServices.GetRequiredService<SpellFeature>();
            return feature.System.GetAuras(HelpTestKit.Online(world, "Helpbuffed")).Any(h => !h.IsRemoved && h.Spell.Id == Fortitude);
        }).Result, "the stranger to carry the buff");

        // The stranger loses the aura (the buff is no reason to hold back now): inside the cooldown (30 minutes) no pass offers it again, even at a time whose roll says yes...
        await world.Host.OnWorldAsync(() =>
        {
            SpellFeature feature = world.Host.WorldServices.GetRequiredService<SpellFeature>();
            Player stranger = HelpTestKit.Online(world, "Helpbuffed");
            feature.System.RemoveAuras(stranger, Fortitude);
            Assert.DoesNotContain(feature.System.GetAuras(stranger), h => !h.IsRemoved && h.Spell.Id == Fortitude);
        });
        await HelpTestKit.UntilAsync(() => brain.PendingHelp == PlayerbotHelpKind.None, "the bot to be free again");
        long later = HelpTestKit.NowWhere(t => t > now + 60_000 && t < now + 20 * 60_000L && PlayerbotHelpRules.Offers(botGuid.Value, otherGuid.Value, t, helpfulness, PlayerbotHelpLane.BuffRate));
        await ScanAsync(world, later);
        Assert.Equal(1, lane.Buffs);

        // ...and once the cooldown is over, a pass whose roll says yes offers it again.
        long after = HelpTestKit.NowWhere(t => t > now + 31 * 60_000L && PlayerbotHelpRules.Offers(botGuid.Value, otherGuid.Value, t, helpfulness, PlayerbotHelpLane.BuffRate));
        await ScanUntilBuffedAsync(world, after);
        Assert.Equal(2, lane.Buffs);
    }

    [Fact]
    public async Task APriest_HelpingAWoundedPlayerInAFight_HealsThemFirst_WithAHealThatLandsOnAFriend()
    {
        await using SocialWorld world = await StartWorldAsync();
        await using WorldTestClient victim = await world.AddPlayerAsync("Helphealvic");
        SpellInfo template = await world.Host.OnWorldAsync(() => world.Host.WorldServices.GetRequiredService<SpellFeature>().System.Store.Get(ArcaneCore.World.Tests.Spells.SpellTestServices.Heal)!);
        SpellInfo friendHeal = template with
        {
            Id = 990_810, Name = "Help Test Friend Heal",
            Effects = [.. template.Effects.Select(e => e with { TargetA = SpellImplicitTarget.UnitFriend, BasePoints = 100_000 })],
        };
        (Guid bot, ObjectGuid botGuid, ObjectGuid victimGuid) = await SetUpAsync(world, "Helphealvic", [friendHeal]);
        uint wounded = 0;
        await world.Host.OnWorldAsync(() =>
        {
            Player player = HelpTestKit.Online(world, "Helphealvic");
            HelpTestKit.Engage(HelpTestKit.SpawnMob(player, 4f), player);
            player.Health = Math.Max(1u, player.MaxHealth / 4);
            wounded = player.Health;
        });
        float helpfulness = PlayerbotTraits.For(botGuid.Value).Helpfulness;
        long now = HelpTestKit.NowWhere(t => PlayerbotHelpRules.Helps(botGuid.Value, victimGuid.Value, (uint)t, helpfulness, 0));

        await ScanAsync(world, now);

        Assert.Equal(1, SocialTestKit.Feature(world.Host).Help.Helped);
        try
        {
            await HelpTestKit.UntilAsync(() => world.Host.OnWorldAsync(() => HelpTestKit.Online(world, "Helphealvic").Health).Result > wounded, "the priest's heal to land on the victim", seconds: 30);
        }
        catch (TimeoutException)
        {
            PlayerbotBrain priest = HelpTestKit.BrainOf(world, bot);
            string state = await world.Host.OnWorldAsync(() => $"helps={priest.HelpsDone} kind={priest.LastHelpKind} pending={priest.PendingHelp} goal={priest.Goal} victimHp={HelpTestKit.Online(world, "Helphealvic").Health}/{HelpTestKit.Online(world, "Helphealvic").MaxHealth} wounded={wounded} priestHp={HelpTestKit.Online(world, "Helpprie").Health} alive={HelpTestKit.Online(world, "Helpprie").IsAlive} combat={HelpTestKit.Online(world, "Helpprie").Combat.IsInCombat}");
            throw new Xunit.Sdk.XunitException("the heal never landed: " + state);
        }

        // The heal is help: the healer is credited (HelpedThem) for it, once the heal landed.
        Assert.True(HelpTestKit.RecorderOf(world).Memory.TryGet(bot, victimGuid.Value, out PlayerbotRelation credit));
        Assert.Equal((short)new PlayerbotSocialMemoryOptions().AffinityPerHelp, credit.Affinity);
    }
}

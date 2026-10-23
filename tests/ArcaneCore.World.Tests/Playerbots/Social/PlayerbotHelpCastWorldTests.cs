using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
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
            world.Host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.GiveLevel(priest, 20);
            foreach (SpellInfo spell in spells) feature.Spellbook.LearnSpell(priest, spell.Id);
            priest.Health = priest.MaxHealth;
        });
        ObjectGuid botGuid = await world.Host.PlayerStateAsync("Helpprie", p => p.Guid);
        ObjectGuid otherGuid = await world.Host.PlayerStateAsync(other, p => p.Guid);
        return (bot, botGuid, otherGuid);
    }

    private static Task ScanAsync(SocialWorld world, long nowMs)
        => world.Host.OnWorldAsync(() => SocialTestKit.Feature(world.Host).Help.Run(SocialTestKit.BotPass(world.Host, nowMs)));

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

        // The same corpse is not offered again, whatever the passes say; and a rival would never have been raised at all.
        for (int i = 1; i <= 3; i++) await ScanAsync(world, now + i * 6_000L);
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

        await ScanAsync(world, now);

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

        // Inside the cooldown (30 minutes) no pass offers it again, even at a time whose roll says yes.
        long later = HelpTestKit.NowWhere(t => t > now + 60_000 && t < now + 20 * 60_000L && PlayerbotHelpRules.Offers(botGuid.Value, otherGuid.Value, t, helpfulness, PlayerbotHelpLane.BuffRate));
        await ScanAsync(world, later);
        Assert.Equal(1, lane.Buffs);
    }
}

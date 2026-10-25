using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Social;

/// <summary>
/// Decision R12 (design 1.1, 5, 10 A7): a PvP kill on a battleground map, or with either side in a coordinator siege group, is organised PvP and
/// not a grudge, so <c>NotePvpKillCore</c> records neither <c>KilledBy</c> nor <c>KilledThem</c> while
/// <see cref="PlayerbotSocialMemoryOptions.OrganisedPvpKills"/> is off (the default); switched on, it records as before. An open-world kill is
/// recorded as always. The existing <c>PlayerbotPvpKillRoutingWorldTests</c> and <c>PlayerbotSocialRecorderTests</c> pass unchanged.
/// <para>Contract: <c>PlayerbotSocialRecorder.InSiegeGroup</c>, a <c>Func&lt;ObjectGuid, bool&gt;?</c> the managed feature wires to the group coordinator.</para>
/// </summary>
public sealed class PlayerbotOrganisedKillTests
{
    private static readonly PlayerbotSocialMemoryOptions Defaults = new();

    private static PlayerbotSocialRecorder RecorderOf(SocialWorld world) => PartyTestHost.Feature(world.Host).Social;

    private static PlayerbotRelation? RelationOf(SocialWorld world, Guid bot, ObjectGuid other)
        => RecorderOf(world).Memory.TryGet(bot, other.Value, out PlayerbotRelation relation) ? relation : null;

    private static Player Online(SocialWorld world, string name) => world.Host.World.FindOnlinePlayer(name)!;

    private static void Slay(Unit killer, Player victim) => victim.Map!.Combat.DealDamage(killer, victim, victim.Health);

    private static void BattlegroundMap(SocialWorld world)
        // Both continents become battleground maps for the duration of a kill, so the bot and the killer stand on one whichever map the test places them.
        => WorldMaps.Of(world.Host.World).Load(new MapContent(
            [new MapTemplate(0, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Test Battleground A", string.Empty),
             new MapTemplate(1, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Test Battleground B", string.Empty)], [], [], [], []));

    private static void OpenWorldMaps(SocialWorld world)
        => WorldMaps.Of(world.Host.World).Load(new MapContent([.. MapRegistry.DefaultContinents], [], [], [], []));

    /// <summary>One Human bot killed by one Orc player on the map the bot stands on; the relation of the bot toward the killer afterwards.</summary>
    private static async Task<(PlayerbotRelation? ByVictim, PlayerbotRelation? BySlayer)> KillAsync(SocialWorld world, string tag, Action<SocialWorld>? prepare = null,
        Action<PlayerbotSocialRecorder, ObjectGuid, ObjectGuid>? seam = null, bool botSlayer = false)
    {
        string victimName = "Ok" + tag + "dead", slayerName = "Ok" + tag + "slay";
        Guid victim = await world.AddBotAsync(victimName);
        Guid slayerBot = Guid.Empty;
        WorldTestClient? client = null;
        if (botSlayer) slayerBot = await world.AddBotAsync(slayerName, race: SocialTestKit.Orc);
        else client = await world.AddPlayerAsync(slayerName, SocialTestKit.Orc);
        try
        {
            ObjectGuid victimGuid = await world.Host.PlayerStateAsync(victimName, p => p.Guid);
            ObjectGuid slayerGuid = await world.Host.PlayerStateAsync(slayerName, p => p.Guid);
            await world.Host.OnWorldAsync(() =>
            {
                seam?.Invoke(RecorderOf(world), victimGuid, slayerGuid);
                prepare?.Invoke(world);
                Slay(Online(world, slayerName), Online(world, victimName));
                Assert.False(Online(world, victimName).IsAlive, "the kill happened");
            });
            PlayerbotRelation? byVictim = RelationOf(world, victim, slayerGuid);
            PlayerbotRelation? bySlayer = botSlayer ? RelationOf(world, slayerBot, victimGuid) : null;
            return (byVictim, bySlayer);
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnOpenWorldKill_IsRecordedAsToday_OnBothSides()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        (PlayerbotRelation? byVictim, PlayerbotRelation? bySlayer) = await KillAsync(world, "open", botSlayer: true);

        Assert.Equal((short)Defaults.AffinityPerKilledBy, byVictim!.Affinity);
        Assert.Equal((short)Defaults.AffinityPerKilledThem, bySlayer!.Affinity);
        Assert.True(byVictim.Flags.HasFlag(PlayerbotRelationFlags.OtherFaction));
    }

    [Fact]
    public async Task ABattlegroundKill_RecordsNothing_OnEitherSide_WhileOrganisedPvpKillsIsOff()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        Assert.False(world.Options.Social.Memory.OrganisedPvpKills);
        try
        {
            (PlayerbotRelation? byVictim, PlayerbotRelation? bySlayer) = await KillAsync(world, "bgoff", prepare: BattlegroundMap, botSlayer: true);

            Assert.Null(byVictim);
            Assert.Null(bySlayer);
        }
        finally
        {
            await world.Host.OnWorldAsync(() => { OpenWorldMaps(world); return true; });
        }
    }

    [Fact]
    public async Task ABattlegroundKillByARealPlayer_RecordsNothing_ForTheBotThatDied()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        try
        {
            (PlayerbotRelation? byVictim, _) = await KillAsync(world, "bgply", prepare: BattlegroundMap);

            Assert.Null(byVictim);
        }
        finally
        {
            await world.Host.OnWorldAsync(() => { OpenWorldMaps(world); return true; });
        }
    }

    [Fact]
    public async Task ABattlegroundKill_IsRecordedAsToday_WhenOrganisedPvpKillsIsOn()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true, configure: o => o.Social.Memory.OrganisedPvpKills = true);
        try
        {
            (PlayerbotRelation? byVictim, PlayerbotRelation? bySlayer) = await KillAsync(world, "bgon", prepare: BattlegroundMap, botSlayer: true);

            Assert.Equal((short)Defaults.AffinityPerKilledBy, byVictim!.Affinity);
            Assert.Equal((short)Defaults.AffinityPerKilledThem, bySlayer!.Affinity);
        }
        finally
        {
            await world.Host.OnWorldAsync(() => { OpenWorldMaps(world); return true; });
        }
    }

    [Fact]
    public async Task AKillWithTheKillerInASiegeGroup_RecordsNothing_UnlessOrganisedPvpKillsIsOn()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        (PlayerbotRelation? byVictim, PlayerbotRelation? bySlayer) = await KillAsync(world, "sgk", seam: (recorder, _, slayer) => recorder.InSiegeGroup = guid => guid == slayer, botSlayer: true);
        Assert.Null(byVictim);
        Assert.Null(bySlayer);

        world.Options.Social.Memory.OrganisedPvpKills = true;
        (byVictim, bySlayer) = await KillAsync(world, "sgkb", seam: (recorder, _, slayer) => recorder.InSiegeGroup = guid => guid == slayer, botSlayer: true);
        Assert.Equal((short)Defaults.AffinityPerKilledBy, byVictim!.Affinity);
        Assert.Equal((short)Defaults.AffinityPerKilledThem, bySlayer!.Affinity);
    }

    [Fact]
    public async Task AKillWithTheVictimInASiegeGroup_RecordsNothing_OnEitherSide()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        (PlayerbotRelation? byVictim, PlayerbotRelation? bySlayer) = await KillAsync(world, "sgv", seam: (recorder, victim, _) => recorder.InSiegeGroup = guid => guid == victim, botSlayer: true);

        Assert.Null(byVictim);
        Assert.Null(bySlayer);
    }

    [Fact]
    public async Task AKillBetweenOthersThanTheSiegeGroup_IsStillRecorded()
    {
        await using SocialWorld world = await SocialWorld.StartAsync(frozen: true);
        // The seam answers for some unrelated guid only: this kill is not organised.
        (PlayerbotRelation? byVictim, PlayerbotRelation? bySlayer) = await KillAsync(world, "sgo", seam: (recorder, _, _) => recorder.InSiegeGroup = guid => guid == ObjectGuid.Player(0x7FFF_FFF0), botSlayer: true);

        Assert.Equal((short)Defaults.AffinityPerKilledBy, byVictim!.Affinity);
        Assert.Equal((short)Defaults.AffinityPerKilledThem, bySlayer!.Affinity);
    }
}

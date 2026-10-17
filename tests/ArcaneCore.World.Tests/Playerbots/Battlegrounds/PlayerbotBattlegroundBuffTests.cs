using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;
using Kind = ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundBuffKind;
using Needs = ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundBuffNeeds;
using Spot = ArcaneCore.World.Playerbots.Battlegrounds.PlayerbotBattlegroundBuffSpot;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// The bots' use of the battleground buff objects (<see cref="PlayerbotBattlegroundBuffs"/>): the choice is unit tested on the board, and
/// end to end a hurt bot in a Warsong Gulch match walks onto the restoration buff, whose trap casts the buff through the match's trap update
/// (vmangos GameObject::Update GameObject.cpp:476-551, BattleGround::HandleTriggerBuff BattleGround.cpp:1713-1757) and is used up.
/// The buff templates are classic-db's (gameobject_template 179871/179904/179905 and the alternates, spells from their data3).
/// </summary>
public sealed class PlayerbotBattlegroundBuffTests
{
    private static readonly ObjectGuid BotA = new(1);
    private static readonly ObjectGuid BotB = new(2);
    private static readonly Vector3 Origin = Vector3.Zero;

    private static Spot SpotOf(ulong guid, Kind kind, float x, float y = 0f) => new(new ObjectGuid(0xF110000000000000UL | guid), kind switch
    {
        Kind.Speed => BattlegroundConstants.SpeedBuffEntry,
        Kind.Restoration => BattlegroundConstants.RegenBuffEntry,
        _ => BattlegroundConstants.BerserkBuffEntry,
    }, kind switch
    {
        Kind.Speed => Content.SpeedBuffSpell,
        Kind.Restoration => Content.RestorationBuffSpell,
        _ => Content.BerserkBuffSpell,
    }, kind, new Vector3(x, y, 0f));

    private static Needs Healthy(bool fighting = false, bool urgent = false) => new(1f, 1f, false, false, fighting, urgent, null);

    private static Spot? Choose(Needs needs, params Spot[] spots) => PlayerbotBattlegroundBuffs.Choose(Origin, needs, spots, _ => false, _ => false);

    // --- the templates ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(179871u, 23451u, (byte)Kind.Speed)]
    [InlineData(179899u, 23451u, (byte)Kind.Speed)]
    [InlineData(180146u, 23978u, (byte)Kind.Speed)]
    [InlineData(179904u, 23493u, (byte)Kind.Restoration)]
    [InlineData(180362u, 24379u, (byte)Kind.Restoration)]
    [InlineData(179905u, 23505u, (byte)Kind.Berserking)]
    [InlineData(180380u, 24378u, (byte)Kind.Berserking)]
    public void ClassicDbBuffTemplates_AreRecognised_ByTheirSpell(uint entry, uint spell, byte kind)
        => Assert.Equal((Kind)kind, PlayerbotBattlegroundBuffBoard.Classify(Content.BuffTrap(entry, spell, 5931, "buff")));

    [Fact]
    public void ATrapThatIsNoBattlegroundBuff_IsNotABuff()
    {
        // classic-db 179900 "Big Bonfire Damage": a trap with radius 5 (data2), spell 23485, no cooldown of 3.
        uint[] data = new uint[GameObjectTemplate.DataCount];
        data[1] = 1;
        data[2] = 5;
        data[3] = 23485;
        Assert.Null(PlayerbotBattlegroundBuffBoard.Classify(new GameObjectTemplate { Entry = 179900, Type = (uint)GameObjectType.Trap, Name = "Big Bonfire Damage", Data = data }));
        Assert.Null(PlayerbotBattlegroundBuffBoard.Classify(Content.GooberBanner()));
    }

    // --- which buff ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void LowHealthBot_PrefersRestoration_OverACloserBerserking()
    {
        Spot restoration = SpotOf(1, Kind.Restoration, 12f);
        Spot berserk = SpotOf(2, Kind.Berserking, 8f);
        Assert.Equal(restoration, Choose(new Needs(0.3f, 1f, false, false, Fighting: true, false, null), restoration, berserk));

        // The same fight at full health: the berserking buff, and restoration is not wanted at all.
        Assert.Equal(berserk, Choose(Healthy(fighting: true), restoration, berserk));
        Assert.Null(Choose(Healthy(fighting: true), restoration));
    }

    [Fact]
    public void LowMana_WantsRestoration_ButAHealthyBotOutOfAFight_WantsNothing()
    {
        Spot restoration = SpotOf(1, Kind.Restoration, 20f);
        Spot berserk = SpotOf(2, Kind.Berserking, 5f);
        Assert.Equal(restoration, Choose(new Needs(1f, 0.2f, false, false, false, false, null), restoration, berserk));
        Assert.Null(Choose(Healthy(), restoration, berserk));
    }

    [Fact]
    public void FlagCarrier_PrefersSpeed_WithinTheDetourCap_AndNeverBerserking()
    {
        var home = new Vector3(100f, 0f, 0f);
        var carrier = new Needs(0.5f, 1f, Carrier: true, false, false, Urgent: true, home);
        Spot speed = SpotOf(1, Kind.Speed, 30f, 10f);              // on the way home: detour about 3.3 yards
        Spot restoration = SpotOf(2, Kind.Restoration, 10f);      // right on the way
        Spot berserk = SpotOf(3, Kind.Berserking, 5f);
        Assert.Equal(speed, Choose(carrier, speed, restoration, berserk));
        Assert.Null(Choose(carrier with { HealthFraction = 1f }, berserk));

        // A speed buff off to the side: 50 + 80.6 - 100 = 30.6 yards of detour, over the urgent cap of 20.
        Spot aside = SpotOf(4, Kind.Speed, 30f, 40f);
        Assert.Null(Choose(carrier with { HealthFraction = 1f }, aside));
        Assert.Equal(restoration, Choose(carrier, aside, restoration));
    }

    [Fact]
    public void Chaser_OfTheEnemyCarrier_TakesSpeed_ButAnyoneElseOnlyOneBesideIt()
    {
        Spot speed = SpotOf(1, Kind.Speed, 15f);
        var chaser = new Needs(1f, 1f, false, ChasingCarrier: true, Fighting: true, Urgent: true, new Vector3(40f, 0f, 0f));
        Assert.Equal(speed, Choose(chaser, speed));
        Assert.Null(Choose(Healthy(), speed));
        Assert.Equal(SpotOf(2, Kind.Speed, 6f), Choose(Healthy(), SpotOf(2, Kind.Speed, 6f)));
    }

    [Fact]
    public void FarBuff_IsIgnored_WhenAnObjectiveIsUrgent()
    {
        Spot far = SpotOf(1, Kind.Restoration, 30f);
        var hurt = new Needs(0.3f, 1f, false, false, false, Urgent: false, null);
        Assert.Equal(far, Choose(hurt, far));
        Assert.Null(Choose(hurt with { Urgent = true }, far));
        Assert.Equal(SpotOf(2, Kind.Restoration, 15f), Choose(hurt with { Urgent = true }, far, SpotOf(2, Kind.Restoration, 15f)));

        // Out of the search radius nothing counts, urgent or not.
        Assert.Null(Choose(hurt, SpotOf(3, Kind.Restoration, PlayerbotBattlegroundBuffs.SearchYards + 1f)));
    }

    [Fact]
    public void ABuffWhoseAuraTheBotHas_IsNotTakenAgain()
    {
        Spot berserk = SpotOf(1, Kind.Berserking, 5f);
        Assert.Null(PlayerbotBattlegroundBuffs.Choose(Origin, Healthy(fighting: true), [berserk], _ => false, spell => spell == Content.BerserkBuffSpell));
    }

    // --- claims, despawns, time caps ----------------------------------------------------------------------------------------

    [Fact]
    public void TwoBotsOfATeam_DoNotChaseTheSameBuff_AnEnemyMay()
    {
        var board = new PlayerbotBattlegroundBuffBoard();
        Spot restoration = SpotOf(1, Kind.Restoration, 10f);
        board.Load([restoration], 0);
        var hurt = new Needs(0.3f, 1f, false, false, false, false, null);
        var a = new PlayerbotBattlegroundBuffs();
        var b = new PlayerbotBattlegroundBuffs();
        var enemy = new PlayerbotBattlegroundBuffs();

        Assert.Equal(restoration, a.Decide(BotA, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 0));
        Assert.Null(b.Decide(BotB, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 200));
        Assert.Equal(restoration, enemy.Decide(new ObjectGuid(3), Team.Horde, Origin, hurt, board, board.Spots, _ => true, _ => false, 200));

        // A keeps heading there (its claim is renewed every think); once it stops renewing, the claim lapses and B may go.
        Assert.Equal(restoration, a.Decide(BotA, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 2_000));
        Assert.Null(b.Decide(BotB, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 4_000));
        Assert.Equal(restoration, b.Decide(BotB, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 2_000 + PlayerbotBattlegroundBuffBoard.ClaimMs + 1));

        // A bot that drops its pursuit releases the claim at once.
        b.Reset(BotB);
        var c = new PlayerbotBattlegroundBuffs();
        Assert.Equal(restoration, c.Decide(new ObjectGuid(4), Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 5_100));
    }

    [Fact]
    public void ANewBoard_ScansOnItsFirstCurrent_ThenOncePerRefreshWindow()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        Map map = world.GetMap(0);
        var board = new PlayerbotBattlegroundBuffBoard();
        Assert.Equal(0, board.Scans);

        // The first call scans whatever the clock reads (an unscanned board is not "fresh").
        board.Current(map, 1_000);
        Assert.Equal(1, board.Scans);

        board.Current(map, 1_000 + PlayerbotBattlegroundBuffBoard.RefreshMs - 1);
        Assert.Equal(1, board.Scans);

        board.Current(map, 1_000 + PlayerbotBattlegroundBuffBoard.RefreshMs);
        Assert.Equal(2, board.Scans);
    }

    private sealed class NullSaveQueue : ICharacterSaveQueue
    {
        public void Enqueue(CharacterState state)
        {
        }
    }

    [Fact]
    public void DespawnedBuff_IsDropped_AndForgottenOnTheBoard()
    {
        var board = new PlayerbotBattlegroundBuffBoard();
        Spot restoration = SpotOf(1, Kind.Restoration, 10f);
        Spot other = SpotOf(2, Kind.Restoration, 25f);
        board.Load([restoration, other], 0);
        var hurt = new Needs(0.3f, 1f, false, false, false, false, null);
        var a = new PlayerbotBattlegroundBuffs();
        Assert.Equal(restoration, a.Decide(BotA, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 0));

        // Someone else took it (the object is gone): A forgets it and goes for the next one.
        bool Spawned(ObjectGuid g) => g != restoration.Guid;
        Assert.Equal(other, a.Decide(BotA, Team.Alliance, Origin, hurt, board, board.Spots, Spawned, _ => false, 200));
        Assert.DoesNotContain(board.Spots, s => s.Guid == restoration.Guid);
        Assert.Equal(0, a.Taken);

        // The bot stood on it when it went: counted as taken.
        a.Reset(BotA);
        Assert.Equal(other, a.Decide(BotA, Team.Alliance, other.Position, hurt, board, board.Spots, _ => true, _ => false, 400));
        Assert.Null(a.Decide(BotA, Team.Alliance, other.Position, hurt, board, board.Spots, _ => false, _ => false, 600));
        Assert.Null(a.Pursuit);
        Assert.Equal(1, a.Taken);
        Assert.Empty(board.Spots);
    }

    [Fact]
    public void ABuffNotReachedInTime_IsGivenUp_AndLeftAloneForAWhile()
    {
        var board = new PlayerbotBattlegroundBuffBoard();
        Spot restoration = SpotOf(1, Kind.Restoration, 10f);
        board.Load([restoration], 0);
        var hurt = new Needs(0.3f, 1f, false, false, false, false, null);
        var a = new PlayerbotBattlegroundBuffs();
        Assert.Equal(restoration, a.Decide(BotA, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, 0));
        Assert.Null(a.Decide(BotA, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, PlayerbotBattlegroundBuffs.PursuitMs + 1));
        Assert.Null(a.Pursuit);
        long later = PlayerbotBattlegroundBuffs.PursuitMs + PlayerbotBattlegroundBuffs.IgnoreMs + 2;
        Assert.Equal(restoration, a.Decide(BotA, Team.Alliance, Origin, hurt, board, board.Spots, _ => true, _ => false, later));
    }

    [Fact]
    public void AHealedBot_StopsHeadingToRestoration()
    {
        var board = new PlayerbotBattlegroundBuffBoard();
        Spot restoration = SpotOf(1, Kind.Restoration, 10f);
        board.Load([restoration], 0);
        var a = new PlayerbotBattlegroundBuffs();
        Assert.Equal(restoration, a.Decide(BotA, Team.Alliance, Origin, new Needs(0.3f, 1f, false, false, false, false, null), board, board.Spots, _ => true, _ => false, 0));
        Assert.Null(a.Decide(BotA, Team.Alliance, Origin, Healthy(), board, board.Spots, _ => true, _ => false, 200));
        Assert.False(board.ClaimedByOther(restoration.Guid, Team.Alliance, BotB, 200));
    }

    // --- end to end ---------------------------------------------------------------------------------------------------------

    private sealed class Scenario(string name, Func<ScenarioContext, Task> body) : IPlayerbotScenario
    {
        public string Name => name;

        public string Description => name;

        public Task RunAsync(ScenarioContext context) => body(context);
    }

    [Fact]
    public async Task Wsg_HurtBot_WalksOntoTheRestorationBuff_TheTrapCastsIt_AndTheBuffIsUsedUp()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartWithWsgBuffsAsync();
        ScenarioReport report = await world.RunAsync(new Scenario("bg-wsg-buff", async context =>
        {
            BattlegroundManager manager = context.Services.GetRequiredService<BattlegroundFeature>().Manager;
            PlayerbotBattlegroundCoordinator coordinator = context.Services.GetRequiredService<PlayerbotBattlegroundCoordinator>();
            SpellFeature spells = context.Services.GetRequiredService<SpellFeature>();
            ScenarioBot ally = await context.LoginAsync("Bgbuffally", 1, characterClass: 1);
            await context.AutonomousAsync(ally);

            // A scripted Horde opponent queues, ports in and is parked far out of the way (it never fights).
            ScenarioBot horde = await context.LoginAsync("Bgbuffhorde", race: 2, characterClass: 1);
            ObjectGuid master = ObjectGuid.WithEntry(HighGuid.Unit, Content.HordeWsgMaster, Content.HordeWsgMaster);
            Vector3 masterAt = Content.MasterAt(Content.HordeWsgMaster);
            await context.PlaceAsync(horde, 0, masterAt.X - 1.5f, masterAt.Y, masterAt.Z, 0f);
            long mark = horde.Mark();
            ScenarioContext.Expect(await horde.BattlemasterHelloAsync(master), "hello refused");
            await horde.WaitForPacketAsync(ArcaneCore.Protocol.WorldOpcode.SmsgBattlefieldList, p => p, since: mark);
            ScenarioContext.Expect(await horde.JoinBattlegroundAsync(master, 489), "join refused");
            await context.WaitUntilAsync("the Horde bot is invited", () => manager.StateOf(horde.Guid).InvitedInstance(BattlegroundQueueType.WarsongGulch) != 0, TimeSpan.FromMinutes(3));
            ScenarioContext.Expect(await horde.PortBattlegroundAsync(489), "port refused");
            await context.WaitUntilAsync("the Horde bot is in the match", () => horde.Session!.Player is { IsInWorld: true, MapId: 489 });
            await context.PlaceAsync(horde, 489, Content.HordeParking.X, Content.HordeParking.Y, Content.HordeParking.Z);

            await context.WaitUntilAsync("the Alliance bot is in the match", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.InMatch, TimeSpan.FromSeconds(60));
            var wsg = (WarsongGulch)(await context.BattlegroundOfAsync(ally))!;
            await context.WaitUntilAsync("the gates open", () => wsg.Status == BattlegroundStatus.InProgress, TimeSpan.FromMinutes(3));
            PlayerbotBattlegroundAI ai = (await context.ReadAsync(() => coordinator.Find(ally.BotId)!.AI))!;

            bool BuffSpawned(uint entry) => ally.RequirePlayer().Map!.FindUpdater<GameObjectMapSystem>()!.GameObjects.Any(g => g.Entry == entry && g.IsSpawned);
            ScenarioContext.Expect(await context.ReadAsync(() => BuffSpawned(BattlegroundConstants.RegenBuffEntry) && BuffSpawned(BattlegroundConstants.BerserkBuffEntry)),
                "both buffs are spawned when the gates open");

            // Hurt the bot: 30% health, nobody near to fight.
            long startMs = await context.ReadAsync(() =>
            {
                Player p = ally.RequirePlayer();
                p.Health = Math.Max(1u, p.MaxHealth * 3 / 10);
                return (long)context.World.Uptime.TotalMilliseconds;
            });

            await context.WaitUntilAsync("the restoration buff's aura is on the bot", () => spells.System.HasAura(ally.RequirePlayer(), Content.RestorationBuffSpell), TimeSpan.FromSeconds(30));
            await context.WaitUntilAsync("the restoration buff is used up and the bot lets it go", () => !BuffSpawned(BattlegroundConstants.RegenBuffEntry)
                && ai.Buffs.Pursuit is null && ai.Buffs.Taken == 1, TimeSpan.FromSeconds(10));

            (bool berserkSpawned, bool berserkAura, int scans, long elapsed) = await context.ReadAsync(() => (
                BuffSpawned(BattlegroundConstants.BerserkBuffEntry),
                spells.System.HasAura(ally.RequirePlayer(), Content.BerserkBuffSpell),
                PlayerbotBattlegroundBuffBoard.Of(ally.RequirePlayer().Map!).Scans,
                (long)context.World.Uptime.TotalMilliseconds - startMs));
            ScenarioContext.Expect(berserkSpawned && !berserkAura, "a bot out of any fight left the berserking buff alone");
            ScenarioContext.Expect(scans <= (elapsed / PlayerbotBattlegroundBuffBoard.RefreshMs) + 2,
                $"the board scanned the map {scans} times in {elapsed} ms: once per {PlayerbotBattlegroundBuffBoard.RefreshMs} ms at most, not per think");
        }), new ScenarioRunOptions { StepTimeout = TimeSpan.FromSeconds(60), MaxDuration = TimeSpan.FromMinutes(15) });
        Assert.True(report.Passed, report + Environment.NewLine + world.Trace());
    }
}

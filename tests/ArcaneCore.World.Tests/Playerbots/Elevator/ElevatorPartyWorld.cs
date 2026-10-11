using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Party;
using ArcaneCore.World.Tests.Playerbots.Tram;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// The Great Lift fixture (<see cref="ElevatorTestWorld"/>'s cars, cliff and shafts without a floor) in a <see cref="PartyTestHost"/>: a
/// real clock and world thread, a bot run by <see cref="ManagedPlayerbotFeature"/> (its brain, its party AI and the hand-off between them)
/// and a real player's client that can invite it. Polls in real time.
/// </summary>
internal sealed class ElevatorPartyWorld : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(150);

    private ElevatorPartyWorld(WorldTestHost host, string botName, Guid botId, WorldTestClient master)
    {
        Host = host;
        BotName = botName;
        BotId = botId;
        Master = master;
    }

    public WorldTestHost Host { get; }

    public string BotName { get; }

    public Guid BotId { get; }

    public WorldTestClient Master { get; }

    public static async Task<ElevatorPartyWorld> StartAsync(string botName)
    {
        WorldTestHost host = PartyTestHost.Start(options =>
        {
            options.Party.InvitePolicy = PlayerbotInvitePolicy.Anyone;
            options.MaxPathPoints = 128;
            options.MaxRouteYards = 2_000;
        }, services =>
        {
            services.AddSingleton<IMapDataStore>(new MapStore());
            services.AddScoped<IGameObjectDataStore>(_ => new GameObjectStore());
            services.AddSingleton(ElevatorTestData.Animation());
        });
        await host.OnWorldAsync(() =>
        {
            WorldCollision.Of(host.World).Install(lineOfSight: new ElevatorTestWorld.Floors(null), pathfinder: new ElevatorTestWorld.CliffPathfinder());
            PlayerbotElevator.UseSurvey(host.World, _ => new ElevatorSurvey(at => at.X < ElevatorTestWorld.Cliff
                ? (ElevatorTestWorld.ThousandNeedles, 0u) : (ElevatorTestWorld.TheBarrens, 0u)));
        });
        Guid id = await PartyTestHost.StartBotAsync(host, botName);
        Vector3 start = ElevatorTestWorld.CanyonStart;
        Assert.True(await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<TeleportFeature>().Teleports
            .TeleportTo(host.World.FindOnlinePlayer(botName)!, ElevatorTestData.Kalimdor, start.X, start.Y, start.Z, 0)), "teleport refused");
        await WaitAsync(host, () => host.World.FindOnlinePlayer(botName) is { IsInWorld: true, MapId: ElevatorTestData.Kalimdor } bot
            && host.WorldServices.GetRequiredService<TeleportFeature>().Teleports.StageOf(bot) is null, "the bot arrives in Thousand Needles");
        WorldTestClient master = await host.EnterWorldAsync("LIFTMASTER", "Liftmaster");
        return new ElevatorPartyWorld(host, botName, id, master);
    }

    private Player Bot => Host.World.FindOnlinePlayer(BotName)!;

    /// <summary>Begin the bot's ride up from the foot and wait (real time) until it is aboard at least <paramref name="above"/> yards up.</summary>
    public async Task RideIntoTheShaftAsync(float above)
    {
        await Host.OnWorldAsync(() =>
        {
            ElevatorLift lift = Assert.Single(PlayerbotElevator.NetworkOf((WorldSession)Bot.Session)!.Lifts);
            PlayerbotElevator.Begin((WorldSession)Bot.Session, Bot, lift, ElevatorLevel.Low, ElevatorTestWorld.Ratchet);
        });
        await WaitAsync(Host, () => PlayerbotElevator.TripOf(Bot) is { Stage: ElevatorStage.Aboard }
            && Bot.Z > ElevatorTestWorld.BottomFloor + above, "the bot rides up the shaft");
    }

    public Task InviteAsync() => Master.SendAsync(WorldOpcode.CmsgGroupInvite, PartyTestHost.CString(BotName));

    /// <summary>Until the party AI drives the bot: where it stood then, whether it was on the car, and the lowest it went meanwhile.</summary>
    public async Task<(bool PartyDrove, Vector3 At, bool Aboard, float Lowest)> WatchHandOverAsync()
    {
        ManagedPlayerbotFeature feature = PartyTestHost.Feature(Host);
        float lowest = float.MaxValue;
        DateTime deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            (Vector3 at, bool aboard) = await Host.OnWorldAsync(() =>
                (new Vector3(Bot.X, Bot.Y, Bot.Z), (Bot.Movement.Flags & MovementFlags.OnTransport) != 0));
            lowest = MathF.Min(lowest, at.Z);
            if (feature.IsPartyDriven(BotId)) return (true, at, aboard, lowest);
            await Task.Delay(20);
        }

        return (false, default, false, lowest);
    }

    private static async Task WaitAsync(WorldTestHost host, Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (!await host.OnWorldAsync(condition))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Master.DisposeAsync();
        await Host.DisposeAsync();
    }

    private sealed class MapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MapContent([.. TramTestData.Maps.Where(m => m.Entry != TramTestData.Tram)], [], [], [], []));
    }

    private sealed class GameObjectStore : IGameObjectDataStore
    {
        public Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(ElevatorTestData.Content(16874, 16876));
    }
}

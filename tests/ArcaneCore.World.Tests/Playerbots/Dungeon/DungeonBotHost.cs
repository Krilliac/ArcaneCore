using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// One world host for the dungeon bot unit tests: the Deadmines content for bots (<see cref="DeadminesTestContent.RegisterForBots"/>),
/// its flat floors, the manual world clock (time moves only through <see cref="AdvanceAsync"/>), and one managed bot session.
/// </summary>
internal sealed class DungeonBotHost : IAsyncDisposable
{
    private DungeonBotHost(WorldTestHost host, WorldSession session)
    {
        Host = host;
        Session = session;
    }

    public WorldTestHost Host { get; }

    public WorldSession Session { get; }

    public Player Player => Session.Player!;

    public TeleportService Teleports => Host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;

    public static async Task<DungeonBotHost> StartAsync(Action<IServiceCollection>? configure = null)
    {
        WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            DeadminesTestContent.RegisterForBots(services);
            services.AddSingleton<IWorldFeature, ManualClockFeature>();
            // The scenario world's factions (ScenarioTestContent): the spirit healer is Stormwind's, friendly to the human bot.
            services.AddSingleton(new FactionTemplateCatalog(
            [
                new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
                new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
                new FactionTemplateRecord(12, 72, 0, OwnMask: 2, FriendlyMask: 2, HostileMask: 8),
            ]));
            services.AddSingleton(new FactionCatalog([new FactionRecord(72, 0, [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0], name: "Stormwind")]));
            configure?.Invoke(services);
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        await host.OnWorldAsync(() => DeadminesTestContent.InstallCollision(host.World));
        return new DungeonBotHost(host, session);
    }

    /// <summary>Let <paramref name="milliseconds"/> of game time pass (world ticks run).</summary>
    public Task AdvanceAsync(uint milliseconds) => Host.World.AdvanceClockAsync(milliseconds);

    public Task<T> OnWorldAsync<T>(Func<T> action) => Host.OnWorldAsync(action);

    public Task OnWorldAsync(Action action) => Host.OnWorldAsync(action);

    /// <summary>Teleport the bot and acknowledge like a client until it stands on <paramref name="mapId"/>.</summary>
    public async Task TeleportAsync(uint mapId, float x, float y, float z, float orientation = 0f)
    {
        bool started = await OnWorldAsync(() => Teleports.TeleportTo(Player, mapId, x, y, z, orientation));
        if (!started) throw new InvalidOperationException($"teleport to map {mapId} refused");
        await AcknowledgeUntilAsync(player => player.MapId == mapId, $"arrives on map {mapId}");
    }

    /// <summary>Tick the world and acknowledge the server's orders (teleports included) until <paramref name="done"/> holds (bounded by game time).</summary>
    public async Task AcknowledgeUntilAsync(Func<Player, bool> done, string what, int ticks = 200)
    {
        for (int i = 0; i < ticks; i++)
        {
            bool reached = await OnWorldAsync(() =>
            {
                if (Session.Player is { } player) PlayerbotMovementControl.Update(Session, player);
                return Session.Player is { IsInWorld: true } p && Teleports.StageOf(p) is null && done(p);
            });
            if (reached) return;
            await AdvanceAsync(50);
        }

        throw new TimeoutException($"timed out waiting for: {what}");
    }

    /// <summary>
    /// Kill the bot where it stands and release its spirit; the graveyard trip (when there is one) runs on the next player ticks.
    /// Waits until <paramref name="released"/> holds for the ghost (default: a few ticks for a ghost that stays where it died).
    /// </summary>
    public async Task KillAndReleaseAsync(Func<Player, bool>? released = null)
    {
        await OnWorldAsync(() =>
        {
            Player.Health = 0;
            Player.Map!.Combat.KillPlayer(Player);
            if (!Player.Map!.Combat.RepopPlayer(Player)) throw new InvalidOperationException("release refused");
        });
        int ticks = 0;
        await AcknowledgeUntilAsync(p => (p.Flags & PlayerFlags.Ghost) != 0 && (released?.Invoke(p) ?? ++ticks > 10), "the release");
    }

    /// <summary>Add a creature beside the bot (directly into the map, as the brain tests do).</summary>
    public static Creature AddCreature(Map map, uint low, float x, float y, float z, uint npcFlags, uint now, uint faction = 0)
    {
        var creature = new Creature(low, new CreatureTemplate
        {
            Entry = low, Name = "creature " + low, CreatureType = 1, NpcFlags = npcFlags, Faction = faction,
            MinLevel = 1, MaxLevel = 1, MinLevelHealth = 20, MaxLevelHealth = 20,
        }, null, CreatureContent.Empty, new Random((int)low));
        creature.Relocate(x, y, z, 0, now);
        map.AddObject(creature);
        return creature;
    }

    public async ValueTask DisposeAsync()
    {
        Session.Kick();
        await Session.ManagedClosed;
        await Host.DisposeAsync();
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }
}
